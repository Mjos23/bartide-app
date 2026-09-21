using System.Text.Json;
using System.Data.Common;
using QRCoder;
using TideCasa.Api.Features.Authentication;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.RestaurantOrdering;

public static class RestaurantOrderingEndpoints
{
    public static void MapRestaurantOrdering(this WebApplication app)
    {
        var guest = app.MapGroup("/api/v1/restaurants/{slug}").WithTags("Restaurant ordering")
            .RequireRateLimiting("restaurant-ordering").AddEndpointFilter<OrderingRequestFilter>();
        guest.MapGet("/menu", async (string slug, string? table, RestaurantOrderingStore store, CancellationToken ct) =>
            Results.Ok(await store.MenuAsync(slug, table, ct)));
        guest.MapPost("/quote", async (string slug, RestaurantQuoteRequest request, RestaurantOrderingStore store, CancellationToken ct) =>
            Results.Ok(await store.QuoteAsync(slug, request, ct)));
        guest.MapPost("/orders", async (string slug, RestaurantOrderRequest request, RestaurantOrderingStore store, HttpContext context, CancellationToken ct) =>
        {
            var result = await store.PlaceAsync(slug, request, context.Connection.RemoteIpAddress?.ToString() ?? "unknown", ct);
            return Results.Json(result.Receipt, statusCode: result.Created ? 201 : 200);
        });
        guest.MapPost("/track", async (string slug, RestaurantTrackingRequest request, RestaurantOrderingStore store, CancellationToken ct) =>
            Results.Ok(await store.TrackAsync(slug, request, ct)));
        guest.MapGet("/tables/{token}/qr", async (string slug, string token, RestaurantOrderingStore store, IConfiguration configuration, IHostEnvironment environment, CancellationToken ct) =>
        {
            var menu = await store.MenuAsync(slug, token, ct);
            if (menu.Table is null) throw new OrderingException("Table not found.", 404, "not_found");
            if (!Uri.TryCreate(configuration["Ordering:PublicBaseUrl"], UriKind.Absolute, out var origin)
                || !string.IsNullOrEmpty(origin.UserInfo) || !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment)
                || origin.AbsolutePath != "/" || (origin.Scheme != "https" && !(environment.IsDevelopment() && origin.Scheme == "http" && origin.IsLoopback)))
                throw new OrderingException("Table QR codes are not available yet.", 503, "qr_unavailable");
            var url = origin.AbsoluteUri.TrimEnd('/') + "/order/" + Uri.EscapeDataString(menu.Slug) + "?table=" + Uri.EscapeDataString(menu.Table.Token);
            using var data = QRCodeGenerator.GenerateQrCode(url, QRCodeGenerator.ECCLevel.Q);
            using var renderer = new SvgQRCode(data);
            return Results.Content(renderer.GetGraphic(8), "image/svg+xml; charset=utf-8");
        });

        var owner = app.MapGroup("/api/v1/tenants/{id}/ordering").WithTags("Restaurant management")
            .RequireAuthorization().AddEndpointFilter<AuthRequestFilter>().AddEndpointFilter<OrderingRequestFilter>();
        owner.MapGet("", async (string id, HttpContext context, RestaurantOrderingStore store, CancellationToken ct) =>
            Results.Ok(await store.WorkspaceAsync(id, User(context), ct)));
        owner.MapPost("/tables", async (string id, CreateRestaurantTableRequest request, HttpContext context, RestaurantOrderingStore store, CancellationToken ct) =>
            Results.Ok(await store.CreateTableAsync(id, User(context), request, ct)));
        owner.MapPost("/tables/{tableId}", async (string id, string tableId, SetRestaurantTableStateRequest request, HttpContext context, RestaurantOrderingStore store, CancellationToken ct) =>
            Results.Ok(await store.SetTableAsync(id, tableId, User(context), request, ct)));
    }

    private static AuthUser User(HttpContext context) => (AuthUser)context.Items[RegisteredBearerHandler.UserItem]!;
}

public sealed class OrderingRequestFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try { return await next(context); }
        catch (OrderingException error)
        {
            return Results.Problem(statusCode: error.Status, title: error.Message, extensions: new Dictionary<string, object?> { ["code"] = error.Code });
        }
        catch (Exception error) when (error is DbException or JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            return Results.Problem(statusCode: 503, title: "Restaurant ordering is temporarily unavailable. Please try again shortly.",
                extensions: new Dictionary<string, object?> { ["code"] = "ordering_unavailable" });
        }
    }
}
