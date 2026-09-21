using TideCasa.Api.Features.Authentication;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.Accounts;

public static class AccountEndpoints
{
    public static void MapAccounts(this WebApplication app)
    {
        app.MapPost("/api/v1/account/workspace", async (RegisterWorkspaceRequest request, HttpContext context, WorkspaceRegistrationStore store, CancellationToken ct) =>
        {
            try
            {
                var result = await store.RegisterAsync((AuthUser)context.Items[RegisteredBearerHandler.UserItem]!, request, ct);
                return Results.Json(result, statusCode: result.Created ? 201 : 200);
            }
            catch (AuthFailureException e) { return Results.Problem(statusCode: e.Status, title: e.Message); }
        }).RequireAuthorization().RequireRateLimiting("public-form").WithMetadata(new TideCasa.Api.Infrastructure.ApiBodyLimit(32 * 1024)).WithTags("Account");
        app.MapGet("/api/v1/account", async (HttpContext context, WorkspaceAccessStore store, CancellationToken ct) =>
            Results.Ok(await store.GetOverviewAsync((AuthUser)context.Items[RegisteredBearerHandler.UserItem]!, ct)))
            .RequireAuthorization().AddEndpointFilter<AuthRequestFilter>().WithTags("Account").Produces<AccountOverview>();
        app.MapGet("/api/v1/tenants/{id}/access", async (string id, HttpContext context, WorkspaceAccessStore store, CancellationToken ct) =>
        {
            var access = await store.GetAccessAsync((AuthUser)context.Items[RegisteredBearerHandler.UserItem]!, id, ct);
            return access is null ? Results.NotFound() : Results.Ok(access);
        }).RequireAuthorization().AddEndpointFilter<AuthRequestFilter>().WithTags("Account").Produces<WorkspaceAccess>().Produces(404);
    }
}
