using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using TideCasa.Blazor.Services;

namespace TideCasa.Blazor.Features.Authentication;

public sealed class FreshCookieEvents(AccountApiClient api) : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var token = context.Properties.GetTokenValue("access_token");
        if (string.IsNullOrEmpty(token)) { await RejectAsync(context); return; }
        context.HttpContext.Items[AuthFlow.TokenItem] = token;
        // Sign-out must still be possible during an API outage. Its POST performs the revocation request.
        if (context.Request.Path.Equals("/auth/session/signout", StringComparison.OrdinalIgnoreCase)) return;
        var result = await api.GetAccountAsync(token, context.HttpContext.RequestAborted);
        if (result.Succeeded && result.Value is { User: not null, Workspaces: not null } account
            && account.User.UserId == context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier))
        {
            context.HttpContext.Items[AuthFlow.AccountItem] = account;
            return;
        }
        if (result.Unavailable) context.HttpContext.Items[AuthFlow.UnavailableItem] = true;
        await RejectAsync(context);
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    public override async Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
    {
        if (context.HttpContext.Items.ContainsKey(AuthFlow.UnavailableItem))
        { await AuthFlow.ServiceUnavailable().ExecuteAsync(context.HttpContext); return; }
        context.Response.Redirect("/signin?return_to=" + Uri.EscapeDataString(AuthFlow.SafeReturnPath(context.Request.Path + context.Request.QueryString)));
    }

    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return context.Response.WriteAsync("This account does not have access to that page.");
    }
}
