using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace TideCasa.Blazor.Services;

public sealed record SalesResult<T>(HttpStatusCode Status, T? Value, string? ProblemTitle = null)
{
    public bool Succeeded => (int)Status is >= 200 and < 300;
}

public sealed class SalesPipelineClient(HttpClient client)
{
    public const string BasePath = "api/v1/owner/sales";
    public async Task<SalesResult<T>> SendAsync<T>(string suffix, HttpMethod method, object? body, string token, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, BasePath + suffix);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await client.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
                return new(response.StatusCode, await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct));
            string? title = null;
            try
            {
                using var problem = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                if (problem.RootElement.TryGetProperty("title", out var field) && field.ValueKind == JsonValueKind.String
                    && field.GetString() is { Length: <= 200 } value) title = value;
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException) { }
            return new(response.StatusCode, default, title);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or NotSupportedException)
        { return new(HttpStatusCode.ServiceUnavailable, default); }
    }
}
