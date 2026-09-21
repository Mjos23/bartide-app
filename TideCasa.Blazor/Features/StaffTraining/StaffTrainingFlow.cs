using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using TideCasa.Blazor.Features.Authentication;
using TideCasa.Blazor.Services;
using TideCasa.Contracts;

namespace TideCasa.Blazor.Features.StaffTraining;

public static partial class StaffTrainingFlow
{
    public static IEndpointRouteBuilder MapStaffTrainingForms(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/team/manage/{tenantId}/{action}", HandleAsync).RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> HandleAsync(string tenantId, string action, HttpContext context, IAntiforgery antiforgery, StaffTrainingClient api)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "same-origin";
        if (!Identifier().IsMatch(tenantId)) return Results.NotFound();
        if (context.Items.ContainsKey(AuthFlow.UnavailableItem)) return AuthFlow.ServiceUnavailable();
        if (context.User.Identity?.IsAuthenticated != true || context.Items[AuthFlow.TokenItem] is not string token) return Results.Unauthorized();
        if (!SameOrigin(context.Request) || !context.Request.HasFormContentType) return Results.BadRequest();
        var body = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (body is { IsReadOnly: false }) body.MaxRequestBodySize = 64 * 1024;
        IFormCollection form;
        try
        {
            await antiforgery.ValidateRequestAsync(context);
            form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (form.Files.Count != 0 || form.Count > 12 || form.Any(f => f.Value.Count != 1)) return Results.BadRequest();
        }
        catch (Exception error) when (error is AntiforgeryValidationException or InvalidDataException or BadHttpRequestException)
        { return Redirect(tenantId, "expired"); }
        string Get(string name) => form[name].ToString().Trim();
        string IdentifierField(string name) => Identifier().IsMatch(Get(name)) ? Get(name) : throw new FormatException();
        int Number(string name, int fallback = 0) => string.IsNullOrEmpty(Get(name)) ? fallback : int.Parse(Get(name), CultureInfo.InvariantCulture);
        bool Flag(string name) => bool.Parse(Get(name));
        object? request; string suffix; var method = HttpMethod.Post;
        try
        {
            switch (action)
            {
                case "member": suffix = "/members"; request = new AddTeamMemberRequest(Get("name"), Get("email"), Get("role")); break;
                case "member-state": suffix = "/members/" + IdentifierField("id"); request = new SetTeamMemberStateRequest(Flag("expected_active"), Flag("active")); break;
                case "shift": suffix = "/shifts"; request = new AddTeamShiftRequest(IdentifierField("member"), ZonedTime(Get("starts"), Get("timezone")), ZonedTime(Get("ends"), Get("timezone")), Get("label")); break;
                case "shift-delete": suffix = "/shifts/" + IdentifierField("id"); request = null; method = HttpMethod.Delete; break;
                case "message": suffix = "/messages"; request = new SendTeamMessageRequest(Get("request_id"), Get("text")); break;
                case "course": suffix = "/courses" + (Get("id").Length == 0 ? "" : "/" + IdentifierField("id")); request = new SaveTrainingCourseRequest(Get("title"), Get("description"), Flag("published"), Number("version")); break;
                case "lesson": suffix = "/lessons" + (Get("id").Length == 0 ? "" : "/" + IdentifierField("id")); request = new SaveTrainingLessonRequest(IdentifierField("course"), Get("title"), Get("description"), Get("video"), Number("position"), Number("version"), Get("uploaded_video").Length == 0 ? null : IdentifierField("uploaded_video")); break;
                case "lesson-delete": suffix = "/lessons/" + IdentifierField("id") + "?expectedVersion=" + Number("version").ToString(CultureInfo.InvariantCulture); request = null; method = HttpMethod.Delete; break;
                case "assignment": suffix = "/courses/" + IdentifierField("course") + "/assignments"; request = new SetStaffCourseAssignmentRequest(IdentifierField("member"), Flag("active"), Number("version", -1)); break;
                case "progress": suffix = "/lessons/" + IdentifierField("id") + "/progress"; request = new SetStaffLessonProgressRequest(Flag("completed")); break;
                default: return Results.NotFound();
            }
        }
        catch (Exception error) when (error is FormatException or OverflowException or ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
        { return Redirect(tenantId, "invalid"); }
        var result = await api.SendAsync(tenantId, suffix, method, request, token, context.RequestAborted);
        return Redirect(tenantId, result.Succeeded ? "saved" : (int)result.Status is 401 or 403 ? "denied" : result.Uncertain ? "unconfirmed" : result.Code switch
        {
            "team_changed" or "lesson_changed" => "changed", "shift_overlap" => "overlap", "member_exists" => "member-exists",
            "course_empty" => "course-empty", "invalid_member" => "member-unavailable", _ => "invalid"
        });
    }

    private static string ZonedTime(string value, string zone)
    {
        if (zone is not ("America/New_York" or "America/Chicago" or "America/Denver" or "America/Los_Angeles" or "UTC")) throw new FormatException();
        if (!DateTime.TryParseExact(value, ["yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) throw new FormatException();
        var timezone = TimeZoneInfo.FindSystemTimeZoneById(zone);
        if (timezone.IsInvalidTime(local) || timezone.IsAmbiguousTime(local)) throw new FormatException();
        return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), timezone.GetUtcOffset(local)).ToUniversalTime().ToString("O");
    }

    public static string? Notice(string? value) => value switch
    {
        "saved" => "Your change is saved.",
        "expired" => "That form expired. Review the current page and try again.",
        "changed" => "This item changed while your page was open. Review it before trying again. To remove a lesson, first save its course as a draft.",
        "denied" => "Your account no longer has permission for that change.",
        "unconfirmed" => "We could not confirm the change. Check the current page before trying again.",
        "overlap" => "That employee already has a shift during those hours.",
        "member-exists" => "That sign-in email is already listed. Restore the existing team member's access instead.",
        "course-empty" => "Add at least one lesson before publishing this course.",
        "member-unavailable" => "Choose an active employee from this team.",
        "invalid" => "Check the form and try again. Use a valid video link, keep shifts between 15 minutes and 16 hours, and select the time zone. For a repeated or skipped daylight-saving hour, enter the equivalent time using UTC.",
        _ => null
    };
    private static IResult Redirect(string tenant, string notice) => Results.LocalRedirect("/workspace/" + Uri.EscapeDataString(tenant) + "/team?notice=" + notice);
    private static bool SameOrigin(HttpRequest request)
    {
        if (!Uri.TryCreate(request.Scheme + "://" + request.Host, UriKind.Absolute, out var expected)) return false;
        var origin = request.Headers.Origin.ToString(); var value = string.IsNullOrEmpty(origin) ? request.Headers.Referer.ToString() : origin;
        return Uri.TryCreate(value, UriKind.Absolute, out var source) && string.IsNullOrEmpty(source.UserInfo)
            && source.Scheme == expected.Scheme && source.IdnHost.Equals(expected.IdnHost, StringComparison.OrdinalIgnoreCase)
            && source.Port == expected.Port && (string.IsNullOrEmpty(origin) || source.AbsolutePath == "/")
            && request.Headers["Sec-Fetch-Site"] != "cross-site";
    }
    [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant)] private static partial Regex Identifier();
}
