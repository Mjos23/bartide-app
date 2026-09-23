using System.Data.Common;
using TideCasa.Api.Features.Authentication;
using TideCasa.Api.Infrastructure;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.EmailTracking;

public static class EmailTrackingEndpoints
{
    public static void MapEmailTracking(this WebApplication app)
    {
        var owner = app.MapGroup("/api/v1/owner/email-campaigns").RequireAuthorization().RequireRateLimiting("restaurant-ordering")
            .WithMetadata(new ApiBodyLimit(4096)).AddEndpointFilter<EmailTrackingFilter>();
        owner.MapGet("", async (HttpContext c, EmailTrackingStore store, CancellationToken ct, bool tests = false, string? campaign = null) =>
            Results.Ok(await store.ReportAsync((AuthUser)c.Items[RegisteredBearerHandler.UserItem]!, tests, campaign, ct)));
        owner.MapPost("", async (CreateEmailCampaign request, HttpContext c, EmailTrackingStore store, CancellationToken ct) =>
            Results.Ok(await store.CreateAsync((AuthUser)c.Items[RegisteredBearerHandler.UserItem]!, request, ct)));
        var events = app.MapGroup("/api/v1/email-events").RequireRateLimiting("public-form").WithMetadata(new ApiBodyLimit(1024)).AddEndpointFilter<EmailTrackingFilter>();
        events.MapPost("/visit", async (StartEmailVisit request, EmailTrackingStore store, CancellationToken ct) => Results.Ok(await store.StartAsync(request, ct)));
        events.MapPost("/click", async (RecordEmailClick request, EmailTrackingStore store, CancellationToken ct) => Results.Ok(await store.ClickAsync(request, ct)));
    }
}

public sealed class EmailTrackingFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        try { return await next(context); }
        catch (EmailTrackingFailure e) { return Results.Problem(statusCode: e.Status, title: e.Message); }
        catch (Exception e) when (e is DbException or InvalidOperationException or FormatException or OverflowException)
        { return Results.Problem(statusCode: 503, title: "Email results are temporarily unavailable."); }
    }
}
