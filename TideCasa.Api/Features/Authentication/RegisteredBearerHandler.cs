using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace TideCasa.Api.Features.Authentication;

public sealed class RegisteredBearerHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, AuthService auth)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "RegisteredBearer";
    public const string UserItem = "TideCasa.VerifiedUser";
    private int challengeStatus = 401;
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Signout revokes the local token before contacting the provider; it must work during provider outages.
        if (Request.Path.StartsWithSegments("/api/v1/auth")) return AuthenticateResult.NoResult();
        var token = ReadToken(Request);
        if (token is null) return AuthenticateResult.NoResult();
        try
        {
            var user = await auth.AuthenticateAsync(token, Context.RequestAborted);
            if (user is null) return AuthenticateResult.Fail("This session is no longer active.");
            Context.Items[UserItem] = user;
            var claims = new[] { new Claim(ClaimTypes.NameIdentifier, user.UserId), new Claim(ClaimTypes.Email, user.Email), new Claim(ClaimTypes.Name, user.DisplayName) };
            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName));
        }
        catch (AuthFailureException exception)
        {
            challengeStatus = exception.Status is 503 or 429 ? exception.Status : 401;
            return AuthenticateResult.Fail("The session could not be verified.");
        }
    }
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "Bearer";
        return Results.Problem(statusCode: challengeStatus, title: challengeStatus == 401 ? "Please sign in to continue." : "Account access is temporarily unavailable. Please try again.").ExecuteAsync(Context);
    }
    public static string? ReadToken(HttpRequest request)
    {
        if (request.Headers.Authorization.Count != 1) return null;
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var token = header[7..];
        return SupabaseAuthClient.ValidToken(token) ? token : null;
    }
}
