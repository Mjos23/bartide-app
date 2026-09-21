using System.Globalization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using TideCasa.Blazor.Features.Authentication;
using TideCasa.Blazor.Services;
using TideCasa.Contracts;

namespace TideCasa.Blazor.Features.LaunchReview;

public static class LaunchReviewFlow
{
    public static void MapTideCasaLaunchReview(this WebApplication app) => app.MapPost("/owner/launch-review/{id}/transition", TransitionAsync);
    public static string? Notice(string? code) => code switch
    {
        "saved" => "The app's publication status was updated. No external deployment or billing change was made.",
        "launch_stale" => "This app changed. Reload and review it again before continuing.",
        "launch_not_ready" => "This app is not ready. Check its build dates and finished items below.",
        "launch_draft" => "A draft needs verified live setup payment before its build can begin.",
        "launch_review_required" => "Review the finished app with the customer and confirm approval before launch.",
        "launch_transition" => "That publication change is not available for this app.",
        "invalid" => "Refresh the form and choose a valid publication change.",
        "unavailable" => "The result could not be confirmed. Check the current status before trying again.", _ => null
    };
    private static async Task<IResult> TransitionAsync(string id, HttpContext context, IAntiforgery csrf, LaunchReviewClient api)
    {
        context.Response.Headers.CacheControl = "no-store"; context.Response.Headers["Referrer-Policy"] = "same-origin";
        if (context.Items.ContainsKey(AuthFlow.UnavailableItem)) return AuthFlow.ServiceUnavailable();
        if (context.User.Identity?.IsAuthenticated != true || context.Items[AuthFlow.TokenItem] is not string token) return Results.Unauthorized();
        if ((context.Items[AuthFlow.AccountItem] as AccountOverview)?.User.IsPlatformOwner != true) return Results.StatusCode(403);
        if (!SameOrigin(context.Request) || !context.Request.HasFormContentType) return Results.BadRequest();
        if (context.Request.ContentLength > 8192) return Results.StatusCode(413);
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = 8192;
        try
        {
            await csrf.ValidateRequestAsync(context); var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (form.Files.Count != 0 || form.Count > 4 || form.Any(x => x.Value.Count != 1)
                || !int.TryParse(form["version"], NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < 0
                || form["status"].ToString() is not ("active" or "paused")) return Back("invalid");
            var result = await api.TransitionAsync(id, new(version, form["status"].ToString(), form["reviewed"].ToString() == "true"), token, context.RequestAborted);
            if ((int)result.Status == 403) return Results.StatusCode(403);
            return Back(result.Succeeded ? "saved" : result.Code is "launch_stale" or "launch_not_ready" or "launch_draft" or "launch_review_required" or "launch_transition" ? result.Code : "unavailable");
        }
        catch (BadHttpRequestException error) when (error.StatusCode == 413) { return Results.StatusCode(413); }
        catch (Exception error) when (error is AntiforgeryValidationException or InvalidDataException or BadHttpRequestException) { return Results.BadRequest(); }
    }
    private static IResult Back(string? code) => Results.LocalRedirect("/owner/launch-review?notice=" + Uri.EscapeDataString(code ?? "unavailable"));
    private static bool SameOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString(); var candidate = origin.Length == 0 ? request.Headers.Referer.ToString() : origin;
        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.UserInfo.Length == 0 && uri.Scheme == request.Scheme && uri.IdnHost.Equals(request.Host.Host, StringComparison.OrdinalIgnoreCase)
            && uri.Port == (request.Host.Port ?? (request.Scheme == "https" ? 443 : 80)) && (origin.Length == 0 || uri.AbsolutePath == "/") && request.Headers["Sec-Fetch-Site"] != "cross-site";
    }
}
