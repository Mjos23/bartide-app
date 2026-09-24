# Driver accounts, shared network, dispatch and payments

## Driver signup and hiring

Drivers enter at `/driver/signin`, create or sign into their own verified account, then open `/driver` to create a profile. Discovery is opt-in. The directory exposes name, biography and delivery ZIPs; it does not expose email, account identifiers or payment details.

Clients open **Hire drivers** at `/workspace/{tenant}/driver-network`. An owner or manager offers an agreed amount per completed delivery and optional notes. A pending offer grants no access to orders. The driver must accept it in their driver workspace before the client can assign work. An accepted hire creates a driver-only team membership and an offline dispatch profile. The driver then explicitly goes available, or the client configures shifts.

Active hire pay cannot be changed in place. The client must finish any active deliveries, end the hire, and send a new offer for the driver to accept. Ending a hire pauses membership and removes location sharing. Neither sign-in, listing a profile, nor re-enabling an ended network member bypasses the accepted-hire requirement.

Clients can continue inviting their own drivers through the delivery desk without a shared-network hire. A pre-existing in-house driver at that client cannot be silently converted into a network hire. A network profile's overall delivery capacity also counts that person's active in-house assignments at other clients. Per-client availability, coverage and capacity remain additional constraints. Agreed pay and network origin are captured at assignment and preserved at completion.

## Client-owned driver accounts

Open **Account → Drivers and dispatch** (drivers see **My deliveries**) or `/workspace/{tenant}/deliveries`.

1. A restaurant owner or manager adds the driver's name and sign-in email in **Add a driver account**. This creates a restaurant team membership; it does not send an email.
2. Share the displayed driver sign-in link. The driver uses their own account, verifies the same email and signs in. Existing registration, email verification, password reset and session handling are reused. An invitation cannot claim an unverified or different email. Pause access in **Team and schedule**.
3. Enable delivery and the delivery workflow in **Menu and settings**. Configure each driver's availability, maximum active deliveries (1–10), and ZIP coverage. Empty coverage means all delivery ZIPs accepted by the restaurant. New profiles default to offline with capacity one.
4. Turn on automatic dispatch for the restaurant or select a plan on an unassigned order. The API checks the durable queue every 15 seconds; **Check for assignments now** runs the same selection immediately. The page refreshes after a saved change; use **Refresh orders** for new assignments.

## Assignment rules

- Restaurant auto dispatch is off by default. Orders without a plan follow this switch. An explicit **Automatic** plan runs even when the restaurant auto switch is off. **Manual** keeps an order out of automatic selection.
- **Scheduled** plans are dispatch times, not promised arrival times or customer preorder slots. They wait until the saved instant, after acceptance. A preferred driver is optional; if selected, the order waits for that driver rather than silently choosing someone else. Plans can be changed to manual to cancel scheduled assignment.
- Only accepted, preparing or ready delivery orders can be auto-assigned. New orders must first be accepted. Phone-payment orders require confirmed paid status. Pickup/table, cancelled, completed and already assigned orders are excluded.
- Eligible drivers have an active, connected restaurant membership, are available, cover the order ZIP, and have spare capacity. **Available now** stays available until changed; **Scheduled** follows the existing team shift calendar; **Offline** prevents new assignments. Going offline does not remove existing deliveries. Drivers can change their own availability, but managers set capacity, coverage and shifts.
- Selection uses lowest active load, then oldest last assignment, then a stable driver ID. It does not estimate travel times or optimize routes. Existing open assignments count against capacity; a physically delivered order releases capacity even if staff payment reconciliation remains outstanding.
- Manual assignment to a configured driver follows the same availability, capacity and ZIP rules. Legacy drivers without a profile retain the existing manual workflow until configured. Reassignment requires a reason and resets acknowledgement/location sharing through the existing delivery workflow.
- Both automatic and manual writers use the application's cross-process transaction lock. Each order assignment and audit entry commits atomically; version checks reject stale edits. Waiting plans survive restart and retry when drivers or capacity become available. Worker failures roll back that workspace and retry on the next pass.

## Driver and delivery tools

