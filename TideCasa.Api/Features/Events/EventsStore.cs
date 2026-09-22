using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Data.Common;
using TideCasa.Api.Infrastructure;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.Events;

public sealed class EventFailure(string message, int status = 400, string code = "invalid_event") : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

public sealed class EventsStore(ApplicationDatabase database)
{
    private sealed record Venue(string Id, string Slug, string Name, string Owner);
    private static string Now() => DateTimeOffset.UtcNow.ToString("O");
    private const string EventColumns = "e.id,e.title,e.details,e.location,e.starts_at,e.ends_at,e.time_zone,e.capacity,(SELECT COUNT(*) FROM tide_event_rsvps r WHERE r.event_id=e.id AND r.state IN('confirmed','checked_in')),e.state,e.version";

    public async Task<RestaurantEvents> ListAsync(string key, bool owner, AuthUser? user, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        using var tx = db.BeginTransaction(deferred: !owner);
        var venue = await GetVenue(db, tx, key, owner, user, ct);
        var events = await Rows(db, tx, "SELECT " + EventColumns + " FROM tide_events e WHERE e.tenant_id=@tenant" +
            (owner ? "" : db.Sql(" AND e.state='published' AND julianday(e.ends_at)>julianday('now')", " AND e.state='published' AND tide_iso_instant(e.ends_at)>statement_timestamp()"))
            + db.Sql(" ORDER BY julianday(e.starts_at) DESC,e.id LIMIT 200", " ORDER BY tide_iso_instant(e.starts_at) DESC NULLS LAST,e.id LIMIT 200"), MapEvent, ct, ("@tenant", venue.Id));
        tx.Commit(); return new(venue.Id, venue.Slug, venue.Name, events);
    }

    public async Task<RestaurantEvent> SaveAsync(string tenant, string? eventId, AuthUser user, SaveEventRequest request, CancellationToken ct)
    {
        if (request is null) throw Invalid();
        var key = RequestKey(request.RequestKey); var id = eventId ?? key;
        var title = Text(request.Title, 160); var details = Text(request.Details, 4000, true, true); var location = Text(request.Location, 300);
        var start = Timestamp(request.StartsAt); var end = Timestamp(request.EndsAt); var zone = Zone(request.TimeZone);
        if (end <= start || end - start > TimeSpan.FromDays(7) || request.Capacity is < 1 or > 500 || request.ExpectedVersion < 0) throw Invalid();
        var hash = Hash(new { action = eventId is null ? "create" : "save", request.ExpectedVersion, title, details, location, start, end, zone, request.Capacity, request.Published });
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        var venue = await GetVenue(db, tx, tenant, true, user, ct);
        if (await Replayed(db, tx, id, key, user, hash, ct)) { var replay = await GetEvent(db, tx, venue.Id, id, ct); tx.Commit(); return replay; }
        if (start <= DateTimeOffset.UtcNow) throw new EventFailure("Choose a future start time.");
        if (eventId is null)
        {
            if (request.ExpectedVersion != 0) throw Stale();
            if (await Count(db, tx, db.Sql("SELECT COUNT(*) FROM tide_events WHERE tenant_id=@tenant AND state<>'cancelled' AND julianday(ends_at)>julianday('now')",
            "SELECT COUNT(*) FROM tide_events WHERE tenant_id=@tenant AND state<>'cancelled' AND tide_iso_instant(ends_at)>statement_timestamp()"), ct, ("@tenant", tenant)) >= 100)
                throw new EventFailure("This business has reached its 100 upcoming-event limit.", 409, "events_full");
            if (await Count(db, tx, "SELECT COUNT(*) FROM tide_events WHERE id=@id", ct, ("@id", id)) != 0) throw Conflict();
            await Run(db, tx, "INSERT INTO tide_events(id,tenant_id,title,details,location,starts_at,ends_at,time_zone,capacity,state,version,created_at,updated_at) VALUES(@id,@tenant,@title,@details,@location,@start,@end,@zone,@capacity,@state,0,@now,@now)", ct,
                ("@id", id), ("@tenant", tenant), ("@title", title), ("@details", details), ("@location", location), ("@start", start.ToString("O")), ("@end", end.ToString("O")), ("@zone", zone), ("@capacity", request.Capacity), ("@state", request.Published ? "published" : "draft"), ("@now", Now()));
        }
        else
        {
            var current = await GetEvent(db, tx, venue.Id, id, ct);
            if (current.Version != request.ExpectedVersion || current.State == "cancelled") throw Stale();
            if (request.Capacity < current.Reserved) throw new EventFailure("Capacity cannot be lower than existing reservations.", 409, "capacity_below_reserved");
            // Existing reservations must not disappear behind an unpublished event.
            if (!request.Published && current.Reserved > 0) throw new EventFailure("This event has reservations. Keep it published or cancel it.", 409, "event_has_guests");
            await Run(db, tx, "UPDATE tide_events SET title=@title,details=@details,location=@location,starts_at=@start,ends_at=@end,time_zone=@zone,capacity=@capacity,state=@state,version=version+1,updated_at=@now WHERE id=@id AND tenant_id=@tenant AND version=@version", ct,
                ("@id", id), ("@tenant", tenant), ("@title", title), ("@details", details), ("@location", location), ("@start", start.ToString("O")), ("@end", end.ToString("O")), ("@zone", zone), ("@capacity", request.Capacity), ("@state", request.Published ? "published" : "draft"), ("@version", request.ExpectedVersion), ("@now", Now()));
        }
        await Audit(db, tx, id, key, user, eventId is null ? "created" : "saved", hash, ct);
        var result = await GetEvent(db, tx, venue.Id, id, ct); tx.Commit(); return result;
    }

