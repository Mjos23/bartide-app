using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Stripe;

namespace TideCasa.Api.Features.ServiceBilling;

public sealed class BillingException(string message, int status = 409, string code = "billing_review") : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

public sealed class ServiceBillingProvider : IDisposable
{
    public const string ApiVersion = "2026-08-26.dahlia";
    public const string TermsVersion = "2026-09-maintenance-v1";
    public const string Purpose = "tide_service_v2";
    private readonly StripeClient? stripe;
    private readonly HttpClient? http;
    public string Environment { get; }
    public string Origin { get; } = "";
    public string AccountId { get; } = "";
    public string MethodConfiguration { get; } = "";
    public string WebhookSecret { get; } = "";
    public bool Ready => stripe is not null;
    public bool CheckoutReady { get; }

    public ServiceBillingProvider(IConfiguration configuration, IHostEnvironment host)
    {
        Environment = configuration["ServiceBilling:Environment"] == "live" ? "live" : "sandbox";
        var key = configuration["ServiceBilling:RestrictedKey"] ?? "";
        WebhookSecret = configuration["ServiceBilling:WebhookSecret"] ?? "";
        AccountId = configuration["ServiceBilling:AccountId"] ?? "";
        MethodConfiguration = configuration["ServiceBilling:PaymentMethodConfigurationId"] ?? "";
        var configuredOrigin = configuration["ServiceBilling:PublicOrigin"] ?? "";
        if (Uri.TryCreate(configuredOrigin, UriKind.Absolute, out var origin) && origin.Scheme == "https" && origin.IsDefaultPort
            && origin.UserInfo.Length == 0 && configuredOrigin == origin.GetLeftPart(UriPartial.Authority)) Origin = configuredOrigin;
        var apiBase = StripeClient.DefaultApiBase;
        var testApi = configuration["ServiceBilling:DevelopmentApiBase"];
        if (!string.IsNullOrEmpty(testApi))
        {
            if (!host.IsDevelopment() || configuration["ServiceBilling:AllowLocalTestProvider"] != "true" || Environment != "sandbox"
                || !Uri.TryCreate(testApi, UriKind.Absolute, out var test) || test.Scheme != "http" || test.Host != "127.0.0.1"
                || test.UserInfo.Length != 0 || test.AbsolutePath != "/" || test.Query.Length != 0 || test.Fragment.Length != 0
                || key != "rk_test_tide_local_fixture")
                throw new InvalidOperationException("A local Stripe fixture requires explicit Development configuration and the synthetic fixture key.");
            apiBase = testApi.TrimEnd('/');
        }
        if (!Regex.IsMatch(key, Environment == "live" ? "^rk_live_[A-Za-z0-9_]+$" : "^rk_test_[A-Za-z0-9_]+$")
            || !WebhookSecret.StartsWith("whsec_", StringComparison.Ordinal) || WebhookSecret.Length < 16
            || !Regex.IsMatch(AccountId, "^acct_[A-Za-z0-9]+$") || Origin.Length == 0
            || Environment == "live" && configuration["ServiceBilling:LiveEnabled"] != "true") return;
        if (StripeConfiguration.ApiVersion != ApiVersion) throw new InvalidOperationException("The service billing SDK API version must match its reviewed notification version.");
        http = new HttpClient(new BoundedStripeHandler(new Uri(apiBase))) { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 1024 * 1024 };
        stripe = new StripeClient(key, httpClient: new SystemNetHttpClient(http, maxNetworkRetries: 1, enableTelemetry: false), apiBase: apiBase);
        CheckoutReady = configuration["ServiceBilling:CheckoutEnabled"] == "true" && configuration["ServiceBilling:CardsOnlyVerified"] == "true"
            && Regex.IsMatch(MethodConfiguration, "^pmc_[A-Za-z0-9]+$");
    }

    public async Task CheckAccountAsync(CancellationToken ct)
    {
        NeedReady(); var account = await GetAsync("/v1/account", ct);
        if (S(account, "id") != AccountId || S(account, "object") != "account") throw Review();
    }

