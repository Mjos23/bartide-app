using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TideCasa.Contracts;

namespace TideCasa.Blazor.Services;

public sealed class LaunchReviewClient(HttpClient client, OrderingRequestContext context)
{
    public Task<RestaurantApiResult<LaunchReviewWorkspace>> ListAsync(string? after, string token, CancellationToken ct) => Send<LaunchReviewWorkspace>("api/v1/owner/launch-review" + (string.IsNullOrEmpty(after) ? "" : "?after=" + Uri.EscapeDataString(after)), null, token, ct);
    public Task<RestaurantApiResult<LaunchReviewProject>> TransitionAsync(string id, LaunchReviewTransitionRequest body, string token, CancellationToken ct) => Send<LaunchReviewProject>("api/v1/owner/launch-review/" + Uri.EscapeDataString(id) + "/transition", body, token, ct);
    private async Task<RestaurantApiResult<T>> Send<T>(string path, object? body, string token, CancellationToken ct)
    {
        if (context.ClientAddress is null) return new(HttpStatusCode.ServiceUnavailable, default);
        try
        {
            using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token); request.Headers.TryAddWithoutValidation("X-Forwarded-For", context.ClientAddress);
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                string? code = null;
                try { using var problem = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct); if (problem.RootElement.TryGetProperty("code", out var found) && found.ValueKind == JsonValueKind.String && found.GetString() is { Length: <= 80 } value) code = value; }
                catch (JsonException) { }
                return new(response.StatusCode, default, code);
            }
            return new(response.StatusCode, await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct));
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException) { return new(HttpStatusCode.ServiceUnavailable, default); }
    }
}