    public async Task<RestaurantEvent> CancelAsync(string tenant, string eventId, AuthUser user, CancelEventRequest request, CancellationToken ct)
    {
        if (request is null) throw Invalid();
        var key = RequestKey(request.RequestKey); var hash = Hash(new { action = "cancel", request.ExpectedVersion });
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        var venue = await GetVenue(db, tx, tenant, true, user, ct); var current = await GetEvent(db, tx, venue.Id, eventId, ct);
        if (!await Replayed(db, tx, eventId, key, user, hash, ct))
        {
            if (current.Version != request.ExpectedVersion || current.State == "cancelled") throw Stale();
            await Run(db, tx, "UPDATE tide_events SET state='cancelled',version=version+1,updated_at=@now WHERE id=@id AND tenant_id=@tenant", ct, ("@now", Now()), ("@id", eventId), ("@tenant", tenant));
            await Audit(db, tx, eventId, key, user, "cancelled", hash, ct);
        }
        var result = await GetEvent(db, tx, venue.Id, eventId, ct); tx.Commit(); return result;
    }

    public async Task<EventGuestList> GuestsAsync(string tenant, string eventId, AuthUser user, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        var venue = await GetVenue(db, tx, tenant, true, user, ct); var selected = await GetEvent(db, tx, venue.Id, eventId, ct);
        var guests = await Rows(db, tx, db.Sql("SELECT user_id,display_name,email,state,version,updated_at FROM tide_event_rsvps WHERE event_id=@id ORDER BY state,display_name COLLATE NOCASE,user_id",
            "SELECT user_id,display_name,email,state,version,updated_at FROM tide_event_rsvps WHERE event_id=@id ORDER BY state,translate(display_name,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz') COLLATE \"C\",user_id"), r => new EventGuest(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.ReadInt32(4), r.GetString(5)), ct, ("@id", eventId));
        tx.Commit(); return new(selected, guests);
    }

