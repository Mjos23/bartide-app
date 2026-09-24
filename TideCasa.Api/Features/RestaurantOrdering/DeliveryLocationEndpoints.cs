using TideCasa.Api.Features.Authentication;
using TideCasa.Api.Features.PublicDemo;
using TideCasa.Api.Infrastructure;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.RestaurantOrdering;

public static class DeliveryLocationEndpoints
{
    public static void MapDeliveryLocations(this WebApplication app)
    {
        var simulated = app.Services.GetRequiredService<PublicDemoOptions>().Enabled
            || app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("SampleBar:Enabled");
        var group=app.MapGroup("/api/v1/tenants/{id}/delivery-location").RequireAuthorization()
            .RequireRateLimiting("delivery-location").WithMetadata(new ApiBodyLimit(4096)).AddEndpointFilter<OrderingRequestFilter>().AddEndpointFilter(async (context,next) => { context.HttpContext.Response.Headers.CacheControl="no-store"; return await next(context); });
        group.MapGet("",async(string id,HttpContext c,RestaurantOrderingStore s,CancellationToken ct)=>Results.Ok(await s.LocationsAsync(id,User(c),simulated,ct)));
        group.MapPost("/settings",async(string id,DeliveryLocationSettings r,HttpContext c,RestaurantOrderingStore s,CancellationToken ct)=>Results.Ok(await s.SetLocationsAsync(id,User(c),r,simulated,ct)));
        group.MapPost("/{orderId}/start",async(string id,string orderId,DeliveryLocationStart r,HttpContext c,RestaurantOrderingStore s,CancellationToken ct)=>Results.Ok(await s.StartLocationAsync(id,orderId,User(c),r,simulated,ct)));
        group.MapPost("/{orderId}/point",async(string id,string orderId,DeliveryLocationUpdate r,HttpContext c,RestaurantOrderingStore s,CancellationToken ct)=>Results.Ok(await s.UpdateLocationAsync(id,orderId,User(c),r,simulated,ct)));
        group.MapPost("/{orderId}/stop",async(string id,string orderId,DeliveryLocationStop r,HttpContext c,RestaurantOrderingStore s,CancellationToken ct)=>Results.Ok(new {stopped=await s.StopLocationAsync(id,orderId,User(c),r,ct)}));
        app.MapPost("/api/v1/restaurants/{slug}/delivery-location",async(string slug,RestaurantTrackingRequest r,RestaurantOrderingStore s,CancellationToken ct)=>Results.Ok(new {location=await s.CustomerLocationAsync(slug,r,ct)}))
            .RequireRateLimiting("delivery-location").WithMetadata(new ApiBodyLimit(4096)).AddEndpointFilter<OrderingRequestFilter>().AddEndpointFilter(async (context,next) => { context.HttpContext.Response.Headers.CacheControl="no-store"; return await next(context); });
    }
    private static AuthUser User(HttpContext c)=>(AuthUser)c.Items[RegisteredBearerHandler.UserItem]!;
}

/// <summary>Latest point only; expires within five minutes plus the one-minute cleanup interval.</summary>
public sealed class DeliveryLocationCleanup(ApplicationDatabase database, ILogger<DeliveryLocationCleanup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromMinutes(1));
        while(await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var db=await database.OpenAsync(stoppingToken);using var tx=db.BeginTransaction(deferred:false);
                await using var q=db.CreateCommand();q.Transaction=tx;
                q.CommandText="""
                    DELETE FROM tide_delivery_locations WHERE received_at < @cutoff OR NOT EXISTS (
                      SELECT 1 FROM bartide_enhanced_orders o JOIN bartide_enhanced_members m ON m.tenant_id=o.tenant_id AND m.id=o.driver_id
                      WHERE o.tenant_id=tide_delivery_locations.tenant_id AND o.id=tide_delivery_locations.order_id
                        AND o.driver_id=tide_delivery_locations.driver_id AND o.status='out_for_delivery'
                        AND m.active=1 AND m.role='driver' AND m.user_id=tide_delivery_locations.user_id)
                    """;
                q.Parameters.AddWithValue("@cutoff",DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O"));
                await q.ExecuteNonQueryAsync(stoppingToken);await tx.CommitAsync(stoppingToken);
            }
            catch(Exception e) when(!stoppingToken.IsCancellationRequested && e is System.Data.Common.DbException or TimeoutException)
            { logger.LogWarning("Delivery location expiry will retry on the next interval."); }
        }
    }
}