Drivers see only their own profile, shifts and assigned orders. The existing delivery workflow provides acknowledgement, collection, directions, customer calling, problem reporting, manager resolution, handoff confirmation, payment reconciliation and optional consent-based location sharing. Drivers do not gain manager permissions or permission to record staff payments. Sign-in does not imply that the driver is currently online; availability is explicitly set or follows their shift.

Native scheduling forms require an explicit time zone and store UTC. Repeated/skipped daylight-saving hours are rejected; enter the equivalent instant in UTC instead. Saved schedule displays are labeled UTC. Current supported input zones are UTC and US Eastern/Central/Mountain/Pacific. The API accepts explicit ISO timestamps with an offset, up to 90 days ahead.

## Persistence and release

The immutable baseline migrations remain unchanged. `DeliveryDispatchSchema` adds `tide_delivery_dispatch` and `tide_delivery_drivers` to local SQLite/development PostgreSQL. Production PostgreSQL startup probes the extension and fails if it is missing.

For the existing production `tide_casa` application, `deploy/app-platform/20260924-production-driver-delivery.sql` combines the location, dispatch, network and payment extensions: nine private tables, restricted `tide_api` DML grants, row-level security, and no anonymous or authenticated-browser access. Run it as the existing schema owner before deploying. It preserves existing records and is safe to rerun. It does not enable payments. This production-specific script was executed twice on an isolated PostgreSQL database, including restricted-role queries and anonymous-access rejection. Applying it to the hosted database remains a separate deployment step.

Before a hosted deployment, the schema owner must review and apply `deploy/app-platform/20260924-delivery-dispatch.sql` with a single intended application schema in `search_path`, then grant `SELECT, INSERT, UPDATE, DELETE` on these two tables to the existing API runtime role. Preserve the deployment's role isolation: no `PUBLIC`, `anon` or `authenticated` access. For the fictional demo, enable row-level security on both tables, add policies scoped to `tenant_id='gulf-lantern'` for `tide_demo_api`, and grant access only to that existing demo role. These permissions must be rehearsed for the actual deployment before release; local owner-role tests do not prove hosted grants.

The shared-demo API allowlist admits only the new tenant-scoped dispatch routes, and its worker restricts work to the configured fictional venue. Shared-network signup/hiring and payment routes are excluded from the public demo. `DeliveryDispatch:WorkerEnabled=false` is honored only in Development for deterministic tests. No hiring invitation messages are sent by this feature.

## Completed delivery payments

The completion transaction creates exactly one delivery payable after handoff. A failed, cancelled or uncompleted delivery produces no payable. It remains pending until the client owner explicitly approves the displayed amount and total. Managers can review payment history but cannot approve payments or receive a hosted checkout link. Driver earnings at `/driver/earnings` are scoped to that signed-in driver. Both workspaces have paginated history; older pending deliveries remain accessible.

- Shared network: the accepted driver's pay is immutable on the payable. The client pays that amount plus a 5% BarTide fee, rounded half-up to cents. **$10.00 driver pay + $0.50 BarTide fee = $10.50 client total.** The fee applies only to completed shared-network deliveries.
- Own drivers: the owner enters and reviews pay before approval. No BarTide network fee applies. Current per-delivery pay is USD $0.50–$1,000.00.
- These payments are separate from the customer's food order payment. Paying the driver does not mark the food order collected or paid.
- The client funds a Stripe hosted Checkout payment. Its destination transfer credits the full agreed driver amount to the driver's connected Stripe account. The platform fee is gross revenue; Stripe processing/Connect costs are borne by the platform balance. For an own-driver payment with zero application fee, the platform still bears its applicable processor costs.
- “Credited to driver Stripe account” confirms the verified transfer, not arrival in a bank. Bank payouts follow Stripe's account settings. Refunds/disputes are reconciled and shown; automatic re-payment and in-app refund issuance are intentionally not provided.

Payout onboarding currently supports US individuals and USD payments. Stripe collects identity/bank details on its hosted page. BarTide stores the recipient account binding, not bank details. Transfers and payouts must both be ready before a new approval can open checkout.

Approval records an immutable provider request before making a network call. Repeated approval, uncertain provider creation and background reconciliation reuse that saved attempt. Only a provider-confirmed expired Checkout can be replaced. An unresolved creation older than 23 hours is quarantined for review rather than reusing an expired provider idempotency window. Paid/refunded/disputed states cannot silently reopen. Driver account creation uses the same durable retry principle.

