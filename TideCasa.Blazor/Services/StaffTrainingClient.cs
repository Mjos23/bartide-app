using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TideCasa.Contracts;

namespace TideCasa.Blazor.Services;

public sealed record StaffTrainingResult(HttpStatusCode Status, StaffTrainingWorkspace? Value, string? Code = null)
{
    public bool Succeeded => (int)Status is >= 200 and < 300 && Value is not null;
    public bool Uncertain => (int)Status >= 500 || Status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
}

public sealed class StaffTrainingClient(HttpClient client, OrderingRequestContext requestContext)
{
    public Task<StaffTrainingResult> WorkspaceAsync(string tenantId, string token, CancellationToken ct) =>
        SendAsync(tenantId, "", HttpMethod.Get, null, token, ct);

    public async Task<StaffTrainingResult> SendAsync(string tenantId, string suffix, HttpMethod method, object? body, string token, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, "api/v1/tenants/" + Uri.EscapeDataString(tenantId) + "/team" + suffix);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (requestContext.ClientAddress is { } address) request.Headers.TryAddWithoutValidation("X-Forwarded-For", address);
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                string? code = null;
                try
                {
                    using var error = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                    if (error.RootElement.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String) code = value.GetString();
                }
                catch (JsonException) { }
                return new(response.StatusCode, null, code);
            }
            return new(response.StatusCode, await response.Content.ReadFromJsonAsync<StaffTrainingWorkspace>(cancellationToken: ct));
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        { return new(HttpStatusCode.ServiceUnavailable, null); }
    }
}
