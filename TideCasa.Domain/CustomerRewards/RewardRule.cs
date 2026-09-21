namespace TideCasa.Domain.CustomerRewards;

public enum RewardRuleKind
{
    PaidItemPunchCard,
    MonthlyVerifiedVisits
}

/// <summary>
/// An immutable version of an owner's reward rule. Persisted rule versions must never be
/// changed in place; create a new VersionId when changing the threshold, item or timezone.
/// </summary>
public sealed record RewardRule
{
    public string Id { get; }
    public string VersionId { get; }
    public string TenantId { get; }
    public RewardRuleKind Kind { get; }
    public int Threshold { get; }
    public string? ItemId { get; }
    public string TimeZoneId { get; }

    public RewardRule(string id, string versionId, string tenantId, RewardRuleKind kind,
        int threshold, string timeZoneId, string? itemId = null)
    {
        Id = RewardInput.Identifier(id, nameof(id));
        VersionId = RewardInput.Identifier(versionId, nameof(versionId));
        TenantId = RewardInput.Identifier(tenantId, nameof(tenantId));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (threshold <= 0) throw new ArgumentOutOfRangeException(nameof(threshold));
        if (kind == RewardRuleKind.PaidItemPunchCard)
            ItemId = RewardInput.Identifier(itemId, nameof(itemId));
        else if (itemId is not null)
            throw new ArgumentException("A monthly visit rule cannot filter menu items.", nameof(itemId));
        TimeZoneId = RewardInput.Identifier(timeZoneId, nameof(timeZoneId));
        _ = RewardInput.TimeZone(TimeZoneId);
        Kind = kind;
        Threshold = threshold;
    }
}

internal static class RewardInput
{
    internal static string Identifier(string? value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200 ||
            value != value.Trim() || value.Any(char.IsControl))
            throw new ArgumentException("Use a nonempty, canonical identifier up to 200 characters.", parameter);
        return value;
    }

    internal static TimeZoneInfo TimeZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException exception)
        {
            throw new ArgumentException("The business timezone is unavailable.", nameof(id), exception);
        }
        catch (InvalidTimeZoneException exception)
        {
            throw new ArgumentException("The business timezone is invalid.", nameof(id), exception);
        }
    }
}
