using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TideCasa.Api.Infrastructure;
using TideCasa.Api.Features.RestaurantOrdering;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.ClientOnboarding;

public sealed class OnboardingException(string message, int status = 400, string code = "onboarding_invalid") : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>Versioned preparation inside the existing durable tenant configuration. No public activation on save.</summary>
public sealed partial class ClientOnboardingStore(ApplicationDatabase database)
{
    internal const string Template = "bartide-venue/1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record Answers(OnboardingBusiness Business, OnboardingBrand Brand, OnboardingService Service, OnboardingTeam Team);
    private sealed record State(string Id, string Slug, string Name, string Owner, string Status, int MenuVersion,
        int ConfigVersion, string? BuildReadyAt, JsonObject Menu, JsonObject Config, JsonObject Workflow, Answers Answers,
        bool IsOwner, RestaurantMenuEditor Editor)
    { public int Revision => checked(MenuVersion + ConfigVersion); }

    public async Task<OnboardingWorkspace> GetAsync(string id, AuthUser user, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        var state = await ReadAsync(db, tx, id, user, true, ct);
        var view = await ViewAsync(db, tx, state, ct); await tx.CommitAsync(ct); return view;
    }

    public Task<OnboardingWorkspace> SaveBusinessAsync(string id, AuthUser user, OnboardingSaveBusiness request, CancellationToken ct) =>
        SaveAsync(id, user, request.ExpectedRevision, a => a with { Business = Validate(request.Business) }, "business", ct);
    public Task<OnboardingWorkspace> SaveBrandAsync(string id, AuthUser user, OnboardingSaveBrand request, CancellationToken ct) =>
        SaveAsync(id, user, request.ExpectedRevision, a => a with { Brand = Validate(request.Brand) }, "brand", ct);
    public Task<OnboardingWorkspace> SaveServiceAsync(string id, AuthUser user, OnboardingSaveService request, CancellationToken ct) =>
        SaveAsync(id, user, request.ExpectedRevision, a => a with { Service = Validate(request.Service) }, "service", ct);
    public Task<OnboardingWorkspace> SaveTeamAsync(string id, AuthUser user, OnboardingSaveTeam request, CancellationToken ct) =>
        SaveAsync(id, user, request.ExpectedRevision, a => a with { Team = Validate(request.Team) }, "team", ct);

    private async Task<OnboardingWorkspace> SaveAsync(string id, AuthUser user, int expected, Func<Answers, Answers> change, string section, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        var s = await ReadAsync(db, tx, id, user, false, ct); Editable(s);
        if (s.Revision != expected) throw Stale();
        var answers = change(s.Answers);
        if (section == "brand")
            foreach (var photo in new[] { answers.Brand.LogoPhotoId, answers.Brand.CoverPhotoId }.Where(p => p is not null))
                if (await Count(db, tx, "SELECT COUNT(*) FROM bartide_photos WHERE id=@photo AND tenant_id=@id AND status='ready'", ct, ("@photo", photo), ("@id", id)) != 1)
                    throw new OnboardingException("Choose a ready photo from this business.");
        s.Workflow["answers"] = JsonSerializer.SerializeToNode(answers, Json);
        s.Workflow["updatedAt"] = Now(); s.Workflow["lastEditor"] = user.UserId;
        if (section is "business" or "brand")
        {
            var venue = s.Menu["venue"] as JsonObject ?? throw Unavailable();
            if (section == "business")
            {
                venue["name"] = answers.Business.Name;
                venue["area"] = string.Join(", ", new[] { answers.Business.City, answers.Business.Region }.Where(v => v.Length > 0));
                venue["website_url"] = answers.Business.Website;
                venue["hours_text"] = HoursText(answers.Business.Hours);
                s.Config["contact_phone"] = answers.Business.Phone;
            }
            else
            {
                venue["tagline"] = answers.Brand.Introduction;
                venue["logo_src"] = answers.Brand.LogoPhotoId is { } logo ? "/media/" + logo : null;
                venue["cover_src"] = answers.Brand.CoverPhotoId is { } cover ? "/media/" + cover : null;
            }
            await Run(db, tx, "UPDATE bartide_customers SET name=@name,menu_json=@menu,version=version+1,updated_at=@now WHERE id=@id AND version=@version", ct,
                ("@name", section == "business" ? answers.Business.Name : s.Name), ("@menu", s.Menu.ToJsonString(Json)), ("@now", Now()), ("@id", id), ("@version", s.MenuVersion));
        }
        if (section == "service") ApplyService(s.Config, answers.Service, answers.Business.Phone);
        await WriteConfig(db, tx, s, ct);
        var result = await ViewAsync(db, tx, await ReadAsync(db, tx, id, user, false, ct), ct);
        await tx.CommitAsync(ct); return result;
    }

