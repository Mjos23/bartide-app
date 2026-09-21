using TideCasa.Api.Features.Authentication;
using TideCasa.Api.Infrastructure;
using TideCasa.Contracts;
namespace TideCasa.Api.Features.DemoRequests;
public static class DemoInboxEndpoints
{
    public static void MapDemoInbox(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/owner/demo-requests").RequireAuthorization().WithMetadata(new ApiBodyLimit(32 * 1024));
        group.MapGet("", async (HttpContext c, DemoInboxStore store, CancellationToken ct) =>
        {
            var user = (AuthUser)c.Items[RegisteredBearerHandler.UserItem]!;
            return user.IsPlatformOwner ? Results.Ok(await store.ReadAsync(user, ct)) : Results.Forbid();
        });
        group.MapPost("/{id}", async (string id, UpdateDemoInquiryRequest request, HttpContext c, DemoInboxStore store, CancellationToken ct) =>
        {
            var user = (AuthUser)c.Items[RegisteredBearerHandler.UserItem]!;
            if (!user.IsPlatformOwner) return Results.Forbid();
            try { return await store.UpdateAsync(id, user, request, ct) ? Results.Ok(new { saved = true }) : Results.Problem(statusCode: 409, title: "This request changed. Refresh before saving."); }
            catch (ArgumentException) { return Results.Problem(statusCode: 400, title: "Check the request status and notes."); }
        });
    }
}
