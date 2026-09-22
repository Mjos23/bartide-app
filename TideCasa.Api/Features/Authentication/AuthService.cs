using System.Text.Json;
using System.Security.Cryptography;
using TideCasa.Api.Features.PublicDemo;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.Authentication;

public sealed class AuthService(SupabaseAuthClient provider, AuthStore store, IConfiguration configuration, PublicDemoOptions demo)
{
    private static string Email(string email) => email.Trim().ToLowerInvariant();
    private AuthUser WithOwner(AuthUser user) => user with
    {
        IsPlatformOwner = !string.IsNullOrWhiteSpace(configuration["Auth:PlatformOwnerUserId"]) &&
            string.Equals(configuration["Auth:PlatformOwnerUserId"], user.UserId, StringComparison.Ordinal)
    };

    public async Task<AuthSession> SignInAsync(SignInRequest request, string address, CancellationToken ct)
    {
        RequireNormalAuthentication();
        var email = Email(request.Email);
        await store.LimitAsync("signin", email, address, ct);
        var data = await provider.SendAsync("/token?grant_type=password", new { email, password = request.Password }, null, HttpMethod.Post, ct);
        return await IssueAsync(data, email, ct);
    }

    public async Task<AuthNotice> SignUpAsync(SignUpRequest request, string address, CancellationToken ct)
    {
        RequireNormalAuthentication();
        var email = Email(request.Email);
        await store.LimitAsync("signup", email, address, ct);
        try { await provider.SendAsync("/signup", new { email, password = request.Password }, null, HttpMethod.Post, ct); }
        catch (AuthFailureException exception) when (exception.Status == 401) { /* Same notice for existing and eligible addresses. */ }
        // Never use a signup response as an authenticated session, even if provider confirmation was misconfigured.
        return new("Check your email for a verification code. If you already have an account, sign in or reset your password.");
    }

    public async Task<AuthSession> VerifyAsync(VerifyEmailRequest request, string address, CancellationToken ct)
    {
        RequireNormalAuthentication();
        var email = Email(request.Email);
        await store.LimitAsync("verify", email, address, ct);
        var data = await provider.SendAsync("/verify", new { email, token = request.Code, type = "signup" }, null, HttpMethod.Post, ct);
        return await IssueAsync(data, email, ct);
    }

    public async Task<AuthNotice> SendCodeAsync(EmailRequest request, string action, string address, CancellationToken ct)
    {
        RequireNormalAuthentication();
        var email = Email(request.Email);
        await store.LimitAsync(action, email, address, ct);
        object body = action == "forgot" ? new { email } : new { email, type = "signup" };
        try { await provider.SendAsync(action == "forgot" ? "/recover" : "/resend", body, null, HttpMethod.Post, ct); }
        catch (AuthFailureException exception) when (exception.Status == 401) { }
        return new("If this address is eligible, an email code is on its way. Check your inbox and spam folder.");
    }

