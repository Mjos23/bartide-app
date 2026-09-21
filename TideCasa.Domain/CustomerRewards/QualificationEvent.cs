namespace TideCasa.Domain.CustomerRewards;

public enum QualificationKind
{
    PaidItemPurchase,
    VerifiedVisit
}

public enum QualificationState
{
    Eligible,
    Cancelled,
    Refunded
}

/// <summary>
/// Trusted server-produced qualification data, not a public request contract.
/// The application must verify paid purchases or staff-approved visits before constructing
/// this input. A source ID names one immutable purchase line or visit within a tenant.
/// Supply one authoritative current state; conflicting snapshots are rejected.
/// </summary>
public sealed record QualificationEvent
{
    public string SourceEventId { get; }
    public string TenantId { get; }
    public string CustomerId { get; }
    public QualificationKind Kind { get; }
    public DateTimeOffset OccurredAt { get; }
    public int Units { get; }
    public string? ItemId { get; }
    public QualificationState State { get; }

    public QualificationEvent(string sourceEventId, string tenantId, string customerId,
        QualificationKind kind, DateTimeOffset occurredAt, int units = 1,
        string? itemId = null, QualificationState state = QualificationState.Eligible)
    {
        SourceEventId = RewardInput.Identifier(sourceEventId, nameof(sourceEventId));
        TenantId = RewardInput.Identifier(tenantId, nameof(tenantId));
        CustomerId = RewardInput.Identifier(customerId, nameof(customerId));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        if (units <= 0 || kind == QualificationKind.VerifiedVisit && units != 1)
            throw new ArgumentOutOfRangeException(nameof(units), "Purchases need positive units; each visit is one event.");
        if (kind == QualificationKind.PaidItemPurchase)
            ItemId = RewardInput.Identifier(itemId, nameof(itemId));
        else if (itemId is not null)
            throw new ArgumentException("A visit cannot claim an item purchase.", nameof(itemId));
        Kind = kind;
        OccurredAt = occurredAt;
        Units = units;
        State = state;
    }
}