Signed webhooks at `/api/stripe/driver-payments/webhook` trigger fresh retrieval of known approved attempts. Client return URLs and webhook contents never mark a payment paid. Verification compares environment, identities, captured amount, destination transfer, application fee and refund/dispute state. Recovery runs each minute, with leased, rotating work batches; server restart retains all plans, obligations and attempts. Sandbox and live financial states are isolated.

## Payment configuration and hosted release

No live Stripe accounts or charges were created during implementation. Driver payout setup and payments are **disabled by default**. Local provider-contract checks are not a real Stripe sandbox or production rehearsal. Before enabling the feature on a host:

1. Apply `deploy/app-platform/20260924-delivery-dispatch.sql`, then `deploy/app-platform/20260924-driver-network-payments.sql` as the existing isolated application schema owner. The second script adds six private tables. Grant the existing API role only the required table DML privileges; no `PUBLIC`, `anon` or `authenticated` grants. Production startup probes all required columns instead of creating them. Rehearse runtime-role permissions on the target deployment; local tests use an owned schema.
2. Configure the dedicated `DriverPayments` settings below using the host's secret store. Do not reuse customer-payment account bindings or webhook endpoints. Use a canonical HTTPS origin for `PublicBaseUrl`.
3. Rehearse the current Accounts v2 recipient permissions, onboarding, readiness, destination card Checkout, exact transfer/application fee, signed events, expiry/retry, and refund/dispute handling in a real Stripe sandbox. Provider API version is `2026-08-26.dahlia`. Confirm the platform's processing costs and supported account configuration before enabling live traffic.
4. Keep `Environment=sandbox` during rehearsal. To enable real payments deliberately set `Environment=live`, use matching live restricted key/webhook secret, and set `LivePaymentsVerified=true` after verification. The last flag is an operator release assertion, not an automated certification.

```json
{
  "DriverPayments": {
    "Environment": "sandbox",
    "PublicBaseUrl": "https://YOUR-APP-ORIGIN",
    "RestrictedKey": "",
    "WebhookSecret": "",
    "SetupEnabled": false,
    "PaymentsEnabled": false,
    "LivePaymentsVerified": false
  }
}
```

`ApiBaseUrl` overrides are accepted only for a loopback synthetic provider in Development with `AllowLocalTestProvider=true`. `DriverPayments:WorkerEnabled=false` is a Development test switch. Never use either to route live payments. Payment recovery is disabled in the public demo.

## Verification

- `scripts/verify-driver-dispatch.py --worker`: account linking, isolation, validation, optimistic concurrency, ZIP/capacity selection, schedule timing/restart, retry and actual background dispatch using a local synthetic identity provider and isolated SQLite database.
- Add `--postgres` to exercise the same behavior in a fresh owned schema on the existing loopback PostgreSQL fixture.
- `scripts/verify-driver-dispatch-web.py`: real rendered native forms, authentication, CSRF, scheduling and role visibility.
- `scripts/verify-driver-network.py` (also `--postgres`): independent signup, discovery privacy, accepted-hire access, concurrent acceptance, global capacity, own-driver distinction and immutable completion payables.
- `scripts/verify-driver-payments.py`: real provider adapter/options against a synthetic loopback Stripe contract, including exact transfers, destinations, fees, readiness, refunds/disputes and return URL checks.
- `scripts/verify-driver-payment-workflow.py` (also `--postgres`): real authenticated API/storage with synthetic Stripe; explicit owner approval, ledger isolation, idempotent retries/concurrent approval, signed-event reconciliation, payment history, recovery rotation and sandbox/live separation.
- `scripts/verify-driver-network-web.py`: native profile, offers, payout setup and payment review forms with CSRF, role restrictions, safe redirects and fee display.
- `node scripts/verify-driver-fee-preview.cjs`: actual browser-script integer cent rounding and independent form previews.
- Existing delivery-workflow and delivery-location scripts cover end-to-end handoff/payment and location consent/isolation regressions.

These checks use fictional data; they do not send invitations, charge customers or modify hosted databases.