    public async Task<OnboardingRelease> BuildAsync(string id, AuthUser user, OnboardingBuildRequest request, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        var s = await ReadAsync(db, tx, id, user, false, ct); Editable(s);
        if (s.Revision != request.ExpectedRevision) throw Stale();
        var hash = await Fingerprint(db, tx, s, ct);
        var existing = Release(s);
        if (existing?.Fingerprint == hash)
        { var same = await ReleaseView(db, tx, s, existing, ct); await tx.CommitAsync(ct); return same; }
        var checks = await Checks(db, tx, s, ct);
        var release = new OnboardingRelease(Guid.NewGuid().ToString("D"), id, s.Slug, hash, Template, Now(), true, false,
            CoreReady(checks), s.Answers.Business, s.Answers.Brand, s.Answers.Service, s.Editor.Categories, s.Editor.Items, checks);
        // Keep a bounded prior immutable snapshot. Assembly is deterministic and committed atomically; an interrupted
        // request can retry without a partially assembled app, duplicate job or external provider operation.
        s.Workflow["previousRelease"] = s.Workflow["release"]?.DeepClone();
        s.Workflow["release"] = JsonSerializer.SerializeToNode(release, Json);
        s.Workflow["updatedAt"] = Now();
        await WriteConfig(db, tx, s, ct); await tx.CommitAsync(ct); return release;
    }

    public async Task<OnboardingRelease> PreviewAsync(string id, AuthUser user, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        var s = await ReadAsync(db, tx, id, user, false, ct);
        var result = await ReleaseView(db, tx, s, Release(s) ?? throw new OnboardingException("Build your private preview first.", 404, "preview_missing"), ct);
        await tx.CommitAsync(ct); return result;
    }

    public async Task<OnboardingWorkspace> ApproveAsync(string id, AuthUser user, OnboardingApproveRequest request, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        var s = await ReadAsync(db, tx, id, user, false, ct); Editable(s);
        if (!s.IsOwner || user.IsPlatformOwner && s.Owner != user.UserId) throw new OnboardingException("The business owner must approve this preview.", 403, "owner_required");
        var release = Release(s);
        if (!request.Confirmed) throw new OnboardingException("Review and confirm the app details first.");
        if (release is null || release.Id != request.ReleaseId || release.Fingerprint != request.Fingerprint
            || release.Fingerprint != await Fingerprint(db, tx, s, ct)) throw Stale();
        if (!CoreReady(await Checks(db, tx, s, ct))) throw new OnboardingException("Complete the required setup details before requesting launch review.", 409, "setup_incomplete");
        if (Text(s.Workflow, "approvedRelease") != release.Id)
        {
            s.Workflow["approvedRelease"] = release.Id; s.Workflow["approvedBy"] = user.UserId;
            s.Workflow["approvedAt"] = Now(); s.Workflow["updatedAt"] = Now();
            await WriteConfig(db, tx, s, ct);
        }
        var view = await ViewAsync(db, tx, await ReadAsync(db, tx, id, user, false, ct), ct);
        await tx.CommitAsync(ct); return view;
    }

