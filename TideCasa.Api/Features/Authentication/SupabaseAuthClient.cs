using System.Text.Json;
using System.Text.RegularExpressions;
using System.Net.Http.Headers;

namespace TideCasa.Api.Features.Authentication;

// Credentials go only to the configured identity provider. Never follow redirects.
public sealed class SupabaseAuthClient(HttpClient http, IConfiguration configuration, IHostEnvironment environment)
{
    private (Uri Origin, string Key) Configuration()
    {
        if (!configuration.GetValue<bool>("Auth:Enabled"))
            throw new AuthFailureException("Sign-in is temporarily unavailable. Please try again later.", 503);
        var key = configuration["Auth:PublishableKey"] ?? "";
        var raw = configuration["Auth:SupabaseUrl"];
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var origin) || origin.UserInfo.Length != 0 ||
            origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0 ||
            !Regex.IsMatch(key, "^sb_publishable_[A-Za-z0-9_-]{16,}$", RegexOptions.CultureInvariant))
            throw Unavailable();
        var productionOrigin = origin.Scheme == "https" && origin.Host.EndsWith(".supabase.co", StringComparison.OrdinalIgnoreCase);
        var localTestOrigin = environment.IsDevelopment() && configuration.GetValue<bool>("Auth:AllowLocalTestProvider") &&
            origin.Scheme == "http" && origin.IsLoopback;
        if (!productionOrigin && !localTestOrigin) throw Unavailable();
        return (origin, key);
    }

    public async Task<JsonElement> SendAsync(string path, object? body, string? token, HttpMethod method, CancellationToken cancellationToken)
    {
        var config = Configuration();
        using var request = new HttpRequestMessage(method, new Uri(config.Origin, "/auth/v1" + path));
        request.Headers.Add("apikey", config.Key);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400 || status >= 500) throw Unavailable();
            if (status == 429) throw new AuthFailureException("Too many attempts. Please wait before trying again.", 429);
            if (!response.IsSuccessStatusCode) throw new AuthFailureException("Check your details or request a new email code.");
            if (status == 204 || response.Content.Headers.ContentLength == 0) return JsonSerializer.SerializeToElement(new { });
            return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            throw Unavailable();
        }
    }

    public async Task<VerifiedIdentity> VerifyAsync(string token, CancellationToken cancellationToken)
    {
        if (!ValidToken(token)) throw new AuthFailureException("Please sign in again.");
        var data = await SendAsync("/user", null, token, HttpMethod.Get, cancellationToken);
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
            !Guid.TryParseExact(id.GetString(), "D", out var providerId) ||
            !data.TryGetProperty("email", out var email) || email.ValueKind != JsonValueKind.String ||
            !data.TryGetProperty("email_confirmed_at", out var confirmed) || confirmed.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(confirmed.GetString(), out _) ||
            data.TryGetProperty("is_anonymous", out var anonymous) && anonymous.ValueKind != JsonValueKind.False)
            throw new AuthFailureException("Please verify your email before signing in.");
        var address = email.GetString()!.Trim().ToLowerInvariant();
        if (!ValidEmail(address)) throw new AuthFailureException("Please verify your email before signing in.");
        string? fullName = null;
        if (data.TryGetProperty("user_metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object &&
            metadata.TryGetProperty("full_name", out var name) && name.ValueKind == JsonValueKind.String)
        {
            var text = name.GetString()!;
            fullName = new string(text.Where(c => !char.IsControl(c)).Take(200).ToArray());
        }
        return new(providerId.ToString(), address, fullName);
    }

    public static bool ValidToken(string? token) => token is { Length: > 0 and < 3800 } &&
        Regex.IsMatch(token, "^[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant);
    public static bool ValidEmail(string? email) => email is { Length: > 0 and <= 254 } &&
        Regex.IsMatch(email, "^[^\\s@]+@[^\\s@]+\\.[^\\s@]+$", RegexOptions.CultureInvariant);
    private static AuthFailureException Unavailable() => new("Sign-in is temporarily unavailable. Please try again.", 503);
}
