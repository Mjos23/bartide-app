namespace TideCasa.Api.Features.Authentication;

public sealed record VerifiedIdentity(string Id, string Email, string? FullName);
public sealed record RegisteredSession(string ProviderUserId, long ExpiresAt);
public sealed class AuthFailureException(string message, int status = 401) : Exception(message)
{
    public int Status { get; } = status;
}