    public async Task<MyEventReservations> MineAsync(string slug, AuthUser user, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: true);
        var venue = await GetVenue(db, tx, slug, false, user, ct);
        var items = await Rows(db, tx, "SELECT " + EventColumns + db.Sql(",mine.state,mine.version FROM tide_events e JOIN tide_event_rsvps mine ON mine.event_id=e.id AND mine.user_id=@user WHERE e.tenant_id=@tenant AND julianday(e.ends_at)>julianday('now','-7 days') ORDER BY julianday(e.starts_at),e.id LIMIT 200",
            ",mine.state,mine.version FROM tide_events e JOIN tide_event_rsvps mine ON mine.event_id=e.id AND mine.user_id=@user WHERE e.tenant_id=@tenant AND tide_iso_instant(e.ends_at)>statement_timestamp()-interval '7 days' ORDER BY tide_iso_instant(e.starts_at) NULLS FIRST,e.id LIMIT 200"),
            r => new MyEventReservation(MapEvent(r), new(r.GetString(11), r.ReadInt32(12), r.GetString(9))), ct, ("@tenant", venue.Id), ("@user", user.UserId));
        tx.Commit(); return new(items);
    }

    public async Task<MyEventRsvp> RsvpAsync(string slug, string eventId, AuthUser user, SetEventRsvpRequest request, CancellationToken ct)
    {
        if (request is null || request.ExpectedVersion < -1) throw Invalid();
        var key = RequestKey(request.RequestKey); var hash = Hash(new { request.ExpectedVersion, request.Attending });
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        var venue = await GetVenue(db, tx, slug, false, user, ct); var selected = await GetEvent(db, tx, venue.Id, eventId, ct);
        var rows = await Rows(db, tx, "SELECT state,version FROM tide_event_rsvps WHERE event_id=@event AND user_id=@user", r => new MyEventRsvp(r.GetString(0), r.ReadInt32(1), selected.State), ct, ("@event", eventId), ("@user", user.UserId));
        var current = rows.SingleOrDefault();
        var prior = await Rows(db, tx, "SELECT request_hash FROM tide_event_rsvp_requests WHERE event_id=@event AND user_id=@user AND request_key=@key", r => r.GetString(0), ct, ("@event", eventId), ("@user", user.UserId), ("@key", key));
        if (prior.Count != 0) { if (prior[0] != hash || current is null) throw Conflict(); tx.Commit(); return current; }
        if (request.ExpectedVersion != (current?.Version ?? -1)) throw Stale();
        if (request.Attending)
        {
            if (selected.State != "published" || Timestamp(selected.StartsAt) <= DateTimeOffset.UtcNow) throw new EventFailure("Reservations are closed for this event.", 409, "event_closed");
            if (current?.State is not ("confirmed" or "checked_in") && selected.Reserved >= selected.Capacity) throw new EventFailure("This event is full.", 409, "event_full");
        }
        else if (current is null || current.State == "checked_in") throw new EventFailure("This reservation cannot be cancelled.", 409, "rsvp_closed");
        var state = request.Attending ? current?.State == "checked_in" ? "checked_in" : "confirmed" : "cancelled";
        // Immutable user identity is authoritative; profiles are only private display snapshots.
        if (current is null)
            await Run(db, tx, "INSERT INTO tide_event_rsvps(event_id,user_id,display_name,email,state,version,created_at,updated_at) VALUES(@event,@user,@name,@email,@state,0,@now,@now)", ct,
                ("@event", eventId), ("@user", user.UserId), ("@name", Text(user.DisplayName, 200)), ("@email", Text(user.Email, 254)), ("@state", state), ("@now", Now()));
        else if (current.State != state)
            await Run(db, tx, "UPDATE tide_event_rsvps SET state=@state,version=version+1,updated_at=@now WHERE event_id=@event AND user_id=@user", ct,
                ("@event", eventId), ("@user", user.UserId), ("@state", state), ("@now", Now()));
        await Run(db, tx, "INSERT INTO tide_event_rsvp_requests(event_id,user_id,request_key,request_hash,created_at) VALUES(@event,@user,@key,@hash,@now)", ct,
            ("@event", eventId), ("@user", user.UserId), ("@key", key), ("@hash", hash), ("@now", Now()));
        await Audit(db, tx, eventId, Guid.NewGuid().ToString("D"), user, state == "cancelled" ? "rsvp_cancelled" : "rsvp_confirmed", hash, ct);
        var version = current is null ? 0 : current.Version + (current.State == state ? 0 : 1);
        tx.Commit(); return new(state, version, selected.State);
    }

    public async Task<EventGuestList> CheckInAsync(string tenant, string eventId, AuthUser user, CheckInEventRequest request, CancellationToken ct)
    {
        if (request is null) throw Invalid();
        var key = RequestKey(request.RequestKey); var hash = Hash(new { action = "check_in", request.UserId, request.ExpectedVersion });
        await using (var db = await database.OpenAsync(ct))
        {
            using var tx = db.BeginTransaction(deferred: false);
            var venue = await GetVenue(db, tx, tenant, true, user, ct); var selected = await GetEvent(db, tx, venue.Id, eventId, ct);
            if (!await Replayed(db, tx, eventId, key, user, hash, ct))
            {
                if (selected.State != "published" || DateTimeOffset.UtcNow < Timestamp(selected.StartsAt).AddHours(-4) || DateTimeOffset.UtcNow > Timestamp(selected.EndsAt))
                    throw new EventFailure("Check-in opens four hours before the event and closes when it ends.", 409, "check_in_closed");
                if (await Run(db, tx, "UPDATE tide_event_rsvps SET state='checked_in',version=version+1,updated_at=@now WHERE event_id=@event AND user_id=@user AND state='confirmed' AND version=@version", ct,
                    ("@event", eventId), ("@user", request.UserId), ("@version", request.ExpectedVersion), ("@now", Now())) != 1) throw Stale();
                await Audit(db, tx, eventId, key, user, "checked_in", hash, ct);
            }
            tx.Commit();
        }
        return await GuestsAsync(tenant, eventId, user, ct);
    }

    private static async Task<Venue> GetVenue(DbConnection db, DbTransaction tx, string key, bool owner, AuthUser? user, CancellationToken ct)
    {
        var venues = await Rows(db, tx, "SELECT c.id,c.slug,c.name,COALESCE(c.user_id,''),c.status,c.vertical,COALESCE(x.settings_json,'{}'),c.email FROM bartide_customers c LEFT JOIN bartide_enhanced_configs x ON x.tenant_id=c.id WHERE " + (owner ? "c.id" : "c.slug") + "=@key", r => Enumerable.Range(0, 8).Select(r.GetString).ToArray(), ct, ("@key", key));
        if (venues.Count != 1) throw Missing();
        var row = venues[0]; using var settings = JsonDocument.Parse(row[6]);
        if (row[4] != "active" || row[5] != "bartide" || !settings.RootElement.TryGetProperty("enabled", out var enabled) || enabled.ValueKind != JsonValueKind.True) throw Missing();
        if (owner)
        {
            if (user is null) throw new EventFailure("Sign in to continue.", 401, "sign_in");
            if (row[3].Length == 0 && !user.IsPlatformOwner && row[7].Equals(user.Email.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                await Run(db, tx, "UPDATE bartide_customers SET user_id=@user WHERE id=@id AND user_id IS NULL", ct, ("@user", user.UserId), ("@id", row[0])); row[3] = user.UserId;
            }
            if (!user.IsPlatformOwner && row[3] != user.UserId && !await TenantStaffAccess.IsManagerAsync(db, tx, row[0], user, ct)) throw new EventFailure("Business owner or manager access is required.", 403, "owner_required");
        }
        return new(row[0], row[1], row[2], row[3]);
    }
    private static async Task<RestaurantEvent> GetEvent(DbConnection db, DbTransaction tx, string tenant, string id, CancellationToken ct) =>
        (await Rows(db, tx, "SELECT " + EventColumns + " FROM tide_events e WHERE e.id=@id AND e.tenant_id=@tenant", MapEvent, ct, ("@id", id), ("@tenant", tenant))).SingleOrDefault() ?? throw Missing();
    private static RestaurantEvent MapEvent(DbDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.ReadInt32(7), r.ReadInt32(8), r.GetString(9), r.ReadInt32(10));
    private static async Task<bool> Replayed(DbConnection db, DbTransaction tx, string id, string key, AuthUser user, string hash, CancellationToken ct)
    {
        var rows = await Rows(db, tx, "SELECT actor_user_id,details_json FROM tide_event_audit WHERE id=@id", r => (r.GetString(0), r.GetString(1)), ct, ("@id", id + ":" + key));
        if (rows.Count == 0) return false;
        if (rows[0].Item1 != user.UserId || rows[0].Item2 != JsonSerializer.Serialize(new { request_hash = hash })) throw Conflict();
        return true;
    }
    private static Task<int> Audit(DbConnection db, DbTransaction tx, string id, string key, AuthUser user, string action, string hash, CancellationToken ct) =>
        Run(db, tx, "INSERT INTO tide_event_audit(id,event_id,actor_user_id,action,details_json,created_at) VALUES(@id,@event,@user,@action,@details,@now)", ct,
            ("@id", id + ":" + key), ("@event", id), ("@user", user.UserId), ("@action", action), ("@details", JsonSerializer.Serialize(new { request_hash = hash })), ("@now", Now()));
    private static string Hash(object value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    private static string RequestKey(string? value) => Guid.TryParseExact(value, "D", out _) ? value! : throw Invalid();
    private static string Text(string? value, int max, bool optional = false, bool multiline = false)
    {
        value = value?.Trim() ?? "";
        if (value.Length > max || !optional && value.Length == 0 || value.Any(c => char.IsControl(c) && !(multiline && c is '\r' or '\n' or '\t'))) throw Invalid();
        return value;
    }
    private static string Zone(string? value)
    {
        value = Text(value, 80);
        if (value != "UTC" && !Regex.IsMatch(value, "^[A-Za-z_+-]+/[A-Za-z_+/-]+$")) throw Invalid();
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(value); return value; }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException) { throw new EventFailure("Choose a supported time zone."); }
    }
    private static DateTimeOffset Timestamp(string? value)
    {
        if (value is null || !Regex.IsMatch(value, "(?:Z|[+-][0-9]{2}:[0-9]{2})$") || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var result)) throw Invalid();
        return result.ToUniversalTime();
    }
    private static EventFailure Invalid() => new("Check the event details, dates and capacity, then try again.");
    private static EventFailure Missing() => new("This event is unavailable.", 404, "event_missing");
    private static EventFailure Stale() => new("These details changed. Refresh before trying again.", 409, "event_changed");
    private static EventFailure Conflict() => new("That request was already used with different details. Refresh and try again.", 409, "request_conflict");
    private static DbCommand Command(DbConnection db, DbTransaction tx, string sql, params (string Key, object? Value)[] args)
    { var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql; foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value); return cmd; }
    private static async Task<int> Run(DbConnection db, DbTransaction tx, string sql, CancellationToken ct, params (string Key, object? Value)[] args)
    { await using var cmd = Command(db, tx, sql, args); return await cmd.ExecuteNonQueryAsync(ct); }
    private static async Task<long> Count(DbConnection db, DbTransaction tx, string sql, CancellationToken ct, params (string Key, object? Value)[] args)
    { await using var cmd = Command(db, tx, sql, args); return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture); }
    private static async Task<List<T>> Rows<T>(DbConnection db, DbTransaction tx, string sql, Func<DbDataReader, T> map, CancellationToken ct, params (string Key, object? Value)[] args)
    { await using var cmd = Command(db, tx, sql, args); await using var reader = await cmd.ExecuteReaderAsync(ct); var rows = new List<T>(); while (await reader.ReadAsync(ct)) rows.Add(map(reader)); return rows; }
}
