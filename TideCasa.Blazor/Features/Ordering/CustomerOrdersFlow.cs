using Microsoft.AspNetCore.Antiforgery;
using TideCasa.Blazor.Features.Authentication;
using TideCasa.Blazor.Services;
using TideCasa.Contracts;

namespace TideCasa.Blazor.Features.Ordering;

public static partial class RestaurantOrderingFlow
{
    private static void MapCustomerOrders(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/customer-orders/{slug}/status", async (string slug, HttpContext c, RestaurantOrderingClient api) =>
        {
            c.Response.Headers.CacheControl = "no-store";
            if (!Identifier().IsMatch(slug) || c.Items[AuthFlow.TokenItem] is not string token || c.Items[AuthFlow.AccountItem] is not AccountOverview account) return Results.Unauthorized();
            var result = await api.CustomerOrdersAsync(slug, token, c.RequestAborted);
            return result.Succeeded ? Results.Ok(new { user = account.User.UserId, orders = result.Value!.Orders.Select(o => new { id = o.OrderId, label = CustomerOrderStatus.Label(o.Status), active = !CustomerOrderStatus.Finished(o.Status) }) })
                : Results.StatusCode((int)result.Status);
        }).RequireAuthorization();
        endpoints.MapGet("/customer-orders/{slug}/session", (string slug, HttpContext c, IAntiforgery af) =>
        {
            c.Response.Headers.CacheControl = "no-store";
            if (!Identifier().IsMatch(slug) || c.Items[AuthFlow.AccountItem] is not AccountOverview account) return Results.Unauthorized();
            return Results.Ok(new { token = af.GetAndStoreTokens(c).RequestToken, user = account.User.UserId });
        }).RequireAuthorization();
        endpoints.MapPost("/customer-orders/{slug}/save", async (string slug, HttpContext c, IAntiforgery af, RestaurantOrderingClient api) =>
        {
            c.Response.Headers.CacheControl = "no-store";
            if (!Identifier().IsMatch(slug) || !SameOrigin(c.Request) || !c.Request.HasFormContentType) return Results.BadRequest();
            if (c.Items[AuthFlow.TokenItem] is not string token || c.Items[AuthFlow.AccountItem] is not AccountOverview account) return Results.Unauthorized();
            var body = c.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (body is { IsReadOnly: false }) body.MaxRequestBodySize = 4096;
            try
            {
                await af.ValidateRequestAsync(c);
                var f = await c.Request.ReadFormAsync(c.RequestAborted);
                if (f.Files.Count != 0 || f.Count > 5 || f.Any(x => x.Value.Count != 1) || f["user"] != account.User.UserId) return Results.BadRequest();
                var result = await api.SaveCustomerOrderAsync(slug, new(f["order_id"].ToString(), f["tracking_key"].ToString()), token, c.RequestAborted);
                return result.Succeeded ? Results.Ok(new { saved = true }) : Results.Json(new { saved = false }, statusCode: (int)result.Status);
            }
            catch (Exception e) when (e is AntiforgeryValidationException or InvalidDataException or BadHttpRequestException)
            { return Results.BadRequest(); }
        }).RequireAuthorization();
    }
}
