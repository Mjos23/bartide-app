namespace TideCasa.Contracts;

public sealed record MerchantPaymentStatus(string TenantId, string Name, string State, bool Sandbox,
    bool OnboardingAvailable, bool PhoneCheckoutAvailable, string? CheckedAt, string Message);
public sealed record StartMerchantOnboardingRequest(string RequestKey, bool ConfirmUsBusiness);
public sealed record MerchantHostedLink(string Url);
public sealed record RestaurantPhoneCheckout(RestaurantOrderReceipt Receipt, string State, string? CheckoutUrl, string? ExpiresAt,
    int RefundedCents = 0, int PendingRefundCents = 0);
