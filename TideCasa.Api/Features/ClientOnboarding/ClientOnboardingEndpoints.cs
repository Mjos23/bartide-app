using System.Data.Common;
using System.Text.Json;
using TideCasa.Api.Features.Authentication;
using TideCasa.Api.Features.RestaurantOrdering;
using TideCasa.Api.Infrastructure;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.ClientOnboarding;

public static class ClientOnboardingEndpoints
{
    public static void MapClientOnboarding(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/tenants/{id}/onboarding").RequireAuthorization()
            .WithMetadata(new ApiBodyLimit(32 * 1024)).AddEndpointFilter<ClientOnboardingFilter>();
        group.MapGet("", async (string id, HttpContext c, ClientOnboardingStore s, CancellationToken ct) => Results.Ok(await s.GetAsync(id, User(c), ct)));
        group.MapPost("/business", async (string id, OnboardingSaveBusiness r, HttpContext c, ClientOnboardingStore s, CancellationToken ct) => Results.Ok(await s.SaveBusinessAsync(id, User(c), r, ct)));
        group.MapPost("/brand", async (string id, OnboardingSaveBrand r, HttpContext c, ClientOnboardingStore s, CancellationToken ct) => Results.Ok(await s.SaveBrandAsync(id, User(c), r, ct)));
        group.MapPost("/service", async (string id, OnboardingSaveService r, HttpContext c, ClientOnboardingStore s, CancellationToken ct) => Results.Ok(await s.SaveServiceAsync(id, User(c), r, ct)));
        group.MapPost("/team", async (string id, OnboardingSaveTeam r, HttpContext c, ClientOnboardingStore s, CancellationToken ct) => Results.Ok(await s.SaveTeamAsync(id, User(c), r, ct)));
        group.MapPost("/build", async (string id, OnboardingBuildRequest r, HttpContext c, ClientOnboardingStore s, CancellationToken ct) => Results.Ok(await s.BuildAsync(id, User(c), r, ct)));
        group.MapGet("/preview", async (string id, HttpContext c, ClientOnboardingStore s, CancellationToken ct) => Results.Ok(await s.PreviewAsync(id, User(c), ct)));
        group.MapPost("/approve", async (string id, OnboardingApproveRequest r, HttpContext c, ClientOnboardingStore s, CancellationToken ct) => Results.Ok(await s.ApproveAsync(id, User(c), r, ct)));
        group.MapPost("/practice", async (string id, OnboardingPracticeRequest r, HttpContext c, ClientOnboardingStore s, CancellationToken ct) => Results.Ok(await s.PracticeAsync(id, User(c), r, ct)));
        app.MapGet("/api/v1/bars/{slug}", async (string slug, ClientOnboardingStore s, CancellationToken ct) => Results.Ok(await s.PublicAsync(slug, ct)))
            .AddEndpointFilter<ClientOnboardingFilter>();
    }
    private static AuthUser User(HttpContext c) => (AuthUser)c.Items[RegisteredBearerHandler.UserItem]!;
}
public sealed class ClientOnboardingFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        try { return await next(context); }
        catch (OnboardingException e) { return Results.Problem(statusCode: e.Status, title: e.Message, extensions: new Dictionary<string, object?> { ["code"] = e.Code }); }
        catch (OrderingException e) { return Results.Problem(statusCode: e.Status, title: e.Message, extensions: new Dictionary<string, object?> { ["code"] = e.Code }); }
        catch (Exception e) when (e is DbException or JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
        { return Results.Problem(statusCode: 503, title: "Business setup is temporarily unavailable. Your saved work is retained.", extensions: new Dictionary<string, object?> { ["code"] = "onboarding_unavailable" }); }
    }
}
