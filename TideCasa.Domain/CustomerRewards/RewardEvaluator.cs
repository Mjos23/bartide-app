using System.Globalization;

namespace TideCasa.Domain.CustomerRewards;

/// <summary>
/// Eligibility only: this is not an issued reward, balance or redemption authorization.
/// PeriodKey is yyyy-MM for monthly rules and "lifetime" for item punch cards.
/// EarnedRewardCount includes previously earned/redeemed rewards; durable issuance must
/// compare its own ledger and enforce uniqueness under the tenant/customer/rule version.
/// </summary>
public sealed record RewardEvaluation(
    string RuleId,
    string RuleVersionId,
    string TenantId,
    string CustomerId,
    string PeriodKey,
    DateTimeOffset EvaluatedAt,
    long QualifiedUnits,
    long EarnedRewardCount,
    long UnitsNeededForNextReward);

public static class RewardEvaluator
{
    /// <summary>
    /// Pure evaluation over supplied authoritative history. Does not read the clock,
    /// authorize anyone, contact a provider or issue/redeem rewards.
    /// </summary>
    public static RewardEvaluation Evaluate(RewardRule rule, string customerId,
        IEnumerable<QualificationEvent> events, DateTimeOffset asOf)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(events);
        customerId = RewardInput.Identifier(customerId, nameof(customerId));
        var zone = RewardInput.TimeZone(rule.TimeZoneId);
        var localNow = TimeZoneInfo.ConvertTime(asOf, zone);
        var monthly = rule.Kind == RewardRuleKind.MonthlyVerifiedVisits;
        var period = monthly ? localNow.ToString("yyyy-MM", CultureInfo.InvariantCulture) : "lifetime";
        var seen = new Dictionary<(string Tenant, string Source), QualificationEvent>();
        long qualifiedUnits = 0;

        foreach (var qualification in events)
        {
            if (qualification is null)
                throw new ArgumentException("Qualification history cannot contain null entries.", nameof(events));
            var key = (qualification.TenantId, qualification.SourceEventId);
            if (seen.TryGetValue(key, out var original))
            {
                if (original != qualification)
                    throw new ArgumentException("A source event ID has conflicting qualification data.", nameof(events));
                continue;
            }
            seen.Add(key, qualification);

            if (qualification.TenantId != rule.TenantId || qualification.CustomerId != customerId ||
                qualification.State != QualificationState.Eligible || qualification.OccurredAt > asOf)
                continue;

            if (monthly)
            {
                if (qualification.Kind != QualificationKind.VerifiedVisit) continue;
                var localEvent = TimeZoneInfo.ConvertTime(qualification.OccurredAt, zone);
                if (localEvent.Year != localNow.Year || localEvent.Month != localNow.Month) continue;
            }
            else if (qualification.Kind != QualificationKind.PaidItemPurchase || qualification.ItemId != rule.ItemId)
                continue;

            qualifiedUnits = checked(qualifiedUnits + qualification.Units);
        }

        var earned = monthly ? qualifiedUnits >= rule.Threshold ? 1L : 0L : qualifiedUnits / rule.Threshold;
        var remaining = monthly
            ? Math.Max(0L, rule.Threshold - qualifiedUnits)
            : rule.Threshold - qualifiedUnits % rule.Threshold;
        return new(rule.Id, rule.VersionId, rule.TenantId, customerId, period, asOf,
            qualifiedUnits, earned, remaining);
    }
}
