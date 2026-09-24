using TideCasa.Api.Features.Authentication;
using TideCasa.Api.Features.PublicDemo;
using TideCasa.Api.Infrastructure;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.RestaurantOrdering;

public static class DeliveryDispatchEndpoints
{
    public static void MapDeliveryDispatch(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/tenants/{id}/delivery-dispatch")
            .RequireAuthorization().WithMetadata(new ApiBodyLimit(16 * 1024))
            .RequireRateLimiting("delivery-location").AddEndpointFilter<OrderingRequestFilter>();
        group.MapGet("", async (string id, HttpContext c, RestaurantOrderingStore s, CancellationToken ct) =>
            Results.Ok(await s.DispatchWorkspaceAsync(id, User(c), ct)));
        group.MapPost("/settings", async (string id, SaveDeliveryDispatchSettings r, HttpContext c, RestaurantOrderingStore s, CancellationToken ct) =>
            Results.Ok(await s.SaveDispatchSettingsAsync(id, User(c), r, ct)));
        group.MapPost("/drivers/{driverId}", async (string id, string driverId, SaveDeliveryDriverProfile r, HttpContext c, RestaurantOrderingStore s, CancellationToken ct) =>
            Results.Ok(await s.SaveDriverProfileAsync(id, driverId, User(c), r, ct)));
        group.MapPost("/orders/{orderId}/plan", async (string id, string orderId, SaveDeliveryAssignmentPlan r, HttpContext c, RestaurantOrderingStore s, CancellationToken ct) =>
            Results.Ok(await s.SaveAssignmentPlanAsync(id, orderId, User(c), r, ct)));
        group.MapPost("/run", async (string id, HttpContext c, RestaurantOrderingStore s, CancellationToken ct) =>
            Results.Ok(new DeliveryDispatchResult(await s.DispatchNowAsync(id, User(c), ct))));
    }
    private static AuthUser User(HttpContext context) => (AuthUser)context.Items[RegisteredBearerHandler.UserItem]!;
}

public sealed class DeliveryDispatchWorker(IServiceScopeFactory scopes, IConfiguration config, IHostEnvironment environment,
    PublicDemoOptions demo, ILogger<DeliveryDispatchWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (environment.IsDevelopment() && !config.GetValue("DeliveryDispatch:WorkerEnabled", true)) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<RestaurantOrderingStore>();
                foreach (var tenant in await store.DispatchTenantsAsync(stoppingToken))
                {
                    if (demo.Enabled && tenant != PublicDemoOptions.Tenant) continue;
                    try { await store.DispatchPendingAsync(tenant, stoppingToken); }
                    catch (OrderingException) { /* Paused or unavailable venues stay unassigned. */ }
                    catch (Exception error) when (error is not OperationCanceledException)
                    { logger.LogWarning("Delivery dispatch could not finish a workspace; it will retry."); }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Delivery dispatch is temporarily unavailable; it will retry."); }
            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) break; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
