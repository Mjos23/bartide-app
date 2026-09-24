using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TideCasa.Contracts;

namespace TideCasa.Blazor.Services;

public sealed partial class RestaurantManagementClient(HttpClient client, OrderingRequestContext requestContext)
{
    public Task<RestaurantApiResult<RestaurantMenuEditor>> GetMenuAsync(string tenant, string token, CancellationToken ct) =>
        SendAsync<RestaurantMenuEditor>(HttpMethod.Get, Path(tenant, "menu"), null, token, ct);
    public Task<RestaurantApiResult<RestaurantMenuEditor>> SaveProfileAsync(string tenant, SaveRestaurantProfileRequest body, string token, CancellationToken ct) =>
        SendAsync<RestaurantMenuEditor>(HttpMethod.Post, Path(tenant, "menu/profile"), body, token, ct);
    public Task<RestaurantApiResult<RestaurantMenuEditor>> SaveCategoryAsync(string tenant, SaveRestaurantCategoryRequest body, string token, CancellationToken ct) =>
        SendAsync<RestaurantMenuEditor>(HttpMethod.Post, Path(tenant, "menu/categories"), body, token, ct);
    public Task<RestaurantApiResult<RestaurantMenuEditor>> SaveItemAsync(string tenant, SaveRestaurantItemRequest body, string token, CancellationToken ct) =>
        SendAsync<RestaurantMenuEditor>(HttpMethod.Post, Path(tenant, "menu/items"), body, token, ct);
    public Task<RestaurantApiResult<RestaurantMenuEditor>> RemoveCategoryAsync(string tenant, string entry, RemoveRestaurantEntryRequest body, string token, CancellationToken ct) =>
        SendAsync<RestaurantMenuEditor>(HttpMethod.Post, Path(tenant, "menu/categories/" + Uri.EscapeDataString(entry) + "/remove"), body, token, ct);
    public Task<RestaurantApiResult<RestaurantMenuEditor>> RemoveItemAsync(string tenant, string entry, RemoveRestaurantEntryRequest body, string token, CancellationToken ct) =>
        SendAsync<RestaurantMenuEditor>(HttpMethod.Post, Path(tenant, "menu/items/" + Uri.EscapeDataString(entry) + "/remove"), body, token, ct);
    public Task<RestaurantApiResult<RestaurantOrderingSettings>> GetSettingsAsync(string tenant, string token, CancellationToken ct) =>
        SendAsync<RestaurantOrderingSettings>(HttpMethod.Get, Path(tenant, "ordering/settings"), null, token, ct);
    public Task<RestaurantApiResult<RestaurantOrderingSettings>> SaveSettingsAsync(string tenant, SaveRestaurantOrderingSettingsRequest body, string token, CancellationToken ct) =>
        SendAsync<RestaurantOrderingSettings>(HttpMethod.Post, Path(tenant, "ordering/settings"), body, token, ct);
    public Task<RestaurantApiResult<RestaurantOperationsWorkspace>> GetOperationsAsync(string tenant, string token, CancellationToken ct) =>
        SendAsync<RestaurantOperationsWorkspace>(HttpMethod.Get, Path(tenant, "ordering/operations"), null, token, ct);
    public Task<RestaurantApiResult<RestaurantOperationsWorkspace>> ChangeOrderAsync(string tenant, string order, ChangeRestaurantOrderRequest body, string token, CancellationToken ct) =>
        SendAsync<RestaurantOperationsWorkspace>(HttpMethod.Post, Path(tenant, "ordering/orders/" + Uri.EscapeDataString(order)), body, token, ct);

    private async Task<RestaurantApiResult<T>> SendAsync<T>(HttpMethod method, string path, object? body, string token, CancellationToken ct)
    {
        if (requestContext.ClientAddress is null || string.IsNullOrWhiteSpace(token)) return new(HttpStatusCode.ServiceUnavailable, default);
        try
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", requestContext.ClientAddress);
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                string? code = null;
                try
                {
                    using var problem = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                    if (problem.RootElement.ValueKind == JsonValueKind.Object && problem.RootElement.TryGetProperty("code", out var value)
                        && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: <= 80 } valueCode) code = valueCode;
                }
                catch (JsonException) { }
                return new(response.StatusCode, default, code);
            }
            var valueResult = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
            return valueResult is null ? new(HttpStatusCode.ServiceUnavailable, default) : new(response.StatusCode, valueResult);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        { return new(HttpStatusCode.ServiceUnavailable, default); }
    }

    private static string Path(string tenant, string path) => "api/v1/tenants/" + Uri.EscapeDataString(tenant) + "/" + path;
}
