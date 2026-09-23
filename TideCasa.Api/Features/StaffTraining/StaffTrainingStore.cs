using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Data.Common;
using TideCasa.Api.Infrastructure;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.StaffTraining;

public sealed class TeamException(string message, int status = 400, string code = "invalid_team_change") : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>Uses the legacy team and course records; explicit staff assignments/progress are additive.</summary>
public sealed class StaffTrainingStore(ApplicationDatabase database)
{
    private sealed record Access(string TenantId, string Name, bool Manager, string? MemberId, string DisplayName, bool Owner = false);
    private static string Now() => DateTimeOffset.UtcNow.ToString("O");
    private static string Id() => Guid.NewGuid().ToString("D");
    private delegate Task Change(DbConnection db, DbTransaction tx, Access access, CancellationToken ct);

    public Task<StaffTrainingWorkspace> WorkspaceAsync(string id, AuthUser user, CancellationToken ct) => ExecuteAsync(id, user, null, ct);

    public Task<StaffTrainingWorkspace> AddMemberAsync(string id, AuthUser user, AddTeamMemberRequest request, CancellationToken ct) =>
        ExecuteAsync(id, user, async (db, tx, a, token) =>
        {
            Manager(a); Required(request);
            var name = Text(request.Name, "Name", 80);
            var email = Text(request.Email, "Sign-in email", 254).ToLowerInvariant();
            if (!Regex.IsMatch(email, "^[^\\s@]+@[^\\s@]+\\.[^\\s@]+$") || !RestaurantStaffRoles.IsStaff(request.Role))
                throw new TeamException("Enter an email and choose a supported team role.");
            if (request.Role == "manager" && !a.Owner) throw new TeamException("Only the business owner can grant manager access.", 403, "owner_required");
            if (await Count(db, tx, "SELECT COUNT(*) FROM bartide_enhanced_members WHERE tenant_id=@tenant", token, ("@tenant", id)) >= 30)
                throw new TeamException("This team supports up to 30 staff records.", 409, "team_full");
            if (await Count(db, tx, db.Sql("SELECT COUNT(*) FROM bartide_enhanced_members WHERE tenant_id=@tenant AND email=@email COLLATE NOCASE",
            "SELECT COUNT(*) FROM bartide_enhanced_members WHERE tenant_id=@tenant AND translate(email,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz')=translate(@email,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz') COLLATE \"C\""), token, ("@tenant", id), ("@email", email)) > 0)
                throw new TeamException("That email is already on this team. Restore its existing access instead.", 409, "member_exists");
            await Run(db, tx, "INSERT INTO bartide_enhanced_members(id,tenant_id,name,email,role,active,created_at) VALUES(@id,@tenant,@name,@email,@role,1,@now)", token,
                ("@id", Id()), ("@tenant", id), ("@name", name), ("@email", email), ("@role", request.Role), ("@now", Now()));
        }, ct);

    public Task<StaffTrainingWorkspace> SetMemberAsync(string id, string memberId, AuthUser user, SetTeamMemberStateRequest request, CancellationToken ct) =>
        ExecuteAsync(id, user, async (db, tx, a, token) =>
        {
            Manager(a); Required(request);
            if (!a.Owner && await Count(db, tx, "SELECT COUNT(*) FROM bartide_enhanced_members WHERE id=@member AND tenant_id=@tenant AND role='manager'", token, ("@member", memberId), ("@tenant", id)) != 0)
                throw new TeamException("Only the business owner can change manager access.", 403, "owner_required");
            var changed = await Run(db, tx, "UPDATE bartide_enhanced_members SET active=@active WHERE id=@member AND tenant_id=@tenant AND active=@expected", token,
                ("@active", request.Active ? 1 : 0), ("@expected", request.ExpectedActive ? 1 : 0), ("@member", memberId), ("@tenant", id));
            if (changed != 1) throw Stale();
            if (!request.Active) await Run(db, tx, "DELETE FROM tide_delivery_locations WHERE tenant_id=@tenant AND driver_id=@member", token, ("@tenant", id), ("@member", memberId));
        }, ct);

