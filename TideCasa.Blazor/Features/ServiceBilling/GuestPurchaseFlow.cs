using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using TideCasa.Blazor.Features.Authentication;
using TideCasa.Blazor.Services;
using TideCasa.Contracts;

namespace TideCasa.Blazor.Features.ServiceBilling;

public static partial class ServiceBillingFlow
{
    private static string PurchaseCookie(HttpContext context) => context.Request.IsHttps ? "__Host-TideCasa.Purchase" : "TideCasa.Purchase";
    public static void UseGuestPurchaseCookie(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/purchase"))
            {
                context.Response.Headers.CacheControl = "no-store";
                // Preserve same-origin form verification while withholding the
                // checkout reference from Stripe and other external destinations.
                context.Response.Headers["Referrer-Policy"] = "same-origin";
                // Keep the purchase cookie, Stripe return and account on one origin.
                if (HttpMethods.IsGet(context.Request.Method) && context.Request.Host.Host.Equals("bar.tide.casa", StringComparison.OrdinalIgnoreCase))
                { context.Response.Redirect("https://tide.casa" + context.Request.Path + context.Request.QueryString); return; }
                var name = PurchaseCookie(context);
                if (HttpMethods.IsGet(context.Request.Method) && context.Request.Path.Value is "/purchase/business" or "/purchase/restaurant"
                    && !ValidPurchaseKey(context.Request.Cookies[name]))
                    context.Response.Cookies.Append(name, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
                        new CookieOptions { HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Lax, Path = "/", MaxAge = TimeSpan.FromDays(30), IsEssential = true });
            }
            await next();
        });
    }
    private static bool ValidPurchaseKey(string? key) => key is not null && Regex.IsMatch(key, "^[a-f0-9]{64}$");
    private static bool PurchaseReference(string order, string session) => Guid.TryParseExact(order, "D", out _) && Regex.IsMatch(session, "^cs_[A-Za-z0-9_]{1,240}$");
    private static async Task<IResult> RestartGuestAsync(HttpContext context, IAntiforgery antiforgery, ServiceBillingClient api)
    {
        var form = await Form(context, antiforgery); if (form is null) return Results.BadRequest();
        var plan = form["plan"].ToString(); var stores = form["appStores"].ToString(); var key = context.Request.Cookies[PurchaseCookie(context)];
        if (plan is not ("restaurant" or "business") || stores is not ("true" or "false") || !ValidPurchaseKey(key) || form["confirmed"] != "true") return Results.BadRequest();
        var result = await api.DiscardGuestAsync(new(key!), context.RequestAborted);
        if (result.Succeeded) context.Response.Cookies.Delete(PurchaseCookie(context), new CookieOptions { Path = "/", Secure = context.Request.IsHttps });
        return Results.LocalRedirect("/purchase/" + plan + "?appStores=" + stores + (result.Succeeded ? "" : "&notice=checkout_review"));
    }
    private static async Task<IResult> GuestCheckoutAsync(HttpContext context, IAntiforgery antiforgery, ServiceBillingClient api)
    {
        var form = await Form(context, antiforgery); if (form is null) return Results.BadRequest();
        var plan = form["plan"].ToString(); var stores = form["appStores"].ToString();
        if (plan is not ("business" or "restaurant") || stores is not ("true" or "false")) return Results.BadRequest();
        var back = "/purchase/" + plan + "?appStores=" + stores;
        var key = context.Request.Cookies[PurchaseCookie(context)];
        if (!ValidPurchaseKey(key) || form["acceptedTerms"] != "true" || form["termsVersion"].ToString().Length > 80) return Results.LocalRedirect(back + "&notice=confirmation");
        var result = await api.GuestCheckoutAsync(new(key!, plan, stores == "true", true, form["termsVersion"].ToString()), context.RequestAborted);
        if (result.Succeeded && result.Value is { } link)
        {
            if (!link.Completed && SafeCheckout(link.Url)) return Results.Redirect(link.Url);
            if (link.Completed && Regex.IsMatch(link.Url, "^/purchase/complete/[a-f0-9-]{36}/cs_[A-Za-z0-9_]{1,240}$")) return Results.LocalRedirect(link.Url);
        }
        var code = result.Code is "guest_options_saved" or "checkout_closed" or "checkout_review" ? result.Code : result.Uncertain ? "unconfirmed" : "invalid";
        return Results.LocalRedirect(back + "&notice=" + code);
    }
    private static async Task<IResult> ClaimGuestAsync(HttpContext context, IAntiforgery antiforgery, ServiceBillingClient api)
    {
        if (context.Items[AuthFlow.TokenItem] is not string token) return Results.Unauthorized();
        var form = await Form(context, antiforgery); if (form is null) return Results.BadRequest();
        var order = form["orderId"].ToString(); var session = form["sessionId"].ToString();
        if (!PurchaseReference(order, session)) return Results.BadRequest();
        var result = await api.ClaimGuestAsync(order, session, token, new(form["businessName"].ToString(), form["contactName"].ToString(), form["area"].ToString()), context.RequestAborted);
        if (result.Succeeded && result.Value is { } saved) return Results.LocalRedirect("/workspace/" + Uri.EscapeDataString(saved.TenantId) + "/billing");
        var notice = result.Code is "buyer_email_required" or "existing_workspace" or "payment_pending" ? result.Code : "unconfirmed";
        return Results.LocalRedirect("/purchase/complete/" + order + "/" + session + "?notice=" + notice);
    }
}
