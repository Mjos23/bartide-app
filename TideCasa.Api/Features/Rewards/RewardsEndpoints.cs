using System.Text.Json;
using System.Data.Common;
using TideCasa.Api.Features.Authentication;
using TideCasa.Api.Infrastructure;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.Rewards;

public static class RewardsEndpoints
{
    public static void MapRewards(this WebApplication app)
    {
        var owner = app.MapGroup("/api/v1/tenants/{id}/rewards").RequireAuthorization()
            .WithTags("Rewards").WithMetadata(new ApiBodyLimit(32 * 1024)).AddEndpointFilter<RewardsFilter>();
        owner.MapGet("", async (string id, HttpContext c, RewardsStore s, CancellationToken ct) => Results.Ok(await s.WorkspaceAsync(id, User(c), ct)));
        owner.MapGet("/employee", async (string id, HttpContext c, RewardsStore s, CancellationToken ct) => Results.Ok(await s.EmployeeWalletAsync(id, User(c), ct)));
        owner.MapPost("/rules", async (string id, SaveRewardRuleRequest r, HttpContext c, RewardsStore s, CancellationToken ct) => Results.Ok(await s.SaveRuleAsync(id, null, User(c), r, ct)));
        owner.MapPost("/rules/{ruleId}", async (string id, string ruleId, SaveRewardRuleRequest r, HttpContext c, RewardsStore s, CancellationToken ct) => Results.Ok(await s.SaveRuleAsync(id, ruleId, User(c), r, ct)));
        owner.MapPost("/qualifications", async (string id, RecordRewardQualificationRequest r, HttpContext c, RewardsStore s, CancellationToken ct) => Results.Ok(await s.QualifyAsync(id, User(c), r, ct)));
        owner.MapPost("/qualifications/{qualificationId}/void", async (string id, string qualificationId, VoidRewardQualificationRequest r, HttpContext c, RewardsStore s, CancellationToken ct) => Results.Ok(await s.VoidAsync(id, qualificationId, User(c), r, ct)));
        owner.MapPost("/points", async (string id, AwardCustomerPointsRequest r, HttpContext c, RewardsStore s, CancellationToken ct) => Results.Ok(await s.PointsAsync(id, User(c), r, ct)));
        owner.MapPost("/redemptions/{rewardId}/resolve", async (string id, string rewardId, ResolveRewardRedemptionRequest r, HttpContext c, RewardsStore s, CancellationToken ct) => Results.Ok(await s.ResolveAsync(id, rewardId, User(c), r, ct)));
        owner.MapPost("/employee-points", async (string id, ChangeEmployeePointsRequest r, HttpContext c, RewardsStore s, CancellationToken ct) => Results.Ok(await s.EmployeePointsAsync(id, User(c), r, ct)));
        var customer = app.MapGroup("/api/v1/restaurants/{slug}/rewards").RequireAuthorization()
            .WithTags("Customer rewards").WithMetadata(new ApiBodyLimit(32 * 1024)).AddEndpointFilter<RewardsFilter>();
        customer.MapGet("", async (string slug, HttpContext c, RewardsStore s, CancellationToken ct) => Results.Ok(await s.WalletAsync(slug, User(c), ct)));
        customer.MapPost("/join", async (string slug, JoinRewardsRequest r, HttpContext c, RewardsStore s, CancellationToken ct) => Results.Ok(await s.JoinAsync(slug, User(c), r, ct)));
        customer.MapPost("/orders", async (string slug, RestaurantTrackingRequest r, HttpContext c, RewardsStore s, CancellationToken ct) => Results.Ok(await s.LinkOrderAsync(slug, User(c), r, ct)));
        customer.MapPost("/redemptions/{rewardId}/request", async (string slug, string rewardId, RequestRewardRedemptionRequest r, HttpContext c, RewardsStore s, CancellationToken ct) => Results.Ok(await s.RequestAsync(slug, rewardId, User(c), r, ct)));
        customer.MapPost("/points-redemptions", async (string slug, RedeemPointsRewardRequest r, HttpContext c, RewardsStore s, CancellationToken ct) => Results.Ok(await s.RedeemPointsAsync(slug, User(c), r, ct)));
    }
    private static AuthUser User(HttpContext context) => (AuthUser)context.Items[RegisteredBearerHandler.UserItem]!;
}

public sealed class RewardsFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext c, EndpointFilterDelegate next)
    {
        try { return await next(c); }
        catch (RewardsException e) { return Results.Problem(statusCode: e.Status, title: e.Message, extensions: new Dictionary<string, object?> { ["code"] = e.Code }); }
        catch (Exception e) when (e is DbException or JsonException or InvalidOperationException or OverflowException or FormatException)
        { return Results.Problem(statusCode: 503, title: "Rewards are temporarily unavailable. Please try again.", extensions: new Dictionary<string, object?> { ["code"] = "rewards_unavailable" }); }
    }
}
