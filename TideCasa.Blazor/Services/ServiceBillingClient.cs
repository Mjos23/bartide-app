using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TideCasa.Contracts;

namespace TideCasa.Blazor.Services;

public sealed record BillingApiResult<T>(HttpStatusCode Status, T? Value, string? Code = null)
{
    public bool Succeeded => (int)Status is >= 200 and < 300;
    public bool Uncertain => (int)Status >= 500 || Status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
}

public sealed class ServiceBillingClient(HttpClient client, OrderingRequestContext requestContext)
{
    public Task<BillingApiResult<GuestPurchaseOptions>> GuestOptionsAsync(CancellationToken ct) => SendAsync<GuestPurchaseOptions>(HttpMethod.Get, "api/v1/service-purchases/options", null, null, ct);
    public Task<BillingApiResult<GuestCheckoutLink>> GuestCheckoutAsync(GuestCheckoutRequest request, CancellationToken ct) => SendAsync<GuestCheckoutLink>(HttpMethod.Post, "api/v1/service-purchases/checkout", null, request, ct);
    public Task<BillingApiResult<ServiceBillingActionResult>> DiscardGuestAsync(GuestPurchaseReset request, CancellationToken ct) => SendAsync<ServiceBillingActionResult>(HttpMethod.Post, "api/v1/service-purchases/discard", null, request, ct);
    public Task<BillingApiResult<GuestPurchaseStatus>> GuestStatusAsync(string order, string session, CancellationToken ct) => SendAsync<GuestPurchaseStatus>(HttpMethod.Get, GuestPath(order, session), null, null, ct);
    public Task<BillingApiResult<WorkspaceRegistration>> ClaimGuestAsync(string order, string session, string token, GuestPurchaseClaim request, CancellationToken ct) => SendAsync<WorkspaceRegistration>(HttpMethod.Post, GuestPath(order, session) + "/claim", token, request, ct);
    private static string GuestPath(string order, string session) => "api/v1/service-purchases/" + Uri.EscapeDataString(order) + "/" + Uri.EscapeDataString(session);
    public Task<BillingApiResult<ServiceBillingWorkspace>> WorkspaceAsync(string tenant, string token, CancellationToken ct) => SendAsync<ServiceBillingWorkspace>(HttpMethod.Get, Path(tenant), token, null, ct);
    public Task<BillingApiResult<ServiceBillingQuote>> QuoteAsync(string tenant, string token, ServiceQuoteRequest request, CancellationToken ct) => SendAsync<ServiceBillingQuote>(HttpMethod.Post, Path(tenant) + "/quote", token, request, ct);
    public Task<BillingApiResult<ServiceCheckoutLink>> CheckoutAsync(string tenant, string token, ServiceCheckoutRequest request, CancellationToken ct) => SendAsync<ServiceCheckoutLink>(HttpMethod.Post, Path(tenant) + "/checkout", token, request, ct);
    public Task<BillingApiResult<ServiceBillingActionResult>> ActionAsync(string tenant, string order, string action, string token, ServiceBillingActionRequest request, CancellationToken ct) =>
        SendAsync<ServiceBillingActionResult>(HttpMethod.Post, Path(tenant) + "/orders/" + Uri.EscapeDataString(order) + "/" + action, token, request, ct);
    public Task<BillingApiResult<ServiceCheckoutLink>> ResumeAsync(string tenant, string order, string token, ServiceBillingActionRequest request, CancellationToken ct) =>
        SendAsync<ServiceCheckoutLink>(HttpMethod.Post, Path(tenant) + "/orders/" + Uri.EscapeDataString(order) + "/resume-checkout", token, request, ct);

    private async Task<BillingApiResult<T>> SendAsync<T>(HttpMethod method, string path, string? token, object? body, CancellationToken ct)
    {
        try
        {
            if (requestContext.ClientAddress is null) return new(HttpStatusCode.ServiceUnavailable, default);
            using var request = new HttpRequestMessage(method, path);
            if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", requestContext.ClientAddress);
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            await response.Content.LoadIntoBufferAsync(1024 * 1024, ct);
            if (!response.IsSuccessStatusCode)
            {
                string? code = null;
                try { using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)); if (error.RootElement.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String) code = value.GetString(); } catch (JsonException) { }
                return new(response.StatusCode, default, code);
            }
            return new(response.StatusCode, await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct));
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException or NotSupportedException)
        { return new(HttpStatusCode.ServiceUnavailable, default); }
    }
    private static string Path(string tenant) => "api/v1/tenants/" + Uri.EscapeDataString(tenant) + "/billing";
}
