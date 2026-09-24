using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using TideCasa.Blazor.Features.Authentication;
using TideCasa.Blazor.Services;
using TideCasa.Contracts;

namespace TideCasa.Blazor.Features.ClientOnboarding;

public static partial class ClientOnboardingFlow
{
    public static void MapClientOnboarding(this WebApplication app)
    {
        app.MapPost("/onboarding/{tenant}/{operation}", SaveAsync).RequireAuthorization();
        app.MapGet("/bar/{slug}/manifest.webmanifest", VenueManifest);
    }

    private static async Task<IResult> VenueManifest(string slug, HttpContext context, ClientOnboardingClient api)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!Regex.IsMatch(slug, "^[a-z0-9-]{1,200}$", RegexOptions.CultureInvariant)) return Results.NotFound();
        var result = await api.PublicAsync(slug, context.RequestAborted);
        if (!result.Succeeded || result.Value is not { } site) return Results.StatusCode(result.Uncertain ? 503 : 404);
        return Results.Json(new
        {
            id = "/restaurant-app/" + Uri.EscapeDataString(site.TenantId), name = site.Business.Name, short_name = site.Business.Name,
            start_url = "/bar/" + Uri.EscapeDataString(site.Slug), scope = "/", display = "standalone",
            background_color = "#f7faf8", theme_color = site.Brand.Accent, prefer_related_applications = false,
            icons = new[] {
                new { src = "/app-icons/icon-192.png", sizes = "192x192", type = "image/png", purpose = "any" },
                new { src = "/app-icons/icon-512.png", sizes = "512x512", type = "image/png", purpose = "any" }
            }
        }, contentType: "application/manifest+json");
    }
    public static string Page(string tenant) => "/workspace/" + Uri.EscapeDataString(tenant) + "/onboarding";
    public static string Preview(string tenant) => "/workspace/" + Uri.EscapeDataString(tenant) + "/preview";
    public static string? Notice(string? code) => code switch
    {
        "saved" => "Your changes are saved. You can leave and continue later.",
        "approved" => "Your approval is saved for this exact preview. BarTide will review it with you before publication, following your scheduled build period.",
        "stale" => "Someone changed this workspace or preview. Your submission was not saved. Review the latest details before submitting again.",
        "not-ready" => "This step needs attention. Review the checklist and finish the required details before continuing.",
        "invalid" => "These details were not saved. Check the field limits and required choices. Use your browser’s Back button to review what you entered.",
        "expired" => "Your form expired and was not saved. Refresh this page before trying again.",
        "unconfirmed" => "We couldn’t confirm this change. Check the saved details before trying again.", _ => null
    };

    private static async Task<IResult> SaveAsync(string tenant, string operation, HttpContext context, IAntiforgery csrf, ClientOnboardingClient api)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers["Referrer-Policy"] = "same-origin";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        if (!Identifier().IsMatch(tenant) || operation is not ("business" or "brand" or "service" or "team" or "build" or "approve" or "practice")) return Results.NotFound();
        if (context.Items.ContainsKey(AuthFlow.UnavailableItem)) return AuthFlow.ServiceUnavailable();
        if (context.Items[AuthFlow.TokenItem] is not string token || context.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
        if (!SameOrigin(context.Request) || !context.Request.HasFormContentType) return Results.BadRequest();
        if (context.Request.ContentLength > 64 * 1024) return Results.StatusCode(413);
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = 64 * 1024;
        IFormCollection? form = null;
        try
        {
            await csrf.ValidateRequestAsync(context);
            form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (form.Files.Count != 0 || form.Count > 70 || form.Any(field => field.Value.Count != 1)) return Results.BadRequest();
            var ct = context.RequestAborted;
            if (operation == "build")
            {
                var build = await api.BuildAsync(tenant, new(Revision(form)), token, ct);
                return build.Succeeded && build.Value is not null ? Results.LocalRedirect(Preview(tenant)) : Back(tenant, operation, Code(build), form);
            }
            if (operation == "approve")
            {
                var approval = await api.ApproveAsync(tenant, new(Text(form, "releaseId", 128, true), Text(form, "fingerprint", 128, true), Check(form, "confirmed")), token, ct);
                return Back(tenant, "launch", approval.Succeeded ? "approved" : Code(approval), form);
            }
            if (operation == "practice")
            {
                var fulfillment = Text(form, "fulfillment", 20, true);
                var request = new RestaurantQuoteRequest([new(Text(form, "itemId", 64, true), 1)], fulfillment, "staff",
                    DeliveryZip: fulfillment == "delivery" && Text(form, "deliveryZip", 5) is { Length: > 0 } zip ? zip : null,
                    TableLabel: fulfillment == "dine-in" && Text(form, "tableLabel", 40) is { Length: > 0 } table ? table : null);
                var practice = await api.PracticeAsync(tenant, new(Text(form, "releaseId", 128, true), Text(form, "fingerprint", 128, true), request), token, ct);
                return practice.Succeeded && practice.Value is not null ? PracticePage(tenant, practice.Value) : Back(tenant, "launch", Code(practice), form);
            }
            var revision = Revision(form);
            RestaurantApiResult<OnboardingWorkspace> result;
            if (operation == "business")
            {
                var hours = Enumerable.Range(0, 7).Select(day => new OnboardingHours(day, Check(form, "closed_" + day), Text(form, "opens_" + day, 5), Text(form, "closes_" + day, 5), Check(form, "overnight_" + day))).ToArray();
                result = await api.BusinessAsync(tenant, new(revision, new(Text(form, "name", 120, true), Text(form, "publicEmail", 254), Text(form, "phone", 30), Text(form, "address", 200),
                    Text(form, "city", 100), Text(form, "region", 2), Text(form, "postalCode", 10), Text(form, "country", 2), Text(form, "timeZone", 100), Text(form, "website", 2048), hours)), token, ct);
            }
            else if (operation == "brand")
                result = await api.BrandAsync(tenant, new(revision, new(Text(form, "style", 20), Text(form, "accent", 7), Text(form, "introduction", 250, multiline: true), Text(form, "logoChoice", 10), Optional(form, "logoPhotoId", 36), Optional(form, "coverPhotoId", 36))), token, ct);
            else if (operation == "service")
            {
                var ordering = Check(form, "ordering");
                result = await api.ServiceAsync(tenant, new(revision, new(ordering, ordering && Check(form, "dineIn"), ordering && Check(form, "pickup"), ordering && Check(form, "delivery"),
                    ordering && Check(form, "payStaff"), ordering && Check(form, "requestCards"), Check(form, "tips"), Amount(form, "taxPercent", 2500, true), Lines(form, "tableLabels", 4200, false), Lines(form, "deliveryZips", 500, true),
                    Amount(form, "deliveryFee", 5000) ?? 0, Amount(form, "deliveryMinimum", 100000) ?? 0, Number(form, "deliveryCapacity", 1, 30), Text(form, "pickupInstructions", 500, multiline: true), Text(form, "paymentInstructions", 500, multiline: true))), token, ct);
            }
            else
                result = await api.TeamAsync(tenant, new(revision, new(Check(form, "ownerHandlesOrders"), Text(form, "orderContact", 254), Check(form, "eventsLater"), Check(form, "rewardsLater"), Check(form, "trainingLater"))), token, ct);
            return Back(tenant, operation, result.Succeeded ? "saved" : Code(result), form);
        }
        catch (AntiforgeryValidationException) { return Back(tenant, operation, "expired", null); }
        catch (BadHttpRequestException error) when (error.StatusCode == 413) { return Results.StatusCode(413); }
        catch (Exception error) when (error is InvalidDataException or BadHttpRequestException or FormException) { return Back(tenant, operation, "invalid", form); }
    }

    private static IResult Back(string tenant, string section, string notice, IFormCollection? form)
    {
        var stores = form?["appStores"].ToString() == "true";
        var referral = form?["referralCode"].ToString() ?? "";
        if (referral.Length > 32) referral = "";
        return Results.LocalRedirect(Page(tenant) + "?notice=" + notice + "&appStores=" + stores.ToString().ToLowerInvariant() + "&referralCode=" + Uri.EscapeDataString(referral) + "#" + (section is "build" or "approve" or "practice" ? "launch" : section));
    }
    private static string Code<T>(RestaurantApiResult<T> result) => (int)result.Status == 409
        ? result.Code?.Contains("stale", StringComparison.Ordinal) == true || result.Code?.Contains("changed", StringComparison.Ordinal) == true ? "stale" : "not-ready"
        : result.Uncertain ? "unconfirmed" : "invalid";
    private static int Revision(IFormCollection form) => Number(form, "revision", 0, int.MaxValue);
    private static int Number(IFormCollection form, string key, int min, int max) => int.TryParse(form[key], NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max ? value : throw new FormException();
    private static bool Check(IFormCollection form, string key) => form[key].ToString() == "true";
    private static string? Optional(IFormCollection form, string key, int max) => Text(form, key, max) is { Length: > 0 } value ? value : null;
    private static string Text(IFormCollection form, string key, int max, bool required = false, bool multiline = false)
    {
        var value = form[key].ToString().Trim();
        if (value.Length > max || required && value.Length == 0 || value.Any(c => char.IsControl(c) && !(multiline && c is '\r' or '\n' or '\t'))) throw new FormException();
        return value;
    }
    private static string[] Lines(IFormCollection form, string key, int max, bool commas) => Text(form, key, max, multiline: true)
        .Split(commas ? [',', ' ', '\r', '\n', '\t'] : ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static int? Amount(IFormCollection form, string key, int max, bool optional = false)
    {
        var raw = Text(form, key, 20);
        if (raw.Length == 0) return optional ? null : 0;
        if (!decimal.TryParse(raw, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) || value < 0 || value * 100 > max || decimal.Truncate(value * 100) != value * 100) throw new FormException();
        return (int)(value * 100);
    }
    private static IResult PracticePage(string tenant, OnboardingPracticeResult result)
    {
        static string E(string value) => HtmlEncoder.Default.Encode(value);
        static string Money(int value) => (value / 100m).ToString("C2", CultureInfo.GetCultureInfo("en-US"));
        var quote = result.Quote;
        var lines = string.Join("", quote.Lines.Select(line => "<li><span>" + line.Quantity + " × " + E(line.Name) + "</span><strong>" + Money(line.Quantity * line.UnitCents) + "</strong></li>"));
        return Results.Content("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><meta name=\"robots\" content=\"noindex,nofollow\"><title>Practice order | BarTide</title><link rel=\"stylesheet\" href=\"/client-onboarding.css\"></head><body class=\"onboarding-practice\"><main class=\"onboarding-shell\"><p class=\"onboarding-eyebrow\">PRIVATE PRACTICE</p><h1>Your practice order.</h1><p>" + E(result.Message) + "</p><section class=\"onboarding-card\"><h2>Guest order summary</h2><ul class=\"practice-lines\">" + lines + "</ul><dl class=\"practice-totals\"><div><dt>Subtotal</dt><dd>" + Money(quote.SubtotalCents) + "</dd></div><div><dt>Tax</dt><dd>" + Money(quote.TaxCents) + "</dd></div><div><dt>Delivery</dt><dd>" + Money(quote.DeliveryFeeCents) + "</dd></div><div><dt>Total</dt><dd>" + Money(quote.TotalCents) + "</dd></div></dl><p>No live order was sent and no payment was collected.</p></section><p><a class=\"onboarding-button\" href=\"" + E(Preview(tenant)) + "\">Back to private preview</a></p></main></body></html>", "text/html; charset=utf-8");
    }
    private static bool SameOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString(); var candidate = origin.Length == 0 ? request.Headers.Referer.ToString() : origin;
        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.UserInfo.Length == 0 && uri.Scheme == request.Scheme && uri.IdnHost.Equals(request.Host.Host, StringComparison.OrdinalIgnoreCase)
            && uri.Port == (request.Host.Port ?? (request.Scheme == "https" ? 443 : 80)) && (origin.Length == 0 || uri.AbsolutePath == "/") && request.Headers["Sec-Fetch-Site"] != "cross-site";
    }
    private sealed class FormException : Exception { }
    [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant)] private static partial Regex Identifier();
}
