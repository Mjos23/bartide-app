namespace TideCasa.Contracts;

public sealed record PackageQuote(int SetupCents, int AppStoresCents, int DiscountCents, int FirstMonthCents, int FirstPaymentCents, int MonthlyCents, string Currency, string TermsVersion);
public sealed record QuoteRequest(bool AppStores = false, string? ReferralCode = null);

// Public contracts contain data only. The API owns all calculations and authorizations.
