using System.Net.Http.Json;
using System.Text.Json;
using TideCasa.Contracts;

namespace TideCasa.Blazor.Services;

public sealed class TideCasaApiClient(HttpClient client, OrderingRequestContext requestContext)
{
    public Task<DemoRequestReceipt> RequestDemoAsync(DemoRequest request) => PostAsync<DemoRequest, DemoRequestReceipt>("api/v1/demo-requests", request);
    public Task<PackageQuote> GetQuoteAsync(bool appStores, string? referralCode = null) => PostAsync<QuoteRequest, PackageQuote>("api/v1/pricing/quote", new(appStores, referralCode));

    private async Task<TResponse> PostAsync<TRequest, TResponse>(string path, TRequest request)
    {
        if (requestContext.ClientAddress is null) throw new ApiRequestException("Refresh the page before sending your request.");
        using var message = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(request) };
        message.Headers.TryAddWithoutValidation("X-Forwarded-For", requestContext.ClientAddress);
        using var response = await client.SendAsync(message);
        if (!response.IsSuccessStatusCode)
        {
            var title = "We could not complete your request. Please try again.";
            try
            {
                using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (error.RootElement.TryGetProperty("title", out var value)) title = value.GetString() ?? title;
            }
            catch (JsonException) { }
            throw new ApiRequestException(title);
        }
        return await response.Content.ReadFromJsonAsync<TResponse>() ?? throw new ApiRequestException("The response could not be confirmed. Please try again.");
    }
}

public sealed class ApiRequestException(string message) : Exception(message);
