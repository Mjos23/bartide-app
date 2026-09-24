using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TideCasa.Contracts;

namespace TideCasa.Blazor.Services;

public sealed class ClientOnboardingClient(HttpClient client, OrderingRequestContext context)
{
    public Task<RestaurantApiResult<OnboardingWorkspace>> WorkspaceAsync(string tenant, string token, CancellationToken ct) => Send<OnboardingWorkspace>(Path(tenant), null, token, ct);
    public Task<RestaurantApiResult<OnboardingWorkspace>> BusinessAsync(string tenant, OnboardingSaveBusiness body, string token, CancellationToken ct) => Send<OnboardingWorkspace>(Path(tenant, "business"), body, token, ct);
    public Task<RestaurantApiResult<OnboardingWorkspace>> BrandAsync(string tenant, OnboardingSaveBrand body, string token, CancellationToken ct) => Send<OnboardingWorkspace>(Path(tenant, "brand"), body, token, ct);
    public Task<RestaurantApiResult<OnboardingWorkspace>> ServiceAsync(string tenant, OnboardingSaveService body, string token, CancellationToken ct) => Send<OnboardingWorkspace>(Path(tenant, "service"), body, token, ct);
    public Task<RestaurantApiResult<OnboardingWorkspace>> TeamAsync(string tenant, OnboardingSaveTeam body, string token, CancellationToken ct) => Send<OnboardingWorkspace>(Path(tenant, "team"), body, token, ct);
    public Task<RestaurantApiResult<OnboardingRelease>> BuildAsync(string tenant, OnboardingBuildRequest body, string token, CancellationToken ct) => Send<OnboardingRelease>(Path(tenant, "build"), body, token, ct);
    public Task<RestaurantApiResult<OnboardingRelease>> PreviewAsync(string tenant, string token, CancellationToken ct) => Send<OnboardingRelease>(Path(tenant, "preview"), null, token, ct);
    public Task<RestaurantApiResult<OnboardingWorkspace>> ApproveAsync(string tenant, OnboardingApproveRequest body, string token, CancellationToken ct) => Send<OnboardingWorkspace>(Path(tenant, "approve"), body, token, ct);
    public Task<RestaurantApiResult<OnboardingPracticeResult>> PracticeAsync(string tenant, OnboardingPracticeRequest body, string token, CancellationToken ct) => Send<OnboardingPracticeResult>(Path(tenant, "practice"), body, token, ct);
    public Task<RestaurantApiResult<OnboardingPublicSite>> PublicAsync(string slug, CancellationToken ct) => Send<OnboardingPublicSite>("api/v1/bars/" + Uri.EscapeDataString(slug), null, null, ct);

    private async Task<RestaurantApiResult<T>> Send<T>(string path, object? body, string? token, CancellationToken ct)
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
                    if (problem.RootElement.ValueKind == JsonValueKind.Object && problem.RootElement.TryGetProperty("code", out var found)
                        && found.ValueKind == JsonValueKind.String && found.GetString() is { Length: <= 80 } value) code = value;
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
    private static string Path(string tenant, string? operation = null) => "api/v1/tenants/" + Uri.EscapeDataString(tenant) + "/onboarding" + (operation is null ? "" : "/" + operation);
}
