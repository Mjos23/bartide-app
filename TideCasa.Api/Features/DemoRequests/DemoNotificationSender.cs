using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
namespace TideCasa.Api.Features.DemoRequests;

public sealed record NotificationResult(string? ProviderId, bool Retryable, string? Code);
public sealed class DemoNotificationSender
{
    private readonly HttpClient client;
    private readonly string from = "", to = "", origin = "", key = "";
    private readonly Uri endpoint = new("https://api.resend.com/emails");
    public bool Ready { get; }
    public DemoNotificationSender(HttpClient client, IConfiguration configuration, IHostEnvironment environment)
    {
        this.client = client;
        var mode = configuration["Notifications:Mode"] ?? "disabled";
        if (mode == "disabled") return;
        if (mode is not ("resend" or "local-test")) throw new InvalidOperationException("Unknown notification mode.");
        if (mode == "local-test")
        {
            if (!environment.IsDevelopment() || configuration["Notifications:AllowLocalProvider"] != "true"
                || !Uri.TryCreate(configuration["Notifications:LocalEndpoint"], UriKind.Absolute, out var test)
                || test.Scheme != "http" || test.UserInfo.Length != 0 || test.Host is not ("localhost" or "127.0.0.1")
                || test.Query.Length != 0 || test.Fragment.Length != 0)
                throw new InvalidOperationException("Local notifications require an explicit Development loopback provider.");
            endpoint = test;
        }
        key = configuration["Notifications:ResendApiKey"] ?? "";
        from = configuration["Notifications:From"] ?? "Tide Casa <hello@auth.tide.casa>";
        to = configuration["Notifications:To"] ?? "hello@tide.casa";
        if (!MailAddress.TryCreate(from, out var sender) || !sender.Host.Equals("auth.tide.casa", StringComparison.OrdinalIgnoreCase)
            || to is not ("hello@tide.casa" or "mb2115323@gmail.com") || from.Any(char.IsControl)) return;
        if (!Uri.TryCreate(configuration["Notifications:PublicBaseUrl"], UriKind.Absolute, out var site)
            || site.UserInfo.Length != 0 || site.AbsolutePath != "/" || site.Query.Length != 0 || site.Fragment.Length != 0
            || !(site.Scheme == "https" || environment.IsDevelopment() && site.Scheme == "http" && site.Host is "localhost" or "127.0.0.1")) return;
        origin = site.GetLeftPart(UriPartial.Authority);
        Ready = !string.IsNullOrWhiteSpace(key) && (mode == "local-test" || key.StartsWith("re_", StringComparison.Ordinal));
    }
    public string Payload(string id) => JsonSerializer.Serialize(new
    {
        from, to = new[] { to }, subject = "New Tide Casa demo request",
        text = "A new demo request is ready in your owner inbox.\n\n" + origin + "/owner/demo-requests\n\nRequest reference: " + id + "\n\nNo appointment has been confirmed. Open the private inbox to review the customer's details and agree on a time."
    });
    public async Task<NotificationResult> SendAsync(string id, string payload, CancellationToken ct)
    {
        if (!Ready) return new(null, true, "not_configured");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Headers.Add("Idempotency-Key", "demo-request/" + id);
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            await response.Content.LoadIntoBufferAsync(64 * 1024, ct);
            if (!response.IsSuccessStatusCode) return new(null, (int)response.StatusCode >= 500 || response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Conflict or HttpStatusCode.RequestTimeout, "provider_" + (int)response.StatusCode);
            using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!data.RootElement.TryGetProperty("id", out var value) || value.ValueKind != JsonValueKind.String
                || !Guid.TryParseExact(value.GetString(), "D", out _)) return new(null, true, "unconfirmed_response");
            return new(value.GetString(), false, null);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException)
        { return new(null, true, "unconfirmed_send"); }
    }
}