    public Task<StaffTrainingWorkspace> AddShiftAsync(string id, AuthUser user, AddTeamShiftRequest request, CancellationToken ct) =>
        ExecuteAsync(id, user, async (db, tx, a, token) =>
        {
            Manager(a); Required(request);
            var label = Text(request.Label, "Shift label", 80);
            var start = Timestamp(request.StartsAt); var end = Timestamp(request.EndsAt);
            if ((end - start).TotalMinutes is < 15 or > 960) throw new TeamException("Use a shift between 15 minutes and 16 hours.");
            await ActiveMember(db, tx, id, request.MemberId, token);
            if (await Count(db, tx, "SELECT COUNT(*) FROM bartide_enhanced_shifts WHERE tenant_id=@tenant AND ends_at>@now", token, ("@tenant", id), ("@now", Now())) >= 300)
                throw new TeamException("This team has reached its 300 upcoming-shift limit.", 409, "schedule_full");
            // julianday compares older ISO offsets correctly, avoiding lexicographic timezone errors.
            if (await Count(db, tx, db.Sql("SELECT COUNT(*) FROM bartide_enhanced_shifts WHERE tenant_id=@tenant AND member_id=@member AND julianday(starts_at)<julianday(@end) AND julianday(ends_at)>julianday(@start)",
            "SELECT COUNT(*) FROM bartide_enhanced_shifts WHERE tenant_id=@tenant AND member_id=@member AND tide_iso_instant(starts_at)<tide_iso_instant(@end) AND tide_iso_instant(ends_at)>tide_iso_instant(@start)"), token,
                ("@tenant", id), ("@member", request.MemberId), ("@start", start.ToString("O")), ("@end", end.ToString("O"))) > 0)
                throw new TeamException("This shift overlaps another shift for that employee.", 409, "shift_overlap");
            await Run(db, tx, "INSERT INTO bartide_enhanced_shifts(id,tenant_id,member_id,starts_at,ends_at,label) VALUES(@id,@tenant,@member,@start,@end,@label)", token,
                ("@id", Id()), ("@tenant", id), ("@member", request.MemberId), ("@start", start.ToString("O")), ("@end", end.ToString("O")), ("@label", label));
        }, ct);

    public Task<StaffTrainingWorkspace> DeleteShiftAsync(string id, string shiftId, AuthUser user, CancellationToken ct) =>
        ExecuteAsync(id, user, async (db, tx, a, token) =>
        {
            Manager(a);
            if (await Run(db, tx, "DELETE FROM bartide_enhanced_shifts WHERE id=@id AND tenant_id=@tenant", token, ("@id", shiftId), ("@tenant", id)) != 1) throw Missing();
        }, ct);

