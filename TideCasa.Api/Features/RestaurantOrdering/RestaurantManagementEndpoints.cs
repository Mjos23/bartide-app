using TideCasa.Api.Features.Authentication;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.RestaurantOrdering;

public static class RestaurantManagementEndpoints
{
    public static void MapRestaurantManagement(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/tenants/{id}").WithTags("Restaurant operations")
            .RequireAuthorization().WithMetadata(new TideCasa.Api.Infrastructure.ApiBodyLimit(64 * 1024))
            .AddEndpointFilter<OrderingRequestFilter>();
        group.MapGet("/menu", async (string id, HttpContext c, RestaurantOrderingStore s, CancellationToken ct) => Results.Ok(await s.EditorAsync(id, User(c), ct)));
        group.MapPost("/menu/profile", async (string id, SaveRestaurantProfileRequest r, HttpContext c, RestaurantOrderingStore s, CancellationToken ct) => Results.Ok(await s.SaveProfileAsync(id, User(c), r, ct)));
        group.MapPost("/menu/categories", async (string id, SaveRestaurantCategoryRequest r, HttpContext c, RestaurantOrderingStore s, CancellationToken ct) => Results.Ok(await s.SaveCategoryAsync(id, User(c), r, ct)));
        group.MapPost("/menu/items", async (string id, SaveRestaurantItemRequest r, HttpContext c, RestaurantOrderingStore s, CancellationToken ct) => Results.Ok(await s.SaveItemAsync(id, User(c), r, ct)));
        group.MapPost("/menu/categories/{entryId}/remove", async (string id, string entryId, RemoveRestaurantEntryRequest r, HttpContext c, RestaurantOrderingStore s, CancellationToken ct) => Results.Ok(await s.RemoveEntryAsync(id, User(c), entryId, true, r, ct)));
        group.MapPost("/menu/items/{entryId}/remove", async (string id, string entryId, RemoveRestaurantEntryRequest r, HttpContext c, RestaurantOrderingStore s, CancellationToken ct) => Results.Ok(await s.RemoveEntryAsync(id, User(c), entryId, false, r, ct)));
        group.MapGet("/ordering/settings", async (string id, HttpContext c, RestaurantOrderingStore s, CancellationToken ct) => Results.Ok(await s.SettingsAsync(id, User(c), ct)));
        group.MapPost("/ordering/settings", async (string id, SaveRestaurantOrderingSettingsRequest r, HttpContext c, RestaurantOrderingStore s, CancellationToken ct) => Results.Ok(await s.SaveSettingsAsync(id, User(c), r, ct)));
        group.MapGet("/ordering/operations", async (string id, HttpContext c, RestaurantOrderingStore s, CancellationToken ct) => Results.Ok(await s.OperationsAsync(id, User(c), ct)));
        group.MapPost("/ordering/orders/{orderId}", async (string id, string orderId, ChangeRestaurantOrderRequest r, HttpContext c, RestaurantOrderingStore s, CancellationToken ct) => Results.Ok(await s.ChangeOrderAsync(id, orderId, User(c), r, ct)));
    }
    private static AuthUser User(HttpContext context) => (AuthUser)context.Items[RegisteredBearerHandler.UserItem]!;
}
