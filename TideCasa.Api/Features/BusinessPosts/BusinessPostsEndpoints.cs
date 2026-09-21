using System.Text.Json;
using System.Data.Common;
using TideCasa.Api.Features.Authentication;
using TideCasa.Api.Infrastructure;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.BusinessPosts;

public static class BusinessPostsEndpoints
{
    public static void AddTideCasaBusinessPosts(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<BusinessPushOptions>();
        services.AddSingleton<BusinessPostsStore>();
        services.AddSingleton<BusinessPushSender>();
        services.AddHostedService<BusinessPushDispatcher>();
    }

    public static void MapTideCasaBusinessPosts(this WebApplication app)
    {
        var owner = app.MapGroup("/api/v1/tenants/{id}/posts").WithTags("Business updates").RequireAuthorization()
            .WithMetadata(new ApiBodyLimit(16 * 1024)).RequireRateLimiting("restaurant-ordering").AddEndpointFilter<BusinessPostsFilter>();
        owner.MapGet("", async (string id, HttpContext context, BusinessPostsStore store, CancellationToken ct) => Results.Ok(await store.WorkspaceAsync(id, User(context), ct)));
        owner.MapPost("", async (string id, CreateBusinessPostRequest request, HttpContext context, BusinessPostsStore store, CancellationToken ct) => Results.Ok(await store.CreateAsync(id, User(context), request, ct)));
        owner.MapPost("/{postId}/publish", async (string id, string postId, TransitionBusinessPostRequest request, HttpContext context, BusinessPostsStore store, CancellationToken ct) => Results.Ok(await store.TransitionAsync(id, postId, User(context), request, true, ct)));
        owner.MapPost("/{postId}/hide", async (string id, string postId, TransitionBusinessPostRequest request, HttpContext context, BusinessPostsStore store, CancellationToken ct) => Results.Ok(await store.TransitionAsync(id, postId, User(context), request, false, ct)));
        var guest = app.MapGroup("/api/v1/restaurants/{slug}/posts").WithTags("Business updates")
            .WithMetadata(new ApiBodyLimit(8 * 1024)).RequireRateLimiting("restaurant-ordering").AddEndpointFilter<BusinessPostsFilter>();
        guest.MapGet("", async (string slug, BusinessPostsStore store, CancellationToken ct) => Results.Ok(await store.FeedAsync(slug, ct)));
        guest.MapGet("/subscriptions/mine", async (string slug, HttpContext context, BusinessPostsStore store, CancellationToken ct) => Results.Ok(await store.MineAsync(slug, User(context), ct))).RequireAuthorization();
        guest.MapPost("/subscriptions", async (string slug, PushSubscriptionRequest request, HttpContext context, BusinessPostsStore store, CancellationToken ct) => Results.Ok(await store.SubscribeAsync(slug, User(context), request, ct))).RequireAuthorization();
        guest.MapDelete("/subscriptions/{subscriptionId}", async (string slug, string subscriptionId, HttpContext context, BusinessPostsStore store, CancellationToken ct) => { await store.UnsubscribeAsync(slug, subscriptionId, User(context), ct); return Results.NoContent(); }).RequireAuthorization();
    }
    private static AuthUser User(HttpContext context) => (AuthUser)context.Items[RegisteredBearerHandler.UserItem]!;
}

public sealed class BusinessPostsFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        try { return await next(context); }
        catch (BusinessPostFailure error) { return Results.Problem(statusCode: error.Status, title: error.Message, extensions: new Dictionary<string, object?> { ["code"] = error.Code }); }
        catch (Exception error) when (error is DbException or JsonException or InvalidOperationException or FormatException or OverflowException)
        { return Results.Problem(statusCode: 503, title: "Business updates are temporarily unavailable. Please try again.", extensions: new Dictionary<string, object?> { ["code"] = "posts_unavailable" }); }
    }
}
