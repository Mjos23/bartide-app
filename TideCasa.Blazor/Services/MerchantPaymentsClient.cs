using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TideCasa.Contracts;

namespace TideCasa.Blazor.Services;

public sealed class MerchantPaymentsClient(HttpClient client, OrderingRequestContext context)
{
    public Task<RestaurantApiResult<MerchantPaymentStatus>> StatusAsync(string tenant, string token, CancellationToken ct) => Send<MerchantPaymentStatus>("api/v1/tenants/" + Uri.EscapeDataString(tenant) + "/payments/connect", null, token, ct);
    public Task<RestaurantApiResult<MerchantHostedLink>> OnboardAsync(string tenant, StartMerchantOnboardingRequest body, string token, CancellationToken ct) => Send<MerchantHostedLink>("api/v1/tenants/" + Uri.EscapeDataString(tenant) + "/payments/connect/onboarding", body, token, ct);
    public async Task<RestaurantApiResult<T>> Send<T>(string path, object? body, string? token, CancellationToken ct)
    {
        if (context.ClientAddress is null) return new(HttpStatusCode.ServiceUnavailable, default);
        try
        {
            using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, path);
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
                    if (problem.RootElement.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: <= 80 } found) code = found;
                }
                catch (JsonException) { }
                return new(response.StatusCode, default, code);
            }
            return new(response.StatusCode, await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct));
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException) { return new(HttpStatusCode.ServiceUnavailable, default); }
    }
}
