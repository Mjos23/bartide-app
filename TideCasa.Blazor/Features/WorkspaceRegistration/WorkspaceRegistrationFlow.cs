using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using TideCasa.Blazor.Features.Authentication;
using TideCasa.Blazor.Services;
using TideCasa.Contracts;

namespace TideCasa.Blazor.Features.WorkspaceRegistration;

public static class WorkspaceRegistrationFlow
{
    public static void MapWorkspaceRegistration(this WebApplication app) => app.MapPost("/account/workspace", SaveAsync).RequireAuthorization();
    private static async Task<IResult> SaveAsync(HttpContext context, IAntiforgery csrf, AccountApiClient client)
    {
        if (context.Items.ContainsKey(AuthFlow.UnavailableItem)) return AuthFlow.ServiceUnavailable();
        if (context.Items[AuthFlow.TokenItem] is not string token) return Results.Unauthorized();
        var origin = context.Request.Headers.Origin.ToString(); var source = origin.Length == 0 ? context.Request.Headers.Referer.ToString() : origin;
        if (!context.Request.HasFormContentType || !Uri.TryCreate(source, UriKind.Absolute, out var actual) || !Uri.TryCreate(context.Request.Scheme + "://" + context.Request.Host, UriKind.Absolute, out var expected)
            || actual.Scheme != expected.Scheme || actual.IdnHost != expected.IdnHost || actual.Port != expected.Port || actual.UserInfo.Length > 0 || origin.Length > 0 && actual.AbsolutePath != "/" || context.Request.Headers["Sec-Fetch-Site"] == "cross-site") return Results.BadRequest();
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } body) body.MaxRequestBodySize = 32 * 1024;
        try
        {
            await csrf.ValidateRequestAsync(context); var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (form.Files.Count != 0 || form.Count > 13 || form.Any(f => f.Value.Count != 1)) return Results.BadRequest();
            var plan = form["plan"].ToString(); if (plan is not ("restaurant" or "business")) return Results.BadRequest();
            var stores = form["appStores"] == "true";
            var referral = form["referralCode"].ToString(); if (referral.Length > 32) return Results.BadRequest();
            var query = $"appStores={stores.ToString().ToLowerInvariant()}&referralCode={Uri.EscapeDataString(referral)}";
            var result = await client.RegisterWorkspaceAsync(new(form["requestId"].ToString(), form["businessName"].ToString(), form["contactName"].ToString(),
                form["area"].ToString(), plan, form["acknowledged"] == "true", form["phone"].ToString(), form["website"].ToString(), form["notes"].ToString()), token, context.Connection.RemoteIpAddress, context.RequestAborted);
            return result.Succeeded && result.Value is not null
                ? Results.LocalRedirect($"/workspace/{Uri.EscapeDataString(result.Value.TenantId)}/billing?{query}")
                : Results.LocalRedirect($"/start/{plan}?{query}&notice={(result.Status == System.Net.HttpStatusCode.Conflict ? "review" : "unconfirmed")}");
        }
        catch (Exception e) when (e is AntiforgeryValidationException or InvalidDataException or BadHttpRequestException) { return Results.LocalRedirect("/start/restaurant?notice=expired"); }
    }
}
