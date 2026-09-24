using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using TideCasa.Api.Features.RestaurantOrdering;
using TideCasa.Api.Infrastructure;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.DriverNetwork;

public sealed class DriverNetworkStore(ApplicationDatabase database)
{
    private sealed record Profile(NetworkDriverProfile Public, string UserId, string Email);
    private sealed record Client(string Name, string Role, string? OwnerId);
    private static string Now() => DateTimeOffset.UtcNow.ToString("O");
    private static string Id() => Guid.NewGuid().ToString("D");
    private static OrderingException Stale() => new("This driver record changed. Refresh before saving.", 409, "network_changed");
    private static OrderingException Missing() => new("This driver or hire is unavailable.", 404, "not_found");
    private static readonly Regex Zip = new("^[0-9]{5}$", RegexOptions.CultureInvariant);

    public async Task<DriverNetworkAccount> AccountAsync(AuthUser user, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: true);
        var profile = await ReadProfile(db, tx, user.UserId, true, ct);
        List<NetworkDriverHire> hires = profile is null ? [] : await Hires(db, tx, null, profile.Public.Id, ct);
        return new(profile?.Public, hires);
    }

    public async Task<DriverNetworkAccount> SaveProfileAsync(AuthUser user, SaveNetworkDriverProfile request, CancellationToken ct)
    {
        if (request is null || request.ExpectedVersion < 0 || request.Capacity is < 1 or > 10 || request.DeliveryZips is null
            || request.DeliveryZips.Count is < 1 or > 50 || request.DeliveryZips.Any(z => z is null || !Zip.IsMatch(z))
            || request.DeliveryZips.Distinct().Count() != request.DeliveryZips.Count)
            throw new OrderingException("Choose 1–50 delivery ZIP codes and a capacity of 1–10 deliveries.");
        var name = Text(request.Name, "Driver name", 80, required: true, multiline: false);
        var bio = Text(request.Bio, "About you", 1000);
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        var prior = await ReadProfile(db, tx, user.UserId, true, ct);
        if ((prior?.Public.Version ?? 0) != request.ExpectedVersion) throw Stale();
        var now = Now();
        await using var command = Command(db, tx, """
            INSERT INTO tide_network_drivers(id,user_id,email,name,bio,zips_json,listed,capacity,version,created_at,updated_at)
            VALUES(@id,@user,@email,@name,@bio,@zips,@listed,@capacity,1,@now,@now)
            ON CONFLICT(user_id) DO UPDATE SET email=@email,name=@name,bio=@bio,zips_json=@zips,listed=@listed,
                capacity=@capacity,version=tide_network_drivers.version+1,updated_at=@now
            """, ("@id", prior?.Public.Id ?? Id()), ("@user", user.UserId), ("@email", user.Email.Trim().ToLowerInvariant()),
            ("@name", name), ("@bio", bio), ("@zips", JsonSerializer.Serialize(request.DeliveryZips)), ("@listed", request.Listed ? 1 : 0),
            ("@capacity", request.Capacity), ("@now", now));
        await command.ExecuteNonQueryAsync(ct); await tx.CommitAsync(ct);
        return await AccountAsync(user, ct);
    }

    public async Task<ClientDriverNetworkWorkspace> WorkspaceAsync(string tenant, AuthUser user, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        var client = await Manager(db, tx, tenant, user, ct);
        var drivers = new List<NetworkDriverProfile>();
        await using (var command = Command(db, tx, "SELECT id,name,bio,zips_json,listed,capacity,version FROM tide_network_drivers WHERE listed=1 ORDER BY name,id LIMIT 500"))
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) drivers.Add(PublicProfile(reader));
        }
        var hires = await Hires(db, tx, tenant, null, ct); await tx.CommitAsync(ct);
        return new(tenant, client.Name, client.Role, drivers, hires);
    }

    public async Task<ClientDriverNetworkWorkspace> OfferAsync(string tenant, AuthUser user, OfferNetworkDriverHire request, CancellationToken ct)
    {
        if (request is null || !Guid.TryParseExact(request.DriverId, "D", out _) || request.ExpectedVersion < 0
            || request.PayPerDeliveryCents is < 50 or > 100000)
            throw new OrderingException("Choose a network driver and delivery pay from $0.50 to $1,000.00.");
        var notes = Text(request.Notes, "Offer notes", 500);
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        var client = await Manager(db, tx, tenant, user, ct);
        var driver = await ReadProfile(db, tx, request.DriverId, false, ct) ?? throw Missing();
        if (!driver.Public.Listed) throw Missing();
        if (client.OwnerId == driver.UserId) throw new OrderingException("A business owner cannot hire their own account as a network driver.", 409, "in_house_driver");
        var prior = (await Hires(db, tx, tenant, request.DriverId, ct)).SingleOrDefault();
        if ((prior?.Version ?? 0) != request.ExpectedVersion) throw Stale();
        if (prior?.Status == "active") throw new OrderingException("End the active hire before offering new terms.", 409, "hire_active");
        await NoOtherMembership(db, tx, tenant, driver, prior?.MemberId, ct);
        var now = Now();
        if (prior?.MemberId is not null)
        {
            await using var pause = Command(db, tx, "UPDATE bartide_enhanced_members SET active=0 WHERE id=@member AND tenant_id=@tenant",
                ("@member", prior.MemberId), ("@tenant", tenant));
            await pause.ExecuteNonQueryAsync(ct);
        }
        await using var command = Command(db, tx, """
            INSERT INTO tide_network_hires(id,tenant_id,driver_id,status,pay_per_delivery_cents,notes,version,created_at,updated_at,updated_by)
            VALUES(@id,@tenant,@driver,'offered',@pay,@notes,1,@now,@now,@actor)
            ON CONFLICT(tenant_id,driver_id) DO UPDATE SET status='offered',pay_per_delivery_cents=@pay,notes=@notes,
                version=tide_network_hires.version+1,updated_at=@now,updated_by=@actor,accepted_at=NULL,ended_at=NULL
            """, ("@id", prior?.Id ?? Id()), ("@tenant", tenant), ("@driver", driver.Public.Id), ("@pay", request.PayPerDeliveryCents),
            ("@notes", notes), ("@now", now), ("@actor", user.UserId));
        await command.ExecuteNonQueryAsync(ct); await tx.CommitAsync(ct);
        return await WorkspaceAsync(tenant, user, ct);
    }

    public async Task<DriverNetworkAccount> RespondAsync(string hireId, AuthUser user, RespondNetworkDriverHire request, CancellationToken ct)
    {
        if (request is null || request.ExpectedVersion < 0 || !Guid.TryParseExact(hireId, "D", out _)) throw Missing();
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        var driver = await ReadProfile(db, tx, user.UserId, true, ct) ?? throw Missing();
        var hire = (await Hires(db, tx, null, driver.Public.Id, ct)).FirstOrDefault(h => h.Id == hireId) ?? throw Missing();
        if (hire.Version != request.ExpectedVersion) throw Stale();
        if (hire.Status != "offered") throw new OrderingException("Only a pending offer can be accepted or declined.", 409, "hire_not_offered");
        var member = hire.MemberId; var now = Now();
        if (request.Accept)
        {
            var client = await ActiveClient(db, tx, hire.TenantId, ct);
            if (client.OwnerId == user.UserId)
                throw new OrderingException("A business owner cannot accept their own network hire.", 409, "in_house_driver");
            // Recheck invitations at acceptance: staff may have been added after the offer.
            var currentDriver = driver with { Email = user.Email.Trim().ToLowerInvariant() };
            await NoOtherMembership(db, tx, hire.TenantId, currentDriver, member, ct);
            if (member is null)
            {
                await using var count = Command(db, tx, "SELECT COUNT(*) FROM bartide_enhanced_members WHERE tenant_id=@tenant", ("@tenant", hire.TenantId));
                if (Convert.ToInt64(await count.ExecuteScalarAsync(ct)) >= 30)
                    throw new OrderingException("This client has reached its 30-person team limit.", 409, "team_full");
                member = Id();
                await using var insert = Command(db, tx, """
                    INSERT INTO bartide_enhanced_members(id,tenant_id,name,email,user_id,role,active,created_at)
                    VALUES(@member,@tenant,@name,@email,@user,'driver',1,@now)
                    """, ("@member", member), ("@tenant", hire.TenantId), ("@name", driver.Public.Name),
                    ("@email", currentDriver.Email), ("@user", user.UserId), ("@now", now));
                await insert.ExecuteNonQueryAsync(ct);
            }
            else
            {
                await using var restore = Command(db, tx, """
                    UPDATE bartide_enhanced_members SET active=1,name=@name,email=@email
                    WHERE id=@member AND tenant_id=@tenant AND user_id=@user AND role='driver'
                    """, ("@member", member), ("@tenant", hire.TenantId), ("@user", user.UserId),
                    ("@name", driver.Public.Name), ("@email", currentDriver.Email));
                if (await restore.ExecuteNonQueryAsync(ct) != 1) throw new OrderingException("This membership needs a client review before it can be restored.", 409, "member_changed");
            }
            await using var dispatch = Command(db, tx, """
                INSERT INTO tide_delivery_drivers(tenant_id,driver_id,availability,capacity,zips_json,version,updated_at,updated_by)
                VALUES(@tenant,@member,'offline',1,'[]',1,@now,@actor)
                ON CONFLICT(tenant_id,driver_id) DO UPDATE SET availability='offline',capacity=1,zips_json='[]',
                    version=tide_delivery_drivers.version+1,updated_at=@now,updated_by=@actor
                """, ("@tenant", hire.TenantId), ("@member", member), ("@now", now), ("@actor", user.UserId));
            await dispatch.ExecuteNonQueryAsync(ct);
        }
        await using var update = Command(db, tx, """
            UPDATE tide_network_hires SET status=@status,member_id=@member,version=version+1,updated_at=@now,updated_by=@actor,accepted_at=@accepted
            WHERE id=@id AND driver_id=@driver AND version=@version AND status='offered'
            """, ("@status", request.Accept ? "active" : "declined"), ("@member", member), ("@now", now), ("@actor", user.UserId),
            ("@accepted", request.Accept ? now : null), ("@id", hire.Id), ("@driver", driver.Public.Id), ("@version", request.ExpectedVersion));
        if (await update.ExecuteNonQueryAsync(ct) != 1) throw Stale();
        await tx.CommitAsync(ct); return await AccountAsync(user, ct);
    }

    public async Task<ClientDriverNetworkWorkspace> EndAsync(string tenant, string hireId, AuthUser user, EndNetworkDriverHire request, CancellationToken ct)
    {
        if (request is null || request.ExpectedVersion < 0 || !Guid.TryParseExact(hireId, "D", out _)) throw Missing();
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        await Manager(db, tx, tenant, user, ct);
        var hire = (await Hires(db, tx, tenant, null, ct)).FirstOrDefault(h => h.Id == hireId) ?? throw Missing();
        if (hire.Version != request.ExpectedVersion) throw Stale();
        if (hire.Status is not ("active" or "offered")) throw new OrderingException("This hire has already ended.", 409, "hire_not_active");
        if (hire.MemberId is not null)
        {
            await using var load = Command(db, tx, "SELECT COUNT(*) FROM bartide_enhanced_orders WHERE tenant_id=@tenant AND driver_id=@member AND status NOT IN ('completed','cancelled','canceled','delivered')",
                ("@tenant", tenant), ("@member", hire.MemberId));
            if (Convert.ToInt64(await load.ExecuteScalarAsync(ct)) > 0)
                throw new OrderingException("Complete or reassign this driver's active deliveries before ending the hire.", 409, "active_deliveries");
            await using var pause = Command(db, tx, "UPDATE bartide_enhanced_members SET active=0 WHERE id=@member AND tenant_id=@tenant", ("@member", hire.MemberId), ("@tenant", tenant));
            await pause.ExecuteNonQueryAsync(ct);
            await using var location = Command(db, tx, "DELETE FROM tide_delivery_locations WHERE tenant_id=@tenant AND driver_id=@member", ("@tenant", tenant), ("@member", hire.MemberId));
            await location.ExecuteNonQueryAsync(ct);
            await using var offline = Command(db, tx, "UPDATE tide_delivery_drivers SET availability='offline',version=version+1,updated_at=@now,updated_by=@actor WHERE tenant_id=@tenant AND driver_id=@member",
                ("@now", Now()), ("@actor", user.UserId), ("@tenant", tenant), ("@member", hire.MemberId));
            await offline.ExecuteNonQueryAsync(ct);
        }
        await using var command = Command(db, tx, "UPDATE tide_network_hires SET status='ended',version=version+1,updated_at=@now,updated_by=@actor,ended_at=@now WHERE id=@id AND tenant_id=@tenant AND version=@version",
            ("@now", Now()), ("@actor", user.UserId), ("@id", hireId), ("@tenant", tenant), ("@version", request.ExpectedVersion));
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw Stale();
        await tx.CommitAsync(ct); return await WorkspaceAsync(tenant, user, ct);
    }

    private static async Task NoOtherMembership(DbConnection db, DbTransaction tx, string tenant, Profile driver, string? ownMember, CancellationToken ct)
    {
        await using var command = Command(db, tx, db.Sql("""
            SELECT COUNT(*) FROM bartide_enhanced_members WHERE tenant_id=@tenant AND (@own IS NULL OR id<>@own)
                AND (user_id=@user OR email=@email COLLATE NOCASE)
            """, """
            SELECT COUNT(*) FROM bartide_enhanced_members WHERE tenant_id=@tenant AND (@own IS NULL OR id<>@own)
                AND (user_id=@user OR translate(email,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz')=translate(@email,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz') COLLATE "C")
            """), ("@tenant", tenant), ("@own", ownMember), ("@user", driver.UserId), ("@email", driver.Email));
        if (Convert.ToInt64(await command.ExecuteScalarAsync(ct)) > 0)
            throw new OrderingException("This person is already on the client's team. Keep their existing in-house membership.", 409, "in_house_driver");
    }

    private static async Task<Client> ActiveClient(DbConnection db, DbTransaction tx, string tenant, CancellationToken ct)
    {
        await using var command = Command(db, tx, "SELECT c.name,c.user_id,c.status,c.vertical,e.settings_json FROM bartide_customers c LEFT JOIN bartide_enhanced_configs e ON e.tenant_id=c.id WHERE c.id=@tenant", ("@tenant", tenant));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.GetString(2) != "active" || reader.GetString(3) != "bartide" || reader.IsDBNull(4))
            throw new OrderingException("An active BarTide client is required.", 403, "forbidden");
        using var config = JsonDocument.Parse(reader.GetString(4));
        if (!config.RootElement.TryGetProperty("enabled", out var enabled) || enabled.ValueKind != JsonValueKind.True)
            throw new OrderingException("This BarTide workspace is unavailable.", 403, "forbidden");
        return new(reader.GetString(0), "", reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private static async Task<Client> Manager(DbConnection db, DbTransaction tx, string tenant, AuthUser user, CancellationToken ct)
    {
        var client = await ActiveClient(db, tx, tenant, ct);
        if (user.IsPlatformOwner || client.OwnerId == user.UserId) return client with { Role = "owner" };
        if (await TenantStaffAccess.IsManagerAsync(db, tx, tenant, user, ct)) return client with { Role = "manager" };
        throw new OrderingException("Only the client owner or manager can hire drivers.", 403, "forbidden");
    }

    private static NetworkDriverProfile PublicProfile(DbDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2),
        JsonSerializer.Deserialize<string[]>(r.GetString(3)) ?? [], r.ReadInt64(4) == 1, r.ReadInt32(5), r.ReadInt32(6));

    private static async Task<Profile?> ReadProfile(DbConnection db, DbTransaction tx, string ident, bool byUser, CancellationToken ct)
    {
        await using var command = Command(db, tx, "SELECT id,name,bio,zips_json,listed,capacity,version,user_id,email FROM tide_network_drivers WHERE " + (byUser ? "user_id" : "id") + "=@id", ("@id", ident));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(PublicProfile(reader), reader.GetString(7), reader.GetString(8)) : null;
    }

    private static async Task<List<NetworkDriverHire>> Hires(DbConnection db, DbTransaction tx, string? tenant, string? driver, CancellationToken ct)
    {
        var result = new List<NetworkDriverHire>();
        await using var command = Command(db, tx, """
            SELECT h.id,h.driver_id,d.name,h.tenant_id,c.name,h.status,h.pay_per_delivery_cents,h.member_id,h.version,h.notes
            FROM tide_network_hires h JOIN tide_network_drivers d ON d.id=h.driver_id JOIN bartide_customers c ON c.id=h.tenant_id
            WHERE (@tenant IS NULL OR h.tenant_id=@tenant) AND (@driver IS NULL OR h.driver_id=@driver)
            ORDER BY h.created_at DESC,h.id
            """, ("@tenant", tenant), ("@driver", driver));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.GetString(5), reader.ReadInt32(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.ReadInt32(8), reader.GetString(9)));
        return result;
    }

    private static string Text(string? value, string label, int max, bool required = false, bool multiline = true)
    {
        var text = value?.Trim() ?? "";
        if (text.Length > max || required && text.Length == 0 || text.Any(c => char.IsControl(c) && (!multiline || c is not ('\r' or '\n' or '\t'))))
            throw new OrderingException($"Enter a valid {label.ToLowerInvariant()} of up to {max} characters.");
        return text;
    }

    internal static DbCommand Command(DbConnection db, DbTransaction tx, string sql, params (string Key, object? Value)[] values)
    {
        var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
        foreach (var (key, value) in values) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return command;
    }
}
