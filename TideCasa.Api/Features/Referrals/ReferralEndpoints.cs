using TideCasa.Api.Features.Authentication;
using TideCasa.Api.Infrastructure;
using TideCasa.Contracts;
namespace TideCasa.Api.Features.Referrals;
public static class ReferralEndpoints
{
    public static void MapReferrals(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/referrals").RequireAuthorization().WithMetadata(new ApiBodyLimit(32 * 1024));
        group.MapGet("", (HttpContext c, ReferralStore store, CancellationToken ct) => store.ReadAsync((AuthUser)c.Items[RegisteredBearerHandler.UserItem]!, ct));
        group.MapPost("/apply", async (ApplyReferralRequest request, HttpContext c, ReferralStore store, CancellationToken ct) =>
        {
            try { await store.ApplyAsync((AuthUser)c.Items[RegisteredBearerHandler.UserItem]!, request, ct); return Results.Ok(new { saved = true }); }
            catch (ReferralFailure e) { return Results.Problem(statusCode: e.Status, title: e.Message); }
        });
        group.MapPost("/{id}/review", async (string id, ReviewReferralRequest request, HttpContext c, ReferralStore store, CancellationToken ct) =>
        {
            try { await store.ReviewAsync(id, (AuthUser)c.Items[RegisteredBearerHandler.UserItem]!, request, ct); return Results.Ok(new { saved = true }); }
            catch (ReferralFailure e) { return Results.Problem(statusCode: e.Status, title: e.Message); }
        });
    }
}
