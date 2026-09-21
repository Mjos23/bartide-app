using System.Text.Json;
using System.Data.Common;
using TideCasa.Api.Features.Authentication;
using TideCasa.Api.Infrastructure;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.SalesPipeline;

public static class SalesPipelineEndpoints
{
    public static void MapSalesPipeline(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/owner/sales").RequireAuthorization().RequireRateLimiting("restaurant-ordering")
            .WithTags("Private sales pipeline").WithMetadata(new ApiBodyLimit(32 * 1024)).AddEndpointFilter<SalesPipelineFilter>();
        group.MapGet("", async (HttpContext context, SalesPipelineStore store, CancellationToken ct, string? search = null, string? stage = null, string? due = null, string? queue = null, int page = 1, int pageSize = 25) =>
            Results.Ok(await store.ListAsync(User(context), search, stage, due, queue, page, pageSize, ct)));
        group.MapGet("/{id}", async (string id, HttpContext context, SalesPipelineStore store, CancellationToken ct) => Results.Ok(await store.GetAsync(id, User(context), ct)));
        group.MapPost("", async (CreateSalesLeadRequest request, HttpContext context, SalesPipelineStore store, CancellationToken ct) => Results.Ok(await store.CreateAsync(User(context), request, ct)));
        group.MapPost("/{id}", async (string id, UpdateSalesLeadRequest request, HttpContext context, SalesPipelineStore store, CancellationToken ct) => Results.Ok(await store.UpdateAsync(id, User(context), request, ct)));
    }
    private static AuthUser User(HttpContext context) => (AuthUser)context.Items[RegisteredBearerHandler.UserItem]!;
}

public sealed class SalesPipelineFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        try { return await next(context); }
        catch (SalesPipelineFailure error) { return Results.Problem(statusCode: error.Status, title: error.Message); }
        catch (Exception error) when (error is DbException or JsonException or InvalidOperationException or FormatException or OverflowException)
        { return Results.Problem(statusCode: 503, title: "The sales pipeline is temporarily unavailable. Please try again."); }
    }
}
