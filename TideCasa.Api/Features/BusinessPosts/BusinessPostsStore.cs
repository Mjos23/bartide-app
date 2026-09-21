using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Data.Common;
using TideCasa.Api.Infrastructure;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.BusinessPosts;

public sealed class BusinessPostFailure(string message, int status = 400, string code = "invalid_post") : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

public sealed partial class BusinessPostsStore(ApplicationDatabase database, BusinessPushOptions push)
{
    private sealed record Venue(string Id, string Slug, string Name, bool Active);
    private const string Columns = "id,title,body,state,version,created_at,published_at";
    private static string Now() => DateTimeOffset.UtcNow.ToString("O");

    public async Task<BusinessPostsFeed> FeedAsync(string slug, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: true);
        var venue = await GetVenue(db, tx, slug, null, false, true, ct);
        var result = await Feed(db, tx, venue, false, ct); tx.Commit(); return result;
    }

    public async Task<BusinessPostWorkspace> WorkspaceAsync(string tenant, AuthUser user, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        var venue = await GetVenue(db, tx, tenant, user, true, false, ct);
        var result = new BusinessPostWorkspace(await Feed(db, tx, venue, true, ct), checked((int)await Count(db, tx, "SELECT COUNT(*) FROM tide_push_subscriptions WHERE tenant_id=@tenant AND active=1", ct, ("@tenant", tenant))), venue.Active);
        tx.Commit(); return result;
    }

    private async Task<BusinessPostsFeed> Feed(DbConnection db, DbTransaction tx, Venue venue, bool owner, CancellationToken ct) =>
        new(venue.Id, venue.Slug, venue.Name, await Rows(db, tx, "SELECT " + Columns + " FROM tide_business_posts WHERE tenant_id=@tenant" +
            (owner ? "" : " AND state='published'") + " ORDER BY " + (owner ? "CASE state WHEN 'draft' THEN 0 WHEN 'published' THEN 1 ELSE 2 END," : "") + "COALESCE(published_at,created_at) DESC,id LIMIT 100", MapPost, ct, ("@tenant", venue.Id)),
            venue.Active && push.Ready, venue.Active && push.Ready ? push.PublicKey : null);

    public async Task<BusinessPost> CreateAsync(string tenant, AuthUser user, CreateBusinessPostRequest request, CancellationToken ct)
    {
        if (request is null) throw Invalid();
        var key = RequestKey(request.RequestKey); var title = Text(request.Title, 120, false); var body = Text(request.Body, 2000, true);
        var hash = Hash(JsonSerializer.Serialize(new { action = "create", title, body }));
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        _ = await GetVenue(db, tx, tenant, user, true, true, ct);
        var prior = await Replay(db, tx, tenant, key, user, hash, ct);
        if (prior is not null) { var replay = await Post(db, tx, tenant, prior, ct); tx.Commit(); return replay; }
        if (await Count(db, tx, "SELECT COUNT(*) FROM tide_business_posts WHERE tenant_id=@tenant", ct, ("@tenant", tenant)) >= 1000 ||
            await Count(db, tx, "SELECT COUNT(*) FROM tide_business_posts WHERE tenant_id=@tenant AND state='draft'", ct, ("@tenant", tenant)) >= 100)
            throw Limit("This business has reached its saved-post limit.");
        var id = Guid.NewGuid().ToString("D");
        await Run(db, tx, "INSERT INTO tide_business_posts(id,tenant_id,title,body,state,version,created_at,updated_at) VALUES(@id,@tenant,@title,@body,'draft',0,@now,@now)", ct,
            ("@id", id), ("@tenant", tenant), ("@title", title), ("@body", body), ("@now", Now()));
        await Remember(db, tx, tenant, key, user, hash, id, ct); var result = await Post(db, tx, tenant, id, ct); tx.Commit(); return result;
    }

    public async Task<BusinessPost> TransitionAsync(string tenant, string postId, AuthUser user, TransitionBusinessPostRequest request, bool publish, CancellationToken ct)
    {
        if (request is null || request.ExpectedVersion < 0) throw Invalid();
        var key = RequestKey(request.RequestKey); var hash = Hash(JsonSerializer.Serialize(new { action = publish ? "publish" : "hide", postId, request.ExpectedVersion }));
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        _ = await GetVenue(db, tx, tenant, user, true, true, ct);
        var current = await Post(db, tx, tenant, postId, ct);
        if (await Replay(db, tx, tenant, key, user, hash, ct) is not null) { tx.Commit(); return current; }
        if (current.Version != request.ExpectedVersion || (publish ? current.State != "draft" : current.State is not ("draft" or "published"))) throw Stale();
        if (publish && (await Count(db, tx, db.Sql("SELECT COUNT(*) FROM tide_business_posts WHERE tenant_id=@tenant AND julianday(published_at)>julianday('now','-1 day')",
            "SELECT COUNT(*) FROM tide_business_posts WHERE tenant_id=@tenant AND tide_iso_instant(published_at)>statement_timestamp()-interval '1 day'"), ct, ("@tenant", tenant)) >= 8 ||
            await Count(db, tx, db.Sql("SELECT COUNT(*) FROM tide_business_posts WHERE tenant_id=@tenant AND julianday(published_at)>julianday('now','-1 hour')",
            "SELECT COUNT(*) FROM tide_business_posts WHERE tenant_id=@tenant AND tide_iso_instant(published_at)>statement_timestamp()-interval '1 hour'"), ct, ("@tenant", tenant)) >= 4))
            throw Limit("You can publish up to four updates per hour and eight per day. Please try again later.");
        await Run(db, tx, "UPDATE tide_business_posts SET state=@state,version=version+1,published_at=CASE WHEN @publish=1 THEN @now ELSE published_at END,updated_at=@now WHERE id=@id AND tenant_id=@tenant AND version=@version", ct,
            ("@state", publish ? "published" : "hidden"), ("@publish", publish ? 1 : 0), ("@now", Now()), ("@id", postId), ("@tenant", tenant), ("@version", request.ExpectedVersion));
        // Publishing while push is disabled must never create a backlog to send when it is later enabled.
        if (publish && push.Ready)
            await Run(db, tx, "INSERT INTO tide_push_outbox(post_id,subscription_id,subscription_generation,state,attempts,next_attempt_at,created_at,updated_at) SELECT @post,id,generation,'pending',0,@now,@now,@now FROM tide_push_subscriptions WHERE tenant_id=@tenant AND active=1", ct,
                ("@post", postId), ("@tenant", tenant), ("@now", Now()));
        if (!publish) await Run(db, tx, "UPDATE tide_push_outbox SET state='cancelled',lease_key=NULL,lease_until=NULL,updated_at=@now WHERE post_id=@post AND state IN('pending','sending')", ct, ("@now", Now()), ("@post", postId));
        await Remember(db, tx, tenant, key, user, hash, postId, ct); var result = await Post(db, tx, tenant, postId, ct); tx.Commit(); return result;
    }

    public async Task<MyPushSubscriptions> MineAsync(string slug, AuthUser user, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: true);
        var venue = await GetVenue(db, tx, slug, user, false, false, ct);
        var rows = await Rows(db, tx, "SELECT id,endpoint_hash,active,created_at FROM tide_push_subscriptions WHERE tenant_id=@tenant AND user_id=@user AND active=1 ORDER BY created_at LIMIT 10", MapSubscription, ct, ("@tenant", venue.Id), ("@user", user.UserId));
        tx.Commit(); return new(rows);
    }

    public async Task<PushSubscriptionInfo> SubscribeAsync(string slug, AuthUser user, PushSubscriptionRequest request, CancellationToken ct)
    {
        if (request is null || !request.Consent || !push.ValidEndpoint(request.Endpoint)) throw new BusinessPostFailure("Enable notifications in a supported browser and confirm your choice.");
        // Browser endpoint identity uses the same URI normalization as the actual HTTP destination.
        // Otherwise casing and an explicit default port can bypass ownership and delivery deduplication.
        var endpoint = Regex.Replace(new Uri(request.Endpoint, UriKind.Absolute).AbsoluteUri,
            "%[0-9A-Fa-f]{2}", match => match.Value.ToUpperInvariant());
        BusinessPushOptions.ValidateSubscriptionKeys(request.P256dh, request.Auth);
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        var venue = await GetVenue(db, tx, slug, user, false, true, ct);
        if (!push.Ready) throw new BusinessPostFailure("Phone notifications are not available yet. You can still read updates here.", 503, "push_unavailable");
        var hash = Hash(endpoint);
        var prior = await Rows(db, tx, "SELECT id,user_id,active,p256dh,auth FROM tide_push_subscriptions WHERE tenant_id=@tenant AND endpoint_hash=@hash", r => (Id: r.GetString(0), User: r.GetString(1), Active: r.ReadBoolean(2), Public: r.GetString(3), Auth: r.GetString(4)), ct, ("@tenant", venue.Id), ("@hash", hash));
        if (prior.Count != 0 && prior[0].User != user.UserId) throw new BusinessPostFailure("This browser subscription belongs to another account. Use the original account or a separate browser profile. Resetting this site's browser data also removes its saved sign-ins and notification follows.", 409, "subscription_conflict");
        if (prior.Count == 0 && (await Count(db, tx, "SELECT COUNT(*) FROM tide_push_subscriptions WHERE user_id=@user", ct, ("@user", user.UserId)) >= 100 ||
            await Count(db, tx, "SELECT COUNT(*) FROM tide_push_subscriptions WHERE tenant_id=@tenant", ct, ("@tenant", venue.Id)) >= 4000 ||
            await Count(db, tx, db.Sql("SELECT COUNT(*) FROM tide_push_subscriptions WHERE user_id=@user AND julianday(created_at)>julianday('now','-1 day')",
            "SELECT COUNT(*) FROM tide_push_subscriptions WHERE user_id=@user AND tide_iso_instant(created_at)>statement_timestamp()-interval '1 day'"), ct, ("@user", user.UserId)) >= 20))
            throw Limit("The saved-device or daily notification setup limit has been reached. Re-enable an existing device or try again later.");
        if (prior.Count == 0 || !prior[0].Active)
        {
            if (await Count(db, tx, "SELECT COUNT(*) FROM tide_push_subscriptions WHERE tenant_id=@tenant AND active=1", ct, ("@tenant", venue.Id)) >= 2000 ||
                await Count(db, tx, "SELECT COUNT(*) FROM tide_push_subscriptions WHERE tenant_id=@tenant AND user_id=@user AND active=1", ct, ("@tenant", venue.Id), ("@user", user.UserId)) >= 5 ||
                await Count(db, tx, "SELECT COUNT(*) FROM tide_push_subscriptions WHERE user_id=@user AND active=1", ct, ("@user", user.UserId)) >= 50)
                throw Limit("The notification subscription limit has been reached. Turn off an unused device first.");
        }
        var id = prior.Count == 0 ? Guid.NewGuid().ToString("D") : prior[0].Id;
        if (prior.Count == 0)
            await Run(db, tx, "INSERT INTO tide_push_subscriptions(id,tenant_id,user_id,endpoint,endpoint_hash,p256dh,auth,active,generation,created_at,updated_at) VALUES(@id,@tenant,@user,@endpoint,@hash,@public,@auth,1,0,@now,@now)", ct,
                ("@id", id), ("@tenant", venue.Id), ("@user", user.UserId), ("@endpoint", endpoint), ("@hash", hash), ("@public", request.P256dh), ("@auth", request.Auth), ("@now", Now()));
        else if (!prior[0].Active || prior[0].Public != request.P256dh || prior[0].Auth != request.Auth)
        {
            await Run(db, tx, "UPDATE tide_push_subscriptions SET active=1,generation=generation+1,p256dh=@public,auth=@auth,updated_at=@now WHERE id=@id", ct, ("@id", id), ("@public", request.P256dh), ("@auth", request.Auth), ("@now", Now()));
            await CancelSubscription(db, tx, id, ct);
        }
        var result = (await Rows(db, tx, "SELECT id,endpoint_hash,active,created_at FROM tide_push_subscriptions WHERE id=@id", MapSubscription, ct, ("@id", id))).Single(); tx.Commit(); return result;
    }

    public async Task UnsubscribeAsync(string slug, string id, AuthUser user, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        var venue = await GetVenue(db, tx, slug, user, false, false, ct);
        // Scope by immutable user even for platform owners. This endpoint never manages another person's subscriptions.
        if (await Count(db, tx, "SELECT COUNT(*) FROM tide_push_subscriptions WHERE id=@id AND tenant_id=@tenant AND user_id=@user", ct, ("@id", id), ("@tenant", venue.Id), ("@user", user.UserId)) == 0) throw Missing();
        await Run(db, tx, "UPDATE tide_push_subscriptions SET active=0,generation=generation+1,updated_at=@now WHERE id=@id AND active=1", ct, ("@id", id), ("@now", Now()));
        await CancelSubscription(db, tx, id, ct); tx.Commit();
    }

    private static Task<int> CancelSubscription(DbConnection db, DbTransaction tx, string id, CancellationToken ct) =>
        Run(db, tx, "UPDATE tide_push_outbox SET state='cancelled',lease_key=NULL,lease_until=NULL,updated_at=@now WHERE subscription_id=@id AND state IN('pending','sending')", ct, ("@id", id), ("@now", Now()));

    private static async Task<Venue> GetVenue(DbConnection db, DbTransaction tx, string key, AuthUser? user, bool owner, bool requireActive, CancellationToken ct)
    {
        var rows = await Rows(db, tx, "SELECT c.id,c.slug,c.name,COALESCE(c.user_id,''),c.email,c.status,c.vertical,COALESCE(x.settings_json,'{}') FROM bartide_customers c LEFT JOIN bartide_enhanced_configs x ON x.tenant_id=c.id WHERE " + (owner ? "c.id" : "c.slug") + "=@key", r => Enumerable.Range(0, 8).Select(r.GetString).ToArray(), ct, ("@key", key));
        if (rows.Count != 1) throw Missing(); var row = rows[0];
        using var config = JsonDocument.Parse(row[7]); var supported = row[6] is "bartide" or "tide-casa";
        var active = row[5] == "active" && supported && config.RootElement.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True;
        if (!supported || requireActive && !active) throw Missing();
        if (owner)
        {
            if (user is null) throw new BusinessPostFailure("Sign in to continue.", 401, "sign_in");
            if (row[3].Length == 0 && !user.IsPlatformOwner && row[4].Equals(user.Email.Trim(), StringComparison.OrdinalIgnoreCase))
            { await Run(db, tx, "UPDATE bartide_customers SET user_id=@user WHERE id=@id AND user_id IS NULL", ct, ("@user", user.UserId), ("@id", row[0])); row[3] = user.UserId; }
            if (!user.IsPlatformOwner && row[3] != user.UserId) throw new BusinessPostFailure("Business owner access is required.", 403, "owner_required");
        }
        return new(row[0], row[1], row[2], active);
    }
    private static BusinessPost MapPost(DbDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.ReadInt32(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6));
    private static PushSubscriptionInfo MapSubscription(DbDataReader r) => new(r.GetString(0), r.GetString(1), r.ReadBoolean(2), r.GetString(3));
    private static async Task<BusinessPost> Post(DbConnection db, DbTransaction tx, string tenant, string id, CancellationToken ct) =>
        (await Rows(db, tx, "SELECT " + Columns + " FROM tide_business_posts WHERE tenant_id=@tenant AND id=@id", MapPost, ct, ("@tenant", tenant), ("@id", id))).SingleOrDefault() ?? throw Missing();
    private static async Task<string?> Replay(DbConnection db, DbTransaction tx, string tenant, string key, AuthUser user, string hash, CancellationToken ct)
    {
        var rows = await Rows(db, tx, "SELECT actor_id,request_hash,post_id FROM tide_business_post_requests WHERE tenant_id=@tenant AND request_key=@key", r => (r.GetString(0), r.GetString(1), r.GetString(2)), ct, ("@tenant", tenant), ("@key", key));
        if (rows.Count == 0) return null;
        if (rows[0].Item1 != user.UserId || rows[0].Item2 != hash) throw new BusinessPostFailure("That request was already used with different details. Refresh and try again.", 409, "request_conflict");
        return rows[0].Item3;
    }
    private static Task<int> Remember(DbConnection db, DbTransaction tx, string tenant, string key, AuthUser user, string hash, string post, CancellationToken ct) =>
        Run(db, tx, "INSERT INTO tide_business_post_requests(tenant_id,request_key,actor_id,request_hash,post_id,created_at) VALUES(@tenant,@key,@user,@hash,@post,@now)", ct, ("@tenant", tenant), ("@key", key), ("@user", user.UserId), ("@hash", hash), ("@post", post), ("@now", Now()));
    internal static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string RequestKey(string? value) => Guid.TryParseExact(value, "D", out _) ? value! : throw Invalid();
    private static string Text(string? value, int max, bool multiline)
    { value = value?.Trim() ?? ""; if (value.Length is 0 || value.Length > max || value.Any(c => char.IsControl(c) && !(multiline && c is '\r' or '\n' or '\t'))) throw Invalid(); return value; }
    private static BusinessPostFailure Invalid() => new("Enter a title up to 120 characters and an update up to 2,000 characters.");
    private static BusinessPostFailure Missing() => new("These business updates are unavailable.", 404, "posts_missing");
    private static BusinessPostFailure Stale() => new("This update changed. Refresh before trying again.", 409, "post_changed");
    private static BusinessPostFailure Limit(string message) => new(message, 429, "posts_limit");
    private static DbCommand Command(DbConnection db, DbTransaction tx, string sql, params (string Key, object? Value)[] args)
    { var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = sql; foreach (var (key, value) in args) command.Parameters.AddWithValue(key, value ?? DBNull.Value); return command; }
    private static async Task<int> Run(DbConnection db, DbTransaction tx, string sql, CancellationToken ct, params (string Key, object? Value)[] args)
    { await using var command = Command(db, tx, sql, args); return await command.ExecuteNonQueryAsync(ct); }
    private static async Task<long> Count(DbConnection db, DbTransaction tx, string sql, CancellationToken ct, params (string Key, object? Value)[] args)
    { await using var command = Command(db, tx, sql, args); return Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture); }
    private static async Task<List<T>> Rows<T>(DbConnection db, DbTransaction tx, string sql, Func<DbDataReader, T> map, CancellationToken ct, params (string Key, object? Value)[] args)
    { await using var command = Command(db, tx, sql, args); await using var reader = await command.ExecuteReaderAsync(ct); var rows = new List<T>(); while (await reader.ReadAsync(ct)) rows.Add(map(reader)); return rows; }
}
