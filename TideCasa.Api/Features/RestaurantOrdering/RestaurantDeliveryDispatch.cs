using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TideCasa.Api.Infrastructure;
using TideCasa.Contracts;
using TideCasa.Api.Features.DriverNetwork;

namespace TideCasa.Api.Features.RestaurantOrdering;

public sealed partial class RestaurantOrderingStore
{
    private static bool DispatchEnabled(Venue venue) => Boolean(venue.Config, "enabled", false)
        && Boolean(venue.Config, "delivery_enabled", false) && Boolean(venue.Config, "delivery_workflow_enabled", false);
    private static void DispatchManager(string role)
    {
        if (role is not ("owner" or "manager")) throw new OrderingException("Only a restaurant owner or manager can manage dispatch.", 403, "forbidden");
    }
    private static OrderingException DispatchStale() => new("Dispatch changed. Refresh before saving.", 409, "stale_dispatch");
    private static DeliveryAssignmentPlan? AssignmentPlan(JsonObject payload) => payload["dispatch_plan"] is JsonObject plan
        ? new(String(plan, "mode"), plan["dispatch_at"]?.GetValue<string>(), plan["preferred_driver_id"]?.GetValue<string>()) : null;

    private static async Task<(bool Automatic, int Version)> DispatchSettings(DbConnection db, DbTransaction tx, string id, CancellationToken ct)
    {
        await using var command = Command(db, tx, "SELECT automatic_assignment,version FROM tide_delivery_dispatch WHERE tenant_id=@id", ("@id", id));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (reader.ReadInt64(0) == 1, reader.ReadInt32(1)) : (false, 0);
    }

