using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Data.Common;
using TideCasa.Api.Infrastructure;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.SalesPipeline;

public sealed class SalesPipelineFailure(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>Private owner workflow. A lead stage is never payment evidence or an access grant.</summary>
public sealed class SalesPipelineStore(ApplicationDatabase database)
{
    private const int MaximumLeads = 20000;
    private const int MaximumOperations = 100000;
    private const string Columns = "l.id,l.name,l.business,l.email,l.phone,l.city,l.vertical,l.stage,l.source,l.referral_code,l.submitted_by,l.next_action,l.follow_up,l.assignee,l.notes,l.tenant_id,l.version,l.created_at,l.updated_at,(SELECT COUNT(*) FROM tide_sales_demo_links d WHERE d.lead_id=l.id)";
    private static readonly HashSet<string> Stages = ["new", "contacted", "pitched", "demo-scheduled", "demo-completed", "proposal", "enrolled", "closed"];

    // Populate deterministic identity hashes and reconcile pre-feature demo requests. The transaction
    // makes restart/retry safe, without modifying existing lead contact, source, notes, or stage.
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        var after = "";
        while (true)
        {
            var rows = await Rows(db, tx, "SELECT id,email,business FROM tide_leads WHERE id>@after ORDER BY id LIMIT 200", r => (Id: r.GetString(0), Email: r.GetString(1), Business: r.GetString(2)), ct, ("@after", after));
            foreach (var row in rows)
                await Execute(db, tx, "UPDATE tide_leads SET identity_hash=@identity WHERE id=@id AND identity_hash<>@identity", ct, ("@identity", Identity(row.Email, row.Business)), ("@id", row.Id));
            if (rows.Count == 0) break;
            after = rows[^1].Id;
        }
        after = "";
        while (true)
        {
            var rows = await Rows(db, tx, "SELECT d.id,d.request_json,d.created_at,d.status,COALESCE(i.note,'') FROM demo_requests d LEFT JOIN tide_demo_inbox i ON i.request_id=d.id WHERE d.id>@after AND NOT EXISTS(SELECT 1 FROM tide_sales_demo_links s WHERE s.request_id=d.id) ORDER BY d.id LIMIT 200", r => (Id: r.GetString(0), Json: r.GetString(1), Created: r.GetString(2), Status: r.GetString(3), Note: r.GetString(4)), ct, ("@after", after));
            foreach (var row in rows)
            {
                DemoRequest? request;
                try { request = JsonSerializer.Deserialize<DemoRequest>(row.Json); }
                catch (JsonException) { continue; }
                if (request is null || request.Id.ToString() != row.Id || !ValidDemo(request) || row.Note.Length > 4000) continue;
                await LinkDemoAsync(db, tx, request, row.Created, ct, row.Status, row.Note);
            }
            if (rows.Count == 0) break;
            after = rows[^1].Id;
        }
        await tx.CommitAsync(ct);
    }

    public async Task<SalesPipelinePage> ListAsync(AuthUser user, string? search, string? stage, string? due, string? queue, int page, int pageSize, CancellationToken ct)
    {
        Owner(user);
        search = Text(search ?? "", 0, 120); stage = Text(stage ?? "", 0, 30); due ??= "all";
        queue = Text(queue ?? "", 0, 30);
        if (queue == "") queue = "all";
        if (page is < 1 or > 10000 || pageSize is < 1 or > 50 || due is not ("all" or "overdue" or "today" or "upcoming" or "unscheduled")
            || queue is not ("all" or "to-pitch" or "pitched" or "follow-up" or "unplanned"))
            throw new SalesPipelineFailure("Choose a valid page, queue and follow-up filter.");
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: true);
        var where = db.Sql("""
            WHERE (@search='' OR instr(lower(l.name),lower(@search))>0 OR instr(lower(l.business),lower(@search))>0
              OR instr(lower(l.email),lower(@search))>0 OR instr(lower(l.city),lower(@search))>0 OR instr(lower(l.source),lower(@search))>0)
              AND (@stage='' OR l.stage=@stage)
              AND (@queue='all' OR (@queue='to-pitch' AND l.stage IN ('new','contacted'))
                OR (@queue='pitched' AND l.stage='pitched')
                OR (@queue='follow-up' AND l.stage NOT IN ('enrolled','closed') AND l.follow_up<>'' AND l.follow_up<=@today)
                OR (@queue='unplanned' AND l.stage NOT IN ('enrolled','closed') AND (l.next_action='' OR l.follow_up='')))
              AND (@due='all' OR (@due='unscheduled' AND l.follow_up='' AND l.stage<>'closed')
                OR (l.stage<>'closed' AND l.follow_up<>'' AND ((@due='overdue' AND l.follow_up<@today)
                  OR (@due='today' AND l.follow_up=@today) OR (@due='upcoming' AND l.follow_up>@today))))
            """, """
            WHERE (@search='' OR strpos(translate(l.name,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),translate(@search,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'))>0 OR strpos(translate(l.business,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),translate(@search,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'))>0
              OR strpos(translate(l.email,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),translate(@search,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'))>0 OR strpos(translate(l.city,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),translate(@search,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'))>0 OR strpos(translate(l.source,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),translate(@search,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'))>0)
              AND (@stage='' OR l.stage=@stage)
              AND (@queue='all' OR (@queue='to-pitch' AND l.stage IN ('new','contacted'))
                OR (@queue='pitched' AND l.stage='pitched')
                OR (@queue='follow-up' AND l.stage NOT IN ('enrolled','closed') AND l.follow_up<>'' AND l.follow_up<=@today)
                OR (@queue='unplanned' AND l.stage NOT IN ('enrolled','closed') AND (l.next_action='' OR l.follow_up='')))
              AND (@due='all' OR (@due='unscheduled' AND l.follow_up='' AND l.stage<>'closed')
                OR (l.stage<>'closed' AND l.follow_up<>'' AND ((@due='overdue' AND l.follow_up<@today)
                  OR (@due='today' AND l.follow_up=@today) OR (@due='upcoming' AND l.follow_up>@today))))
            """);
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var values = new (string, object?)[] { ("@search", search), ("@stage", stage), ("@due", due), ("@queue", queue), ("@today", today) };
        // Counts describe the owner's full pipeline, including when a queue, search
        // or page is selected. Keep them consistent with the list's read snapshot.
        var summary = await Rows(db, tx, """
            SELECT COUNT(CASE WHEN stage IN ('new','contacted') THEN 1 END),
              COUNT(CASE WHEN stage='pitched' THEN 1 END),
              COUNT(CASE WHEN stage NOT IN ('enrolled','closed') AND follow_up<>'' AND follow_up<=@today THEN 1 END),
              COUNT(CASE WHEN stage NOT IN ('enrolled','closed') AND (next_action='' OR follow_up='') THEN 1 END)
            FROM tide_leads
            """, r => new SalesPipelineSummary(r.ReadInt32(0), r.ReadInt32(1), r.ReadInt32(2), r.ReadInt32(3)), ct, ("@today", today));
        var total = await Scalar(db, tx, "SELECT COUNT(*) FROM tide_leads l " + where, ct, values);
        var rows = await Rows(db, tx, $"SELECT {Columns} FROM tide_leads l {where} ORDER BY CASE WHEN l.stage='closed' THEN 1 ELSE 0 END,CASE WHEN l.follow_up='' THEN 1 ELSE 0 END,l.follow_up,l.updated_at DESC,l.id LIMIT @limit OFFSET @offset", Lead, ct,
            [.. values, ("@limit", pageSize), ("@offset", (page - 1) * pageSize)]);
        await tx.CommitAsync(ct);
        return new(rows, checked((int)total), page, pageSize, summary[0]);
    }

    public async Task<SalesLeadDetail> GetAsync(string id, AuthUser user, CancellationToken ct)
    {
        Owner(user); Id(id);
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: true);
        var detail = await Detail(db, tx, id, ct); await tx.CommitAsync(ct); return detail;
    }

    public async Task<SalesLeadDetail> CreateAsync(AuthUser user, CreateSalesLeadRequest request, CancellationToken ct)
    {
        Owner(user);
        if (request is null) throw new SalesPipelineFailure("Add the prospect’s details.");
        Key(request.RequestKey);
        var normalized = request with
        {
            Name = Text(request.Name, 1, 120), Business = Text(request.Business, 1, 150), Email = Email(request.Email),
            Phone = Text(request.Phone, 0, 40), City = Text(request.City, 0, 120), Vertical = Text(request.Vertical, 1, 30),
            Source = Text(request.Source, 1, 80), ReferralCode = Text(request.ReferralCode, 0, 32).ToUpperInvariant(), PrivateNote = Text(request.PrivateNote, 0, 4000, true)
        };
        if (normalized.Vertical is not ("bartide" or "tide-casa" or "beach-glam" or "fit-tide")) throw new SalesPipelineFailure("Choose a listed business app.");
        var hash = Hash(JsonSerializer.Serialize(normalized));
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        var replay = await Replay(db, tx, user.UserId, request.RequestKey, "create", hash, ct);
        if (replay is not null) { var result = await Detail(db, tx, replay, ct); await tx.CommitAsync(ct); return result with { ExistingProspect = true }; }
        await Capacity(db, tx, ct);
        if (normalized.ReferralCode != "" && await Scalar(db, tx, db.Sql("SELECT COUNT(*) FROM tide_referral_profiles WHERE code=@code",
            "SELECT COUNT(*) FROM tide_referral_profiles WHERE translate(code,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz')=translate(@code,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz') COLLATE \"C\""), ct, ("@code", normalized.ReferralCode)) != 1)
            throw new SalesPipelineFailure("Use an existing sales engineer’s referral code or leave it blank.");
        var identity = Identity(normalized.Email, normalized.Business);
        var existing = await ExactMatch(db, tx, identity, ct);
        var id = existing ?? Guid.NewGuid().ToString(); var now = DateTimeOffset.UtcNow.ToString("O");
        if (existing is null)
        {
            await LeadCapacity(db, tx, ct);
            await Execute(db, tx, """
                INSERT INTO tide_leads(id,name,business,email,phone,city,vertical,stage,notes,follow_up,version,updated_at,source,submitted_by,created_at,identity_hash,referral_code)
                VALUES(@id,@name,@business,@email,@phone,@city,@vertical,'new',@note,'',0,@now,@source,@actor,@now,@identity,@referral)
                """, ct, ("@id", id), ("@name", normalized.Name), ("@business", normalized.Business), ("@email", normalized.Email), ("@phone", normalized.Phone), ("@city", normalized.City),
                ("@vertical", normalized.Vertical), ("@note", normalized.PrivateNote), ("@now", now), ("@source", normalized.Source), ("@actor", user.UserId), ("@identity", identity), ("@referral", normalized.ReferralCode));
            await History(db, tx, id, "created", "Prospect added. Saving a prospect does not send a message or subscribe the contact to marketing.", user.UserId, now, ct, normalized.PrivateNote);
        }
        // Exact email + business repeats point to the original lead; its attribution and private work are not overwritten.
        await Operation(db, tx, user.UserId, request.RequestKey, "create", hash, id, now, ct);
        var detail = await Detail(db, tx, id, ct); await tx.CommitAsync(ct); return detail with { ExistingProspect = existing is not null };
    }

    public async Task<SalesLeadDetail> UpdateAsync(string id, AuthUser user, UpdateSalesLeadRequest request, CancellationToken ct)
    {
        Owner(user); Id(id);
        if (request is null || request.ExpectedVersion < 0) throw new SalesPipelineFailure("Refresh the prospect before saving.");
        Key(request.RequestKey);
        var normalized = request with
        {
            Stage = Text(request.Stage, 1, 30), NextAction = Text(request.NextAction, 0, 240), FollowUpDate = Date(request.FollowUpDate),
            Assignee = Text(request.Assignee, 0, 100), PrivateNote = Text(request.PrivateNote, 0, 4000, true), WorkspaceId = string.IsNullOrWhiteSpace(request.WorkspaceId) ? null : Text(request.WorkspaceId, 1, 200)
        };
        var hash = Hash(JsonSerializer.Serialize(normalized)); var operation = "update:" + id;
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        var replay = await Replay(db, tx, user.UserId, request.RequestKey, operation, hash, ct);
        if (replay is not null) { var result = await Detail(db, tx, replay, ct); await tx.CommitAsync(ct); return result; }
        var before = (await Detail(db, tx, id, ct)).Lead;
        if (before.Version != request.ExpectedVersion) throw new SalesPipelineFailure("This prospect changed. Refresh before saving your changes.", 409);
        if (!Stages.Contains(normalized.Stage) && normalized.Stage != before.Stage) throw new SalesPipelineFailure("Choose a listed sales stage.");
        if (normalized.WorkspaceId is not null && await Scalar(db, tx, "SELECT COUNT(*) FROM bartide_customers WHERE id=@id", ct, ("@id", normalized.WorkspaceId)) != 1)
            throw new SalesPipelineFailure("Choose an existing client workspace or leave it blank.");
        var preserveImportedNote = before.PrivateNote != "" && await Scalar(db, tx, "SELECT COUNT(*) FROM tide_sales_history WHERE lead_id=@id AND kind IN ('created','updated')", ct, ("@id", id)) == 0;
        await Capacity(db, tx, ct, preserveImportedNote ? 2 : 1);
        var now = DateTimeOffset.UtcNow.ToString("O");
        var changed = await Execute(db, tx, """
            UPDATE tide_leads SET stage=@stage,next_action=@next,follow_up=@date,assignee=@assignee,notes=@note,tenant_id=@workspace,version=version+1,updated_at=@now
            WHERE id=@id AND version=@expected
            """, ct, ("@stage", normalized.Stage), ("@next", normalized.NextAction), ("@date", normalized.FollowUpDate), ("@assignee", normalized.Assignee), ("@note", normalized.PrivateNote),
            ("@workspace", normalized.WorkspaceId), ("@now", now), ("@id", id), ("@expected", request.ExpectedVersion));
        if (changed != 1) throw new SalesPipelineFailure("This prospect changed. Refresh before saving your changes.", 409);
        var changes = new List<string>();
        if (before.Stage != normalized.Stage) changes.Add($"Stage: {before.Stage} → {normalized.Stage} (manual tracking).");
        if (before.NextAction != normalized.NextAction) changes.Add("Next action changed.");
        if (before.FollowUpDate != normalized.FollowUpDate) changes.Add("Follow-up date: " + (normalized.FollowUpDate == "" ? "cleared" : normalized.FollowUpDate) + ".");
        if (before.Assignee != normalized.Assignee) changes.Add("Assigned contact label changed.");
        if (before.PrivateNote != normalized.PrivateNote) changes.Add("Private note changed.");
        if (before.WorkspaceId != normalized.WorkspaceId) changes.Add(normalized.WorkspaceId is null ? "Workspace reference removed." : "Existing workspace referenced; access and billing unchanged.");
        if (preserveImportedNote)
            await History(db, tx, id, "created", "Existing private note preserved before the first pipeline edit.", user.UserId, before.UpdatedAt, ct, before.PrivateNote);
        await History(db, tx, id, "updated", changes.Count == 0 ? "Prospect reviewed; details unchanged." : string.Join(' ', changes), user.UserId, now, ct, normalized.PrivateNote);
        await Operation(db, tx, user.UserId, request.RequestKey, operation, hash, id, now, ct);
        var detail = await Detail(db, tx, id, ct); await tx.CommitAsync(ct); return detail;
    }

    // Called inside the demo-request + notification-outbox transaction, never through a separate connection.
    public static async Task LinkDemoAsync(DbConnection db, DbTransaction tx, DemoRequest request, string createdAt, CancellationToken ct, string inboxStatus = "requested", string inboxNote = "")
    {
        if (await Scalar(db, tx, "SELECT COUNT(*) FROM tide_sales_demo_links WHERE request_id=@id", ct, ("@id", request.Id.ToString())) != 0) return;
        if (!ValidDemo(request)) throw new SalesPipelineFailure("Check the demo contact details.");
        await Capacity(db, tx, ct);
        var identity = Identity(request.Email, request.Business);
        var id = await ExactMatch(db, tx, identity, ct);
        if (id is null)
        {
            await LeadCapacity(db, tx, ct); id = Guid.NewGuid().ToString();
            var stage = inboxStatus switch { "contacted" => "contacted", "scheduled" => "demo-scheduled", "closed" => "closed", _ => "new" };
            var next = stage switch { "closed" => "", "demo-scheduled" => "Prepare the agreed demo", _ => "Agree a demo time" };
            await Execute(db, tx, """
                INSERT INTO tide_leads(id,name,business,email,phone,city,vertical,stage,notes,follow_up,version,updated_at,source,submitted_by,created_at,identity_hash,next_action)
                VALUES(@id,@name,@business,@email,@phone,@city,@vertical,@stage,@note,'',0,@now,'book-demo',NULL,@now,@identity,@next)
                """, ct, ("@id", id), ("@name", request.Name.Trim()), ("@business", request.Business.Trim()), ("@email", request.Email.Trim().ToLowerInvariant()), ("@phone", request.Phone.Trim()),
                ("@city", request.City.Trim()), ("@vertical", request.BusinessType), ("@now", createdAt), ("@identity", identity), ("@stage", stage), ("@note", inboxNote), ("@next", next));
        }
        await Execute(db, tx, "INSERT INTO tide_sales_demo_links(request_id,lead_id,created_at) VALUES(@request,@lead,@now)", ct, ("@request", request.Id.ToString()), ("@lead", id), ("@now", createdAt));
        var summary = inboxStatus switch
        {
            "contacted" => "Earlier demo request linked; the inbox records that contact was made. No marketing subscription was created.",
            "scheduled" => "Earlier demo request linked; the inbox records an agreed demo. No marketing subscription was created.",
            "closed" => "Earlier closed demo request linked for reference. No marketing subscription was created.",
            _ => "Demo request received. Requested time is not a confirmed appointment; no marketing subscription was created."
        };
        await History(db, tx, id, "demo-linked", summary, "demo-request", createdAt, ct, inboxNote);
    }

    private static async Task<SalesLeadDetail> Detail(DbConnection db, DbTransaction tx, string id, CancellationToken ct)
    {
        var lead = (await Rows(db, tx, $"SELECT {Columns} FROM tide_leads l WHERE l.id=@id", Lead, ct, ("@id", id))).SingleOrDefault()
            ?? throw new SalesPipelineFailure("Prospect not found.", 404);
        var history = await Rows(db, tx, "SELECT id,kind,summary,actor,created_at,private_note FROM tide_sales_history WHERE lead_id=@id ORDER BY created_at DESC,id DESC LIMIT 50", r => new SalesLeadActivity(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5)), ct, ("@id", id));
        var historyCount = await Scalar(db, tx, "SELECT COUNT(*) FROM tide_sales_history WHERE lead_id=@id", ct, ("@id", id));
        var requests = await Rows(db, tx, "SELECT d.id,d.status,d.request_json,d.created_at FROM tide_sales_demo_links s JOIN demo_requests d ON d.id=s.request_id WHERE s.lead_id=@id ORDER BY d.created_at DESC,d.id LIMIT 25", r => (Id: r.GetString(0), Status: r.GetString(1), Json: r.GetString(2), Created: r.GetString(3)), ct, ("@id", id));
        var demos = new List<SalesDemoRequest>();
        foreach (var row in requests)
        {
            try { if (JsonSerializer.Deserialize<DemoRequest>(row.Json) is { } request) demos.Add(new(row.Id, row.Status, request.PreferredTimes, request.TimeZone, request.Goals, row.Created, request.Phone, request.CanText)); }
            catch (JsonException) { /* Imported invalid payload stays preserved; it cannot break the private lead view. */ }
        }
        var workspaces = await Rows(db, tx, db.Sql("SELECT id,name FROM bartide_customers ORDER BY CASE WHEN id=@linked THEN 0 ELSE 1 END,name COLLATE NOCASE,id LIMIT 201",
            "SELECT id,name FROM bartide_customers ORDER BY CASE WHEN id=@linked THEN 0 ELSE 1 END,translate(name,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz') COLLATE \"C\",id LIMIT 201"), r => new SalesWorkspaceOption(r.GetString(0), r.GetString(1)), ct, ("@linked", lead.WorkspaceId));
        return new(lead, history, checked((int)historyCount), demos, workspaces.Take(200).ToArray(), workspaces.Count > 200);
    }
    private static SalesLead Lead(DbDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8), r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10), r.GetString(11), r.GetString(12), r.GetString(13), r.GetString(14), r.IsDBNull(15) ? null : r.GetString(15), r.ReadInt32(16), r.GetString(17), r.GetString(18), r.ReadInt32(19));
    private static async Task<string?> ExactMatch(DbConnection db, DbTransaction tx, string identity, CancellationToken ct)
    {
        if (identity == "") return null;
        var matches = await Rows(db, tx, "SELECT id FROM tide_leads WHERE identity_hash=@identity ORDER BY id LIMIT 2", r => r.GetString(0), ct, ("@identity", identity));
        // Ambiguous legacy duplicates are preserved, never guessed or merged.
        return matches.Count == 1 ? matches[0] : null;
    }
    private static async Task<string?> Replay(DbConnection db, DbTransaction tx, string actor, string key, string operation, string hash, CancellationToken ct)
    {
        var matches = await Rows(db, tx, "SELECT operation,payload_hash,lead_id FROM tide_sales_operations WHERE actor=@actor AND request_key=@key", r => (Operation: r.GetString(0), Hash: r.GetString(1), Id: r.GetString(2)), ct, ("@actor", actor), ("@key", key));
        if (matches.Count == 0) return null;
        if (matches[0].Operation != operation || matches[0].Hash != hash) throw new SalesPipelineFailure("That submission was already used with different details. Refresh before trying again.", 409);
        return matches[0].Id;
    }
    private static Task<int> Operation(DbConnection db, DbTransaction tx, string actor, string key, string operation, string hash, string id, string now, CancellationToken ct) =>
        Execute(db, tx, "INSERT INTO tide_sales_operations(actor,request_key,operation,payload_hash,lead_id,created_at) VALUES(@actor,@key,@operation,@hash,@id,@now)", ct, ("@actor", actor), ("@key", key), ("@operation", operation), ("@hash", hash), ("@id", id), ("@now", now));
    private static Task<int> History(DbConnection db, DbTransaction tx, string id, string kind, string summary, string actor, string now, CancellationToken ct, string privateNote = "") =>
        Execute(db, tx, "INSERT INTO tide_sales_history(id,lead_id,kind,summary,actor,created_at,private_note) VALUES(@event,@id,@kind,@summary,@actor,@now,@note)", ct, ("@event", Guid.NewGuid().ToString()), ("@id", id), ("@kind", kind), ("@summary", summary), ("@actor", actor), ("@now", now), ("@note", privateNote));
    private static async Task Capacity(DbConnection db, DbTransaction tx, CancellationToken ct, int historyEntries = 1)
    {
        if (await Scalar(db, tx, "SELECT COUNT(*) FROM tide_sales_operations", ct) >= MaximumOperations || await Scalar(db, tx, "SELECT COUNT(*) FROM tide_sales_history", ct) > MaximumOperations - historyEntries)
            throw new SalesPipelineFailure("The sales history needs an archive review before more records can be saved.", 429);
    }
    private static async Task LeadCapacity(DbConnection db, DbTransaction tx, CancellationToken ct)
    {
        if (await Scalar(db, tx, "SELECT COUNT(*) FROM tide_leads", ct) >= MaximumLeads) throw new SalesPipelineFailure("The prospect limit has been reached. Review existing records first.", 429);
    }
    private static bool ValidDemo(DemoRequest request)
    {
        var errors = new List<ValidationResult>();
        return request.Id != Guid.Empty && Validator.TryValidateObject(request, new ValidationContext(request), errors, true)
            && request.Name is not null && request.Business is not null && request.Email is not null && request.Phone is not null && request.City is not null
            && request.Goals is not null && request.ContactWebsite is not null;
    }
    private static void Owner(AuthUser user) { if (!user.IsPlatformOwner) throw new SalesPipelineFailure("Only the Tide Casa owner can access the sales pipeline.", 403); }
    private static void Key(string? key) { if (key is null || !Guid.TryParseExact(key, "D", out var value) || value == Guid.Empty) throw new SalesPipelineFailure("Refresh the form before saving."); }
    private static void Id(string? id) { if (string.IsNullOrWhiteSpace(id) || id.Length > 200 || id.Any(char.IsControl)) throw new SalesPipelineFailure("Prospect not found.", 404); }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Identity(string email, string business) => string.IsNullOrWhiteSpace(email) ? "" : Hash(JsonSerializer.Serialize(new[] { email.Trim().ToLowerInvariant(), business.Trim().ToLowerInvariant() }));
    private static string Email(string? value)
    {
        var email = Text(value, 0, 254).ToLowerInvariant();
        if (email != "" && !new EmailAddressAttribute().IsValid(email)) throw new SalesPipelineFailure("Enter a valid email address or leave it blank.");
        return email;
    }
    private static string Date(string? value)
    {
        var date = Text(value, 0, 10);
        if (date != "" && !DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) throw new SalesPipelineFailure("Choose a valid follow-up date.");
        return date;
    }
    private static string Text(string? value, int minimum, int maximum, bool multiline = false)
    {
        if (value is null || value.Length > maximum || value.Any(c => char.IsControl(c) && (!multiline || c is not ('\r' or '\n' or '\t')))) throw new SalesPipelineFailure("Check the length and characters in the prospect details.");
        value = value.Trim(); if (value.Length < minimum) throw new SalesPipelineFailure("Complete the required prospect details."); return value;
    }
    private static async Task<List<T>> Rows<T>(DbConnection db, DbTransaction tx, string sql, Func<DbDataReader, T> read, CancellationToken ct, params (string, object?)[] values)
    {
        await using var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
        foreach (var (key, value) in values) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(ct); var rows = new List<T>();
        while (await reader.ReadAsync(ct)) rows.Add(read(reader)); return rows;
    }
    private static async Task<long> Scalar(DbConnection db, DbTransaction tx, string sql, CancellationToken ct, params (string, object?)[] values)
    {
        await using var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
        foreach (var (key, value) in values) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }
    private static async Task<int> Execute(DbConnection db, DbTransaction tx, string sql, CancellationToken ct, params (string, object?)[] values)
    {
        await using var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
        foreach (var (key, value) in values) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(ct);
    }
}
