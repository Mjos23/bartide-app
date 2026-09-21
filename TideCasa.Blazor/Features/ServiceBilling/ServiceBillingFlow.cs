using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using TideCasa.Blazor.Features.Authentication;
using TideCasa.Blazor.Services;
using TideCasa.Contracts;

namespace TideCasa.Blazor.Features.ServiceBilling;

public static partial class ServiceBillingFlow
{
    public static IEndpointRouteBuilder MapServiceBillingForms(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/service-billing/{tenant}/checkout", CheckoutAsync).RequireAuthorization();
        endpoints.MapPost("/service-billing/purchase/guest-checkout", GuestCheckoutAsync);
        endpoints.MapPost("/service-billing/purchase/restart", RestartGuestAsync);
        endpoints.MapPost("/service-billing/purchase/claim", ClaimGuestAsync).RequireAuthorization();
        endpoints.MapPost("/service-billing/{tenant}/orders/{orderId}/{action}", ActionAsync).RequireAuthorization();
        return endpoints;
    }
    private static async Task<IResult> CheckoutAsync(string tenant, HttpContext context, IAntiforgery antiforgery, ServiceBillingClient api)
    {
        if (!Identifier().IsMatch(tenant)) return Results.NotFound();
        if (context.Items.ContainsKey(AuthFlow.UnavailableItem)) return AuthFlow.ServiceUnavailable();
        if (context.Items[AuthFlow.TokenItem] is not string token) return Results.Unauthorized();
        var form = await Form(context, antiforgery); if (form is null) return Results.BadRequest();
        if (!Guid.TryParseExact(form["requestId"], "D", out _) || form["acceptedTerms"] != "true" || form["referralCode"].ToString().Length > 32
            || form["quoteFingerprint"].ToString().Length != 64 || form["termsVersion"].ToString().Length > 80) return Back(tenant, "invalid");
        var appStores = form["appStores"].ToString(); if (appStores is not ("true" or "false")) return Back(tenant, "invalid");
        var result = await api.CheckoutAsync(tenant, token, new(form["requestId"].ToString(), appStores == "true", form["referralCode"].ToString(), form["quoteFingerprint"].ToString(), true, form["termsVersion"].ToString()), context.RequestAborted);
        if (result.Succeeded && result.Value is { } link && SafeCheckout(link.Url)) return Results.Redirect(link.Url);
        return Back(tenant, Code(result.Code, result.Uncertain));
    }
    private static async Task<IResult> ActionAsync(string tenant, string orderId, string action, HttpContext context, IAntiforgery antiforgery, ServiceBillingClient api)
    {
        if (!Identifier().IsMatch(tenant) || !Guid.TryParseExact(orderId, "D", out _) || action is not ("cancel-renewal" or "discard-checkout" or "resume-checkout")) return Results.NotFound();
        if (context.Items.ContainsKey(AuthFlow.UnavailableItem)) return AuthFlow.ServiceUnavailable();
        if (context.Items[AuthFlow.TokenItem] is not string token) return Results.Unauthorized();
        var form = await Form(context, antiforgery); if (form is null) return Results.BadRequest();
        if (!Guid.TryParseExact(form["requestId"], "D", out _) || form["confirmed"] != "true") return Back(tenant, "confirmation");
        var request = new ServiceBillingActionRequest(form["requestId"].ToString(), true);
        if (action == "resume-checkout")
        {
            var resumed = await api.ResumeAsync(tenant, orderId, token, request, context.RequestAborted);
            if (resumed.Succeeded && resumed.Value is { } link && SafeCheckout(link.Url)) return Results.Redirect(link.Url);
            return Back(tenant, Code(resumed.Code, resumed.Uncertain));
        }
        var result = await api.ActionAsync(tenant, orderId, action, token, request, context.RequestAborted);
        return Back(tenant, result.Succeeded ? action == "cancel-renewal" ? "cancelled" : "discarded" : Code(result.Code, result.Uncertain));
    }
    private static async Task<IFormCollection?> Form(HttpContext context, IAntiforgery antiforgery)
    {
        if (!SameOrigin(context.Request) || !context.Request.HasFormContentType) return null;
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } body) body.MaxRequestBodySize = 8192;
        try
        {
            await antiforgery.ValidateRequestAsync(context); var form = await context.Request.ReadFormAsync(context.RequestAborted);
            return form.Files.Count == 0 && form.Count <= 10 && form.All(x => x.Value.Count == 1 && x.Value.ToString().Length <= 2048) ? form : null;
        }
        catch (Exception error) when (error is AntiforgeryValidationException or InvalidDataException or BadHttpRequestException) { return null; }
    }
    private static bool SafeCheckout(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == "checkout.stripe.com" && uri.IsDefaultPort && uri.UserInfo.Length == 0;
    private static IResult Back(string tenant, string notice) => Results.LocalRedirect("/workspace/" + Uri.EscapeDataString(tenant) + "/billing?notice=" + notice);
    private static string Code(string? code, bool uncertain) => code switch { "quote_changed" => "changed", "already_paid" => "paid", "historical_purchase" => "historical", "referral_unavailable" => "referral", "confirmation_required" or "terms_required" => "confirmation", "cards_unverified" => "unavailable", "checkout_review" => "review", _ => uncertain ? "unconfirmed" : "invalid" };
    public static string? Notice(string? code) => code switch
    {
        "cancelled" => "Renewal canceled. Your paid period continues; this does not request a refund.", "discarded" => "The unpaid checkout was discarded. You can review new options.",
        "changed" => "Your price or referral changed. Review the current quote before continuing.", "paid" => "Payment is already recorded. Your billing history is below.",
        "historical" => "An earlier payment is recorded. Contact hello@tide.casa before starting a new maintenance purchase.", "referral" => "That referral code is unavailable. Check it or remove it.",
        "confirmation" => "Please review and confirm the billing terms or action.", "unavailable" => "Card payments are still being verified. Please try again later.",
        "review" => "This saved checkout needs a billing review. Contact hello@tide.casa.", "unconfirmed" => "We could not confirm that change. Refresh your billing history before trying again.",
        "invalid" => "That billing change could not be completed. Refresh and review your details.", _ => null
    };
    private static bool SameOrigin(HttpRequest request)
    {
        if (!Uri.TryCreate(request.Scheme + "://" + request.Host, UriKind.Absolute, out var expected)) return false;
        var origin = request.Headers.Origin.ToString(); var value = origin.Length == 0 ? request.Headers.Referer.ToString() : origin;
        return Uri.TryCreate(value, UriKind.Absolute, out var actual) && actual.UserInfo.Length == 0 && actual.Scheme == expected.Scheme && actual.IdnHost.Equals(expected.IdnHost, StringComparison.OrdinalIgnoreCase)
            && actual.Port == expected.Port && (origin.Length == 0 || actual.AbsolutePath == "/") && request.Headers["Sec-Fetch-Site"] != "cross-site";
    }
    [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant)] private static partial Regex Identifier();
}
