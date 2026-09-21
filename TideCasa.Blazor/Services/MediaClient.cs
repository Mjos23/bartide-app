using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TideCasa.Contracts;

namespace TideCasa.Blazor.Services;

public sealed record MediaApiResult<T>(HttpStatusCode Status, T? Value, string? Code = null)
{
    public bool Succeeded => (int)Status is >= 200 and < 300;
    public bool Uncertain => (int)Status >= 500 || Status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
}

public sealed class MediaClient(HttpClient client, OrderingRequestContext requestContext)
{
    public Task<MediaApiResult<MediaWorkspace>> WorkspaceAsync(string tenant, string token, CancellationToken ct) =>
        SendAsync<MediaWorkspace>(HttpMethod.Get, Path(tenant), token, null, null, null, ct);

    public async Task<MediaApiResult<MediaUploadResult>> UploadAsync(string tenant, string kind, string token,
        string requestId, string filename, string type, long length, Stream input, CancellationToken ct)
    {
        using var content = new StreamContent(input, 81920);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        content.Headers.ContentLength = length;
        return await SendAsync<MediaUploadResult>(HttpMethod.Post, Path(tenant) + "/" + Uri.EscapeDataString(kind), token,
            content, requestId, filename, ct);
    }

    public Task<MediaApiResult<object>> DeleteAsync(string tenant, string kind, string id, string token, CancellationToken ct) =>
        SendAsync<object>(HttpMethod.Delete, Path(tenant) + "/" + Uri.EscapeDataString(kind) + "/" + Uri.EscapeDataString(id), token, null, null, null, ct);

    public async Task<HttpResponseMessage?> ReadPublicPhotoAsync(string id, bool head, string? range, CancellationToken ct)
    {
        try
        {
            if (requestContext.ClientAddress is null) return null;
            using var request = new HttpRequestMessage(head ? HttpMethod.Head : HttpMethod.Get, "api/v1/media/photos/" + Uri.EscapeDataString(id));
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", requestContext.ClientAddress);
            if (!string.IsNullOrEmpty(range)) request.Headers.TryAddWithoutValidation("Range", range);
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException) { return null; }
    }

    public async Task<HttpResponseMessage?> ReadAsync(string tenant, string kind, string id, string token, bool head, string? range, CancellationToken ct)
    {
        try
        {
            if (requestContext.ClientAddress is null) return null;
            using var request = Request(head ? HttpMethod.Head : HttpMethod.Get,
                Path(tenant) + "/" + Uri.EscapeDataString(kind) + "/" + Uri.EscapeDataString(id), token);
            if (!string.IsNullOrEmpty(range)) request.Headers.TryAddWithoutValidation("Range", range);
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException) { return null; }
    }

    private async Task<MediaApiResult<T>> SendAsync<T>(HttpMethod method, string path, string token, HttpContent? content,
        string? requestId, string? filename, CancellationToken ct)
    {
        try
        {
            if (requestContext.ClientAddress is null) return new(HttpStatusCode.ServiceUnavailable, default);
            using var request = Request(method, path, token); request.Content = content;
            if (requestId is not null) request.Headers.TryAddWithoutValidation("X-Request-Id", requestId);
            if (filename is not null) request.Headers.TryAddWithoutValidation("X-File-Name", Uri.EscapeDataString(filename));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            // Provider responses here contain metadata only. Bound them even if the API is misconfigured.
            await response.Content.LoadIntoBufferAsync(1024 * 1024, ct);
            if (response.StatusCode == HttpStatusCode.NoContent) return new(response.StatusCode, default);
            if (!response.IsSuccessStatusCode)
            {
                string? code = null;
                try { using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)); if (problem.RootElement.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String) code = value.GetString(); } catch (JsonException) { }
                return new(response.StatusCode, default, code);
            }
            return new(response.StatusCode, await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct));
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        { return new(HttpStatusCode.ServiceUnavailable, default); }
    }

    private HttpRequestMessage Request(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", requestContext.ClientAddress);
        return request;
    }
    private static string Path(string tenant) => "api/v1/tenants/" + Uri.EscapeDataString(tenant) + "/media";
}
