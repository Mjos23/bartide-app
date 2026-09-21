using System.Security.Cryptography;
using System.Text;
using System.Data.Common;
using TideCasa.Api.Infrastructure;
using TideCasa.Contracts;

namespace TideCasa.Api.Features.Authentication;

/// <summary>Local identity mapping and revocation registry; never validates provider credentials itself.</summary>
public sealed class AuthStore(ApplicationDatabase database, TimeProvider timeProvider)
{
    private const string Unavailable = "Account access is temporarily unavailable. Please try again.";
    private const string MigrationReview = "Your existing account needs a migration review. Please contact Tide Casa.";
    private static readonly HashSet<string> LimitedActions = ["signin", "signup", "verify", "resend", "forgot", "reset"];

    public async Task<AuthUser> ResolveAsync(VerifiedIdentity identity, CancellationToken cancellationToken = default)
    {
        var providerId = ProviderId(identity.Id);
        var email = Email(identity.Email);
        try
        {
            await using var connection = await database.OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction(deferred: false);
            string? appUserId;
            await using (var existing = connection.CreateCommand())
            {
                existing.Transaction = transaction;
                existing.CommandText = "SELECT app_user_id FROM bartide_auth_identities WHERE provider_user_id=@provider";
                existing.Parameters.AddWithValue("@provider", providerId);
                appUserId = await existing.ExecuteScalarAsync(cancellationToken) as string;
            }
            if (appUserId is null)
            {
                var claims = new List<(string UserId, int Approved)>();
                await using (var claim = connection.CreateCommand())
                {
                    claim.Transaction = transaction;
                    claim.CommandText = connection.Sql("SELECT legacy_user_id,approved FROM bartide_auth_legacy_claims WHERE email=@email COLLATE NOCASE LIMIT 2",
            "SELECT legacy_user_id,approved FROM bartide_auth_legacy_claims WHERE translate(email,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz')=translate(@email,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz') COLLATE \"C\" LIMIT 2");
                    claim.Parameters.AddWithValue("@email", email);
                    await using var reader = await claim.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken)) claims.Add((reader.GetString(0), reader.ReadInt32(1)));
                }
                if (claims.Count > 1 || claims.Any(claim => claim.Approved != 1))
                    throw new AuthFailureException(MigrationReview, 409);
                appUserId = claims.Count == 1 ? claims[0].UserId : "supabase:" + providerId;
                if (claims.Count == 1)
                {
                    await using var conflicts = connection.CreateCommand();
                    conflicts.Transaction = transaction;
                    conflicts.CommandText = "SELECT COUNT(DISTINCT email) FROM bartide_auth_legacy_claims WHERE legacy_user_id=@user";
                    conflicts.Parameters.AddWithValue("@user", appUserId);
                    if (Convert.ToInt64(await conflicts.ExecuteScalarAsync(cancellationToken)) != 1)
                        throw new AuthFailureException(MigrationReview, 409);
                }
                await using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText = """
                        INSERT INTO bartide_auth_identities(provider_user_id,app_user_id,verified_email,created_at)
                        VALUES (@provider,@user,@email,@now) ON CONFLICT DO NOTHING
                        """;
                    insert.Parameters.AddWithValue("@provider", providerId);
                    insert.Parameters.AddWithValue("@user", appUserId);
                    insert.Parameters.AddWithValue("@email", email);
                    insert.Parameters.AddWithValue("@now", timeProvider.GetUtcNow().ToString("O"));
                    await insert.ExecuteNonQueryAsync(cancellationToken);
                }
                await using var bound = connection.CreateCommand();
                bound.Transaction = transaction;
                bound.CommandText = "SELECT app_user_id FROM bartide_auth_identities WHERE provider_user_id=@provider";
                bound.Parameters.AddWithValue("@provider", providerId);
                if ((await bound.ExecuteScalarAsync(cancellationToken) as string) != appUserId)
                    throw new AuthFailureException(MigrationReview, 409);
            }
            if (string.IsNullOrWhiteSpace(appUserId)) throw new AuthFailureException(MigrationReview, 409);
            await transaction.CommitAsync(cancellationToken);
            var fullName = identity.FullName is null ? null : identity.FullName[..Math.Min(identity.FullName.Length, 200)];
            return new(appUserId, email, string.IsNullOrWhiteSpace(fullName) ? email : fullName, fullName);
        }
        catch (DbException exception) when (DatabaseExtensions.IsConstraintViolation(exception))
        {
            throw new AuthFailureException(MigrationReview, 409);
        }
        catch (DbException) { throw new AuthFailureException(Unavailable, 503); }
    }

    public async Task RegisterSessionAsync(string token, string providerId, int expiresIn, CancellationToken cancellationToken = default)
    {
        if (!ValidToken(token) || expiresIn <= 0) throw new AuthFailureException("Invalid authentication response.", 502);
        providerId = ProviderId(providerId);
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var expiresAt = now + Math.Min(3600, expiresIn);
        try
        {
            await using var connection = await database.OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction(deferred: false);
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO bartide_auth_sessions(token_hash,provider_user_id,expires_at)
                    VALUES (@hash,@provider,@expiry) ON CONFLICT(token_hash) DO NOTHING
                    """;
                insert.Parameters.AddWithValue("@hash", Hash(token));
                insert.Parameters.AddWithValue("@provider", providerId);
                insert.Parameters.AddWithValue("@expiry", expiresAt);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var check = connection.CreateCommand())
            {
                check.Transaction = transaction;
                check.CommandText = "SELECT provider_user_id,expires_at FROM bartide_auth_sessions WHERE token_hash=@hash";
                check.Parameters.AddWithValue("@hash", Hash(token));
                await using var reader = await check.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken) || reader.GetString(0) != providerId || reader.ReadInt64(1) <= now)
                    throw new AuthFailureException("Please sign in again.", 401);
            }
            // A repeated registration never extends an existing token's application expiry.
            await using (var cleanup = connection.CreateCommand())
            {
                cleanup.Transaction = transaction;
                cleanup.CommandText = "DELETE FROM bartide_auth_sessions WHERE expires_at<=@now";
                cleanup.Parameters.AddWithValue("@now", now);
                await cleanup.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbException) { throw new AuthFailureException(Unavailable, 503); }
    }

    public async Task<RegisteredSession?> FindSessionAsync(string token, CancellationToken cancellationToken = default)
    {
        if (!ValidToken(token)) return null;
        try
        {
            await using var connection = await database.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT provider_user_id,expires_at FROM bartide_auth_sessions WHERE token_hash=@hash AND expires_at>@now";
            command.Parameters.AddWithValue("@hash", Hash(token));
            command.Parameters.AddWithValue("@now", timeProvider.GetUtcNow().ToUnixTimeSeconds());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? new(reader.GetString(0), reader.ReadInt64(1)) : null;
        }
        catch (DbException) { throw new AuthFailureException(Unavailable, 503); }
    }

    public async Task RevokeAsync(string token, CancellationToken cancellationToken = default)
    {
        if (!ValidToken(token)) return;
        try
        {
            await using var connection = await database.OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction(deferred: false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM bartide_auth_sessions WHERE token_hash=@hash";
            command.Parameters.AddWithValue("@hash", Hash(token));
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbException) { throw new AuthFailureException(Unavailable, 503); }
    }

    public async Task RevokeAllAsync(string providerId, CancellationToken cancellationToken = default)
    {
        providerId = ProviderId(providerId);
        try
        {
            await using var connection = await database.OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction(deferred: false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM bartide_auth_sessions WHERE provider_user_id=@provider";
            command.Parameters.AddWithValue("@provider", providerId);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbException) { throw new AuthFailureException(Unavailable, 503); }
    }

    public async Task LimitAsync(string action, string email, string clientAddress, CancellationToken cancellationToken = default)
    {
        if (!LimitedActions.Contains(action)) throw new AuthFailureException("Choose a valid account action.", 400);
        email = Email(email);
        if (string.IsNullOrWhiteSpace(clientAddress) || clientAddress.Length > 200) clientAddress = "unknown";
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var window = now / 600;
        var expiresAt = (window + 1) * 600;
        var threshold = action == "signin" ? 20 : 8;
        var limited = false;
        try
        {
            await using var connection = await database.OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction(deferred: false);
            foreach (var source in new[] { "ip:" + clientAddress, "email:" + email })
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO bartide_auth_limits(id,count,expires_at) VALUES (@id,1,@expiry)
                    ON CONFLICT(id) DO UPDATE SET count=bartide_auth_limits.count+1 RETURNING count
                    """;
                command.Parameters.AddWithValue("@id", $"{action}:{window}:" + Hash(source));
                command.Parameters.AddWithValue("@expiry", expiresAt);
                var count = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
                limited |= count > threshold;
            }
            await using (var cleanup = connection.CreateCommand())
            {
                cleanup.Transaction = transaction;
                cleanup.CommandText = "DELETE FROM bartide_auth_limits WHERE expires_at<=@now";
                cleanup.Parameters.AddWithValue("@now", now);
                await cleanup.ExecuteNonQueryAsync(cancellationToken);
            }
            // Rejected attempts remain counted, including both email and trusted-IP buckets.
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbException) { throw new AuthFailureException(Unavailable, 503); }
        if (limited) throw new AuthFailureException("Too many attempts. Please wait ten minutes before trying again.", 429);
    }

    private static bool ValidToken(string? token) => !string.IsNullOrWhiteSpace(token) && token.Length <= 16384;
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string ProviderId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200 || value != value.Trim() || value.Any(char.IsControl))
            throw new AuthFailureException("Please verify your account before signing in.", 401);
        return value;
    }
    private static string Email(string? value)
    {
        var email = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(email) || email.Length > 254 || !email.Contains('@') || email.Any(char.IsControl))
            throw new AuthFailureException("Enter a valid email address.", 400);
        return email;
    }
}
