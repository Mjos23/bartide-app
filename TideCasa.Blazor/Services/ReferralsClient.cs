using System.Net;
using System.Net.Http.Headers;
using TideCasa.Contracts;
namespace TideCasa.Blazor.Services;
public sealed class ReferralsClient(HttpClient client)
{
    public async Task<ReferralDashboard?> ReadAsync(string token, CancellationToken ct)
    {
        try
        {
            using var request = Request(HttpMethod.Get, "", token);
            using var response = await client.SendAsync(request, ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<ReferralDashboard>(cancellationToken: ct) : null;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException) { return null; }
    }
    public Task<HttpStatusCode> ApplyAsync(ApplyReferralRequest body, string token, CancellationToken ct) => SaveAsync("/apply", body, token, ct);
    public Task<HttpStatusCode> ReviewAsync(string id, ReviewReferralRequest body, string token, CancellationToken ct) => SaveAsync("/" + Uri.EscapeDataString(id) + "/review", body, token, ct);
    private async Task<HttpStatusCode> SaveAsync<T>(string path, T body, string token, CancellationToken ct)
    {
        try
        {
            using var request = Request(HttpMethod.Post, path, token); request.Content = JsonContent.Create(body);
            using var response = await client.SendAsync(request, ct); return response.StatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { return HttpStatusCode.ServiceUnavailable; }
    }
    private static HttpRequestMessage Request(HttpMethod method, string path, string token)
    { var request = new HttpRequestMessage(method, "api/v1/referrals" + path); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token); return request; }
}
