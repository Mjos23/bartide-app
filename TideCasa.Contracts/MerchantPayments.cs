namespace TideCasa.Contracts;

public sealed record MerchantPaymentStatus(string TenantId, string Name, string State, bool Sandbox,
    bool OnboardingAvailable, bool PhoneCheckoutAvailable, string? CheckedAt, string Message)
{
    public string ConnectionState { get; init; } = "unknown";
    public string CardPaymentsState { get; init; } = "unknown";
    public string PayoutsState { get; init; } = "unknown";
    public string CheckoutState { get; init; } = "disabled";
    public string NextAction { get; init; } = "refresh";
    public bool Stale { get; init; }
    public IReadOnlyList<string> RequirementCategories { get; init; } = [];
    public string? BusinessEmail { get; init; }
    public string? DashboardUrl { get; init; }
    public string? TestOrderUrl { get; init; }
    public bool EmbeddedOnboardingAvailable { get; init; }
}
public sealed record StartMerchantOnboardingRequest(string RequestKey, bool ConfirmUsBusiness);
public sealed record MerchantHostedLink(string Url);
public sealed record MerchantEmbeddedSession(string ClientSecret, string PublishableKey, long ExpiresAt)
{
    public override string ToString() => "MerchantEmbeddedSession { Sensitive session omitted }";
}
public sealed record RestaurantPhoneCheckout(RestaurantOrderReceipt Receipt, string State, string? CheckoutUrl, string? ExpiresAt,
    int RefundedCents = 0, int PendingRefundCents = 0);
