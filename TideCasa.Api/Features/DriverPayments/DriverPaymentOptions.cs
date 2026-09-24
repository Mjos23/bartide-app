using System.Text.RegularExpressions;
using TideCasa.Api.Features.RestaurantOrdering;

namespace TideCasa.Api.Features.DriverPayments;

/// <summary>Driver funds are a separate, explicitly enabled Connect integration.</summary>
public sealed class DriverPaymentOptions
{
    public const string Purpose = "tide_driver_delivery";
    public string Key { get; }
    public string? ApiBase { get; }
    public string PublicBase { get; }
    public string WebhookSecret { get; }
    public string Environment { get; }
    public bool Sandbox { get; }
    public bool Configured { get; }
    public bool SetupEnabled { get; }
    public bool PaymentsEnabled { get; }
    public bool LocalTest { get; }

    public DriverPaymentOptions(IConfiguration configuration, IHostEnvironment environment)
    {
        var section = configuration.GetSection("DriverPayments");
        Environment = section["Environment"] ?? "sandbox";
        Sandbox = Environment == "sandbox";
        Key = section["RestrictedKey"] ?? "";
        WebhookSecret = section["WebhookSecret"] ?? "";
        var candidate = section["ApiBaseUrl"];
        LocalTest = environment.IsDevelopment() && Sandbox && section.GetValue<bool>("AllowLocalTestProvider") && LocalOrigin(candidate);
        ApiBase = LocalTest ? candidate!.TrimEnd('/') : null;
        var publicCandidate = section["PublicBaseUrl"];
        var publicValid = Uri.TryCreate(publicCandidate, UriKind.Absolute, out var origin)
            && origin.UserInfo.Length == 0 && origin.AbsolutePath == "/" && origin.Query.Length == 0 && origin.Fragment.Length == 0
            && (origin.Scheme == "https" || LocalTest && LocalOrigin(publicCandidate));
        PublicBase = publicValid ? origin!.AbsoluteUri.TrimEnd('/') : "";
        Configured = Environment is "sandbox" or "live" && publicValid
            && Regex.IsMatch(Key, Sandbox ? "^rk_test_[A-Za-z0-9_]{12,240}$" : "^rk_live_[A-Za-z0-9_]{12,240}$", RegexOptions.CultureInvariant)
            && (string.IsNullOrEmpty(candidate) || LocalTest);
        var approvedEnvironment = Sandbox || section.GetValue<bool>("LivePaymentsVerified");
        SetupEnabled = Configured && approvedEnvironment && section.GetValue<bool>("SetupEnabled");
        PaymentsEnabled = SetupEnabled && section.GetValue<bool>("PaymentsEnabled")
            && Regex.IsMatch(WebhookSecret, "^whsec_[A-Za-z0-9_]{12,240}$", RegexOptions.CultureInvariant);
    }

    public static bool AccountId(string? value) => value is not null && Regex.IsMatch(value, "^acct_[A-Za-z0-9]{6,80}$", RegexOptions.CultureInvariant);
    private static bool LocalOrigin(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "http" && uri.Host == "127.0.0.1" && uri.UserInfo.Length == 0
        && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0;
    public bool HostedUrl(string? value, bool onboarding) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.IsDefaultPort
        && uri.IdnHost == (onboarding ? "connect.stripe.com" : "checkout.stripe.com");
    public void RequireSetup()
    {
        if (!SetupEnabled) throw new OrderingException("Driver payment setup is not available yet.", 503, "driver_payments_disabled");
    }
    public void RequirePayments()
    {
        if (!PaymentsEnabled) throw new OrderingException("Driver payments are not available yet.", 503, "driver_payments_disabled");
    }
}
