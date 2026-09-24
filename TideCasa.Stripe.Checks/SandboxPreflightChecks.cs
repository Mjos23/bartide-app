using System.Net;
using System.Text;
using System.Text.Json;
using TideCasa.Api.Infrastructure.Payments;

internal static class SandboxPreflightChecks
{
    internal static async Task RunAsync(Action<string, bool> check)
    {
        const string key = "rk_test_synthetic_preflight_000000";
        Dictionary<string, string?> Settings() => new()
        {
            ["MerchantPayments:RestrictedKey"] = key,
            ["MerchantPayments:PlatformAccountId"] = SandboxPreflight.SelectedSandbox,
            ["MerchantPayments:PublicBaseUrl"] = "https://owner.example.invalid",
            ["MerchantPayments:PublishableKey"] = "pk_test_synthetic_preflight_000000",
            ["MerchantPayments:ConnectWebhookSecret"] = "whsec_synthetic_snapshot_000000",
            ["MerchantPayments:AccountWebhookSecret"] = "whsec_synthetic_thin_000000"
        };
        IConfiguration Config(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var networkCalls = 0;
        StripeHttpTransport Never() { networkCalls++; throw new InvalidOperationException("Unexpected network access"); }
        var missing = await SandboxPreflight.EvaluateAsync(Config([]), new CheckEnvironment(), true, Never);
        check("Preflight missing configuration stops before any provider request", missing.Status == "blocked" && !missing.ConfigurationComplete && networkCalls == 0);
        foreach (var (field, value) in new[]
        {
            ("RestrictedKey", "rk_live_synthetic_preflight_000000"),
            ("PlatformAccountId", "acct_differentplatform"),
            ("PublicBaseUrl", "https://owner.example.invalid/not-an-origin"),
            ("ApiBaseUrl", "http://127.0.0.1:12345")
        })
        {
            var values = Settings(); values["MerchantPayments:" + field] = value;
            var result = await SandboxPreflight.EvaluateAsync(Config(values), new CheckEnvironment("Development"), true, Never);
            check("Preflight rejects unsafe or mismatched " + field, result.Status == "blocked" && !result.ReadChecksPassed && networkCalls == 0);
        }
        var configOnly = await SandboxPreflight.EvaluateAsync(Config(Settings()), new CheckEnvironment(), false, Never);
        check("Complete configuration does not infer payment verification or enable features", configOnly.ConfigurationComplete && configOnly.Status == "configuration_checked"
            && !configOnly.ReadChecksPassed && !configOnly.PaymentFlowVerified && networkCalls == 0 && configOnly.Checks.Single(c => c.Name == "feature_gates").Detail.Contains("False"));
        var sharedSecrets = Settings(); sharedSecrets["MerchantPayments:AccountWebhookSecret"] = sharedSecrets["MerchantPayments:ConnectWebhookSecret"];
        var shared = await SandboxPreflight.EvaluateAsync(Config(sharedSecrets), new CheckEnvironment(), false, Never);
        check("Preflight refuses a shared snapshot and thin endpoint signing secret", shared.Status == "blocked" && shared.Checks.Single(c => c.Name == "separate_webhook_secrets").Status == "blocked");

        foreach (var mode in new[] { "valid", "empty", "wrong-platform", "live-account", "missing-mode", "permission-error" })
        {
            var requests = new List<string>(); var correctWire = true;
            var report = await SandboxPreflight.EvaluateAsync(Config(Settings()), new CheckEnvironment(), true,
                () => new StripeHttpTransport(key, handler: new Handler((request, token) =>
                {
                    requests.Add(request.RequestUri!.PathAndQuery);
                    correctWire &= request.Method == HttpMethod.Get && !request.Headers.Contains("Stripe-Account")
                        && !request.Headers.Contains("Idempotency-Key") && request.Headers.GetValues("Stripe-Version").Single() == "2026-08-26.dahlia"
                        && request.Headers.Authorization!.ToString() == "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(key + ":"));
                    if (mode == "permission-error") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
                    { Content = new StringContent("{\"error\":{\"message\":\"PRIVATE_PROVIDER_MARKER\",\"code\":\"permission_denied\"}}") });
                    var json = request.RequestUri.AbsolutePath == "/v1/account"
                        ? JsonSerializer.Serialize(new { id = mode == "wrong-platform" ? "acct_wrongplatform" : SandboxPreflight.SelectedSandbox, @object = "account" })
                        : mode == "empty" ? "{\"data\":[],\"next_page_url\":null}"
                        : mode == "missing-mode" ? "{\"data\":[{\"id\":\"acct_syntheticmerchant\",\"object\":\"v2.core.account\"}]}"
                        : JsonSerializer.Serialize(new { data = new[] { new { id = "acct_syntheticmerchant", @object = "v2.core.account", livemode = mode == "live-account" } } });
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
                })));
            check("Preflight read boundary " + mode, correctWire && requests.SequenceEqual(mode is "wrong-platform" or "permission-error"
                ? ["/v1/account"] : new[] { "/v1/account", "/v2/core/accounts?limit=1" }));
            check("Preflight outcome " + mode, !report.PaymentFlowVerified && (mode == "valid"
                ? report.Status == "read_checks_passed" && report.ReadChecksPassed
                : report.Status == "blocked" && report.ReadChecksPassed == (mode == "empty")));
            var serialized = JsonSerializer.Serialize(report);
            check("Preflight redacts credentials and provider bodies " + mode,
                Settings().Where(p => p.Key.EndsWith("Key") || p.Key.EndsWith("Secret")).All(p => !serialized.Contains(p.Value!)) && !serialized.Contains("PRIVATE_PROVIDER_MARKER"));
        }
    }
}
