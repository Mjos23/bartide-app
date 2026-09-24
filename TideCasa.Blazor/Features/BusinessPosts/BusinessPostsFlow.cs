using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using TideCasa.Blazor.Features.Authentication;
using TideCasa.Blazor.Services;
using TideCasa.Contracts;

namespace TideCasa.Blazor.Features.BusinessPosts;

public static class BusinessPostsFlow
{
    public static string Owner(string id) => "/workspace/" + Uri.EscapeDataString(id) + "/posts";
    public static string Public(string slug) => "/updates/" + Uri.EscapeDataString(slug);
    public static string Manifest(string slug) => Public(slug) + "/manifest.webmanifest";
    public static string Manage(string id) => "/post-management/" + Uri.EscapeDataString(id);
    public static string Subscriptions(string slug) => "/post-subscriptions/" + Uri.EscapeDataString(slug);
    public static void MapBusinessPostForms(this WebApplication app)
    {
        app.MapGet("/updates/{slug}/manifest.webmanifest", BusinessManifest);
        app.MapPost("/post-management/{tenant}/create", Create);
        app.MapPost("/post-management/{tenant}/{post}/{action}", Transition);
        app.MapPost("/post-subscriptions/{slug}/subscribe", Subscribe);
        app.MapPost("/post-subscriptions/{slug}/{id}/remove", Unsubscribe);
    }
    private static async Task<IResult> BusinessManifest(string slug, HttpContext context, BusinessPostsClient api)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!Identifier(slug)) return Results.NotFound();
        var response = await api.SendAsync<BusinessPostsFeed>(BusinessPostsClient.PublicPath(slug), HttpMethod.Get, null, null, context.RequestAborted);
        if ((int)response.Status == StatusCodes.Status404NotFound) return Results.NotFound();
        if (!response.Succeeded || response.Value is not { } feed || string.IsNullOrWhiteSpace(feed.Name) ||
            string.IsNullOrEmpty(feed.TenantId) || string.IsNullOrEmpty(feed.Slug) || !Identifier(feed.TenantId) || !Identifier(feed.Slug))
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        var icons = context.Request.Host.Host.Equals("bar.tide.casa", StringComparison.OrdinalIgnoreCase) ? "/app-icons/" : "/tide-casa/app-icons/";
        return Results.Json(new
        {
            id = "/business-app/" + Uri.EscapeDataString(feed.TenantId),
            name = feed.Name,
            short_name = feed.Name,
            start_url = Public(feed.Slug),
            scope = "/",
            display = "standalone",
            background_color = "#f7faf8",
            theme_color = "#123b48",
            prefer_related_applications = false,
            icons = new[]
            {
                new { src = icons + "icon-192.png", sizes = "192x192", type = "image/png", purpose = "any" },
                new { src = icons + "icon-512.png", sizes = "512x512", type = "image/png", purpose = "any" }
            }
        }, contentType: "application/manifest+json");
    }
    private static async Task<IResult> Create(string tenant, HttpContext context, IAntiforgery csrf, BusinessPostsClient api)
    {
        var read = await Read(context, csrf); if (read.Error is not null) return read.Error;
        if (!Identifier(tenant)) return Results.NotFound();
        try
        {
            var request = new CreateBusinessPostRequest(Key(read.Form!), Text(read.Form!, "title", 120, true), Text(read.Form!, "body", 2000, true, true));
            var response = await api.SendAsync<BusinessPost>(BusinessPostsClient.OwnerPath(tenant), HttpMethod.Post, request, read.Token, context.RequestAborted);
            return Redirect(Owner(tenant), response.Succeeded ? "draft" : Code(response));
        }
        catch (InvalidForm) { return Redirect(Owner(tenant), "invalid"); }
    }
    private static async Task<IResult> Transition(string tenant, string post, string action, HttpContext context, IAntiforgery csrf, BusinessPostsClient api)
    {
        var read = await Read(context, csrf); if (read.Error is not null) return read.Error;
        if (!Identifier(tenant) || !Guid.TryParseExact(post, "D", out _) || action is not ("publish" or "hide")) return Results.NotFound();
        try
        {
            if (action == "publish" && Text(read.Form!, "confirm_publish", 5) != "true") return Redirect(Owner(tenant), "confirm");
            if (!int.TryParse(read.Form!["version"], NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < 0) throw new InvalidForm();
            var request = new TransitionBusinessPostRequest(Key(read.Form!), version);
            var response = await api.SendAsync<BusinessPost>(BusinessPostsClient.OwnerPath(tenant, "/" + post + "/" + action), HttpMethod.Post, request, read.Token, context.RequestAborted);
            return Redirect(Owner(tenant), response.Succeeded ? action == "publish" ? "published" : "hidden" : Code(response));
        }
        catch (InvalidForm) { return Redirect(Owner(tenant), "invalid"); }
    }
    private static async Task<IResult> Subscribe(string slug, HttpContext context, IAntiforgery csrf, BusinessPostsClient api)
    {
        var read = await Read(context, csrf); if (read.Error is not null) return read.Error;
        if (!Identifier(slug)) return Results.NotFound();
        try
        {
            if (Text(read.Form!, "consent", 5) != "true") return Results.BadRequest();
            var request = new PushSubscriptionRequest(Text(read.Form!, "endpoint", 2048, true), Text(read.Form!, "p256dh", 150, true), Text(read.Form!, "auth", 80, true), true);
            var response = await api.SendAsync<PushSubscriptionInfo>(BusinessPostsClient.PublicPath(slug, "/subscriptions"), HttpMethod.Post, request, read.Token, context.RequestAborted);
            return response.Succeeded ? Results.Json(response.Value) : Results.Json(new { code = Code(response) }, statusCode: (int)response.Status);
        }
        catch (InvalidForm) { return Results.BadRequest(); }
    }
    private static async Task<IResult> Unsubscribe(string slug, string id, HttpContext context, IAntiforgery csrf, BusinessPostsClient api)
    {
        var read = await Read(context, csrf); if (read.Error is not null) return read.Error;
        if (!Identifier(slug) || !Guid.TryParseExact(id, "D", out _)) return Results.NotFound();
        var response = await api.SendAsync<object>(BusinessPostsClient.PublicPath(slug, "/subscriptions/" + id), HttpMethod.Delete, null, read.Token, context.RequestAborted);
        // Ordinary forms also provide a way to stop a device without running JavaScript.
        return Redirect(Public(slug), response.Succeeded ? "unsubscribed" : Code(response));
    }
    private static async Task<FormRead> Read(HttpContext context, IAntiforgery csrf)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (context.Items.ContainsKey(AuthFlow.UnavailableItem)) return new(null, null, AuthFlow.ServiceUnavailable());
        if (context.User.Identity?.IsAuthenticated != true || context.Items[AuthFlow.TokenItem] is not string token) return new(null, null, Results.Unauthorized());
        if (!SameOrigin(context.Request) || !context.Request.HasFormContentType) return new(null, null, Results.BadRequest());
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = 16 * 1024;
        try
        {
            await csrf.ValidateRequestAsync(context);
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (form.Files.Count != 0 || form.Count > 9 || form.Any(x => x.Value.Count != 1)) return new(null, null, Results.BadRequest());
            return new(form, token, null);
        }
        catch (Exception error) when (error is AntiforgeryValidationException or InvalidDataException or BadHttpRequestException) { return new(null, null, Results.BadRequest()); }
    }
    private static bool Identifier(string text) => Regex.IsMatch(text, "^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant);
    private static string Key(IFormCollection form) => Guid.TryParseExact(form["request_key"], "D", out _) ? form["request_key"].ToString() : throw new InvalidForm();
    private static string Text(IFormCollection form, string name, int max, bool required = false, bool multiline = false)
    {
        var value = form[name].ToString().Trim();
        if (value.Length > max || required && value.Length == 0 || value.Any(c => char.IsControl(c) && !(multiline && c is '\r' or '\n' or '\t'))) throw new InvalidForm();
        return value;
    }
    private static bool SameOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString(); var candidate = origin.Length == 0 ? request.Headers.Referer.ToString() : origin;
        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.UserInfo.Length == 0 && uri.Scheme == request.Scheme
            && uri.IdnHost.Equals(request.Host.Host, StringComparison.OrdinalIgnoreCase) && uri.Port == (request.Host.Port ?? (request.Scheme == "https" ? 443 : 80))
            && (origin.Length == 0 || uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0) && request.Headers["Sec-Fetch-Site"] != "cross-site";
    }
    private static IResult Redirect(string path, string notice) => Results.LocalRedirect(path + "?notice=" + notice);
    private static string Code<T>(PostsResult<T> response) => response.Code == "posts_limit" ? "limit" : response.Code == "push_unavailable" ? "unavailable"
        : response.Code == "subscription_conflict" ? "device-account" : response.Uncertain ? "uncertain" : (int)response.Status is 401 or 403 ? "denied" : response.Code switch
    {
        "post_changed" or "request_conflict" or "post_state" => "changed", "post_limit" or "publish_limit" or "subscription_limit" => "limit", "push_unavailable" or "push_disabled" => "unavailable", _ => "invalid"
    };
    public static string? Notice(string? code) => code switch
    {
        "draft" => "Draft saved. Review it below before publishing.",
        "published" => "Your update is published. Where phone notifications are enabled, delivery is queued for customers who have opted in.",
        "hidden" => "This update is hidden. Pending notifications are stopped; a notification already delivered cannot be recalled.",
        "unsubscribed" => "Updates from this business are turned off for that device.",
        "confirm" => "Confirm you want to publish this update and notify customers who opted in.",
        "changed" => "This post changed while the page was open. Review the current post before trying again.",
        "denied" => "Your account cannot make that change.",
        "limit" => "The update or device limit has been reached. Please try again later.",
        "unavailable" => "Phone notifications are not available here yet. You can still read updates on this page.",
        "uncertain" => "We couldn’t confirm that change. Refresh to check its status before trying again.",
        "invalid" => "Check the details and try again.", _ => null
    };
    public static string When(string? value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
        ? date.UtcDateTime.ToString("MMM d, yyyy · HH:mm 'UTC'", CultureInfo.GetCultureInfo("en-US")) : "";
    private sealed record FormRead(IFormCollection? Form, string? Token, IResult? Error);
    private sealed class InvalidForm : Exception;
}
