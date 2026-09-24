using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileProviders;
using TideCasa.Api.Features.MerchantPayments;
using TideCasa.Api.Infrastructure.Payments;
using static TideCasa.Api.Infrastructure.Payments.StripeJson;

// A diagnostic command only. It cannot create accounts, sessions, payments, keys or destinations.
internal static class SandboxPreflight
{
    internal const string SelectedSandbox = "acct_1UHOd4QkpV0WCm81";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length is < 2 or > 3 || args.Length == 3 && args[2] != "--probe")
        {
            Console.Error.WriteLine("Usage: --sandbox-preflight <source-root> <report-path> [--probe]");
            return 64;
        }
        try
        {
            var apiRoot = Path.Combine(Path.GetFullPath(args[0]), "TideCasa.Api");
            var environmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? Environments.Production;
            if (!Regex.IsMatch(environmentName, "^[A-Za-z0-9_-]{1,40}$")) throw new InvalidOperationException();
            var builder = new ConfigurationBuilder().SetBasePath(apiRoot)
                .AddJsonFile("appsettings.json", optional: false)
                .AddJsonFile("appsettings." + environmentName + ".json", optional: true);
            if (environmentName == Environments.Development)
            {
                var secrets = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Microsoft", "UserSecrets", "TideCasa-Api-Development", "secrets.json");
                builder.AddJsonFile(secrets, optional: true);
            }
            var configuration = builder.AddEnvironmentVariables().Build();
            using var configurationLifetime = configuration as IDisposable;
            var report = await EvaluateAsync(configuration, new CheckEnvironment(environmentName, apiRoot), args.Length == 3);
            var path = Path.GetFullPath(args[1]); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, Json) + Environment.NewLine);
            Console.WriteLine(report.Status);
            foreach (var check in report.Checks) Console.WriteLine(check.Status + ": " + check.Name + " — " + check.Detail);
            Console.WriteLine("This read-only check does not verify onboarding, payment creation, 3DS or webhook delivery.");
            return report.Checks.Any(c => c.Status == "blocked") ? 2 : 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException or ArgumentException)
        {
            // Configuration exceptions can include secret values; never print the exception.
            Console.Error.WriteLine("The readiness check could not read its configuration or write its report.");
            return 2;
        }
    }

    internal static async Task<PreflightReport> EvaluateAsync(IConfiguration configuration, IHostEnvironment environment,
        bool probe, Func<StripeHttpTransport>? transportFactory = null)
    {
        var checks = new List<PreflightCheck>();
        void Need(string name, bool okay, string okayText, string missingText) =>
            checks.Add(new(name, okay ? "passed" : "blocked", okay ? okayText : missingText));
        var options = new MerchantPaymentOptions(configuration, environment);
        Need("restricted_key", Regex.IsMatch(options.Key, "^rk_test_[A-Za-z0-9_]{12,240}$"),
            "Sandbox restricted key is present; its value is omitted.", "Configure MerchantPayments:RestrictedKey with the selected sandbox's restricted key.");
        Need("sandbox_identity", options.PlatformAccount == SelectedSandbox,
            "The configured platform matches the selected Tide Casa sandbox.", "MerchantPayments:PlatformAccountId must match the selected Tide Casa sandbox.");
        Need("public_origin", options.PublicBase.Length != 0,
            "The owner return origin is valid.", "Configure MerchantPayments:PublicBaseUrl with the owner portal origin.");
        Need("real_provider_origin", string.IsNullOrEmpty(configuration["MerchantPayments:ApiBaseUrl"]),
            "The diagnostic is restricted to the real HTTPS Stripe API origin.", "Remove the local fixture API override before a real sandbox probe.");
        Need("publishable_key", Regex.IsMatch(options.PublishableKey, "^pk_test_[A-Za-z0-9_]{12,240}$"),
            "A sandbox browser key is present; matching account ownership still needs external verification.", "Configure MerchantPayments:PublishableKey for embedded sandbox setup.");
        Need("connect_webhook_secret", MerchantPaymentOptions.ValidSecret(options.SnapshotSecret),
            "The merchant snapshot signing secret is present.", "Configure MerchantPayments:ConnectWebhookSecret for connected-account snapshot events.");
        Need("account_webhook_secret", MerchantPaymentOptions.ValidSecret(options.ThinSecret),
            "The account thin-event signing secret is present.", "Configure MerchantPayments:AccountWebhookSecret for Accounts v2 thin events.");
        Need("separate_webhook_secrets", options.SnapshotSecret.Length > 0 && options.ThinSecret.Length > 0 && options.SnapshotSecret != options.ThinSecret,
            "The webhook endpoints have distinct secrets.", "Use each endpoint's own signing secret; these endpoints must remain separate.");
        checks.Add(new("feature_gates", "info", $"Hosted setup: {options.OnboardingEnabled}; checkout: {options.CheckoutEnabled}; embedded setup: {options.EmbeddedOnboardingEnabled}. This command never changes these gates."));

        var configReady = checks.All(c => c.Status != "blocked");
        var serverReady = options.Configured && options.PlatformAccount == SelectedSandbox
            && string.IsNullOrEmpty(configuration["MerchantPayments:ApiBaseUrl"]);
        var readChecksPassed = false;
        if (!probe) checks.Add(new("stripe_reads", "unverified", "No provider calls requested. Add --probe to check the configured sandbox with GET requests only."));
        else if (!serverReady) checks.Add(new("stripe_reads", "blocked", "No provider call made because the server sandbox configuration is incomplete or unsafe."));
        else
        {
            using var client = transportFactory?.Invoke() ?? new StripeHttpTransport(options.Key);
            try
            {
                // Authenticates the key's own account. Never select or infer identity from account-list contents.
                var account = await client.GetAsync("/v1/account", null, CancellationToken.None);
                var identityMatches = S(account, "object") == "account" && S(account, "id") == SelectedSandbox;
                Need("provider_identity", identityMatches, "The key resolves to the selected sandbox account.", "The key did not resolve to the selected sandbox account; further reads stopped.");
                if (identityMatches)
                {
                    var accounts = await client.GetAsync("/v2/core/accounts?limit=1", null, CancellationToken.None);
                    var data = P(accounts, "data");
                    var shapeValid = data.ValueKind == JsonValueKind.Array && data.GetArrayLength() <= 1
                        && data.EnumerateArray().All(a => S(a, "object") == "v2.core.account" && B(a, "livemode") == false && MerchantPaymentOptions.AccountId(S(a, "id")));
                    Need("accounts_v2_read", shapeValid, "Accounts v2 read accepted the pinned API request.", "Accounts v2 returned incomplete or unexpected sandbox evidence.");
                    readChecksPassed = shapeValid;
                    if (shapeValid) checks.Add(new("connected_accounts", data.GetArrayLength() == 0 ? "blocked" : "info",
                        data.GetArrayLength() == 0 ? "No open connected account exists for an onboarding/payment rehearsal. No account was created."
                        : "An account exists. This sample does not prove BarTide ownership, readiness or a saved tenant binding."));
                }
            }
            catch (StripeTransportException error)
            {
                checks.Add(new("stripe_reads", "blocked", "The read-only Stripe check failed: " + error.Kind + ". No provider body or credential was recorded."));
            }
        }
        return new(DateTimeOffset.UtcNow, StripeHttpTransport.ApiVersion, SelectedSandbox,
            checks.Any(c => c.Status == "blocked") ? "blocked" : readChecksPassed ? "read_checks_passed" : "configuration_checked",
            configReady, probe, readChecksPassed, false, checks,
            ["Real hosted and embedded onboarding", "Card-only and wallet policy", "Payment creation, cancellation and 3DS", "Webhook delivery and refund readback", "Existing-account OAuth model and authorization", "Deployment"]);
    }
}

internal sealed record PreflightCheck(string Name, string Status, string Detail);
internal sealed record PreflightReport(DateTimeOffset CheckedAt, string ApiVersion, string SandboxAccount,
    string Status, bool ConfigurationComplete, bool NetworkRequested, bool ReadChecksPassed,
    bool PaymentFlowVerified, IReadOnlyList<PreflightCheck> Checks, IReadOnlyList<string> StillUnverified);
internal sealed class CheckEnvironment(string name = "Production", string root = "") : IHostEnvironment
{
    public string EnvironmentName { get; set; } = name;
    public string ApplicationName { get; set; } = "TideCasa.Stripe.Checks";
    public string ContentRootPath { get; set; } = root;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
