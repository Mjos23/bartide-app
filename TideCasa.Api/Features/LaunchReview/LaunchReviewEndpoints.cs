using System.Data.Common;
using TideCasa.Api.Features.Authentication;
using TideCasa.Api.Infrastructure;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.LaunchReview;

public static class LaunchReviewEndpoints
{
    public static void AddTideCasaLaunchReview(this IServiceCollection services) => services.AddScoped<LaunchReviewStore>();
    public static void MapTideCasaLaunchReview(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/owner/launch-review").RequireAuthorization().WithTags("Launch review")
            .WithMetadata(new ApiBodyLimit(8 * 1024)).AddEndpointFilter<LaunchReviewFilter>();
        group.MapGet("", async (string? after, HttpContext c, LaunchReviewStore store, CancellationToken ct) => Results.Ok(await store.ListAsync(User(c), after, ct)));
        group.MapPost("/{id}/transition", async (string id, LaunchReviewTransitionRequest request, HttpContext c, LaunchReviewStore store, CancellationToken ct) => Results.Ok(await store.TransitionAsync(id, User(c), request, ct)));
    }
    private static AuthUser User(HttpContext context) => (AuthUser)context.Items[RegisteredBearerHandler.UserItem]!;
}
public sealed class LaunchReviewFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        try { return await next(context); }
        catch (LaunchReviewException error) { return Results.Problem(statusCode: error.Status, title: error.Message, extensions: new Dictionary<string, object?> { ["code"] = error.Code }); }
        catch (Exception error) when (error is DbException or InvalidOperationException or FormatException or OverflowException)
        { return Results.Problem(statusCode: 503, title: "Launch review is temporarily unavailable.", extensions: new Dictionary<string, object?> { ["code"] = "launch_unavailable" }); }
    }
}