    public Task<StaffTrainingWorkspace> MessageAsync(string id, AuthUser user, SendTeamMessageRequest request, CancellationToken ct) =>
        ExecuteAsync(id, user, async (db, tx, a, token) =>
        {
            Required(request);
            if (!Guid.TryParseExact(request.RequestId, "D", out _)) throw new TeamException("Refresh the message and try again.");
            var body = Text(request.Text, "Message", 2000, multiline: true);
            var previous = await Rows(db, tx, "SELECT tenant_id,author_id,body FROM bartide_enhanced_messages WHERE id=@id", r => new[] { r.GetString(0), r.GetString(1), r.GetString(2) }, token, ("@id", request.RequestId));
            if (previous.Count > 0)
            {
                if (previous[0][0] != id || previous[0][1] != user.UserId || previous[0][2] != body)
                    throw new TeamException("That message request was already used. Start a new message.", 409, "message_conflict");
                return;
            }
            if (await Count(db, tx, db.Sql("SELECT COUNT(*) FROM bartide_enhanced_messages WHERE tenant_id=@tenant AND author_id=@user AND julianday(created_at)>julianday(@since)",
            "SELECT COUNT(*) FROM bartide_enhanced_messages WHERE tenant_id=@tenant AND author_id=@user AND tide_iso_instant(created_at)>tide_iso_instant(@since)"), token,
                ("@tenant", id), ("@user", user.UserId), ("@since", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"))) >= 20)
                throw new TeamException("Please wait a minute before sending more messages.", 429, "message_rate_limited");
            await Run(db, tx, "INSERT INTO bartide_enhanced_messages(id,tenant_id,author_id,author,body,created_at) VALUES(@id,@tenant,@user,@author,@body,@now)", token,
                ("@id", request.RequestId), ("@tenant", id), ("@user", user.UserId), ("@author", a.DisplayName), ("@body", body), ("@now", Now()));
        }, ct);

    public Task<StaffTrainingWorkspace> SaveCourseAsync(string id, string? courseId, AuthUser user, SaveTrainingCourseRequest request, CancellationToken ct) =>
        ExecuteAsync(id, user, async (db, tx, a, token) =>
        {
            Manager(a); Required(request);
            var title = Text(request.Title, "Course title", 120); var description = Text(request.Description, "Course description", 2000, true, true);
            if (request.ExpectedVersion < 0) throw Stale();
            if (courseId is null)
            {
                if (request.Published) throw new TeamException("Save a draft and add a lesson before publishing.");
                if (await Count(db, tx, "SELECT COUNT(*) FROM fit_courses WHERE tenant_id=@tenant", token, ("@tenant", id)) >= 12)
                    throw new TeamException("This workspace supports up to 12 courses.", 409, "courses_full");
                await Run(db, tx, "INSERT INTO fit_courses(id,tenant_id,title,description,published,version,created_at,updated_at) VALUES(@id,@tenant,@title,@description,0,0,@now,@now)", token,
                    ("@id", Id()), ("@tenant", id), ("@title", title), ("@description", description), ("@now", Now()));
            }
            else
            {
                if (request.Published && await Count(db, tx, "SELECT COUNT(*) FROM fit_lessons WHERE tenant_id=@tenant AND course_id=@course", token, ("@tenant", id), ("@course", courseId)) == 0)
                    throw new TeamException("Add a lesson before publishing this course.", 409, "course_empty");
                if (await Run(db, tx, "UPDATE fit_courses SET title=@title,description=@description,published=@published,version=version+1,updated_at=@now WHERE id=@id AND tenant_id=@tenant AND version=@version", token,
                    ("@title", title), ("@description", description), ("@published", request.Published ? 1 : 0), ("@now", Now()), ("@id", courseId), ("@tenant", id), ("@version", request.ExpectedVersion)) != 1) throw Stale();
            }
        }, ct);

    public Task<StaffTrainingWorkspace> SaveLessonAsync(string id, string? lessonId, AuthUser user, SaveTrainingLessonRequest request, CancellationToken ct) =>
        ExecuteAsync(id, user, async (db, tx, a, token) =>
        {
            Manager(a); Required(request);
            var title = Text(request.Title, "Lesson title", 120); var description = Text(request.Description, "Lesson notes", 4000, true, true);
            if (request.Position is < 1 or > 999 || request.ExpectedVersion < 0) throw new TeamException("Choose a lesson position from 1 to 999 and refresh before saving.");
            string kind, source;
            if (!string.IsNullOrWhiteSpace(request.UploadedVideoId))
            {
                if (!string.IsNullOrWhiteSpace(request.VideoUrl) || !Guid.TryParseExact(request.UploadedVideoId, "D", out _)
                    || await Count(db, tx, "SELECT COUNT(*) FROM fit_videos WHERE id=@video AND tenant_id=@tenant AND status='ready'", token,
                        ("@video", request.UploadedVideoId), ("@tenant", id)) != 1)
                    throw new TeamException("Choose one ready video from this restaurant, or enter a YouTube or Vimeo link.");
                kind = "upload"; source = request.UploadedVideoId;
            }
            else (kind, source) = VideoLink(request.VideoUrl);
            if (await Count(db, tx, "SELECT COUNT(*) FROM fit_courses WHERE id=@course AND tenant_id=@tenant", token, ("@course", request.CourseId), ("@tenant", id)) != 1) throw Missing();
            if (lessonId is null)
            {
                if (await Count(db, tx, "SELECT COUNT(*) FROM fit_lessons WHERE tenant_id=@tenant AND course_id=@course", token, ("@tenant", id), ("@course", request.CourseId)) >= 40)
                    throw new TeamException("This course supports up to 40 lessons.", 409, "lessons_full");
                await Run(db, tx, "INSERT INTO fit_lessons(id,tenant_id,course_id,title,description,video_kind,video_source,position,version,created_at,updated_at) VALUES(@id,@tenant,@course,@title,@description,@kind,@source,@position,0,@now,@now)", token,
                    ("@id", Id()), ("@tenant", id), ("@course", request.CourseId), ("@title", title), ("@description", description), ("@kind", kind), ("@source", source), ("@position", request.Position), ("@now", Now()));
            }
            else if (await Run(db, tx, "UPDATE fit_lessons SET title=@title,description=@description,video_kind=@kind,video_source=@source,position=@position,version=version+1,updated_at=@now WHERE id=@id AND tenant_id=@tenant AND course_id=@course AND version=@version", token,
                ("@title", title), ("@description", description), ("@kind", kind), ("@source", source), ("@position", request.Position), ("@now", Now()), ("@id", lessonId), ("@tenant", id), ("@course", request.CourseId), ("@version", request.ExpectedVersion)) != 1) throw Stale();
        }, ct);

    public Task<StaffTrainingWorkspace> DeleteLessonAsync(string id, string lessonId, int version, AuthUser user, CancellationToken ct) =>
        ExecuteAsync(id, user, async (db, tx, a, token) =>
        {
            Manager(a);
            if (version < 0 || await Run(db, tx, "DELETE FROM fit_lessons WHERE id=@id AND tenant_id=@tenant AND version=@version AND EXISTS(SELECT 1 FROM fit_courses c WHERE c.id=fit_lessons.course_id AND c.tenant_id=@tenant AND c.published=0)", token,
                ("@id", lessonId), ("@tenant", id), ("@version", version)) != 1)
                throw new TeamException("Set the course to draft and refresh before removing a lesson.", 409, "lesson_changed");
        }, ct);

    public Task<StaffTrainingWorkspace> AssignAsync(string id, string courseId, AuthUser user, SetStaffCourseAssignmentRequest request, CancellationToken ct) =>
        ExecuteAsync(id, user, async (db, tx, a, token) =>
        {
            Manager(a); Required(request);
            if (request.ExpectedVersion < -1) throw Stale();
            if (request.Active) await ActiveMember(db, tx, id, request.MemberId, token);
            else if (await Count(db, tx, "SELECT COUNT(*) FROM bartide_enhanced_members WHERE tenant_id=@tenant AND id=@member", token, ("@tenant", id), ("@member", request.MemberId)) != 1) throw Missing();
            if (await Count(db, tx, "SELECT COUNT(*) FROM fit_courses WHERE id=@course AND tenant_id=@tenant", token, ("@course", courseId), ("@tenant", id)) != 1) throw Missing();
            var changed = request.ExpectedVersion == -1
                ? await Run(db, tx, "INSERT INTO tide_staff_course_assignments(tenant_id,member_id,course_id,active,version,assigned_at,updated_at) VALUES(@tenant,@member,@course,@active,0,@now,@now) ON CONFLICT(member_id,course_id) DO NOTHING", token,
                    ("@tenant", id), ("@member", request.MemberId), ("@course", courseId), ("@active", request.Active ? 1 : 0), ("@now", Now()))
                : await Run(db, tx, "UPDATE tide_staff_course_assignments SET active=@active,version=version+1,updated_at=@now WHERE tenant_id=@tenant AND member_id=@member AND course_id=@course AND version=@version", token,
                    ("@active", request.Active ? 1 : 0), ("@now", Now()), ("@tenant", id), ("@member", request.MemberId), ("@course", courseId), ("@version", request.ExpectedVersion));
            if (changed != 1) throw Stale();
        }, ct);

    public Task<StaffTrainingWorkspace> ProgressAsync(string id, string lessonId, AuthUser user, SetStaffLessonProgressRequest request, CancellationToken ct) =>
        ExecuteAsync(id, user, async (db, tx, a, token) =>
        {
            Required(request);
            if (a.MemberId is null) throw new TeamException("Progress belongs to the assigned employee. Use their own account to save it.", 403, "staff_required");
            if (await Count(db, tx, "SELECT COUNT(*) FROM fit_lessons l JOIN fit_courses c ON c.id=l.course_id AND c.tenant_id=l.tenant_id JOIN tide_staff_course_assignments x ON x.course_id=c.id AND x.tenant_id=c.tenant_id WHERE l.id=@lesson AND l.tenant_id=@tenant AND c.published=1 AND x.member_id=@member AND x.active=1", token,
                ("@lesson", lessonId), ("@tenant", id), ("@member", a.MemberId)) != 1) throw Missing();
            await Run(db, tx, "INSERT INTO tide_staff_lesson_progress(tenant_id,member_id,lesson_id,completed,updated_at) VALUES(@tenant,@member,@lesson,@completed,@now) ON CONFLICT(member_id,lesson_id) DO UPDATE SET completed=excluded.completed,updated_at=excluded.updated_at WHERE tide_staff_lesson_progress.tenant_id=excluded.tenant_id", token,
                ("@tenant", id), ("@member", a.MemberId), ("@lesson", lessonId), ("@completed", request.Completed ? 1 : 0), ("@now", Now()));
        }, ct);

    private async Task<StaffTrainingWorkspace> ExecuteAsync(string id, AuthUser user, Change? change, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        using var tx = db.BeginTransaction(deferred: false);
        var access = await Authorize(db, tx, id, user, ct);
        if (change is not null) await change(db, tx, access, ct);
        var result = await Workspace(db, tx, access, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    private static async Task<Access> Authorize(DbConnection db, DbTransaction tx, string id, AuthUser user, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(user.UserId) || string.IsNullOrWhiteSpace(user.Email)) throw new TeamException("Sign in again.", 401, "sign_in");
        var tenants = await Rows(db, tx, "SELECT c.name,c.user_id,c.status,c.vertical,e.settings_json FROM bartide_customers c LEFT JOIN bartide_enhanced_configs e ON e.tenant_id=c.id WHERE c.id=@tenant",
            r => new[] { r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? "{}" : r.GetString(4) }, ct, ("@tenant", id));
        if (tenants.Count != 1) throw Missing();
        var tenant = tenants[0];
        using var config = JsonDocument.Parse(tenant[4]);
        if (tenant[2] != "active" || tenant[3] != "bartide" || !config.RootElement.TryGetProperty("enabled", out var enabled) || enabled.ValueKind != JsonValueKind.True)
            throw new TeamException("This restaurant workspace is not active.", 403, "team_unavailable");
        var email = user.Email.Trim().ToLowerInvariant();
        if (tenant[1].Length == 0 && !user.IsPlatformOwner)
        {
            await Run(db, tx, db.Sql("UPDATE bartide_customers SET user_id=@user WHERE id=@tenant AND user_id IS NULL AND email=@email COLLATE NOCASE",
            "UPDATE bartide_customers SET user_id=@user WHERE id=@tenant AND user_id IS NULL AND translate(email,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz')=translate(@email,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz') COLLATE \"C\""), ct,
                ("@user", user.UserId), ("@tenant", id), ("@email", email));
            tenant[1] = (await Rows(db, tx, "SELECT user_id FROM bartide_customers WHERE id=@tenant", r => r.IsDBNull(0) ? "" : r.GetString(0), ct, ("@tenant", id)))[0];
        }
        if (user.IsPlatformOwner || tenant[1] == user.UserId) return new(id, tenant[0], true, null, tenant[0] + " owner", true);
        var member = await TenantStaffAccess.FindAsync(db, tx, id, user, ct);
        if (member is null) throw new TeamException("You do not have active access to this team.", 403, "team_forbidden");
        return new(id, tenant[0], member.Role == "manager", member.Id, member.Name);
    }

    private static async Task<StaffTrainingWorkspace> Workspace(DbConnection db, DbTransaction tx, Access a, CancellationToken ct)
    {
        var args = new (string, object?)[] { ("@tenant", a.TenantId), ("@manager", a.Manager ? 1 : 0), ("@member", a.MemberId) };
        var members = await Rows(db, tx, db.Sql("SELECT id,name,email,role,active,user_id FROM bartide_enhanced_members WHERE tenant_id=@tenant AND (@manager=1 OR active=1) ORDER BY name COLLATE NOCASE,id",
            "SELECT id,name,email,role,active,user_id FROM bartide_enhanced_members WHERE tenant_id=@tenant AND (@manager=1 OR active=1) ORDER BY translate(name,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz') COLLATE \"C\",id"), r => new TeamMember(r.GetString(0), r.GetString(1), a.Manager ? r.GetString(2) : null, r.GetString(3), r.ReadInt64(4) == 1, !r.IsDBNull(5)), ct, args);
        var shifts = await Rows(db, tx, db.Sql("SELECT id,member_id,starts_at,ends_at,label FROM bartide_enhanced_shifts WHERE tenant_id=@tenant AND julianday(ends_at)>julianday('now','-7 days') ORDER BY julianday(starts_at),id LIMIT 300",
            "SELECT id,member_id,starts_at,ends_at,label FROM bartide_enhanced_shifts WHERE tenant_id=@tenant AND tide_iso_instant(ends_at)>statement_timestamp()-interval '7 days' ORDER BY tide_iso_instant(starts_at) NULLS FIRST,id LIMIT 300"), r => new TeamShift(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)), ct, args);
        var messages = await Rows(db, tx, "SELECT id,author,body,created_at FROM (SELECT id,author,body,created_at FROM bartide_enhanced_messages WHERE tenant_id=@tenant ORDER BY created_at DESC,id DESC LIMIT 100) ORDER BY created_at,id", r => new TeamMessage(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)), ct, args);
        const string visibleCourse = "(@manager=1 OR (c.published=1 AND EXISTS(SELECT 1 FROM tide_staff_course_assignments x WHERE x.tenant_id=c.tenant_id AND x.course_id=c.id AND x.member_id=@member AND x.active=1)))";
        var courses = await Rows(db, tx, "SELECT c.id,c.title,c.description,c.published,c.version FROM fit_courses c WHERE c.tenant_id=@tenant AND " + visibleCourse + " ORDER BY c.created_at,c.id", r => new TrainingCourse(r.GetString(0), r.GetString(1), r.GetString(2), r.ReadInt64(3) == 1, r.ReadInt32(4)), ct, args);
        var lessons = await Rows(db, tx, "SELECT l.id,l.course_id,l.title,l.description,l.video_kind,l.video_source,l.position,l.version,v.id FROM fit_lessons l JOIN fit_courses c ON c.id=l.course_id AND c.tenant_id=l.tenant_id LEFT JOIN fit_videos v ON l.video_kind='upload' AND v.id=l.video_source AND v.tenant_id=l.tenant_id AND v.status='ready' WHERE l.tenant_id=@tenant AND " + visibleCourse + " ORDER BY l.course_id,l.position,l.created_at,l.id", r => new TrainingLesson(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.IsDBNull(8) ? SafeVideo(r.GetString(4), r.GetString(5)) : "/private-media/" + Uri.EscapeDataString(a.TenantId) + "/video/" + Uri.EscapeDataString(r.GetString(8)), r.ReadInt32(6), r.ReadInt32(7), r.IsDBNull(8) ? null : r.GetString(8)), ct, args);
        var assignments = await Rows(db, tx, "SELECT x.member_id,x.course_id,x.active,x.version FROM tide_staff_course_assignments x JOIN fit_courses c ON c.id=x.course_id AND c.tenant_id=x.tenant_id JOIN bartide_enhanced_members m ON m.id=x.member_id AND m.tenant_id=x.tenant_id WHERE x.tenant_id=@tenant AND (@manager=1 OR (x.member_id=@member AND x.active=1 AND c.published=1)) ORDER BY x.member_id,x.course_id", r => new StaffCourseAssignment(r.GetString(0), r.GetString(1), r.ReadInt64(2) == 1, r.ReadInt32(3)), ct, args);
        var progress = await Rows(db, tx, "SELECT p.member_id,p.lesson_id,p.completed,p.updated_at FROM tide_staff_lesson_progress p JOIN fit_lessons l ON l.id=p.lesson_id AND l.tenant_id=p.tenant_id JOIN fit_courses c ON c.id=l.course_id AND c.tenant_id=l.tenant_id JOIN bartide_enhanced_members m ON m.id=p.member_id AND m.tenant_id=p.tenant_id WHERE p.tenant_id=@tenant AND (@manager=1 OR (p.member_id=@member AND " + visibleCourse + ")) ORDER BY p.member_id,p.lesson_id", r => new StaffLessonProgress(r.GetString(0), r.GetString(1), r.ReadInt64(2) == 1, r.GetString(3)), ct, args);
        var videos = a.Manager ? await Rows(db, tx, "SELECT id,name FROM fit_videos WHERE tenant_id=@tenant AND status='ready' ORDER BY created_at DESC LIMIT 100", r => new TrainingVideoOption(r.GetString(0), r.GetString(1)), ct, args) : [];
        return new(a.TenantId, a.Name, a.Manager, a.MemberId, members, shifts, messages, courses, lessons, assignments, progress, videos, a.Owner);
    }

    private static string SafeVideo(string kind, string source)
    {
        if (kind is not ("youtube" or "vimeo")) return "";
        try { var link = VideoLink(source); return link.Kind == kind ? link.Source : ""; }
        catch (TeamException) { return ""; }
    }

    private static (string Kind, string Source) VideoLink(string value)
    {
        value = Text(value, "YouTube or Vimeo link", 2000);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 || !uri.IsDefaultPort)
            throw new TeamException("Use a secure YouTube or Vimeo video link.");
        var host = uri.IdnHost.ToLowerInvariant(); var path = uri.AbsolutePath; string? video = null;
        if (host is "youtube.com" or "www.youtube.com" or "m.youtube.com" or "youtube-nocookie.com" or "www.youtube-nocookie.com")
        {
            if (path == "/watch") video = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query).GetValueOrDefault("v").ToString();
            else { var match = Regex.Match(path, "^/(?:embed|shorts)/([A-Za-z0-9_-]{11})/?$"); if (match.Success) video = match.Groups[1].Value; }
        }
        else if (host == "youtu.be") video = path.TrimStart('/');
        if (video is not null && Regex.IsMatch(video, "^[A-Za-z0-9_-]{11}$")) return ("youtube", "https://www.youtube-nocookie.com/embed/" + video);
        if (host is "vimeo.com" or "www.vimeo.com" or "player.vimeo.com")
        {
            var match = Regex.Match(path, "^/(?:video/)?([0-9]{1,15})(?:/([a-fA-F0-9]{6,32}))?/?$");
            if (match.Success)
            {
                var hash = match.Groups[2].Success ? match.Groups[2].Value : Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query).GetValueOrDefault("h").ToString();
                if (hash.Length == 0 || Regex.IsMatch(hash, "^[a-fA-F0-9]{6,32}$")) return ("vimeo", "https://player.vimeo.com/video/" + match.Groups[1].Value + (hash.Length == 0 ? "" : "?h=" + hash));
            }
        }
        throw new TeamException("Use a direct YouTube or Vimeo video link.");
    }

    private static async Task ActiveMember(DbConnection db, DbTransaction tx, string tenant, string member, CancellationToken ct)
    {
        if (await Count(db, tx, "SELECT COUNT(*) FROM bartide_enhanced_members WHERE tenant_id=@tenant AND id=@member AND active=1 AND role IN('manager','bartender','server','kitchen','driver')", ct, ("@tenant", tenant), ("@member", member)) != 1)
            throw new TeamException("Choose an active employee on this team.", 400, "invalid_member");
    }
    private static void Manager(Access a) { if (!a.Manager) throw new TeamException("Business owner or manager access is required.", 403, "manager_required"); }
    private static void Required(object? request) { if (request is null) throw new TeamException("Enter the requested details."); }
    private static TeamException Missing() => new("This team item is unavailable.", 404, "not_found");
    private static TeamException Stale() => new("This item changed. Refresh before saving again.", 409, "team_changed");
    private static string Text(string? value, string label, int max, bool optional = false, bool multiline = false)
    {
        value = value?.Trim() ?? "";
        if (value.Length > max || (!optional && value.Length == 0) || value.Any(c => char.IsControl(c) && !(multiline && c is '\r' or '\n' or '\t')))
            throw new TeamException($"{label}: enter {(optional ? "up to" : "1–")}{max} characters.");
        return value;
    }
    private static DateTimeOffset Timestamp(string? value)
    {
        if (value is null || !Regex.IsMatch(value, "(?:Z|[+-][0-9]{2}:[0-9]{2})$") || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var result))
            throw new TeamException("Choose a valid date and time with its time zone.");
        return result.ToUniversalTime();
    }
    private static DbCommand Command(DbConnection db, DbTransaction tx, string sql, params (string Key, object? Value)[] args)
    {
        var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return cmd;
    }
    private static async Task<int> Run(DbConnection db, DbTransaction tx, string sql, CancellationToken ct, params (string Key, object? Value)[] args)
    { await using var cmd = Command(db, tx, sql, args); return await cmd.ExecuteNonQueryAsync(ct); }
    private static async Task<long> Count(DbConnection db, DbTransaction tx, string sql, CancellationToken ct, params (string Key, object? Value)[] args)
    { await using var cmd = Command(db, tx, sql, args); return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture); }
    private static async Task<List<T>> Rows<T>(DbConnection db, DbTransaction tx, string sql, Func<DbDataReader, T> map, CancellationToken ct, params (string Key, object? Value)[] args)
    {
        await using var cmd = Command(db, tx, sql, args); await using var reader = await cmd.ExecuteReaderAsync(ct); var rows = new List<T>();
        while (await reader.ReadAsync(ct)) rows.Add(map(reader));
        return rows;
    }
}
