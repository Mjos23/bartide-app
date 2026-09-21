namespace TideCasa.Contracts;

public sealed record ServiceBillingQuote(bool AppStores, string? ReferralCode, int SetupCents, int StoresCents, int DiscountCents,
    int FirstPaymentCents, int MonthlyCents, string Currency, string TermsVersion, string Fingerprint);
public sealed record ServiceQuoteRequest(bool AppStores = false, string? ReferralCode = null);
public sealed record ServiceCheckoutRequest(string RequestId, bool AppStores, string? ReferralCode, string QuoteFingerprint, bool AcceptedTerms, string TermsVersion);
public sealed record ServiceCheckoutLink(string OrderId, string Url, string Environment);
public sealed record ServiceBillingActionRequest(string RequestId, bool Confirmed);
public sealed record ServiceBillingActionResult(string Message);
public sealed record ServiceBillingInvoice(string Id, string Kind, long AmountCents, string Status, long RefundedCents,
    long RefundPendingCents, long RefundFailedCents, string? PaidAt, string? Url);
public sealed record ServiceBillingOrder(string Id, string Environment, string Status, long InitialCents, long MonthlyCents, long FirstPaymentCents,
    bool AppStores, string SubscriptionStatus, bool CancelAtPeriodEnd, long? PeriodEnd, string? PaidAt,
    bool CanCancel, bool CanDiscard, IReadOnlyList<ServiceBillingInvoice> Invoices);
public sealed record HistoricalBillingOrder(string Id, string Environment, string Channel, string Status, long AmountCents,
    long RefundedCents, long RefundPendingCents, string? PaidAt, string? Url);
public sealed record ServiceBillingWorkspace(string TenantId, string Name, string Environment, bool CheckoutAvailable, bool CanPurchase,
    string AvailabilityMessage, IReadOnlyList<ServiceBillingOrder> Orders, IReadOnlyList<HistoricalBillingOrder> HistoricalOrders);

public sealed record GuestPurchaseOptions(bool CheckoutAvailable, string TermsVersion);
public sealed record GuestCheckoutRequest(string CheckoutKey, string Plan, bool AppStores, bool AcceptedTerms, string TermsVersion);
public sealed record GuestCheckoutLink(string OrderId, string Url, string Environment, bool Completed = false);
public sealed record GuestPurchaseStatus(string Status, string Plan, bool AppStores, long FirstPaymentCents, string Environment);
public sealed record GuestPurchaseClaim(string BusinessName, string ContactName, string Area);
public sealed record GuestPurchaseReset(string CheckoutKey);
