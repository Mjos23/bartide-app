using Microsoft.AspNetCore.Antiforgery;
using TideCasa.Blazor.Features.Authentication;
using TideCasa.Blazor.Services;

namespace TideCasa.Blazor.Features.MerchantPayments;

public static class MerchantPaymentsFlow
{
    public static void MapTideCasaMerchantPayments(this WebApplication app)
    {
        app.MapPost("/merchant-payments/{tenant}/onboarding", OnboardAsync);
        app.MapPost("/merchant-payments/{tenant}/session", SessionAsync);
        app.MapGet("/merchant-payments/{tenant}/refresh", RefreshAsync);
    }
    public static string Page(string tenant) => "/workspace/" + Uri.EscapeDataString(tenant) + "/payments";
    public static string? Notice(string? value) => value switch
    {
        "invalid" => "Refresh the page, confirm the business details, and try again.",
        "unavailable" => "Stripe setup could not be opened. Your existing connection is saved; try again shortly.",
        "denied" => "Only this business’s owner can manage its payment connection.", _ => null
    };
    private static async Task<IResult> OnboardAsync(string tenant, HttpContext context, IAntiforgery csrf, MerchantPaymentsClient api)
    {
        Headers(context);
        if (context.Items.ContainsKey(AuthFlow.UnavailableItem)) return AuthFlow.ServiceUnavailable();
        if (context.User.Identity?.IsAuthenticated != true || context.Items[AuthFlow.TokenItem] is not string token) return Results.Unauthorized();
        if (!context.Request.HasFormContentType || !SameOrigin(context.Request)) return Results.BadRequest();
        try
        {
            await csrf.ValidateRequestAsync(context); var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (form.Count > 5 || form.Files.Count != 0 || form.Any(x => x.Value.Count != 1) || !Guid.TryParseExact(form["request_key"], "D", out _)
                || form["confirm_us"].ToString() != "true") return Results.LocalRedirect(Page(tenant) + "?notice=invalid");
            return Redirect(tenant, await api.OnboardAsync(tenant, new(form["request_key"].ToString(), true), token, context.RequestAborted));
        }
        catch (Exception error) when (error is AntiforgeryValidationException or InvalidDataException or BadHttpRequestException) { return Results.BadRequest(); }
    }
    private static async Task<IResult> RefreshAsync(string tenant, HttpContext context, MerchantPaymentsClient api)
    {
        Headers(context);
        if (context.Items.ContainsKey(AuthFlow.UnavailableItem)) return AuthFlow.ServiceUnavailable();
        if (context.User.Identity?.IsAuthenticated != true || context.Items[AuthFlow.TokenItem] is not string token)
            return Results.LocalRedirect("/signin?return_to=" + Uri.EscapeDataString("/merchant-payments/" + Uri.EscapeDataString(tenant) + "/refresh"));
        // Only refresh a previously bound account. A GET must not create one.
        var status = await api.StatusAsync(tenant, token, context.RequestAborted);
        if (!status.Succeeded || status.Value?.State is not ("ready" or "onboarding" or "restricted")) return Results.LocalRedirect(Page(tenant) + "?notice=unavailable");
        return Redirect(tenant, await api.OnboardAsync(tenant, new(Guid.NewGuid().ToString("D"), true), token, context.RequestAborted));
    }
    private static async Task<IResult> SessionAsync(string tenant, HttpContext context, IAntiforgery csrf, MerchantPaymentsClient api)
    {
        Headers(context);
        if (context.Items.ContainsKey(AuthFlow.UnavailableItem)) return AuthFlow.ServiceUnavailable();
        if (context.User.Identity?.IsAuthenticated != true || context.Items[AuthFlow.TokenItem] is not string token) return Results.Unauthorized();
        if (!context.Request.HasFormContentType || !SameOrigin(context.Request)) return Results.BadRequest();
        try
        {
            await csrf.ValidateRequestAsync(context); var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (form.Count > 5 || form.Files.Count != 0 || form.Any(x => x.Value.Count != 1) || !Guid.TryParseExact(form["request_key"], "D", out _)
                || form["confirm_us"].ToString() != "true") return Results.BadRequest();
            var result = await api.SessionAsync(tenant, new(form["request_key"].ToString(), true), token, context.RequestAborted);
            return result.Succeeded && result.Value is not null ? Results.Ok(result.Value) : Results.StatusCode((int)result.Status);
        }
        catch (Exception error) when (error is AntiforgeryValidationException or InvalidDataException or BadHttpRequestException) { return Results.BadRequest(); }
    }
    private static IResult Redirect(string tenant, RestaurantApiResult<TideCasa.Contracts.MerchantHostedLink> result)
    {
        if (result.Succeeded && Uri.TryCreate(result.Value?.Url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.IdnHost == "connect.stripe.com" && uri.UserInfo.Length == 0 && uri.IsDefaultPort) return Results.Redirect(uri.AbsoluteUri);
        return Results.LocalRedirect(Page(tenant) + "?notice=" + ((int)result.Status is 401 or 403 ? "denied" : "unavailable"));
    }
    private static void Headers(HttpContext context)
    { context.Response.Headers.CacheControl = "no-store"; context.Response.Headers.Pragma = "no-cache"; context.Response.Headers["Referrer-Policy"] = "same-origin"; context.Response.Headers["X-Frame-Options"] = "DENY"; }
    private static bool SameOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString(); var candidate = origin.Length == 0 ? request.Headers.Referer.ToString() : origin;
        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.UserInfo.Length == 0 && uri.Scheme == request.Scheme && uri.IdnHost.Equals(request.Host.Host, StringComparison.OrdinalIgnoreCase)
            && uri.Port == (request.Host.Port ?? (request.Scheme == "https" ? 443 : 80)) && (origin.Length == 0 || uri.AbsolutePath == "/") && request.Headers["Sec-Fetch-Site"] != "cross-site";
    }
}
