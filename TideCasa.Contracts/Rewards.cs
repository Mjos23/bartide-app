namespace TideCasa.Contracts;

public sealed record RewardRuleCard(string Id, string VersionId, int Version, string Title,
    string Reward, string Kind, int Threshold, string? ItemId, string TimeZoneId, bool Active);
public sealed record RewardProgress(string RuleId, string VersionId, long QualifiedUnits, long UnitsNeeded, string Period);
public sealed record CustomerReward(string Id, string RuleId, string Title, string Reward,
    string Kind, string State, string EarnedAt, int PointsCost);
public sealed record RewardMember(string Id, string Name, long Points);
public sealed record RewardAudit(string Id, string MemberId, string Kind, string Description,
    int Units, string State, string OccurredAt, string Actor);
public sealed record RewardWallet(string TenantId, string Slug, string Name, string? MemberId,
    long Points, IReadOnlyList<RewardRuleCard> Rules, IReadOnlyList<RewardProgress> Progress,
    IReadOnlyList<CustomerReward> Rewards);
public sealed record ManagedCustomerReward(string MemberId, string MemberName, CustomerReward Reward);
public sealed record EmployeeRewardBalance(string MemberId, string Name, bool Active, long Points);
public sealed record EmployeeRewardEntry(string Id, string MemberId, int Delta, string Reason,
    string Actor, string CreatedAt);
public sealed record RewardsWorkspace(string TenantId, string Slug, string Name,
    IReadOnlyList<RewardRuleCard> Rules, IReadOnlyList<RewardMember> Members,
    IReadOnlyList<ManagedCustomerReward> Rewards, IReadOnlyList<RewardAudit> Qualifications,
    IReadOnlyList<EmployeeRewardBalance> Employees, IReadOnlyList<EmployeeRewardEntry> EmployeeLedger);
public sealed record EmployeeRewardsWallet(string TenantId, string Name, string MemberId,
    long Points, IReadOnlyList<EmployeeRewardEntry> Ledger);
public sealed record JoinRewardsRequest(string RequestId, string Name);
// Kind: punch-card, monthly-visits, or points. New versions start fresh progress;
// existing issued rewards keep their original terms. ExpectedVersion=-1 creates a rule.
public sealed record SaveRewardRuleRequest(string RequestId, int ExpectedVersion, string Title,
    string Reward, string Kind, int Threshold, string? ItemId, string TimeZoneId, bool Active);
// Source: verified-visit, verified-purchase, or paid-order. Manual records require an
// owner-verified source reference and audit note; paid orders derive item/units from storage.
public sealed record RecordRewardQualificationRequest(string RequestId, string MemberId,
    string RuleId, string Source, string SourceReference, int Units, string? ItemId, string Note);
public sealed record VoidRewardQualificationRequest(string RequestId, string Reason);
public sealed record AwardCustomerPointsRequest(string RequestId, string MemberId,
    int Points, string SourceReference, string Reason);
public sealed record RequestRewardRedemptionRequest(string RequestId);
public sealed record RedeemPointsRewardRequest(string RequestId, string RuleId);
public sealed record ResolveRewardRedemptionRequest(string RequestId, string Action, string Note);
public sealed record ChangeEmployeePointsRequest(string RequestId, string MemberId,
    int Delta, string SourceReference, string Reason);
public sealed record RewardChangeResult(string Id, string Message);
