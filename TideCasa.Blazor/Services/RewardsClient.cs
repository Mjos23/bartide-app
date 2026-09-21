using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
namespace TideCasa.Blazor.Services;

public sealed record RewardsResult<T>(HttpStatusCode Status, T? Value, string? Message = null)
{
    public bool Succeeded => (int)Status is >= 200 and < 300 && Value is not null;
    public bool Uncertain => (int)Status >= 500 || Status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
}
public sealed class RewardsClient(HttpClient client)
{
    public async Task<RewardsResult<T>> SendAsync<T>(string path, string token, object? body, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, "api/v1/" + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                string? message = null;
                try { using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)); if (problem.RootElement.TryGetProperty("title", out var title)) message = title.GetString(); } catch (JsonException) { }
                return new(response.StatusCode, default, message);
            }
            return new(response.StatusCode, await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct));
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        { return new(HttpStatusCode.ServiceUnavailable, default); }
    }
    public static string Owner(string id) => "tenants/" + Uri.EscapeDataString(id) + "/rewards";
    public static string Customer(string slug) => "restaurants/" + Uri.EscapeDataString(slug) + "/rewards";
}
