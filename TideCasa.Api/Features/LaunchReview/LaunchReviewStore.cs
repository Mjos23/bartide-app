using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Data.Common;
using TideCasa.Api.Infrastructure;
using TideCasa.Contracts;
using TideCasa.Api.Features.ClientOnboarding;

namespace TideCasa.Api.Features.LaunchReview;

public sealed class LaunchReviewException(string message, int status = 400, string code = "launch_invalid") : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

public sealed class LaunchReviewStore(ApplicationDatabase database)
{
    private sealed record Project(string Id, string Name, string Vertical, string Status, int Version,
        string? EnrolledAt, string? BuildReadyAt, string Menu, int BuildDays);
    private const string Columns = "id,name,vertical,status,version,enrolled_at,build_ready_at,menu_json,CASE WHEN EXISTS(SELECT 1 FROM tide_service_orders s WHERE s.tenant_id=bartide_customers.id AND s.status='paid' AND s.monthly_cents IN (14900,19900)) THEN 30 ELSE 7 END";
    private static readonly Regex Id = new("^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant);
    private static readonly Regex ItemId = new("^[a-z][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex ZonedDate = new("^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}(?:\\.\\d{1,7})?(?:Z|[+-]\\d{2}:\\d{2})$", RegexOptions.CultureInvariant);

    public async Task<LaunchReviewWorkspace> ListAsync(AuthUser user, string? after, CancellationToken ct)
    {
        RequireOwner(user);
        if (after is { Length: > 0 } && !Id.IsMatch(after)) throw new LaunchReviewException("Reload the project list.");
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: true);
        await using var command = Command(db, tx, "SELECT " + Columns + " FROM bartide_customers WHERE vertical IN ('bartide','tide-casa') AND id>@after ORDER BY id LIMIT 101", ("@after", after ?? ""));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var projects = new List<LaunchReviewProject>(); var now = DateTimeOffset.UtcNow;
        while (await reader.ReadAsync(ct)) projects.Add(View(Read(reader), now));
        await reader.DisposeAsync();
        for (var index = 0; index < projects.Count; index++)
            if (projects[index].CanLaunch && await ClientOnboardingStore.LaunchBlockAsync(db, tx, projects[index].Id, ct) is { } reason)
                projects[index] = projects[index] with { CanLaunch = false, LaunchBlockReason = reason };
        var more = projects.Count > 100; if (more) projects.RemoveAt(100);
        return new(projects, more ? projects[^1].Id : null);
    }

    public async Task<LaunchReviewProject> TransitionAsync(string id, AuthUser user, LaunchReviewTransitionRequest request, CancellationToken ct)
    {
        RequireOwner(user);
        if (!Id.IsMatch(id) || request is null || request.ExpectedVersion < 0 || request.Status is not ("active" or "paused"))
            throw new LaunchReviewException("Choose a valid publication change.");
        await using var db = await database.OpenAsync(ct); using var tx = db.BeginTransaction(deferred: false);
        Project project;
        await using (var command = Command(db, tx, "SELECT " + Columns + " FROM bartide_customers WHERE id=@id AND vertical IN ('bartide','tide-casa')", ("@id", id)))
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new LaunchReviewException("This project is unavailable.", 404, "launch_missing");
            project = Read(reader);
        }
        if (project.Status == "draft") throw new LaunchReviewException("Enrollment starts only after a verified live setup payment. A manual approval cannot start the build.", 409, "launch_draft");
        if (project.Version != request.ExpectedVersion) throw new LaunchReviewException("This app changed. Reload and review it again.", 409, "launch_stale");
        var now = DateTimeOffset.UtcNow;
        if (project.Status == "building")
        {
            if (request.Status != "active" || !request.ReviewedWithCustomer) throw new LaunchReviewException("Review the finished app with the customer and confirm approval before launch.", 400, "launch_review_required");
            var view = View(project, now);
            if (!view.CanLaunch) throw new LaunchReviewException(view.LaunchBlockReason ?? "This build is not ready for launch.", 409, "launch_not_ready");
            if (await ClientOnboardingStore.LaunchBlockAsync(db, tx, id, ct, publish: true) is { } reason)
                throw new LaunchReviewException(reason, 409, "launch_not_ready");
        }
        else if (!(project.Status == "active" && request.Status == "paused" || project.Status == "paused" && request.Status == "active"))
            throw new LaunchReviewException("This publication change is unavailable.", 409, "launch_transition");
        await using var save = Command(db, tx, "UPDATE bartide_customers SET status=@target,version=version+1,updated_at=@now WHERE id=@id AND status=@status AND version=@version", ("@target", request.Status), ("@now", now.ToString("O")), ("@id", id), ("@status", project.Status), ("@version", project.Version));
        if (await save.ExecuteNonQueryAsync(ct) != 1) throw new LaunchReviewException("This app changed. Reload and review it again.", 409, "launch_stale");
        await tx.CommitAsync(ct);
        return View(project with { Status = request.Status, Version = project.Version + 1 }, now);
    }

    private static LaunchReviewProject View(Project project, DateTimeOffset now)
    {
        var items = FinishedItems(project.Menu); string? blocked = null;
        var enrolled = ParseDate(project.EnrolledAt); var due = ParseDate(project.BuildReadyAt);
        if (project.Status == "draft") blocked = "Awaiting verified live setup payment. The build has not started.";
        else if (project.Status == "building")
        {
            if (enrolled is null || due is null || due.Value - enrolled.Value < TimeSpan.FromDays(project.BuildDays)) blocked = "The enrollment and scheduled build dates need review.";
            else if (now < due.Value) blocked = "The scheduled build period is still in progress.";
            else if (items == 0) blocked = "Add the finished menu or service items before launch. Uploaded source files alone are not a finished app.";
        }
        return new(project.Id, project.Name, project.Vertical, project.Status, project.Version, enrolled?.ToUniversalTime().ToString("O"), due?.ToUniversalTime().ToString("O"), items, project.Status == "building" && blocked is null, blocked);
    }
    private static DateTimeOffset? ParseDate(string? value) => value is { Length: <= 40 } && ZonedDate.IsMatch(value)
        && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
    private static int FinishedItems(string raw)
    {
        try
        {
            using var json = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 32 });
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return 0;
            return items.EnumerateArray().Count(item => item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && ItemId.IsMatch(id.GetString() ?? "")
                && item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                && name.GetString() is { Length: > 0 and <= 160 } text && !string.IsNullOrWhiteSpace(text) && !text.Any(char.IsControl));
        }
        catch (JsonException) { return 0; }
    }
    private static Project Read(DbDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.ReadInt32(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetString(7), reader.ReadInt32(8));
    private static void RequireOwner(AuthUser user)
    { if (!user.IsPlatformOwner) throw new LaunchReviewException("Platform owner access is required.", 403, "launch_forbidden"); }
    private static DbCommand Command(DbConnection db, DbTransaction tx, string sql, params (string Name, object Value)[] values)
    { var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql; foreach (var (name, value) in values) cmd.Parameters.AddWithValue(name, value); return cmd; }
}
