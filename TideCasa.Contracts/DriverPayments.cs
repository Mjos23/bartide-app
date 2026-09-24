namespace TideCasa.Contracts;

public sealed record DriverPaymentLine(string Id, string TenantId, string RestaurantName, string OrderId, string OrderNumber,
    string DriverName, string Source, int DriverPayCents, int PlatformFeeCents, int TotalCents, string Status,
    string CompletedAt, int Version, string? CheckoutUrl = null);
public sealed record DriverPayoutStatus(string State, bool SetupAvailable, bool PaymentsEnabled, bool Sandbox, string Message);
public sealed record DriverEarningsWorkspace(DriverPayoutStatus Payout, IReadOnlyList<DriverPaymentLine> Payments, int Page=0, bool HasMore=false);
public sealed record ClientDriverPaymentsWorkspace(string TenantId, string Name, bool CanApprove, bool PaymentsEnabled,
    bool Sandbox, IReadOnlyList<DriverPaymentLine> Payments, int Page=0, bool HasMore=false);
public sealed record StartDriverPayoutSetup(string RequestKey, bool ConfirmUsIndividual);
public sealed record ApproveDriverPayment(int ExpectedVersion, int DriverPayCents, bool ConfirmPayment, int ReturnPage=0);
public sealed record DriverPaymentCheckout(DriverPaymentLine Payment, string? Url);
public sealed record DriverHostedLink(string Url);
