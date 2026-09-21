using TideCasa.Domain.CustomerRewards;

var passed = 0;
var now = DateTimeOffset.Parse("2026-09-20T12:00:00Z");
var card = new RewardRule("coffee-card", "coffee-v1", "venue-a", RewardRuleKind.PaidItemPunchCard, 9, "America/New_York", "coffee");
var monthly = new RewardRule("monthly-visits", "visits-v1", "venue-a", RewardRuleKind.MonthlyVerifiedVisits, 5, "America/New_York");

QualificationEvent Purchase(string id, int units = 1, string tenant = "venue-a", string customer = "customer-a",
    string item = "coffee", QualificationState state = QualificationState.Eligible, DateTimeOffset? at = null) =>
    new(id, tenant, customer, QualificationKind.PaidItemPurchase, at ?? now.AddDays(-1), units, item, state);
QualificationEvent Visit(string id, DateTimeOffset? at = null, string tenant = "venue-a", string customer = "customer-a",
    QualificationState state = QualificationState.Eligible) =>
    new(id, tenant, customer, QualificationKind.VerifiedVisit, at ?? now.AddDays(-1), state: state);
RewardEvaluation Evaluate(RewardRule rule, params QualificationEvent[] events) => RewardEvaluator.Evaluate(rule, "customer-a", events, now);

Check("Empty purchase history earns no reward", () => Equal(0L, Evaluate(card).EarnedRewardCount));
Check("Eight paid items stay below nine-item threshold", () => {
    var result = Evaluate(card, Purchase("eight", 8)); Equal(0L, result.EarnedRewardCount); Equal(1L, result.UnitsNeededForNextReward);
});
Check("Ninth paid item earns exactly one free-item entitlement", () => Equal(1L, Evaluate(card, Purchase("nine", 9)).EarnedRewardCount));
Check("Seventeen items do not earn the second entitlement", () => {
    var result = Evaluate(card, Purchase("seventeen", 17)); Equal(1L, result.EarnedRewardCount); Equal(1L, result.UnitsNeededForNextReward);
});
Check("Eighteen items earn two entitlements", () => Equal(2L, Evaluate(card, Purchase("eighteen", 18)).EarnedRewardCount));
Check("Separate paid lines accumulate", () => Equal(1L, Evaluate(card, Purchase("line-a", 4), Purchase("line-b", 5)).EarnedRewardCount));
Check("Exact event retries count only once", () => {
    var original = Purchase("retry", 5); Equal(5L, Evaluate(card, original, original).QualifiedUnits);
});
Check("Conflicting event quantities are rejected", () => Throws<ArgumentException>(() => Evaluate(card, Purchase("same", 5), Purchase("same", 9))));
Check("Conflicting customer identity under one source is rejected", () => Throws<ArgumentException>(() => Evaluate(card, Purchase("same"), Purchase("same", customer: "other"))));
Check("Conflicting event states require authoritative reconciliation", () => Throws<ArgumentException>(() => Evaluate(card, Purchase("same"), Purchase("same", state: QualificationState.Refunded))));
Check("Source identifiers are isolated by tenant", () => Equal(9L, Evaluate(card, Purchase("same", 9), Purchase("same", 99, tenant: "venue-b")).QualifiedUnits));
Check("Wrong tenant cannot contribute purchases", () => Equal(0L, Evaluate(card, Purchase("wrong-tenant", 9, tenant: "venue-b")).QualifiedUnits));
Check("Wrong customer cannot contribute purchases", () => Equal(0L, Evaluate(card, Purchase("wrong-customer", 9, customer: "customer-b")).QualifiedUnits));
Check("Wrong item cannot contribute purchases", () => Equal(0L, Evaluate(card, Purchase("wrong-item", 9, item: "tea")).QualifiedUnits));
Check("Visits do not count as paid items", () => Equal(0L, Evaluate(card, Visit("visit")).QualifiedUnits));
Check("Cancelled and refunded purchases are excluded", () => Equal(0L, Evaluate(card,
    Purchase("cancelled", 9, state: QualificationState.Cancelled), Purchase("refunded", 9, state: QualificationState.Refunded)).QualifiedUnits));
Check("Future paid items are excluded", () => Equal(0L, Evaluate(card, Purchase("future", 9, at: now.AddTicks(1))).QualifiedUnits));
Check("Event exactly at the evaluation instant qualifies", () => Equal(1L, Evaluate(card, Purchase("current", 9, at: now)).EarnedRewardCount));
Check("Four visits remain below monthly threshold", () => {
    var result = Evaluate(monthly, Enumerable.Range(1, 4).Select(n => Visit("visit-" + n)).ToArray());
    Equal(0L, result.EarnedRewardCount); Equal(1L, result.UnitsNeededForNextReward);
});
Check("Five visits earn the monthly reward", () => Equal(1L, Evaluate(monthly, Enumerable.Range(1, 5).Select(n => Visit("visit-" + n)).ToArray()).EarnedRewardCount));
Check("Ten visits still earn only one monthly reward", () => {
    var result = Evaluate(monthly, Enumerable.Range(1, 10).Select(n => Visit("visit-" + n)).ToArray());
    Equal(1L, result.EarnedRewardCount); Equal(0L, result.UnitsNeededForNextReward);
});
Check("Duplicate visits do not satisfy threshold", () => {
    var visit = Visit("same-visit"); Equal(1L, Evaluate(monthly, visit, visit, visit, visit, visit).QualifiedUnits);
});
Check("Purchases do not count as verified visits", () => Equal(0L, Evaluate(monthly, Purchase("purchase", 5)).QualifiedUnits));
Check("Visits from another tenant or customer are excluded", () => Equal(0L, Evaluate(monthly,
    Visit("other-tenant", tenant: "venue-b"), Visit("other-customer", customer: "customer-b")).QualifiedUnits));
