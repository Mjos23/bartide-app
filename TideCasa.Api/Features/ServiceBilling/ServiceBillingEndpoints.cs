using System.Text;
using System.Text.Json;
using System.Data.Common;
using Stripe;
using TideCasa.Api.Features.Authentication;
using TideCasa.Api.Infrastructure;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.ServiceBilling;

public static class ServiceBillingEndpoints
{
    public static IServiceCollection AddTideCasaServiceBilling(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddSingleton<ServiceBillingProvider>(); services.AddScoped<ServiceBillingStore>(); services.AddHostedService<ServiceBillingInboxWorker>();
        return services;
    }
    public static void MapTideCasaServiceBilling(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/tenants/{tenant}/billing").RequireAuthorization().WithTags("Service billing").AddEndpointFilter<ServiceBillingFilter>();
        group.MapGet("", async (string tenant, HttpContext context, ServiceBillingStore store, CancellationToken ct) => Results.Ok(await store.WorkspaceAsync(tenant, User(context), ct)));
        group.MapPost("/quote", async (string tenant, ServiceQuoteRequest request, HttpContext context, ServiceBillingStore store, CancellationToken ct) => Results.Ok(await store.QuoteAsync(tenant, User(context), request, ct)));
        group.MapPost("/checkout", async (string tenant, ServiceCheckoutRequest request, HttpContext context, ServiceBillingStore store, CancellationToken ct) => Results.Ok(await store.CheckoutAsync(tenant, User(context), request, ct)));
        group.MapPost("/orders/{orderId}/resume-checkout", async (string tenant, string orderId, ServiceBillingActionRequest request, HttpContext context, ServiceBillingStore store, CancellationToken ct) => Results.Ok(await store.ResumeCheckoutAsync(tenant, orderId, User(context), request, ct)));
        group.MapPost("/orders/{orderId}/cancel-renewal", async (string tenant, string orderId, ServiceBillingActionRequest request, HttpContext context, ServiceBillingStore store, CancellationToken ct) => Results.Ok(await store.ActionAsync(tenant, orderId, "cancel", User(context), request, ct)));
        group.MapPost("/orders/{orderId}/discard-checkout", async (string tenant, string orderId, ServiceBillingActionRequest request, HttpContext context, ServiceBillingStore store, CancellationToken ct) => Results.Ok(await store.ActionAsync(tenant, orderId, "discard", User(context), request, ct)));
        app.MapPost("/api/v1/webhooks/stripe/service", async (HttpContext context, ServiceBillingStore store, CancellationToken ct) =>
        {
            if (context.Request.Headers["Stripe-Signature"].Count != 1 || context.Request.Headers["Stripe-Signature"].ToString().Length > 4096) return Results.BadRequest();
            using var body = new MemoryStream(); var buffer = new byte[16384];
            for (;;) { var read = await context.Request.Body.ReadAsync(buffer, ct); if (read == 0) break; if (body.Length + read > 256 * 1024) return Results.StatusCode(413); body.Write(buffer, 0, read); }
            var raw = new UTF8Encoding(false, true).GetString(body.ToArray());
            await store.AcceptWebhookAsync(raw, context.Request.Headers["Stripe-Signature"].ToString(), ct);
            return Results.Ok(new { received = true });
        }).WithMetadata(new ApiBodyLimit(256 * 1024)).AddEndpointFilter<ServiceBillingFilter>();
    }
    private static AuthUser User(HttpContext context) => (AuthUser)context.Items[RegisteredBearerHandler.UserItem]!;
}

public sealed class ServiceBillingFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try { return await next(context); }
        catch (BillingException error) { return Problem(error.Status, error.Message, error.Code); }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or ArgumentException or FormatException)
        { return Problem(400, "Check the billing request and try again.", "invalid_billing_request"); }
        catch (Exception error) when (error is DbException or StripeException or HttpRequestException or OperationCanceledException or InvalidOperationException or OverflowException)
        { return Problem(503, "Billing is temporarily unavailable. Check your saved billing history before trying again.", "billing_unavailable"); }
    }
    private static IResult Problem(int status, string title, string code) => Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code });
}

public sealed class ServiceBillingInboxWorker(IServiceScopeFactory scopes, ServiceBillingProvider provider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                if (!provider.Ready) continue;
                using var scope = scopes.CreateScope(); await scope.ServiceProvider.GetRequiredService<ServiceBillingStore>().RetryPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { /* Durable inbox work remains pending; never log provider payloads. */ }
        }
    }
}
