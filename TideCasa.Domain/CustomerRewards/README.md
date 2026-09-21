# Customer rewards domain slice

`RewardEvaluator` evaluates eligibility from trusted server-produced purchase or visit
events. It has no authentication, HTTP endpoints, database, provider connection or UI.
It does not grant public callers permission to say that a purchase is paid or a visit
is verified. Those facts must be established by a future application service.

- An item punch card qualifies only the configured item. Every complete threshold earns
  one free-item entitlement: 9 paid units earn 1, 18 earn 2. Counts span supplied history.
- A monthly visit rule earns at most one entitlement per customer and business-local
  calendar month. Each unique verified visit is one event, not a submitted visit quantity.
- Future, cancelled and refunded events do not qualify. A refunded event is excluded
  entirely; partial-refund allocation into individual qualification units is not implemented.
- Exact duplicate source IDs count once. Conflicting data for the same tenant/source ID
  fails closed, including conflicts in customer, quantity, time or current state. The caller
  must reconcile provider updates into one authoritative snapshot before evaluation.
- Timezone conversion uses the supplied business timezone and evaluation instant, including
  local month boundaries and daylight-saving offsets. There is no ambient clock dependency.
- Rule properties are immutable. An application must persist each rule/version pair once
  and prevent a caller from reusing a version ID with altered terms. This pure evaluator
  cannot enforce global persistence uniqueness or decide rule-version transition policies.

The result is eligibility, **not an issued reward or redeemable balance**. Durable issuance
must scope uniqueness to tenant, customer, rule version, period and entitlement ordinal;
compare already-issued history inside a transaction; handle rule changes and qualification
reversals; and authorize both issuance and redemption. Do not issue all returned rewards
again every time this evaluator runs. Do not apply a newly created rule retroactively
without an explicit application policy selecting the eligible history.

Customer identity is scoped to a tenant. Employee rewards are a different domain and are
not included. Paid purchases must exclude free reward items when producing qualification
events, or those freebies would incorrectly earn more punches.
