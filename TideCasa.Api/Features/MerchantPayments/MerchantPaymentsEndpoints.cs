using System.Text;
using System.Text.Json;
using System.Data.Common;
using Stripe;
using TideCasa.Api.Features.Authentication;
using TideCasa.Api.Features.RestaurantOrdering;
using TideCasa.Api.Infrastructure;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.MerchantPayments;

public static class MerchantPaymentsEndpoints
{
    public static void AddTideCasaMerchantPayments(this IServiceCollection services)
    {
        services.AddSingleton<MerchantPaymentOptions>(); services.AddSingleton<MerchantStripeProvider>();
        services.AddScoped<MerchantPaymentsStore>(); services.AddHostedService<MerchantRecovery>();
    }
    public static void MapTideCasaMerchantPayments(this WebApplication app)
    {
        var owners = app.MapGroup("/api/v1/tenants/{id}/payments/connect").WithTags("Restaurant card payments").RequireAuthorization().AddEndpointFilter<MerchantPaymentFilter>();
        owners.MapGet("", async (string id, HttpContext context, MerchantPaymentsStore store, CancellationToken ct) => Results.Ok(await store.StatusAsync(id, User(context), ct)));
        owners.MapPost("/onboarding", async (string id, StartMerchantOnboardingRequest request, HttpContext context, MerchantPaymentsStore store, CancellationToken ct) => Results.Ok(await store.OnboardAsync(id, User(context), request, ct)));
        var guests = app.MapGroup("/api/v1/restaurants/{slug}").WithTags("Restaurant card payments").RequireRateLimiting("restaurant-ordering").AddEndpointFilter<MerchantPaymentFilter>();
        guests.MapPost("/checkout", async (string slug, RestaurantOrderRequest request, HttpContext context, MerchantPaymentsStore store, CancellationToken ct) =>
            Results.Ok(await store.CheckoutAsync(slug, request, context.Connection.RemoteIpAddress?.ToString() ?? "unknown", ct)));
        guests.MapPost("/checkout/track", async (string slug, RestaurantTrackingRequest request, MerchantPaymentsStore store, CancellationToken ct) => Results.Ok(await store.TrackPaymentAsync(slug, request, false, ct)));
        guests.MapPost("/checkout/cancel", async (string slug, RestaurantTrackingRequest request, MerchantPaymentsStore store, CancellationToken ct) => Results.Ok(await store.TrackPaymentAsync(slug, request, true, ct)));
        app.MapPost("/api/stripe/connect/webhook", (HttpContext context, MerchantPaymentsStore store, CancellationToken ct) => Webhook("snapshot", context, store, ct)).WithMetadata(new ApiBodyLimit(256 * 1024));
        app.MapPost("/api/stripe/accounts/webhook", (HttpContext context, MerchantPaymentsStore store, CancellationToken ct) => Webhook("thin", context, store, ct)).WithMetadata(new ApiBodyLimit(256 * 1024));
    }
    private static async Task<IResult> Webhook(string type, HttpContext context, MerchantPaymentsStore store, CancellationToken ct)
    {
        if (context.Request.Headers["Stripe-Signature"].Count != 1 || context.Request.Headers["Stripe-Signature"].ToString().Length > 2048) return Results.BadRequest();
        try
        {
            using var reader = new StreamReader(context.Request.Body, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
            var body = await reader.ReadToEndAsync(ct);
            if (Encoding.UTF8.GetByteCount(body) > 256 * 1024) return Results.StatusCode(413);
            await store.AcceptNotificationAsync(type, body, context.Request.Headers["Stripe-Signature"].ToString(), ct);
            return Results.Accepted();
        }
        catch (StripeException) { return Results.BadRequest(); }
        catch (MerchantFailure error) { return Results.StatusCode(error.Status); }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or BadHttpRequestException) { return Results.BadRequest(); }
        catch (Exception error) when (error is DbException or InvalidOperationException or FormatException or OverflowException) { return Results.StatusCode(503); }
    }
    private static AuthUser User(HttpContext context) => (AuthUser)context.Items[RegisteredBearerHandler.UserItem]!;
}
public sealed class MerchantPaymentFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try { return await next(context); }
        catch (MerchantFailure error) { return Problem(error.Message, error.Status, error.Code); }
        catch (OrderingException error) { return Problem(error.Message, error.Status, error.Code); }
        catch (Exception error) when (error is StripeException or HttpRequestException or TaskCanceledException or DbException or JsonException or InvalidOperationException or FormatException or OverflowException)
        { return Problem("The payment result could not be confirmed. Keep this order and try again shortly.", 503, "merchant_unavailable"); }
    }
    private static IResult Problem(string message, int status, string code) => Results.Problem(statusCode: status, title: message, extensions: new Dictionary<string, object?> { ["code"] = code });
}