Check("Cancelled, refunded and future visits are excluded", () => Equal(0L, Evaluate(monthly,
    Visit("cancelled", state: QualificationState.Cancelled), Visit("refunded", state: QualificationState.Refunded),
    Visit("future", now.AddMinutes(1))).QualifiedUnits));
Check("Month boundary follows New York time, not UTC", () => {
    var result = Evaluate(monthly, Visit("still-august", DateTimeOffset.Parse("2026-09-01T03:59:59Z")),
        Visit("september", DateTimeOffset.Parse("2026-09-01T04:00:00Z")));
    Equal(1L, result.QualifiedUnits); Equal("2026-09", result.PeriodKey);
});
Check("Evaluation month also follows business time", () => {
    var result = RewardEvaluator.Evaluate(monthly, "customer-a", [Visit("august", DateTimeOffset.Parse("2026-08-31T23:00:00Z"))],
        DateTimeOffset.Parse("2026-09-01T03:30:00Z"));
    Equal("2026-08", result.PeriodKey); Equal(1L, result.QualifiedUnits);
});
Check("Winter month boundary uses standard-time offset", () => {
    var result = RewardEvaluator.Evaluate(monthly, "customer-a", [Visit("still-november", DateTimeOffset.Parse("2026-12-01T04:59:59Z")),
        Visit("december", DateTimeOffset.Parse("2026-12-01T05:00:00Z"))], DateTimeOffset.Parse("2026-12-10T12:00:00Z"));
    Equal(1L, result.QualifiedUnits); Equal("2026-12", result.PeriodKey);
});
Check("An old year with the same month does not qualify", () => Equal(0L, Evaluate(monthly, Visit("last-year", DateTimeOffset.Parse("2025-09-19T12:00:00Z"))).QualifiedUnits));
Check("Equivalent timestamp offsets produce the same result", () => {
    Equal(Evaluate(monthly, Visit("offset", DateTimeOffset.Parse("2026-09-10T12:00:00Z"))).QualifiedUnits,
        Evaluate(monthly, Visit("offset", DateTimeOffset.Parse("2026-09-10T08:00:00-04:00"))).QualifiedUnits);
});
Check("Eligibility preserves the exact rule version and customer", () => {
    var result = Evaluate(card, Purchase("version", 9));
    Equal("coffee-v1", result.RuleVersionId); Equal("coffee-card", result.RuleId); Equal("venue-a", result.TenantId); Equal("customer-a", result.CustomerId);
});
Check("Evaluation is repeatable and does not issue rewards", () => {
    var history = new[] { Purchase("repeat", 9) }; Equal(Evaluate(card, history), Evaluate(card, history));
});
Check("Large quantities use 64-bit accumulation", () => Equal(2L * int.MaxValue, Evaluate(card,
    Purchase("large-a", int.MaxValue), Purchase("large-b", int.MaxValue)).QualifiedUnits));
Check("Zero or negative thresholds are rejected", () => {
    foreach (var threshold in new[] { 0, -1 }) Throws<ArgumentOutOfRangeException>(() => new RewardRule("r", "v", "t", RewardRuleKind.MonthlyVerifiedVisits, threshold, "UTC"));
});
Check("Unknown timezone is rejected", () => Throws<ArgumentException>(() => new RewardRule("r", "v", "t", RewardRuleKind.MonthlyVerifiedVisits, 5, "No/Such-Time-Zone")));
Check("Missing item filter is rejected for punch cards", () => Throws<ArgumentException>(() => new RewardRule("r", "v", "t", RewardRuleKind.PaidItemPunchCard, 9, "UTC")));
Check("Item filter is rejected for monthly visits", () => Throws<ArgumentException>(() => new RewardRule("r", "v", "t", RewardRuleKind.MonthlyVerifiedVisits, 5, "UTC", "coffee")));
Check("Zero and negative purchase quantities are rejected", () => {
    Throws<ArgumentOutOfRangeException>(() => Purchase("zero", 0)); Throws<ArgumentOutOfRangeException>(() => Purchase("negative", -1));
});
Check("One visit cannot claim multiple visit units", () => Throws<ArgumentOutOfRangeException>(() => new QualificationEvent("v", "t", "c", QualificationKind.VerifiedVisit, now, 5)));
Check("Invalid enum values are rejected", () => {
    Throws<ArgumentOutOfRangeException>(() => new RewardRule("r", "v", "t", (RewardRuleKind)99, 5, "UTC"));
    Throws<ArgumentOutOfRangeException>(() => new QualificationEvent("e", "t", "c", (QualificationKind)99, now));
    Throws<ArgumentOutOfRangeException>(() => Visit("state", state: (QualificationState)99));
});
Check("Empty or ambiguous identifiers are rejected", () => {
    Throws<ArgumentException>(() => new RewardRule("r", " ", "t", RewardRuleKind.MonthlyVerifiedVisits, 5, "UTC"));
    Throws<ArgumentException>(() => Purchase(" source-with-space"));
    Throws<ArgumentException>(() => RewardEvaluator.Evaluate(card, "", [], now));
});
Check("Null history entries are rejected", () => Throws<ArgumentException>(() => RewardEvaluator.Evaluate(card, "customer-a", [null!], now)));

Console.WriteLine($"PASS: {passed} customer reward domain checks.");

void Check(string label, Action check)
{
    try { check(); passed++; Console.WriteLine("PASS " + label); }
    catch (Exception exception) { Console.Error.WriteLine("FAIL " + label + ": " + exception); Environment.Exit(1); }
}
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}.");
}
static void Throws<TException>(Action action) where TException : Exception
{
    try { action(); }
    catch (TException) { return; }
    throw new Exception($"Expected {typeof(TException).Name}.");
}
