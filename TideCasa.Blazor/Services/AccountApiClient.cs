using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TideCasa.Contracts;

namespace TideCasa.Blazor.Services;

public sealed record AuthApiResult<T>(HttpStatusCode Status, T? Value)
{
    public bool Succeeded => (int)Status is >= 200 and < 300;
    public bool Unavailable => (int)Status >= 500;
}

public sealed class AccountApiClient(IHttpClientFactory clients)
{
    public const string ClientName = "TideCasaAuthApi";

    public Task<AuthApiResult<T>> PostAsync<T>(string operation, object body, IPAddress? clientAddress, CancellationToken cancellationToken) =>
        SendAsync<T>(HttpMethod.Post, "api/v1/auth/" + operation, body, null, clientAddress, cancellationToken);

    public Task<AuthApiResult<AccountOverview>> GetAccountAsync(string token, CancellationToken cancellationToken) =>
        SendAsync<AccountOverview>(HttpMethod.Get, "api/v1/account", null, token, null, cancellationToken);

    public Task<AuthApiResult<WorkspaceAccess>> GetWorkspaceAccessAsync(string tenantId, string token, CancellationToken cancellationToken) =>
        SendAsync<WorkspaceAccess>(HttpMethod.Get, "api/v1/tenants/" + Uri.EscapeDataString(tenantId) + "/access", null, token, null, cancellationToken);

    public Task<AuthApiResult<AuthNotice>> SignOutAsync(string token, CancellationToken cancellationToken) =>
        SendAsync<AuthNotice>(HttpMethod.Post, "api/v1/auth/signout", null, token, null, cancellationToken);

    public Task<AuthApiResult<WorkspaceRegistration>> RegisterWorkspaceAsync(RegisterWorkspaceRequest request, string token, IPAddress? address, CancellationToken cancellationToken) =>
        SendAsync<WorkspaceRegistration>(HttpMethod.Post, "api/v1/account/workspace", request, token, address, cancellationToken);

    private async Task<AuthApiResult<T>> SendAsync<T>(HttpMethod method, string path, object? body, string? token, IPAddress? clientAddress, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = JsonContent.Create(body);
            if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            // This is the parsed connection address after trusted-proxy handling, never an incoming header.
            if (clientAddress is not null) request.Headers.TryAddWithoutValidation("X-Forwarded-For", clientAddress.ToString());
            using var response = await clients.CreateClient(ClientName).SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return new(response.StatusCode, default);
            if (response.StatusCode == HttpStatusCode.NoContent) return new(response.StatusCode, default);
            var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken);
            return value is null ? new(HttpStatusCode.ServiceUnavailable, default) : new(response.StatusCode, value);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            return new(HttpStatusCode.ServiceUnavailable, default);
        }
    }

    public static Uri ValidateBaseUrl(string? configured, bool development)
    {
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp
                && ((development && uri.IsLoopback) || (uri.Host == "api" && uri.Port == 8080)))))
            throw new InvalidOperationException("Configure a trusted HTTPS API base URL, internal http://api:8080/, or a development loopback URL.");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
    }
}
