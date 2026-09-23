using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using TideCasa.Blazor.Features.Authentication;
using TideCasa.Blazor.Services;
using TideCasa.Contracts;

namespace TideCasa.Blazor.Features.EmailTracking;

public static class EmailTrackingFlow
{
    private const string Cookie = "TideCasa.EmailVisit";
    public const string OwnerApi = "api/v1/owner/email-campaigns";
    private static ITimeLimitedDataProtector Protector(HttpContext context) => context.RequestServices.GetRequiredService<IDataProtectionProvider>()
        .CreateProtector("TideCasa.EmailVisit.v1").ToTimeLimitedDataProtector();
    private static bool Hex(string? value, int length) => value?.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string? ReadVisit(HttpContext context)
    {
        if (!context.Request.Cookies.TryGetValue(Cookie, out var value) || value.Length > 1024) return null;
        try { return Protector(context).Unprotect(value); } catch (CryptographicException) { return null; }
    }
    private static bool PrivacyOptOut(HttpContext context) => context.Request.Headers["Sec-GPC"] == "1" || context.Request.Headers["DNT"] == "1";

    public static void UseEmailTracking(this WebApplication app)
    {
        if (app.Configuration["PublicDemo:Enabled"] == "true") return;
        app.Use(async (context, next) =>
        {
            if (HttpMethods.IsGet(context.Request.Method) && context.Request.Path == "/"
                && (app.Environment.IsDevelopment() || context.Request.Host.Host.Equals("bar.tide.casa", StringComparison.OrdinalIgnoreCase))
                && context.Request.Query.TryGetValue("campaign", out var value) && value.Count == 1 && Hex(value.ToString(), 32))
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["Referrer-Policy"] = "same-origin";
                var campaign = value.ToString();
                if (!PrivacyOptOut(context) && ReadVisit(context)?.StartsWith(campaign + ":", StringComparison.Ordinal) != true)
                {
                    // Tracking failure must never prevent the home page loading.
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                    timeout.CancelAfter(TimeSpan.FromSeconds(3));
                    var api = context.RequestServices.GetRequiredService<EmailTrackingClient>();
                    var result = await api.SendAsync<EmailVisit>("api/v1/email-events/visit", HttpMethod.Post, new StartEmailVisit(campaign), null, timeout.Token);
                    if (result.Succeeded && Hex(result.Value?.SessionId, 48))
                        context.Response.Cookies.Append(Cookie, Protector(context).Protect(campaign + ":" + result.Value!.SessionId, TimeSpan.FromMinutes(30)),
                            new CookieOptions { HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Lax, Path = "/", MaxAge = TimeSpan.FromMinutes(30), IsEssential = false });
                }
            }
            await next();
        });
    }

    public static void MapEmailTrackingForms(this WebApplication app)
    {
        if (app.Configuration["PublicDemo:Enabled"] == "true") return;
        app.MapPost("/email-events/click", async (HttpContext context, EmailTrackingClient api) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (PrivacyOptOut(context)) return Results.NoContent();
            if (!SameOrigin(context) || !context.Request.HasJsonContentType()) return Results.BadRequest();
            Limit(context, 1024);
            var visit = ReadVisit(context)?.Split(':');
            if (visit is not { Length: 2 } || !Hex(visit[0], 32) || !Hex(visit[1], 48)) return Results.NoContent();
            try
            {
                var click = await context.Request.ReadFromJsonAsync<BrowserClick>(context.RequestAborted);
                if (click?.Path is null || !EmailCampaignDefaults.Paths.ContainsKey(click.Path)) return Results.BadRequest();
                await api.SendAsync<EmailClickReceipt>("api/v1/email-events/click", HttpMethod.Post, new RecordEmailClick(visit[1], click.Path), null, context.RequestAborted);
                return Results.NoContent();
            }
            catch (Exception e) when (e is JsonException or InvalidDataException or BadHttpRequestException) { return Results.BadRequest(); }
        });
        app.MapPost("/owner/email-results/create", async (HttpContext context, IAntiforgery csrf, EmailTrackingClient api) =>
        {
            if (context.Items.ContainsKey(AuthFlow.UnavailableItem)) return AuthFlow.ServiceUnavailable();
            if ((context.Items[AuthFlow.AccountItem] as AccountOverview)?.User.IsPlatformOwner != true) return Results.Forbid();
            if (context.Items[AuthFlow.TokenItem] is not string token || !SameOrigin(context) || !context.Request.HasFormContentType) return Results.BadRequest();
            Limit(context, 4096);
            try
            {
                await csrf.ValidateRequestAsync(context);
                var form = await context.Request.ReadFormAsync(context.RequestAborted);
                if (form.Files.Count != 0 || form.Count > 4 || form.Any(x => x.Value.Count != 1 || x.Value.ToString().Length > 200)) return Results.BadRequest();
                var request = new CreateEmailCampaign(form["requestKey"].ToString(), form["name"].ToString(), form["mode"] == "test");
                if (form["mode"] != "test" && form["mode"] != "production") return Results.BadRequest();
                var result = await api.SendAsync<EmailCampaign>(OwnerApi, HttpMethod.Post, request, token, context.RequestAborted);
                if (result.Succeeded && result.Value is { } saved)
                    return Results.LocalRedirect("/owner/email-results?tests=" + saved.IsTest.ToString().ToLowerInvariant() + "&campaign=" + Uri.EscapeDataString(saved.Id) + "&notice=saved");
                return Results.LocalRedirect("/owner/email-results?notice=failed");
            }
            catch (Exception e) when (e is AntiforgeryValidationException or InvalidDataException or BadHttpRequestException)
            { return Results.LocalRedirect("/owner/email-results?notice=expired"); }
        }).RequireAuthorization();
    }
    private sealed record BrowserClick(string Path);
    private static void Limit(HttpContext context, long bytes)
    { if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } body) body.MaxRequestBodySize = bytes; }
    private static bool SameOrigin(HttpContext context)
    {
        var source = context.Request.Headers.Origin.ToString();
        return Uri.TryCreate(source, UriKind.Absolute, out var actual) && actual.UserInfo.Length == 0 && actual.AbsolutePath == "/"
            && Uri.TryCreate(context.Request.Scheme + "://" + context.Request.Host, UriKind.Absolute, out var expected)
            && actual.Scheme == expected.Scheme && actual.IdnHost == expected.IdnHost && actual.Port == expected.Port
            && context.Request.Headers["Sec-Fetch-Site"] != "cross-site";
    }
}
