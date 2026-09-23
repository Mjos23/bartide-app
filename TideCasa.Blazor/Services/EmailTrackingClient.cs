using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace TideCasa.Blazor.Services;

public sealed class EmailTrackingClient(HttpClient client)
{
    public async Task<SalesResult<T>> SendAsync<T>(string path, HttpMethod method, object? body, string? token, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path);
            if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await client.SendAsync(request, ct);
            return response.IsSuccessStatusCode
                ? new(response.StatusCode, await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct))
                : new(response.StatusCode, default);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or NotSupportedException)
        { return new(HttpStatusCode.ServiceUnavailable, default); }
    }
}
