using System.Net;
using System.Net.Http.Headers;
using TideCasa.Contracts;
namespace TideCasa.Blazor.Services;
public sealed class DemoInboxClient(HttpClient client)
{
    public async Task<DemoInbox?> ReadAsync(string token, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/owner/demo-requests"); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request, ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<DemoInbox>(cancellationToken: ct) : null;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException) { return null; }
    }
    public async Task<HttpStatusCode> SaveAsync(string id, UpdateDemoInquiryRequest body, string token, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/owner/demo-requests/" + Uri.EscapeDataString(id)); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token); request.Content = JsonContent.Create(body);
            using var response = await client.SendAsync(request, ct); return response.StatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { return HttpStatusCode.ServiceUnavailable; }
    }
}
