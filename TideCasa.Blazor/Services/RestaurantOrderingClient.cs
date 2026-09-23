using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TideCasa.Contracts;

namespace TideCasa.Blazor.Services;

public sealed record RestaurantApiResult<T>(HttpStatusCode Status, T? Value, string? Code = null)
{
    public bool Succeeded => (int)Status is >= 200 and < 300 && Value is not null;
    public bool Uncertain => (int)Status >= 500 || Status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
}

public sealed partial class RestaurantOrderingClient(HttpClient client, OrderingRequestContext requestContext)
{
    public Task<RestaurantApiResult<RestaurantMenu>> GetMenuAsync(string slug, string? table, CancellationToken ct = default) =>
        SendAsync<RestaurantMenu>(HttpMethod.Get, RestaurantPath(slug) + "/menu" + (string.IsNullOrWhiteSpace(table) ? "" : "?table=" + Uri.EscapeDataString(table)), null, null, ct);

    public Task<RestaurantApiResult<RestaurantQuote>> QuoteAsync(string slug, RestaurantQuoteRequest body, CancellationToken ct = default) =>
        SendAsync<RestaurantQuote>(HttpMethod.Post, RestaurantPath(slug) + "/quote", body, null, ct);

    public Task<RestaurantApiResult<RestaurantOrderReceipt>> OrderAsync(string slug, RestaurantOrderRequest body, CancellationToken ct = default) =>
        SendAsync<RestaurantOrderReceipt>(HttpMethod.Post, RestaurantPath(slug) + "/orders", body, null, ct);

    public Task<RestaurantApiResult<RestaurantOrderReceipt>> TrackAsync(string slug, RestaurantTrackingRequest body, CancellationToken ct = default) =>
        SendAsync<RestaurantOrderReceipt>(HttpMethod.Post, RestaurantPath(slug) + "/track", body, null, ct);

    public Task<RestaurantApiResult<RestaurantPhoneCheckout>> CheckoutAsync(string slug, RestaurantOrderRequest body, CancellationToken ct = default) =>
        SendAsync<RestaurantPhoneCheckout>(HttpMethod.Post, RestaurantPath(slug) + "/checkout", body, null, ct);
    public Task<RestaurantApiResult<RestaurantPhoneCheckout>> TrackCheckoutAsync(string slug, RestaurantTrackingRequest body, CancellationToken ct = default) =>
        SendAsync<RestaurantPhoneCheckout>(HttpMethod.Post, RestaurantPath(slug) + "/checkout/track", body, null, ct);
    public Task<RestaurantApiResult<RestaurantPhoneCheckout>> CancelCheckoutAsync(string slug, RestaurantTrackingRequest body, CancellationToken ct = default) =>
        SendAsync<RestaurantPhoneCheckout>(HttpMethod.Post, RestaurantPath(slug) + "/checkout/cancel", body, null, ct);

    public Task<RestaurantApiResult<RestaurantOrderingWorkspace>> GetWorkspaceAsync(string tenantId, string token, CancellationToken ct) =>
        SendAsync<RestaurantOrderingWorkspace>(HttpMethod.Get, WorkspacePath(tenantId), null, token, ct);

    public Task<RestaurantApiResult<RestaurantOrderingWorkspace>> CreateTableAsync(string tenantId, CreateRestaurantTableRequest body, string token, CancellationToken ct) =>
        SendAsync<RestaurantOrderingWorkspace>(HttpMethod.Post, WorkspacePath(tenantId) + "/tables", body, token, ct);

    public Task<RestaurantApiResult<RestaurantOrderingWorkspace>> SetTableAsync(string tenantId, string tableId, SetRestaurantTableStateRequest body, string token, CancellationToken ct) =>
        SendAsync<RestaurantOrderingWorkspace>(HttpMethod.Post, WorkspacePath(tenantId) + "/tables/" + Uri.EscapeDataString(tableId), body, token, ct);

    public async Task<byte[]?> GetQrAsync(string slug, string table, CancellationToken ct)
    {
        try
        {
            if (requestContext.ClientAddress is null) return null;
            using var request = new HttpRequestMessage(HttpMethod.Get, RestaurantPath(slug) + "/tables/" + Uri.EscapeDataString(table) + "/qr");
            ForwardAddress(request);
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "image/svg+xml") return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            return bytes.Length is > 0 and <= 256 * 1024 ? bytes : null;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or NotSupportedException) { return null; }
    }

    private async Task<RestaurantApiResult<T>> SendAsync<T>(HttpMethod method, string path, object? body, string? token, CancellationToken ct)
    {
        try
        {
            if (requestContext.ClientAddress is null) return new(HttpStatusCode.ServiceUnavailable, default, "client_context_unavailable");
            using var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = JsonContent.Create(body);
            if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            ForwardAddress(request);
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                string? code = null;
                try
                {
                    using var problem = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                    if (problem.RootElement.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String) code = value.GetString();
                }
                catch (JsonException) { }
                return new(response.StatusCode, default, code);
            }
            var result = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
            return result is null ? new(HttpStatusCode.ServiceUnavailable, default) : new(response.StatusCode, result);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        { return new(HttpStatusCode.ServiceUnavailable, default); }
    }

    private void ForwardAddress(HttpRequestMessage request)
    {
        if (requestContext.ClientAddress is { } address) request.Headers.TryAddWithoutValidation("X-Forwarded-For", address);
    }

    private static string RestaurantPath(string slug) => "api/v1/restaurants/" + Uri.EscapeDataString(slug);
    private static string WorkspacePath(string tenantId) => "api/v1/tenants/" + Uri.EscapeDataString(tenantId) + "/ordering";
}