    public async Task<AuthNotice> ResetAsync(ResetPasswordRequest request, string address, CancellationToken ct)
    {
        RequireNormalAuthentication();
        var email = Email(request.Email);
        await store.LimitAsync("reset", email, address, ct);
        var data = await provider.SendAsync("/verify", new { email, token = request.Code, type = "recovery" }, null, HttpMethod.Post, ct);
        var (token, _) = SessionFields(data);
        var identity = await provider.VerifyAsync(token, ct);
        if (identity.Email != email) throw new AuthFailureException("Request a new password reset code.");
        // Once recovery is verified, attempt local and provider revocation even if the update/response fails.
        // Use an independent bounded token so a disconnected browser cannot cancel revocation after a password change.
        try { await provider.SendAsync("/user", new { password = request.Password }, token, HttpMethod.Put, ct); }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await store.RevokeAllAsync(identity.Id, cleanup.Token); }
            finally
            {
                try { await provider.SendAsync("/logout?scope=global", new { }, token, HttpMethod.Post, cleanup.Token); }
                catch (AuthFailureException) { /* Local registry still prevents reuse in this application. */ }
            }
        }
        return new("Password updated. Sign in with your new password.");
    }

    public async Task SignOutAsync(string? token, CancellationToken ct)
    {
        if (!SupabaseAuthClient.ValidToken(token)) return;
        if (demo.Enabled)
        {
            if (PublicDemoOptions.IsDemoToken(token!))
            {
                using var revoke = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await store.RevokeAsync(token!, revoke.Token);
            }
            return;
        }
        if (token!.StartsWith("demo.", StringComparison.Ordinal)) return;
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await store.RevokeAsync(token!, cleanup.Token);
        try { await provider.SendAsync("/logout?scope=local", new { }, token, HttpMethod.Post, cleanup.Token); }
        catch (AuthFailureException) { /* Local revocation is authoritative for this API. */ }
    }

    public async Task<AuthUser?> AuthenticateAsync(string token, CancellationToken ct)
    {
        if (demo.Enabled)
        {
            if (!PublicDemoOptions.IsDemoToken(token)) return null;
            var registered = await store.FindSessionAsync(token, ct);
            if (registered is null || demo.Identity(registered.ProviderUserId) is not { } person) return null;
            var user = await DemoUserAsync(person, ct);
            var current = await store.FindSessionAsync(token, ct);
            return current?.ProviderUserId == person.Id ? user : null;
        }
        // A copied demo registry/token can never become a normal provider session.
        if (token.StartsWith("demo.", StringComparison.Ordinal)) return null;
        var session = await store.FindSessionAsync(token, ct);
        if (session is null) return null;
        var identity = await provider.VerifyAsync(token, ct);
        if (identity.Id != session.ProviderUserId) return null;
        // Recheck after network I/O so logout while /user is in flight cannot restore access.
        var fresh = await store.FindSessionAsync(token, ct);
        if (fresh is null || fresh.ProviderUserId != identity.Id) return null;
        return WithOwner(await store.ResolveAsync(identity, ct));
    }

    public async Task<AuthSession> DemoSwitchAsync(DemoSwitchRequest request, CancellationToken ct)
    {
        if (!demo.Enabled) throw new AuthFailureException("Not found.", 404);
        var person = demo.Person(request.PersonKey) ?? throw new AuthFailureException("Choose a person from the fictional sample bar.", 400);
        var user = await DemoUserAsync(person, ct);
        var token = "demo." + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)) + ".session";
        const int expires = 1800;
        await store.RegisterSessionAsync(token, person.Id, expires, ct);
        return new(token, "Bearer", expires, user);
    }

    private async Task<AuthUser> DemoUserAsync(VerifiedIdentity person, CancellationToken ct)
    {
        var user = await store.ResolveAsync(person, ct);
        if (user.UserId != "supabase:" + person.Id || user.Email != person.Email)
            throw new AuthFailureException("The fictional sample identity needs a reset.", 503);
        return user with { IsPlatformOwner = false };
    }

    private void RequireNormalAuthentication()
    {
        if (demo.Enabled) throw new AuthFailureException("Use the fictional sample people to switch perspectives.", 403);
    }

    private async Task<AuthSession> IssueAsync(JsonElement data, string expectedEmail, CancellationToken ct)
    {
        var (token, expires) = SessionFields(data);
        var identity = await provider.VerifyAsync(token, ct);
        if (identity.Email != expectedEmail) throw new AuthFailureException("Check your details or request a new email code.");
        var user = WithOwner(await store.ResolveAsync(identity, ct));
        await store.RegisterSessionAsync(token, identity.Id, expires, ct);
        return new(token, "Bearer", expires, user);
    }

    private static (string Token, int Expires) SessionFields(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("access_token", out var value) ||
            value.ValueKind != JsonValueKind.String || !SupabaseAuthClient.ValidToken(value.GetString()) ||
            !data.TryGetProperty("expires_in", out var duration) || duration.ValueKind != JsonValueKind.Number || !duration.TryGetInt32(out var seconds) || seconds <= 0)
            throw new AuthFailureException("Invalid sign-in response. Please try again.", 502);
        return (value.GetString()!, Math.Min(3600, seconds));
    }
}
