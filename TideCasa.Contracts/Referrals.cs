namespace TideCasa.Contracts;

public sealed record ReferralProfile(string Id, string Name, string Introduction, string Status, string? Code, int DiscountPercent, string TermsVersion, string TermsAcceptedAt, string CreatedAt, string UpdatedAt);
public sealed record ReferralSale(string Kind, long GrossCents, long RefundedCents, long CommissionCents, string PaidAt);
public sealed record ReferralTotals(long SaleCount, long NetSalesCents, long CommissionCents);
public sealed record ReferralApplication(ReferralProfile Profile, string Email, string ReviewToken, long NetSalesCents, long CommissionCents);
public sealed record ReferralDashboard(bool Owner, ReferralProfile? Profile, IReadOnlyList<ReferralSale> Sales, ReferralTotals Totals, IReadOnlyList<ReferralApplication> Applications, string TermsVersion);
public sealed record ApplyReferralRequest(string Name, string Introduction, bool AcceptedTerms, string TermsVersion);
public sealed record ReviewReferralRequest(string ExpectedReviewToken, string Status, string? Code, int DiscountPercent);
