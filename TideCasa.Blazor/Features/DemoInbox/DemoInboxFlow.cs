using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using TideCasa.Blazor.Features.Authentication;
using TideCasa.Blazor.Services;
using TideCasa.Contracts;
namespace TideCasa.Blazor.Features.DemoInbox;
public static class DemoInboxFlow
{
    public static void MapDemoInboxForms(this WebApplication app) => app.MapPost("/owner/demo-requests/{id}", SaveAsync).RequireAuthorization();
    private static async Task<IResult> SaveAsync(string id, HttpContext context, IAntiforgery csrf, DemoInboxClient client)
    {
        if (context.Items.ContainsKey(AuthFlow.UnavailableItem)) return AuthFlow.ServiceUnavailable();
        if ((context.Items[AuthFlow.AccountItem] as AccountOverview)?.User.IsPlatformOwner != true) return Results.Forbid();
        if (context.Items[AuthFlow.TokenItem] is not string token || !Guid.TryParseExact(id, "D", out _)) return Results.BadRequest();
        var origin = context.Request.Headers.Origin.ToString(); var source = origin.Length == 0 ? context.Request.Headers.Referer.ToString() : origin;
        if (!context.Request.HasFormContentType || !Uri.TryCreate(source, UriKind.Absolute, out var actual) || !Uri.TryCreate(context.Request.Scheme + "://" + context.Request.Host, UriKind.Absolute, out var expected)
            || actual.Scheme != expected.Scheme || actual.IdnHost != expected.IdnHost || actual.Port != expected.Port || actual.UserInfo.Length != 0 || origin.Length > 0 && actual.AbsolutePath != "/" || context.Request.Headers["Sec-Fetch-Site"] == "cross-site") return Results.BadRequest();
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } body) body.MaxRequestBodySize = 32 * 1024;
        try
        {
            await csrf.ValidateRequestAsync(context); var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (form.Files.Count > 0 || form.Count > 4 || form.Any(f => f.Value.Count != 1) || !int.TryParse(form["version"], out var version)) return Results.BadRequest();
            var result = await client.SaveAsync(id, new(version, form["status"].ToString(), form["note"].ToString()), token, context.RequestAborted);
            return Results.LocalRedirect("/owner/demo-requests?notice=" + ((int)result is >= 200 and < 300 ? "saved" : (int)result == 409 ? "changed" : "unconfirmed"));
        }
        catch (Exception e) when (e is AntiforgeryValidationException or InvalidDataException or BadHttpRequestException) { return Results.LocalRedirect("/owner/demo-requests?notice=expired"); }
    }
}
