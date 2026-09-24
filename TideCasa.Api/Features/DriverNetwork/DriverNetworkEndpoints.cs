using TideCasa.Api.Features.Authentication;
using TideCasa.Api.Features.RestaurantOrdering;
using TideCasa.Api.Infrastructure;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.DriverNetwork;

public static class DriverNetworkEndpoints
{
    public static void MapDriverNetwork(this WebApplication app)
    {
        var drivers = app.MapGroup("/api/v1/drivers").RequireAuthorization()
            .RequireRateLimiting("delivery-location").WithMetadata(new ApiBodyLimit(16 * 1024))
            .AddEndpointFilter<OrderingRequestFilter>().AddEndpointFilter<AuthRequestFilter>()
            .AddEndpointFilter(async (c, next) => { c.HttpContext.Response.Headers.CacheControl = "no-store"; return await next(c); });
        drivers.MapGet("/me", async (HttpContext c, DriverNetworkStore s, CancellationToken ct) =>
            Results.Ok(await s.AccountAsync(User(c), ct)));
        drivers.MapPost("/me", async (SaveNetworkDriverProfile r, HttpContext c, DriverNetworkStore s, CancellationToken ct) =>
            Results.Ok(await s.SaveProfileAsync(User(c), r, ct)));
        drivers.MapPost("/hires/{hireId}/respond", async (string hireId, RespondNetworkDriverHire r, HttpContext c, DriverNetworkStore s, CancellationToken ct) =>
            Results.Ok(await s.RespondAsync(hireId, User(c), r, ct)));

        var clients = app.MapGroup("/api/v1/tenants/{id}/driver-network").RequireAuthorization()
            .RequireRateLimiting("delivery-location").WithMetadata(new ApiBodyLimit(16 * 1024))
            .AddEndpointFilter<OrderingRequestFilter>().AddEndpointFilter<AuthRequestFilter>()
            .AddEndpointFilter(async (c, next) => { c.HttpContext.Response.Headers.CacheControl = "no-store"; return await next(c); });
        clients.MapGet("", async (string id, HttpContext c, DriverNetworkStore s, CancellationToken ct) =>
            Results.Ok(await s.WorkspaceAsync(id, User(c), ct)));
        clients.MapPost("/offers", async (string id, OfferNetworkDriverHire r, HttpContext c, DriverNetworkStore s, CancellationToken ct) =>
            Results.Ok(await s.OfferAsync(id, User(c), r, ct)));
        clients.MapPost("/hires/{hireId}/end", async (string id, string hireId, EndNetworkDriverHire r, HttpContext c, DriverNetworkStore s, CancellationToken ct) =>
            Results.Ok(await s.EndAsync(id, hireId, User(c), r, ct)));
    }

    private static AuthUser User(HttpContext context) => (AuthUser)context.Items[RegisteredBearerHandler.UserItem]!;
}