    private static async Task<List<TeamShift>> DriverShifts(DbConnection db, DbTransaction tx, string id, CancellationToken ct)
    {
        var shifts = new List<TeamShift>();
        await using var command = Command(db, tx, db.Sql("""
            SELECT s.id,s.member_id,s.starts_at,s.ends_at,s.label FROM bartide_enhanced_shifts s
            JOIN bartide_enhanced_members m ON m.id=s.member_id AND m.tenant_id=s.tenant_id
            WHERE s.tenant_id=@id AND m.active=1 AND m.role='driver' AND julianday(s.ends_at)>julianday(@now)
            ORDER BY julianday(s.starts_at) LIMIT 300
            """, """
            SELECT s.id,s.member_id,s.starts_at,s.ends_at,s.label FROM bartide_enhanced_shifts s
            JOIN bartide_enhanced_members m ON m.id=s.member_id AND m.tenant_id=s.tenant_id
            WHERE s.tenant_id=@id AND m.active=1 AND m.role='driver' AND tide_iso_instant(s.ends_at)>tide_iso_instant(@now)
            ORDER BY tide_iso_instant(s.starts_at) LIMIT 300
            """), ("@id", id), ("@now", Stamp()));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) shifts.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        return shifts;
    }

    private static async Task<List<DeliveryDriverProfile>> DriverProfiles(DbConnection db, DbTransaction tx, string id, IReadOnlyList<TeamShift> shifts, CancellationToken ct)
    {
        var drivers = new List<DeliveryDriverProfile>(); var now = DateTimeOffset.UtcNow;
        await using var command = Command(db, tx, """
            SELECT m.id,m.name,m.user_id,p.availability,p.capacity,p.zips_json,p.version,p.last_assigned_at,
              (SELECT COUNT(*) FROM bartide_enhanced_orders o WHERE o.tenant_id=m.tenant_id AND o.driver_id=m.id
                AND o.status NOT IN ('completed','cancelled','canceled','delivered'))
            FROM bartide_enhanced_members m LEFT JOIN tide_delivery_drivers p ON p.tenant_id=m.tenant_id AND p.driver_id=m.id
            WHERE m.tenant_id=@id AND m.role='driver' AND m.active=1 ORDER BY m.name,m.id
            """, ("@id", id));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var driver = reader.GetString(0); var availability = reader.IsDBNull(3) ? "offline" : reader.GetString(3);
            var linked = !reader.IsDBNull(2) && reader.GetString(2).Length > 0;
            var onShift = shifts.Any(s => s.MemberId == driver && DateTimeOffset.TryParse(s.StartsAt, out var start)
                && DateTimeOffset.TryParse(s.EndsAt, out var end) && start <= now && end > now);
            drivers.Add(new(driver, reader.GetString(1), linked, availability,
                reader.IsDBNull(4) ? 1 : reader.ReadInt32(4), reader.IsDBNull(5) ? [] : JsonSerializer.Deserialize<string[]>(reader.GetString(5), Json) ?? [],
                reader.ReadInt32(8), linked && (availability == "available" || availability == "scheduled" && onShift),
                reader.IsDBNull(6) ? 0 : reader.ReadInt32(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        return drivers;
    }

    public async Task<DeliveryDispatchWorkspace> DispatchWorkspaceAsync(string id, AuthUser user, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        var venue = await ReadVenueAsync(db, tx, id, true, ct);
        var access = await OperatorAsync(db, tx, venue, user, ct);
        if (access.Role is not ("owner" or "manager" or "driver")) throw new OrderingException("Driver or dispatch access is required.", 403, "forbidden");
        var settings = await DispatchSettings(db, tx, id, ct);
        var shifts = await DriverShifts(db, tx, id, ct);
        var drivers = await DriverProfiles(db, tx, id, shifts, ct);
        if (access.Role == "driver")
        {
            drivers = drivers.Where(d => d.Id == access.MemberId).ToList();
            shifts = shifts.Where(s => s.MemberId == access.MemberId).ToList();
        }
        await tx.CommitAsync(ct);
        return new(id, access.Role, access.MemberId, settings.Automatic, settings.Version, DispatchEnabled(venue), drivers, shifts, Stamp());
    }

    public async Task<DeliveryDispatchWorkspace> SaveDispatchSettingsAsync(string id, AuthUser user, SaveDeliveryDispatchSettings request, CancellationToken ct)
    {
        if (request is null || request.ExpectedVersion < 0) throw new OrderingException("Review dispatch settings first.");
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        var venue = await ReadVenueAsync(db, tx, id, true, ct);
        DispatchManager((await OperatorAsync(db, tx, venue, user, ct)).Role);
        if (request.AutomaticAssignment && !DispatchEnabled(venue)) throw new OrderingException("Enable delivery and the delivery workflow in menu settings first.", 409, "delivery_disabled");
        var saved = await DispatchSettings(db, tx, id, ct);
        if (saved.Version != request.ExpectedVersion) throw DispatchStale();
        await using var command = Command(db, tx, """
            INSERT INTO tide_delivery_dispatch(tenant_id,automatic_assignment,version,updated_at,updated_by) VALUES(@id,@auto,1,@now,@actor)
            ON CONFLICT(tenant_id) DO UPDATE SET automatic_assignment=@auto,version=tide_delivery_dispatch.version+1,updated_at=@now,updated_by=@actor
            """, ("@id", id), ("@auto", request.AutomaticAssignment ? 1 : 0), ("@now", Stamp()), ("@actor", user.UserId));
        await command.ExecuteNonQueryAsync(ct); await tx.CommitAsync(ct);
        return await DispatchWorkspaceAsync(id, user, ct);
    }

    public async Task<DeliveryDispatchWorkspace> SaveDriverProfileAsync(string id, string driverId, AuthUser user, SaveDeliveryDriverProfile request, CancellationToken ct)
    {
        if (request is null || request.ExpectedVersion < 0 || request.Availability is not ("offline" or "available" or "scheduled")
            || request.Capacity is < 1 or > 10 || request.DeliveryZips is null || request.DeliveryZips.Count > 50
            || request.DeliveryZips.Any(z => z is null || !ZipPattern.IsMatch(z)) || request.DeliveryZips.Distinct().Count() != request.DeliveryZips.Count)
            throw new OrderingException("Choose availability, 1–10 orders and valid delivery ZIP codes.");
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        var venue = await ReadVenueAsync(db, tx, id, true, ct); var access = await OperatorAsync(db, tx, venue, user, ct);
        if (access.Role is not ("owner" or "manager") && (access.Role != "driver" || access.MemberId != driverId))
            throw new OrderingException("You can only change your own driver availability.", 403, "forbidden");
        var profiles = await DriverProfiles(db, tx, id, await DriverShifts(db, tx, id, ct), ct);
        var profile = profiles.FirstOrDefault(d => d.Id == driverId) ?? throw new OrderingException("Active driver not found.", 404, "not_found");
        if (profile.Version != request.ExpectedVersion) throw DispatchStale();
        if (access.Role == "driver" && (profile.Capacity != request.Capacity || !profile.DeliveryZips.Order().SequenceEqual(request.DeliveryZips.Order())))
            throw new OrderingException("Only a manager can change delivery limits and ZIP codes.", 403, "forbidden");
        await using var command = Command(db, tx, """
            INSERT INTO tide_delivery_drivers(tenant_id,driver_id,availability,capacity,zips_json,version,updated_at,updated_by)
            VALUES(@id,@driver,@availability,@capacity,@zips,1,@now,@actor)
            ON CONFLICT(tenant_id,driver_id) DO UPDATE SET availability=@availability,capacity=@capacity,zips_json=@zips,
                version=tide_delivery_drivers.version+1,updated_at=@now,updated_by=@actor
            """, ("@id", id), ("@driver", driverId), ("@availability", request.Availability), ("@capacity", request.Capacity),
            ("@zips", JsonSerializer.Serialize(request.DeliveryZips, Json)), ("@now", Stamp()), ("@actor", user.UserId));
        await command.ExecuteNonQueryAsync(ct); await tx.CommitAsync(ct);
        return await DispatchWorkspaceAsync(id, user, ct);
    }

    public async Task<RestaurantOperationsWorkspace> SaveAssignmentPlanAsync(string id, string orderId, AuthUser user, SaveDeliveryAssignmentPlan request, CancellationToken ct)
    {
        if (request is null || request.Mode is not ("manual" or "automatic" or "scheduled") || !Guid.TryParseExact(orderId, "D", out _))
            throw new OrderingException("Choose manual, automatic or scheduled dispatch.");
        string? instant = null;
        if (request.Mode == "scheduled")
        {
            // Require an explicit offset; interpreting a local wall-clock time on the server can dispatch hours early.
            var value = request.DispatchAt;
            if (value is null || value.Length > 50 || !Regex.IsMatch(value, @"T.*(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)
                || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when)
                || when < DateTimeOffset.UtcNow || when > DateTimeOffset.UtcNow.AddDays(90))
                throw new OrderingException("Choose a future dispatch time within 90 days, including its time zone.");
            instant = when.ToUniversalTime().ToString("O");
        }
        else if (!string.IsNullOrEmpty(request.DispatchAt) || !string.IsNullOrEmpty(request.PreferredDriverId))
            throw new OrderingException("Only scheduled dispatch accepts a time or a preferred driver.");
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        var venue = await ReadVenueAsync(db, tx, id, true, ct); DispatchManager((await OperatorAsync(db, tx, venue, user, ct)).Role);
        if (!DispatchEnabled(venue)) throw new OrderingException("Enable delivery and its workflow before planning dispatch.", 409, "delivery_disabled");
        JsonObject payload; int version;
        await using (var read = Command(db, tx, "SELECT payload_json,version,driver_id FROM bartide_enhanced_orders WHERE tenant_id=@id AND id=@order", ("@id", id), ("@order", orderId)))
        {
            await using var reader = await read.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new OrderingException("Order not found.", 404, "not_found");
            payload = Parse(reader.GetString(0)); version = reader.ReadInt32(1);
            if (!reader.IsDBNull(2)) throw new OrderingException("This order already has a driver. Use reassignment instead.", 409, "already_assigned");
        }
        if (version != request.ExpectedVersion) throw DispatchStale();
        if (String(payload, "fulfillment") != "delivery" || String(payload, "status") is not ("new" or "accepted" or "preparing" or "ready"))
            throw new OrderingException("Only an open, unassigned delivery can be scheduled.", 409, "invalid_transition");
        if (!string.IsNullOrEmpty(request.PreferredDriverId))
        {
            await using var preferred = Command(db, tx, "SELECT COUNT(*) FROM bartide_enhanced_members WHERE tenant_id=@id AND id=@driver AND role='driver' AND active=1 AND user_id IS NOT NULL AND user_id<>''",
                ("@id", id), ("@driver", request.PreferredDriverId));
            if (Convert.ToInt64(await preferred.ExecuteScalarAsync(ct)) != 1) throw new OrderingException("Choose an active driver with a connected account.");
        }
        payload["dispatch_plan"] = new JsonObject { ["mode"] = request.Mode, ["dispatch_at"] = instant, ["preferred_driver_id"] = string.IsNullOrEmpty(request.PreferredDriverId) ? null : request.PreferredDriverId };
        await SaveDispatchOrder(db, tx, id, orderId, payload, null, version, "plan-delivery-" + request.Mode, user.UserId, ct);
        await tx.CommitAsync(ct); return await OperationsAsync(id, user, ct);
    }

    // All dispatch writers take the existing cross-process database transaction lock before reading load.
    // This is the same boundary as manual assignment, preventing concurrent capacity oversubscription.
    public async Task<int> DispatchNowAsync(string id, AuthUser user, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        var venue = await ReadVenueAsync(db, tx, id, true, ct); DispatchManager((await OperatorAsync(db, tx, venue, user, ct)).Role);
        var count = await DispatchOrders(db, tx, venue, user.UserId, ct); await tx.CommitAsync(ct); return count;
    }
    internal async Task<int> DispatchPendingAsync(string id, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        var venue = await ReadVenueAsync(db, tx, id, true, ct);
        var count = await DispatchOrders(db, tx, venue, "system:delivery-dispatch", ct); await tx.CommitAsync(ct); return count;
    }
    internal async Task<List<string>> DispatchTenantsAsync(CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: true);
        await using var command = Command(db, tx, "SELECT DISTINCT tenant_id FROM bartide_enhanced_orders WHERE driver_id IS NULL AND status IN ('accepted','preparing','ready')");
        await using var reader = await command.ExecuteReaderAsync(ct); var ids = new List<string>();
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetString(0));
        return ids;
    }
    private static async Task<int> DispatchOrders(DbConnection db, DbTransaction tx, Venue venue, string actor, CancellationToken ct)
    {
        if (!DispatchEnabled(venue)) return 0;
        var settings = await DispatchSettings(db, tx, venue.Id, ct);
        var drivers = await DriverProfiles(db, tx, venue.Id, await DriverShifts(db, tx, venue.Id, ct), ct);
        var orders = new List<(string Id, JsonObject Payload, int Version)>();
        await using (var command = Command(db, tx, "SELECT id,payload_json,version FROM bartide_enhanced_orders WHERE tenant_id=@id AND driver_id IS NULL AND status IN ('accepted','preparing','ready') ORDER BY created_at,id", ("@id", venue.Id)))
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) orders.Add((reader.GetString(0), Parse(reader.GetString(1)), reader.ReadInt32(2)));
        }
        var count = 0; var now = DateTimeOffset.UtcNow;
        foreach (var order in orders)
        {
            var payload = order.Payload; var receipt = Receipt(payload); var plan = AssignmentPlan(payload);
            if (receipt.Quote.Fulfillment != "delivery" || receipt.Quote.PaymentMethod == "phone" && receipt.PaymentStatus != "paid") continue;
            if (plan?.Mode == "manual" || plan is null && !settings.Automatic) continue;
            if (plan?.Mode == "scheduled" && (!DateTimeOffset.TryParse(plan.DispatchAt, out var due) || due > now)) continue;
            if (plan is not null && plan.Mode is not ("automatic" or "scheduled")) continue;
            var candidates = drivers.Where(d => d.AvailableNow && d.ActiveOrders < d.Capacity
                    && (d.DeliveryZips.Count == 0 || d.DeliveryZips.Contains(String(payload, "zip")))
                    && (plan?.PreferredDriverId is not { Length: > 0 } preferred || d.Id == preferred))
                .OrderBy(d => d.ActiveOrders).ThenBy(d => d.LastAssignedAt, StringComparer.Ordinal).ThenBy(d => d.Id, StringComparer.Ordinal);
            DeliveryDriverProfile? selected = null;
            foreach (var candidate in candidates)
            {
                try { await DriverNetworkPolicy.ReadAssignmentAsync(db, tx, venue.Id, candidate.Id, ct); selected = candidate; break; }
                catch (OrderingException error) when (error.Code == "driver_unavailable") { }
            }
            if (selected is null) continue; // Durable waiting queue; retried when a driver or capacity becomes available.
            await ChangeDeliveryAsync(db, tx, venue.Id, payload, null, "owner", actor,
                new(order.Version, "assign-driver", selected.Id), ct);
            await SaveDispatchOrder(db, tx, venue.Id, order.Id, payload, selected.Id, order.Version,
                plan?.Mode == "scheduled" ? "scheduled-assign-driver" : "auto-assign-driver", actor, ct);
            drivers[drivers.FindIndex(d => d.Id == selected.Id)] = selected with { ActiveOrders = selected.ActiveOrders + 1, LastAssignedAt = Stamp() };
            count++;
        }
        return count;
    }

    private static async Task ValidateDriverAssignment(DbConnection db, DbTransaction tx, string tenant, string? driver, JsonObject payload, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(driver)) await DriverNetworkPolicy.ReadAssignmentAsync(db, tx, tenant, driver, ct);
        await using var exists = Command(db, tx, "SELECT COUNT(*) FROM tide_delivery_drivers WHERE tenant_id=@id AND driver_id=@driver", ("@id", tenant), ("@driver", (object?)driver ?? DBNull.Value));
        if (Convert.ToInt64(await exists.ExecuteScalarAsync(ct)) == 0) return; // Legacy manual drivers are unchanged until configured.
        var profiles = await DriverProfiles(db, tx, tenant, await DriverShifts(db, tx, tenant, ct), ct);
        var profile = profiles.FirstOrDefault(d => d.Id == driver);
        if (profile is null || !profile.AvailableNow || profile.ActiveOrders >= profile.Capacity
            || profile.DeliveryZips.Count > 0 && !profile.DeliveryZips.Contains(String(payload, "zip")))
            throw new OrderingException("Choose an available driver with capacity who serves this delivery ZIP code.", 409, "driver_unavailable");
    }

    private static async Task CaptureDriverSource(DbConnection db, DbTransaction tx, string tenant, string driver, JsonObject payload, CancellationToken ct)
    {
        var network = await DriverNetworkPolicy.ReadAssignmentAsync(db, tx, tenant, driver, ct);
        if (network is null) { payload.Remove("network_assignment"); return; }
        payload["network_assignment"] = new JsonObject { ["hire_id"] = network.HireId, ["driver_id"] = network.DriverId,
            ["driver_user_id"] = network.DriverUserId, ["pay_per_delivery_cents"] = network.PayPerDeliveryCents };
    }

    private static async Task SaveDispatchOrder(DbConnection db, DbTransaction tx, string tenant, string order, JsonObject payload,
        string? driver, int version, string action, string actor, CancellationToken ct)
    {
        var now = Stamp(); payload["version"] = version + 1; payload["updated_at"] = now;
        var history = payload["history"] as JsonArray;
        if (history is null) { history = new(); payload["history"] = history; }
        history.Add(new JsonObject { ["status"] = String(payload, "status"), ["action"] = action, ["at"] = now,
            ["actor_id"] = actor, ["driver_id"] = driver, ["dispatch_plan"] = payload["dispatch_plan"]?.DeepClone() });
        await using var command = Command(db, tx, """
            UPDATE bartide_enhanced_orders SET payload_json=@json,status=@status,driver_id=@driver,version=version+1,updated_at=@now
            WHERE tenant_id=@tenant AND id=@order AND version=@version
            """, ("@json", payload.ToJsonString(Json)), ("@status", String(payload, "status")), ("@driver", (object?)driver ?? DBNull.Value), ("@now", now),
            ("@tenant", tenant), ("@order", order), ("@version", version));
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw DispatchStale();
    }
}
