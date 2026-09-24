using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using TideCasa.Blazor.Features.Authentication;
using TideCasa.Blazor.Services;
using TideCasa.Contracts;
namespace TideCasa.Blazor.Features.Referrals;
public static class ReferralFlow
{
    public static void MapReferralForms(this WebApplication app)
    {
        app.MapPost("/account/referrals/{id}/review", (string id, HttpContext c, IAntiforgery csrf, ReferralsClient client) => SaveAsync(id, c, csrf, client)).RequireAuthorization();
    }
    private static async Task<IResult> SaveAsync(string? id, HttpContext context, IAntiforgery csrf, ReferralsClient client)
    {
        if (context.Items.ContainsKey(AuthFlow.UnavailableItem)) return AuthFlow.ServiceUnavailable();
        if (id is not null && (context.Items[AuthFlow.AccountItem] as AccountOverview)?.User.IsPlatformOwner != true) return Results.Forbid();
        if (context.Items[AuthFlow.TokenItem] is not string token || id is not null && !Guid.TryParseExact(id, "D", out _)) return Results.BadRequest();
        var origin = context.Request.Headers.Origin.ToString(); var source = origin.Length == 0 ? context.Request.Headers.Referer.ToString() : origin;
        if (!context.Request.HasFormContentType || !Uri.TryCreate(source, UriKind.Absolute, out var actual) || !Uri.TryCreate(context.Request.Scheme + "://" + context.Request.Host, UriKind.Absolute, out var expected)
            || actual.Scheme != expected.Scheme || actual.IdnHost != expected.IdnHost || actual.Port != expected.Port || actual.UserInfo.Length != 0 || origin.Length > 0 && actual.AbsolutePath != "/" || context.Request.Headers["Sec-Fetch-Site"] == "cross-site") return Results.BadRequest();
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = 32 * 1024;
        try
        {
            await csrf.ValidateRequestAsync(context); var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (form.Files.Count != 0 || form.Count > 5 || form.Any(f => f.Value.Count != 1)) return Results.BadRequest();
            System.Net.HttpStatusCode result;
            if (id is null)
                result = await client.ApplyAsync(new(form["name"].ToString(), form["introduction"].ToString(), form["acceptedTerms"] == "true", form["termsVersion"].ToString()), token, context.RequestAborted);
            else
            {
                if (!int.TryParse(form["discountPercent"], out var discount)) return Results.BadRequest();
                result = await client.ReviewAsync(id, new(form["reviewToken"].ToString(), form["status"].ToString(), form["code"].ToString(), discount), token, context.RequestAborted);
            }
            return Results.LocalRedirect("/account/referrals?notice=" + ((int)result is >= 200 and < 300 ? "saved" : (int)result == 409 ? "changed" : (int)result == 400 ? "invalid" : "unconfirmed"));
        }
        catch (Exception e) when (e is AntiforgeryValidationException or InvalidDataException or BadHttpRequestException) { return Results.LocalRedirect("/account/referrals?notice=expired"); }
    }
}
