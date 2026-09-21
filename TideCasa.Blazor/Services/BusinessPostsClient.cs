using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TideCasa.Contracts;

namespace TideCasa.Blazor.Services;

public sealed record PostsResult<T>(HttpStatusCode Status, T? Value, string? Code = null)
{
    public bool Succeeded => (int)Status is >= 200 and < 300;
    public bool Uncertain => (int)Status >= 500 || Status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
}

public sealed class BusinessPostsClient(HttpClient client, OrderingRequestContext context)
{
    public static string OwnerPath(string id, string suffix = "") => "api/v1/tenants/" + Uri.EscapeDataString(id) + "/posts" + suffix;
    public static string PublicPath(string slug, string suffix = "") => "api/v1/restaurants/" + Uri.EscapeDataString(slug) + "/posts" + suffix;
    public async Task<PostsResult<T>> SendAsync<T>(string path, HttpMethod method, object? body, string? token, CancellationToken ct)
    {
        if (context.ClientAddress is null) return new(HttpStatusCode.ServiceUnavailable, default);
        try
        {
            using var request = new HttpRequestMessage(method, path);
            if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", context.ClientAddress);
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                string? code = null;
                try
                {
                    using var problem = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                    if (problem.RootElement.ValueKind == JsonValueKind.Object && problem.RootElement.TryGetProperty("code", out var value)
                        && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: <= 80 } found) code = found;
                }
                catch (JsonException) { }
                return new(response.StatusCode, default, code);
            }
            return response.StatusCode == HttpStatusCode.NoContent ? new(response.StatusCode, default)
                : new(response.StatusCode, await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct));
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        { return new(HttpStatusCode.ServiceUnavailable, default); }
    }
}