    public async Task CheckMethodsAsync(CancellationToken ct)
    {
        if (!CheckoutReady) throw Unavailable();
        await CheckAccountAsync(ct);
        var config = await GetAsync("/v1/payment_method_configurations/" + MethodConfiguration, ct);
        if (S(config, "id") != MethodConfiguration || !B(config, "active") || !Enabled(P(config, "card"))) throw new BillingException("Card payments are still being verified.", 503, "cards_unverified");
        foreach (var field in config.EnumerateObject())
            if (field.Name != "card" && field.Value.ValueKind == JsonValueKind.Object && Enabled(field.Value))
                throw new BillingException("Card payments are still being verified.", 503, "cards_unverified");
    }
    private static bool Enabled(JsonElement method) => B(method, "available") && S(P(method, "display_preference"), "value") == "on";

    public Task<JsonElement> GetAsync(string path, CancellationToken ct) => SendAsync(HttpMethod.Get, path, null, null, ct);
    public Task<JsonElement> PostAsync(string path, IReadOnlyDictionary<string, string> parameters, string key, CancellationToken ct) => SendAsync(HttpMethod.Post, path, parameters, key, ct);
    private async Task<JsonElement> SendAsync(HttpMethod method, string path, IReadOnlyDictionary<string, string>? parameters, string? key, CancellationToken ct)
    {
        NeedReady();
        if (!path.StartsWith("/v1/", StringComparison.Ordinal) || path.Contains("..", StringComparison.Ordinal) || path.Contains('#')) throw Review();
        string? content = null;
        if (parameters is not null) { using var encoded = new FormUrlEncodedContent(parameters); content = await encoded.ReadAsStringAsync(ct); }
        var response = await stripe!.RawRequestAsync(method, path, content, new RawRequestOptions { IdempotencyKey = key }, ct);
        if (response.Content.Length > 1024 * 1024) throw Unavailable();
        using var json = JsonDocument.Parse(response.Content, new JsonDocumentOptions { MaxDepth = 48 });
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw Review();
        return json.RootElement.Clone();
    }
    public void NeedReady() { if (!Ready) throw Unavailable(); }
    public static BillingException Unavailable() => new("Payments are being connected. Please try again later.", 503, "billing_unavailable");
    public static BillingException Review() => new("Payment details need a review before this change can continue.", 422, "payment_association");
    public static string? HostedUrl(string? value, string host)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.IsDefaultPort && uri.Host == host
            && uri.UserInfo.Length == 0 ? uri.AbsoluteUri : null;
    }
    public static bool Id(string? value, string prefix) => value is not null && Regex.IsMatch(value, "^" + prefix + (prefix == "cs" ? "_(?:test_|live_)?" : "_") + "[A-Za-z0-9]{1,180}$", RegexOptions.CultureInvariant);
    public static JsonElement P(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var property) ? property : default;
    public static string? S(JsonElement value, string key) { var property = P(value, key); return property.ValueKind == JsonValueKind.String ? property.GetString() : null; }
    public static bool B(JsonElement value, string key) => P(value, key).ValueKind == JsonValueKind.True;
    public static long N(JsonElement value, string key) => P(value, key).TryGetInt64Safe();
    public static string? ObjectId(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : S(value, "id");
    public static string? ObjectId(JsonElement value, string key) => ObjectId(P(value, key));
    public static IReadOnlyList<JsonElement> Data(JsonElement value)
    { var data = P(value, "data"); if (data.ValueKind != JsonValueKind.Array) throw Review(); return data.EnumerateArray().Select(x => x.Clone()).ToArray(); }
    public void Dispose() => http?.Dispose();

    private sealed class BoundedStripeHandler(Uri expected) : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri is not { } uri || uri.Scheme != expected.Scheme || uri.Host != expected.Host || uri.Port != expected.Port) throw new HttpRequestException("Unexpected payment provider destination.");
            var response = await base.SendAsync(request, ct);
            try { await response.Content.LoadIntoBufferAsync(1024 * 1024, ct); return response; }
            catch { response.Dispose(); throw; }
        }
    }
}

internal static class BillingJsonExtensions
{
    internal static long TryGetInt64Safe(this JsonElement element) => element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var value) ? value : long.MinValue;
}
