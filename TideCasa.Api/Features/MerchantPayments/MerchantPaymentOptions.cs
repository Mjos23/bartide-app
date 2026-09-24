using System.Text.RegularExpressions;

namespace TideCasa.Api.Features.MerchantPayments;

public sealed class MerchantFailure(string message, int status = 400, string code = "merchant_invalid") : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

public sealed class MerchantPaymentOptions
{
    public const string Purpose = "tide_restaurant_order";
    public const string Environment = "test";
    public string Key { get; }
    public string? ApiBase { get; }
    public string PublicBase { get; }
    public string PlatformAccount { get; }
    public string SnapshotSecret { get; }
    public string ThinSecret { get; }
    public bool Configured { get; }
    public bool OnboardingEnabled { get; }
    public bool CheckoutEnabled { get; }
    public bool LocalTest { get; }
    public string PublishableKey { get; }
    public bool EmbeddedOnboardingEnabled { get; }
    public MerchantPaymentOptions(IConfiguration configuration, IHostEnvironment environment)
    {
        var section = configuration.GetSection("MerchantPayments");
        Key = section["RestrictedKey"] ?? ""; PlatformAccount = section["PlatformAccountId"] ?? "";
        SnapshotSecret = section["ConnectWebhookSecret"] ?? ""; ThinSecret = section["AccountWebhookSecret"] ?? "";
        var candidate = section["ApiBaseUrl"];
        LocalTest = environment.IsDevelopment() && section.GetValue<bool>("AllowLocalTestProvider") && IsLocalOrigin(candidate, out _);
        ApiBase = LocalTest ? candidate!.TrimEnd('/') : null;
        var publicCandidate = section["PublicBaseUrl"];
        var publicValid = Uri.TryCreate(publicCandidate, UriKind.Absolute, out var origin) && origin.UserInfo.Length == 0 && origin.AbsolutePath == "/" && origin.Query.Length == 0 && origin.Fragment.Length == 0
            && (origin.Scheme == "https" || environment.IsDevelopment() && IsLocalOrigin(publicCandidate, out _));
        PublicBase = publicValid ? origin!.AbsoluteUri.TrimEnd('/') : "";
        Configured = publicValid && Regex.IsMatch(Key, "^rk_test_[A-Za-z0-9_]{12,240}$") && AccountId(PlatformAccount)
            && (string.IsNullOrEmpty(candidate) || LocalTest);
        OnboardingEnabled = Configured && section.GetValue<bool>("OnboardingEnabled");
        CheckoutEnabled = OnboardingEnabled && section.GetValue<bool>("CheckoutEnabled") && section.GetValue<bool>("CardsOnlyVerified")
            && ValidSecret(SnapshotSecret) && ValidSecret(ThinSecret);
        PublishableKey = section["PublishableKey"] ?? "";
        EmbeddedOnboardingEnabled = OnboardingEnabled && section.GetValue<bool>("EmbeddedOnboardingEnabled")
            && section.GetValue<bool>("EmbeddedOnboardingVerified") && Regex.IsMatch(PublishableKey, "^pk_test_[A-Za-z0-9_]{12,240}$");
    }
    public static bool AccountId(string? value) => value is not null && Regex.IsMatch(value, "^acct_[A-Za-z0-9]{6,80}$");
    public static bool ValidSecret(string value) => Regex.IsMatch(value, "^whsec_[A-Za-z0-9_]{12,240}$");
    private static bool IsLocalOrigin(string? value, out Uri? uri) => Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == "http" && uri.Host == "127.0.0.1" && uri.UserInfo.Length == 0 && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0;
    public bool HostedUrl(string? url, bool onboarding) => Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme == "https" && parsed.UserInfo.Length == 0 && parsed.IsDefaultPort
        && (onboarding ? parsed.IdnHost == "connect.stripe.com" : parsed.IdnHost == "checkout.stripe.com");
    public void RequireOnboarding() { if (!OnboardingEnabled) throw new MerchantFailure("Restaurant payment setup is not available yet.", 503, "merchant_disabled"); }
    public void RequireCheckout() { if (!CheckoutEnabled) throw new MerchantFailure("Phone payments are not available yet. Choose pay staff.", 503, "phone_unavailable"); }
}
