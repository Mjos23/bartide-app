using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using TideCasa.Blazor.Services;
using TideCasa.Contracts;

namespace TideCasa.Blazor.Features.Authentication;

public static class AuthFlow
{
    public static readonly object TokenItem = new();
    public static readonly object AccountItem = new();
    public static readonly object UnavailableItem = new();

    public static string SafeReturnPath(string? input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > 2048) return "/account";
        var local = input.Split('#')[0];
        var path = local.Split('?')[0];
        // Keep redirects local; only explicit purchase choices may cross authentication.
        if (!Regex.IsMatch(path, "^/[A-Za-z0-9_/-]*$", RegexOptions.CultureInvariant)
            || path.StartsWith("//", StringComparison.Ordinal) || path.StartsWith("/auth/session/", StringComparison.OrdinalIgnoreCase)) return "/account";
        var purchase = path is "/purchase/restaurant" or "/purchase/business";
        var checkout = path is "/start/restaurant" or "/start/business"
            || Regex.IsMatch(path, "^/workspace/[A-Za-z0-9_-]{1,128}/billing$", RegexOptions.CultureInvariant);
        if ((!purchase && !checkout) || !local.Contains('?')) return path;
        var query = QueryHelpers.ParseQuery(local[(local.IndexOf('?') + 1)..]);
        var allowed = new Dictionary<string, string?>();
        if (checkout && query.TryGetValue("appStores", out var stores) && stores.Count == 1 && stores[0] is "true" or "false")
            allowed["appStores"] = stores[0];
        var referralKey = purchase ? "ref" : "referralCode";
        if (query.TryGetValue(referralKey, out var referral) && referral.Count == 1
            && referral[0] is { } code && Regex.IsMatch(code, "^[A-Za-z0-9][A-Za-z0-9-]{2,31}$", RegexOptions.CultureInvariant))
            allowed[referralKey] = code.ToUpperInvariant();
        return QueryHelpers.AddQueryString(path, allowed);
    }

    public static string? Notice(string? value) => value switch
    {
        "invalid" => "We couldn’t complete that request. Check your details and try again.",
        "check-email" => "Check your email for a verification code. If you already have an account, sign in or reset your password.",
        "reset-email" => "If an account matches, a password reset code has been sent. Check your email, including Spam.",
        "password-reset" => "Your password has been updated. Sign in with your new password.",
        "password-mismatch" => "Passwords don’t match. Enter the same new password in both fields.",
        "signed-out" => "You’re signed out.",
        "local-signout" => "You’re signed out on this device. We couldn’t confirm server-side revocation. Please try again later if you need to end other sessions.",
        _ => null
    };

    public static IEndpointRouteBuilder MapTideCasaAuthentication(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/auth/session/{action}", HandleAsync);
        return endpoints;
    }

    private static async Task<IResult> HandleAsync(string action, HttpContext context, IAntiforgery antiforgery, AccountApiClient api, IConfiguration configuration)
    {
        var demo = PublicDemoWeb.Enabled(configuration);
        if (demo ? action is not ("demo-switch" or "signout") : action is not ("signin" or "signup" or "verify" or "resend" or "forgot" or "reset" or "signout")) return Results.NotFound();
        if (context.Items.ContainsKey(UnavailableItem) && action != "signout") return ServiceUnavailable();
        if (!IsSameOrigin(context.Request) || !context.Request.HasFormContentType) return Failure(StatusCodes.Status400BadRequest, "Please open the form on this site and try again.");
        var bodyFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodyFeature is { IsReadOnly: false }) bodyFeature.MaxRequestBodySize = 16 * 1024;
        IFormCollection form;
        try
        {
            await antiforgery.ValidateRequestAsync(context);
            form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (form.Files.Count != 0 || form.Count > 8 || form.Any(field => field.Value.Count != 1)) return Failure(400, "Please check the form and try again.");
        }
        catch (Exception error) when (error is AntiforgeryValidationException or InvalidDataException or BadHttpRequestException)
        { return Failure(400, "The form has expired. Reload the page and try again."); }

        var returnTo = SafeReturnPath(form["return_to"].ToString());
        if (demo && returnTo is not ("/account" or "/rewards/gulf-lantern" or "/customer/gulf-lantern")) returnTo = "/account";
        if (action == "signout")
        {
            var auth = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            var token = auth.Properties?.GetTokenValue("access_token") ?? context.Items[TokenItem] as string;
            var confirmed = string.IsNullOrEmpty(token);
            try
            {
                if (!string.IsNullOrEmpty(token)) confirmed = (await api.SignOutAsync(token, context.RequestAborted)).Succeeded;
            }
            finally { await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme); }
            return Results.LocalRedirect(demo ? "/sample-bar" : "/signin?notice=" + (confirmed ? "signed-out" : "local-signout"));
        }

        var email = form["email"].ToString().Trim();
        var password = form["password"].ToString();
        var code = form["code"].ToString().Trim();
        // Confirm before contacting the API, so a typo cannot consume a recovery code.
        if (action == "reset" && (string.IsNullOrEmpty(form["confirm_password"])
            || !string.Equals(password, form["confirm_password"].ToString(), StringComparison.Ordinal)))
            return Redirect("/reset-password", "password-mismatch", returnTo);
        object request = action switch
        {
            "demo-switch" => new { personKey = form["person_key"].ToString() },
            "signin" => new SignInRequest(email, password),
            "signup" => new SignUpRequest(email, password),
            "verify" => new VerifyEmailRequest(email, code),
            "reset" => new ResetPasswordRequest(email, code, password),
            _ => new EmailRequest(email)
        };
        var page = action switch { "signup" => "/signup", "verify" or "resend" => "/verify-email", "forgot" => "/forgot-password", "reset" => "/reset-password", _ => "/signin" };
        if (!Validator.TryValidateObject(request, new ValidationContext(request), null, true)) return Redirect(page, "invalid", returnTo);
        if (action is "signin" or "verify" or "demo-switch")
        {
            var result = await api.PostAsync<AuthSession>(action, request, context.Connection.RemoteIpAddress, context.RequestAborted);
            if (result.Unavailable) return ServiceUnavailable();
            if ((int)result.Status == 429) return Failure(429, "Please wait a little before trying again.");
            if (!result.Succeeded || result.Value is not { } session) return Redirect(page, "invalid", returnTo);
            if (!ValidSession(session)) return ServiceUnavailable();
            // API supplies identity. No owner/admin role is inferred from email or provider metadata.
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, session.User.UserId)], CookieAuthenticationDefaults.AuthenticationScheme);
            var now = DateTimeOffset.UtcNow;
            var properties = new AuthenticationProperties { IssuedUtc = now, ExpiresUtc = now.AddSeconds(Math.Min(session.ExpiresIn, 3600)), AllowRefresh = false, IsPersistent = false };
            properties.StoreTokens([new AuthenticationToken { Name = "access_token", Value = session.AccessToken }]);
            try
            {
                if (demo)
                {
                    var old = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    if (old.Properties?.GetTokenValue("access_token") is { } prior)
                        await api.SignOutAsync(prior, context.RequestAborted);
                }
                await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity), properties);
            }
            catch (InvalidOperationException) { return ServiceUnavailable(); }
            return Results.LocalRedirect(returnTo);
        }
        var notice = await api.PostAsync<AuthNotice>(action, request, context.Connection.RemoteIpAddress, context.RequestAborted);
        if (notice.Unavailable) return ServiceUnavailable();
        if ((int)notice.Status == 429) return Failure(429, "Please wait a little before trying again.");
        if (!notice.Succeeded) return Redirect(page, "invalid", returnTo);
        if (action == "reset")
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Redirect("/signin", "password-reset", returnTo);
        }
        return action == "forgot" ? Redirect("/reset-password", "reset-email", returnTo) : Redirect("/verify-email", "check-email", returnTo);
    }

    private static bool ValidSession(AuthSession session) =>
        session.ExpiresIn > 0 && string.Equals(session.TokenType, "bearer", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(session.AccessToken) && session.AccessToken.Length <= 16384 && Regex.IsMatch(session.AccessToken, "^[A-Za-z0-9._~-]+$", RegexOptions.CultureInvariant)
        && session.User is not null && !string.IsNullOrWhiteSpace(session.User.UserId) && session.User.UserId.Length <= 128;

    private static IResult Redirect(string page, string notice, string returnTo) =>
        Results.LocalRedirect(page + "?notice=" + notice + "&return_to=" + Uri.EscapeDataString(returnTo));

    private static bool IsSameOrigin(HttpRequest request)
    {
        if (!Uri.TryCreate(request.Scheme + "://" + request.Host, UriKind.Absolute, out var expected)) return false;
        var origin = request.Headers.Origin.ToString();
        var candidate = string.IsNullOrEmpty(origin) ? request.Headers.Referer.ToString() : origin;
        return Uri.TryCreate(candidate, UriKind.Absolute, out var source) && string.IsNullOrEmpty(source.UserInfo)
            && source.Scheme == expected.Scheme && source.IdnHost.Equals(expected.IdnHost, StringComparison.OrdinalIgnoreCase)
            && source.Port == expected.Port && (string.IsNullOrEmpty(origin) || source.AbsolutePath == "/")
            && request.Headers["Sec-Fetch-Site"] != "cross-site";
    }

    public static IResult ServiceUnavailable() => Failure(503, "Sign-in is temporarily unavailable. Please try again shortly.");
    private static IResult Failure(int status, string message) => Results.Content(
        "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>Account | Tide Casa</title><link rel=\"stylesheet\" href=\"/bartide-one.css\"><link rel=\"stylesheet\" href=\"/app.css\"></head><body><main class=\"bt-contact-main\"><section><h1>Let’s try that again.</h1><p>" + HtmlEncoder.Default.Encode(message) + "</p><a class=\"bt-one-link\" href=\"/signin\">Return to sign in</a></section></main></body></html>",
        "text/html; charset=utf-8", statusCode: status);
}
