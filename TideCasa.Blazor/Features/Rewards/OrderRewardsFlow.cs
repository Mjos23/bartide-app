using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using TideCasa.Blazor.Features.Authentication;
using TideCasa.Blazor.Services;
using TideCasa.Contracts;

namespace TideCasa.Blazor.Features.Rewards;

public static partial class RewardsFlow
{
    private static void MapOrderRewards(WebApplication app)
    {
        app.MapGet("/rewards/orders/{slug}/session", (string slug, HttpContext c, IAntiforgery af) =>
        {
            c.Response.Headers.CacheControl = "no-store";
            if (!Identifier().IsMatch(slug) || c.Items[AuthFlow.AccountItem] is not AccountOverview account) return Results.Unauthorized();
            return Results.Ok(new { token = af.GetAndStoreTokens(c).RequestToken, user = account.User.UserId });
        });
        app.MapPost("/rewards/orders/{slug}/link", async (string slug, HttpContext c, IAntiforgery af, RewardsClient api) =>
        {
            c.Response.Headers.CacheControl = "no-store";
            if (!Identifier().IsMatch(slug) || !SameOrigin(c.Request) || !c.Request.HasFormContentType) return Results.BadRequest();
            if (c.Items[AuthFlow.TokenItem] is not string token || c.Items[AuthFlow.AccountItem] is not AccountOverview account) return Results.Unauthorized();
            if (c.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } body) body.MaxRequestBodySize = 4096;
            try
            {
                await af.ValidateRequestAsync(c);
                var f = await c.Request.ReadFormAsync(c.RequestAborted);
                if (f.Files.Count != 0 || f.Count > 5 || f.Any(x => x.Value.Count != 1) || f["user"] != account.User.UserId) return Results.BadRequest();
                var result = await api.SendAsync<RewardChangeResult>(RewardsClient.Customer(slug) + "/orders", token,
                    new RestaurantTrackingRequest(f["order_id"].ToString(), f["tracking_key"].ToString()), c.RequestAborted);
                return Results.Json(new { linked = result.Succeeded }, statusCode: (int)result.Status);
            }
            catch (Exception e) when (e is AntiforgeryValidationException or InvalidDataException or BadHttpRequestException) { return Results.BadRequest(); }
        });
    }
}
