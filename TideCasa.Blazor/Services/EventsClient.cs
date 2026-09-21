using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TideCasa.Contracts;

namespace TideCasa.Blazor.Services;

public sealed record EventsResult<T>(HttpStatusCode Status, T? Value, string? Code = null)
{
    public bool Succeeded => (int)Status is >= 200 and < 300 && Value is not null;
    public bool Uncertain => (int)Status >= 500 || Status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
}

public sealed class EventsClient(HttpClient client, OrderingRequestContext context)
{
    public static string OwnerPath(string id, string suffix = "") => "api/v1/tenants/" + Uri.EscapeDataString(id) + "/events" + suffix;
    public static string PublicPath(string slug, string suffix = "") => "api/v1/restaurants/" + Uri.EscapeDataString(slug) + "/events" + suffix;
    public Task<EventsResult<RestaurantEvents>> ListAsync(string key, bool owner, string? token, CancellationToken ct) =>
        SendAsync<RestaurantEvents>(owner ? OwnerPath(key) : PublicPath(key), HttpMethod.Get, null, token, ct);
    public Task<EventsResult<EventGuestList>> GuestsAsync(string id, string eventId, string token, CancellationToken ct) =>
        SendAsync<EventGuestList>(OwnerPath(id, "/" + Uri.EscapeDataString(eventId) + "/guests"), HttpMethod.Get, null, token, ct);
    public Task<EventsResult<MyEventReservations>> MineAsync(string slug, string token, CancellationToken ct) =>
        SendAsync<MyEventReservations>(PublicPath(slug, "/mine"), HttpMethod.Get, null, token, ct);
    public async Task<EventsResult<T>> SendAsync<T>(string path, HttpMethod method, object? body, string? token, CancellationToken ct)
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
                    if (problem.RootElement.ValueKind == JsonValueKind.Object && problem.RootElement.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: <= 80 } found) code = found;
                }
                catch (JsonException) { }
                return new(response.StatusCode, default, code);
            }
            return new(response.StatusCode, await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct));
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException) { return new(HttpStatusCode.ServiceUnavailable, default); }
    }
}