    public async Task<OnboardingPracticeResult> PracticeAsync(string id, AuthUser user, OnboardingPracticeRequest request, CancellationToken ct)
    {
        var release = await PreviewAsync(id, user, ct);
        if (!release.Current || release.Id != request.ReleaseId || release.Fingerprint != request.Fingerprint) throw Stale();
        var quote = RestaurantOrderingStore.OnboardingQuote(release, request.Order);
        return new(quote with { CanSubmit = false, UnavailableReason = "Private practice only. No order or payment was created." },
            "Practice checked the saved menu, fulfillment rules, tax and tip. No order was sent and no payment was taken.");
    }

    public async Task<OnboardingPublicSite> PublicAsync(string slug, CancellationToken ct)
    {
        if (slug.Length > 200 || !Regex.IsMatch(slug, "^[a-z0-9-]+$")) throw Missing();
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: true);
        var id = await Scalar(db, tx, "SELECT id FROM bartide_customers WHERE slug=@slug AND status='active' AND vertical='bartide'", ct, ("@slug", slug)) as string ?? throw Missing();
        var s = await ReadState(db, tx, id, false, ct);
        if (Text(s.Workflow, "publishedRelease").Length == 0) throw Missing();
        // Daily menu edits remain authoritative after launch; onboarding-only private metadata is never returned.
        var profile = s.Answers.Business with { Name = s.Editor.Profile.Name, Website = s.Editor.Profile.Website };
        var result = new OnboardingPublicSite(id, slug, profile, s.Answers.Brand with { Introduction = s.Editor.Profile.Tagline }, s.Editor.Categories,
            s.Editor.Items, Bool(s.Config, "enabled") && Bool(s.Config, "accepting_orders"), s.Editor.Profile.Hours);
        await tx.CommitAsync(ct); return result;
    }

    private static async Task<State> ReadAsync(DbConnection db, DbTransaction tx, string id, AuthUser user, bool initialize, CancellationToken ct)
    {
        if (!Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,128}$")) throw Missing();
        string owner, status, name;
        await using (var q = Command(db, tx, "SELECT COALESCE(user_id,''),status,name FROM bartide_customers WHERE id=@id AND vertical='bartide'", ("@id", id)))
        {
            await using var r = await q.ExecuteReaderAsync(ct); if (!await r.ReadAsync(ct)) throw Missing();
            owner = r.GetString(0); status = r.GetString(1); name = r.GetString(2);
        }
        if (status is not ("draft" or "building" or "active")) throw Missing();
        var isOwner = owner == user.UserId || user.IsPlatformOwner;
        if (!isOwner && !await TenantStaffAccess.IsManagerAsync(db, tx, id, user, ct, allowPreparation: true))
            throw new OnboardingException("This setup belongs to another business.", 403, "onboarding_forbidden");
        if (initialize && isOwner && status is "draft" or "building")
        {
            await Run(db, tx, "INSERT INTO bartide_enhanced_configs(tenant_id,settings_json,version,updated_at) VALUES(@id,@json,0,@now) ON CONFLICT(tenant_id) DO NOTHING", ct,
                ("@id", id), ("@json", "{\"enabled\":false,\"accepting_orders\":false}"), ("@now", Now()));
            var initial = await Scalar(db, tx, "SELECT settings_json FROM bartide_enhanced_configs WHERE tenant_id=@id", ct, ("@id", id)) as string;
            var config = Parse(initial ?? throw Missing());
            if (config["onboarding"] is null)
            {
                var menu = Parse((await Scalar(db, tx, "SELECT menu_json FROM bartide_customers WHERE id=@id", ct, ("@id", id))) as string ?? throw Missing());
                var answers = Defaults(name, menu);
                config["onboarding"] = new JsonObject { ["schema"] = "bartide-onboarding/1", ["answers"] = JsonSerializer.SerializeToNode(answers, Json), ["createdAt"] = Now(), ["updatedAt"] = Now() };
                await Run(db, tx, "UPDATE bartide_enhanced_configs SET settings_json=@json,version=version+1,updated_at=@now WHERE tenant_id=@id", ct,
                    ("@json", config.ToJsonString(Json)), ("@now", Now()), ("@id", id));
            }
        }
        return await ReadState(db, tx, id, isOwner, ct);
    }

    private static async Task<State> ReadState(DbConnection db, DbTransaction tx, string id, bool owner, CancellationToken ct)
    {
        string slug, name, user, status, raw, config; int version, configVersion; string? due;
        await using (var q = Command(db, tx, "SELECT c.slug,c.name,COALESCE(c.user_id,''),c.status,c.version,c.menu_json,e.settings_json,e.version,c.build_ready_at FROM bartide_customers c JOIN bartide_enhanced_configs e ON e.tenant_id=c.id WHERE c.id=@id AND c.vertical='bartide'", ("@id", id)))
        {
            await using var r = await q.ExecuteReaderAsync(ct); if (!await r.ReadAsync(ct)) throw new OnboardingException("The owner needs to start business setup first.", 409, "setup_required");
            slug = r.GetString(0); name = r.GetString(1); user = r.GetString(2); status = r.GetString(3); version = r.ReadInt32(4);
            raw = r.GetString(5); config = r.GetString(6); configVersion = r.ReadInt32(7); due = r.IsDBNull(8) ? null : r.GetString(8);
        }
        var settings = Parse(config); var workflow = settings["onboarding"] as JsonObject ?? throw new OnboardingException("Start business setup first.", 409, "setup_required");
        if (Text(workflow, "schema") != "bartide-onboarding/1") throw Unavailable();
        var answers = workflow["answers"]?.Deserialize<Answers>(Json) ?? throw Unavailable();
        var editor = await RestaurantOrderingStore.ReadOnboardingMenuAsync(db, tx, id, ct);
        answers = answers with { Business = answers.Business with { Name = editor.Profile.Name, Website = editor.Profile.Website,
                Phone = settings["contact_phone"]?.GetValue<string>() ?? answers.Business.Phone },
            Brand = answers.Brand with { Introduction = editor.Profile.Tagline }, Service = ProjectService(settings, answers.Service) };
        return new(id, slug, name, user, status, version, configVersion, due, Parse(raw), settings, workflow, answers, owner, editor);
    }

    private static async Task<OnboardingWorkspace> ViewAsync(DbConnection db, DbTransaction tx, State s, CancellationToken ct)
    {
        var checks = await Checks(db, tx, s, ct); var release = Release(s);
        var current = release is not null && release.Fingerprint == await Fingerprint(db, tx, s, ct);
        var approved = current && Text(s.Workflow, "approvedRelease") == release!.Id && Text(s.Workflow, "approvedBy") == s.Owner;
        checks.Add(new("preview", "launch", current ? "ready" : "needs_attention", current ? "Your private preview matches the saved setup." : "Build a preview of your latest details.", Setup(s.Id, "launch")));
        checks.Add(new("owner_approval", "launch", approved ? "ready" : "needs_attention", approved ? "The owner approved this version for launch review." : "The owner needs to review and approve the current preview.", Setup(s.Id, "launch")));
        checks.Add(new("publication", "launch", s.Status == "active" ? "ready" : "waiting", s.Status == "active" ? "Your app is published." : "BarTide reviews the approved app with you after the scheduled build period.", Setup(s.Id, "launch")));
        return new(s.Id, s.Slug, s.Name, s.Status, s.Revision, s.IsOwner, Text(s.Workflow, "updatedAt"),
            s.Answers.Business, s.Answers.Brand, s.Answers.Service, s.Answers.Team, checks, s.Editor.Items.Count,
            release?.Id, approved, approved ? Text(s.Workflow, "approvedAt") : null, current && CoreReady(checks) && s.IsOwner && s.Status != "active", s.BuildReadyAt);
    }

    private static async Task<OnboardingRelease> ReleaseView(DbConnection db, DbTransaction tx, State s, OnboardingRelease r, CancellationToken ct)
    {
        var current = r.Fingerprint == await Fingerprint(db, tx, s, ct);
        var checks = await Checks(db, tx, s, ct);
        return r with { Current = current, Approved = current && Text(s.Workflow, "approvedRelease") == r.Id && Text(s.Workflow, "approvedBy") == s.Owner,
            CanApprove = current && s.IsOwner && s.Status != "active" && CoreReady(checks), Checks = checks };
    }

    private static OnboardingRelease? Release(State s) => s.Workflow["release"]?.Deserialize<OnboardingRelease>(Json);
    private static async Task<string> Fingerprint(DbConnection db, DbTransaction tx, State s, CancellationToken ct)
    {
        var cfg = (JsonObject)s.Config.DeepClone(); cfg.Remove("onboarding"); cfg.Remove("enabled"); cfg.Remove("accepting_orders");
        var members = new List<string>();
        await using var q = Command(db, tx, "SELECT id,name,email,role,active FROM bartide_enhanced_members WHERE tenant_id=@id ORDER BY id", ("@id", s.Id));
        await using (var r = await q.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) members.Add(JsonSerializer.Serialize(new { id = r.GetString(0), name = r.GetString(1), email = r.GetString(2), role = r.GetString(3), active = r.ReadInt32(4) }, Json));
        var raw = JsonSerializer.Serialize(new { template = Template, menu = s.Menu, config = cfg, answers = s.Answers, members }, Json);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    private static async Task WriteConfig(DbConnection db, DbTransaction tx, State s, CancellationToken ct)
    {
        var json = s.Config.ToJsonString(Json);
        if (Encoding.UTF8.GetByteCount(json) > 900_000) throw new OnboardingException("This preview is too large. Reduce the menu descriptions or media selections.");
        if (await Run(db, tx, "UPDATE bartide_enhanced_configs SET settings_json=@json,version=version+1,updated_at=@now WHERE tenant_id=@id AND version=@version", ct,
            ("@json", json), ("@now", Now()), ("@id", s.Id), ("@version", s.ConfigVersion)) != 1) throw Stale();
    }

    private static void Editable(State s) { if (s.Status == "active") throw new OnboardingException("Use your workspace tools to maintain the published app.", 409, "already_published"); }
    private static string Now() => DateTimeOffset.UtcNow.ToString("O");
    private static string Setup(string id, string section) => "/workspace/" + Uri.EscapeDataString(id) + "/onboarding#" + section;
    private static JsonObject Parse(string value) => JsonNode.Parse(value, documentOptions: new() { MaxDepth = 32 }) as JsonObject ?? throw Unavailable();
    private static string Text(JsonObject node, string key) => node[key]?.GetValue<string>() ?? "";
    private static bool Bool(JsonObject node, string key) => node[key]?.GetValue<bool>() ?? false;
    private static OnboardingException Stale() => new("Your setup changed. Reload and review the latest version.", 409, "onboarding_stale");
    private static OnboardingException Missing() => new("This business is unavailable.", 404, "onboarding_missing");
    private static OnboardingException Unavailable() => new("Business setup is temporarily unavailable.", 503, "onboarding_unavailable");
    private static DbCommand Command(DbConnection db, DbTransaction tx, string sql, params (string Key, object? Value)[] args)
    { var q = db.CreateCommand(); q.Transaction = tx; q.CommandText = sql; foreach (var (k, v) in args) q.Parameters.AddWithValue(k, v); return q; }
    private static async Task<object?> Scalar(DbConnection db, DbTransaction tx, string sql, CancellationToken ct, params (string Key, object? Value)[] args)
    { await using var q = Command(db, tx, sql, args); return await q.ExecuteScalarAsync(ct); }
    private static async Task<long> Count(DbConnection db, DbTransaction tx, string sql, CancellationToken ct, params (string Key, object? Value)[] args) => Convert.ToInt64(await Scalar(db, tx, sql, ct, args), CultureInfo.InvariantCulture);
    private static async Task<int> Run(DbConnection db, DbTransaction tx, string sql, CancellationToken ct, params (string Key, object? Value)[] args)
    { await using var q = Command(db, tx, sql, args); return await q.ExecuteNonQueryAsync(ct); }
}
