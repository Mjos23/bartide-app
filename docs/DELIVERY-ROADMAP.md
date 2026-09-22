# Tide Casa delivery roadmap

Prepared September 22, 2026. Phase 1 is implemented for the Gulf Lantern release; see [phase-one behavior and recovery](DELIVERY-PHASE-ONE.md). Phases 2–4 remain planning only and are paused at the user's request.

## Objective and sequence

Give each restaurant a dependable way to assign deliveries, help its drivers complete them, and keep customers informed. Start with the existing C# app and restaurant-approved drivers. Add location tracking in stages without holding up sales or ordinary ordering.

| Phase | Difficulty estimate | Outcome | Exit requirement |
| --- | --- | --- | --- |
| 1. Improve existing delivery | 4/10 | Delivery desk, My deliveries, assignment acknowledgement, directions, customer updates and delivery confirmation | Complete and recover a delivery across manager, driver and customer views |
| 2. GPS in the web app | 6/10 | Permission-based location sharing during active deliveries while the driver page is visible | Real-phone tests distinguish fresh, stale, stopped and unavailable locations |
| 3. Dedicated driver mobile app | 8/10 | Background location, navigation handoff and connection-loss recovery | iPhone and Android field trials meet agreed reliability and battery targets |
| 4. Delivery-service integration | 5–6/10 | Optional provider driver app and dispatch/tracking integration | Selected provider passes reconciliation, isolation, failure and cost checks |

These are relative engineering estimates, not calendar commitments. Follow this sequence. Phases 3 and 4 are later product decisions, not prerequisites for releasing Phase 1. Phase 4 remains optional if our own driver tools meet client needs.

## Starting point verified in the source

- Owner/manager can assign an active driver belonging to the same restaurant.
- A driver role exists, and the operations query filters drivers to their assigned orders. Writes check restaurant access, assignment and order version.
- Order statuses already include ready, out for delivery and completed.
- Delivery settings include allowed ZIP codes, fees, minimums and capacity.
- Customers currently press Check order status; they do not yet receive automatic delivery updates in that page.
- Staff enrollment already uses an employee's verified sign-in email. The manager shares a workspace link; the current form explicitly does not email invitations.
- Completion currently requires a recorded payment. Drivers cannot use the staff payment-recording action.
- Customer tracking uses an order ID plus a secret tracking key, matched to the restaurant. Preserve that boundary when adding delivery details.
- No live GPS collection was found in the inspected delivery implementation.

This is a source assessment, not a new runtime verification of every existing behavior.

## Phase 1 — Make the existing workflow complete

### User experience

**Owner/manager:** a delivery-only desk with Needs assignment, Assigned, Ready, On the way, Delivered and Needs attention. Show the assigned driver, acknowledgement, last status change and payment badge. Allow assignment and controlled reassignment. Start with manual assignment rather than automatic route optimization.

**Driver:** a phone-friendly My deliveries screen with only that driver's jobs. Each job shows pickup location, customer address, order summary, notes and contact action. Primary actions: Acknowledge, Open directions, Collected / Out for delivery, Delivered and Report a problem. Contact and status changes are intended for when the driver is safely stopped.

**Customer:** retain the restaurant's existing order receipt, adding a delivery progress timeline, an approved driver display name, last-updated time and restaurant contact. Refresh automatically while the receipt is visible; retain manual refresh and clearly show connection failure. Phase 1 does not claim a live map or calculated arrival time.

### Build slices, in order

1. **Define delivery and payment states.** Add delivery-specific timestamps and assignment acknowledgement without changing the meaning of existing kitchen or payment statuses. Assigning an order does not mean the driver has accepted it. A delivery problem does not automatically cancel or refund an order.
2. **Manager delivery desk.** Reuse tenant-scoped assignment and role checks. Support only active, account-linked restaurant drivers. Display unlinked drivers as needing setup. Reassignment invalidates the former driver's authority and requires the new driver to acknowledge; manager overrides record a reason.
3. **Driver workspace and enrollment.** Reuse verified sign-in and restaurant membership. Manager adds the driver's email and shares the access link. No public driver directory or self-assignment. Automated invitation emails can be a later convenience rather than a launch dependency. Add one-tap directions and existing customer contact information to each assigned delivery.
4. **Customer updates.** Add lightweight status reads to the existing receipt. Proposed starting interval: 15 seconds while visible, with backoff on errors, no overlapping requests and polling stopped after terminal delivery status or page disposal. Resume on returning to the page. Do not reload the full menu or full restaurant order board for each status update.
5. **Handoff confirmation and recovery.** Record delivery timestamp, responsible driver and optional short note. Add clear customer unavailable, address issue and unable-to-deliver reasons for manager attention. Phase 1 proof is an authenticated driver confirmation; photos, signatures and recipient codes are deferred. Make retries safe so a double tap does not produce duplicate confirmations.
6. **Rehearse, measure and release.** Run the checks below, publish to Gulf Lantern first, then make the optional delivery workspace available per restaurant after a reviewed release.

### Payment decision for the first release

Record delivery confirmation separately from financial settlement. A paid delivery may complete the existing order when delivery is confirmed. An unpaid delivery displays Delivered — payment outstanding to authorized staff, with the customer's payment status remaining accurate. An authorized manager/server records money actually received using the existing payment workflow; the driver does not gain payment or refund permissions merely by confirming delivery.

Review capacity counting as part of this change: a physically delivered order must not keep consuming an active delivery slot merely because payment reconciliation is outstanding. Outstanding money must remain visible in the staff queue. The implementation needs a tested rule for both cases before release.

### Acceptance checks

- Place a fictional delivery order, assign it, acknowledge it as the driver, prepare it, collect it, confirm delivery and reconcile payment. The customer sees each public status automatically without refreshing the entire page.
- Repeat with unpaid staff payment: Delivered is visible, the unpaid balance remains, and neither delivery confirmation nor retries mark it paid.
- A driver cannot see, change or acknowledge another driver's order or another restaurant's delivery.
- Reassignment, paused membership, expired sessions and competing updates reject stale writes without losing the current assignment.
- Duplicate submissions, lost connections and reloads do not duplicate delivery events or lose an acknowledged update.
- A failed delivery can enter Needs attention and return to a valid next action. Cancellation and refunds keep their existing permissions and rules.
- Dine-in, pickup, menu quantities, the floating cart, table QR links, Add to home screen and the 23-orders-per-location-per-minute rule continue to work.
- Test local fixtures first; test PostgreSQL isolation before any hosted release. The shared public demo uses only fictional addresses and people. It sends no real messages and collects no real payments.

**Phase 1 is independently useful and can be shown to prospects before GPS is built.**

## Phase 2 — Add foreground GPS to the web app

Build only after Phase 1 provides stable assignment, delivery sessions and customer status reads.

### Experience and boundaries

- Driver chooses Start sharing for an active assigned delivery and grants browser location permission. Show Sharing, Permission needed, Paused / unavailable and Stop sharing explicitly.
- Manager sees active drivers for their own restaurant with an update age. Customer sees only the driver location authorized for their own active delivery, using the existing protected tracking access.
- Show location age and reported accuracy. A missing signal is unavailable, not a fabricated point. A stale point must not be labeled live.
- Customers provide a delivery address; they do not need to grant access to their own GPS.
- Begin sharing customer-visible location when the driver starts that customer's delivery leg, not merely when a driver is assigned to several orders. Do not disclose other stops or other customers' addresses. For the initial pilot, allow one customer-visible active leg per driver.
- End public sharing when delivered, cancelled, reassigned or access is revoked. Recheck authorization on each update/read. The server must reject later uploads from an invalidated delivery session.

### Technical and data approach

- Add a small location record separate from the order's growing history or JSON payload. Keep the latest accepted point, device capture time, server receipt time, accuracy and delivery-session identity.
- Proposed pilot settings: no more than one upload per 15 seconds; refresh at least roughly every 30 seconds when the page can obtain a valid fix. Mark a point stale after 60 seconds without an accepted fresh sample. These are tuning targets to validate on real phones, not guarantees about browser scheduling.
- Reject invalid coordinates, out-of-order sequences and implausibly old/future capture times. Delayed uploads must not make an old position appear fresh. GPS is operational information, not proof that a delivery occurred.
- Poll or push only small location/status changes to viewers. Use a restaurant-level batch for the manager map rather than a separate full order request for each driver.
- Separate GPS/status limits from new-order admission limits. Measure write volume, response size and database connections against the present hosting baseline before widening rollout.
- Stop location reads/uploads for ended sessions. Keep no continuous route history in the initial release; delete the last coordinate after the operational need ends. Delivery audit events can remain without coordinates.
- Select the embedded map provider and address-to-coordinate service before building the map. Confirm price, usage limits, attribution and permitted storage. Simple directions links do not cover embedded maps, geocoding or traffic-based arrival estimates.
- Defer automated ETA promises and route optimization. If an arrival window is displayed initially, identify it as restaurant-entered.

### Device limitation and exit checks

The web Geolocation specification delivers position updates to fully active, visible documents. A home-screen shortcut does not make background tracking dependable. Opening navigation, switching apps, losing connectivity or locking the phone may interrupt updates. Use Last updated and a stale state rather than pretending otherwise.

Test Safari and Chrome on real phones, including installed home-screen mode, permission denial/revocation, poor accuracy, app switching, lock/unlock, offline/reconnect, reassignment and completed deliveries. Verify customers never see another delivery's location. Pilot with consenting internal testers in a private environment: the publicly accessible fictional driver switcher must not expose testers' real location.

**Exit:** foreground tracking is honestly labeled and useful, fallback order status still works, and measured server/data use fits an agreed budget. If continuous tracking is required, proceed to Phase 3 rather than promising it from the browser.

## Phase 3 — Dedicated driver mobile app

- Keep manager and customer experiences on the restaurant's existing web app. Add only the driver mobile client, reusing authenticated delivery APIs and state rules.
- Evaluate a C#-friendly mobile approach against location, notifications and navigation needs before selecting a framework. Recheck current iOS/Android background execution and store requirements at implementation time.
- Provide restaurant-approved sign-in, assigned deliveries, acknowledgement, navigation handoff, controlled background location and delivery/problem confirmation.
- Queue necessary updates during connection loss with stable event IDs and capture timestamps. Reconnect safely; expired sessions or reassignment must not resurrect old actions. Never replay historical coordinates as the driver's current live position.
- Show whether tracking is on, limit it to active delivery work, and preserve permission revocation and account/device revocation.
- Trial on real iPhone/Android hardware with screen lock, navigation, incoming calls, low battery, poor reception, process termination and phone restart. Set battery and update-gap targets before calling background tracking dependable; the operating system can still interrupt it.
- Start with internal device distribution. Developer accounts, signing, distribution and any new subscription costs require a concrete cost/distribution decision when this phase starts.

**Exit:** a controlled field pilot demonstrates dependable-enough tracking for the promised service, safe offline recovery and support instructions for stopped tracking. Phase 1 manual delivery operation remains available during mobile problems.

## Phase 4 — Optional delivery-management service

Evaluate this after the preceding phases in the requested order. A provider is an optional operating choice for a restaurant, not a required replacement for Tide Casa delivery.

- Compare driver app reliability, customer branding, task pricing, map/tracking access, data handling, multiple-restaurant isolation, API/webhook support and cancellation behavior against actual client volume.
- Keep orders and restaurant relationships in Tide Casa. Designate one dispatcher of record per delivery so native and provider assignment cannot compete.
- Add explicit restaurant/order/driver-to-provider mappings. Create external jobs with idempotency keys; verify and deduplicate callbacks, reject out-of-order state changes, and periodically reconcile missing updates.
- Provider delivery events may update fulfillment; they must not independently declare a restaurant payment received or initiate a refund.
- Preserve the restaurant-branded customer receipt where permitted, or make an intentional branded handoff. Validate tracking-link access and expiry rather than exposing a reusable provider account link.
- Agree the failure fallback before launch. If the provider is down, staff see Needs attention; manual takeover must reconcile or cancel any existing provider job to avoid dispatching two drivers.
- Select one provider for a sandbox proof, calculate recurring cost at realistic volume, and present the specific account permissions and expenditure before connecting a paid service.

Onfleet is a researched example, not a selected vendor or approved purchase. Reassess available services and prices at this phase.

**Exit:** complete a provider-backed test delivery and demonstrate callback replay, outage recovery, cancellation, reassignment, payment separation and isolation between restaurants.

## Release and rollback approach

1. Use local fictional fixtures for implementation and failure tests. Add focused tests for authorization, concurrency, payment separation and recovery; UI layout changes get browser checks.
2. Extend the delivery rehearsal with manager, driver and customer on separate devices. Include a second fictional driver to test reassignment and revoked access.
3. Run a controlled 20-restaurant scenario in an isolated test environment, respecting the current 23-new-orders-per-location-per-minute allowance. Exercise simultaneous assignments and status reads; add location load in Phase 2. Verify zero cross-restaurant reads/writes. Compare latency, errors, memory and database usage to a recorded baseline before claiming production capacity. Historical load reports alone are insufficient.
4. Build any database changes additively, verify old orders still render, and rehearse rollback against a backup. Prefer an additive migration and a forward fix over destructive schema reversal.
5. Enable the enhanced delivery module for Gulf Lantern first; keep real personal data and location out of the public shared demo. Then enable per participating restaurant, with GPS and provider integrations separately controlled.
6. Disabling a new feature must stop new usage cleanly while preserving a way to finish active deliveries and reconcile outstanding money. If the old UI cannot represent new states, retain the compatible delivery-management path until those deliveries are resolved; a feature switch alone is not a complete rollback.
7. Save the tested source archive, migration/recovery notes and release evidence for each deployed phase. No additional hosting, paid provider or messaging service is assumed in this plan.

## First implementation handoff

Start with Phase 1 slices 1–2: confirm delivery/payment states, define the new per-restaurant feature setting, and implement the manager delivery desk using existing assignment rules. Keep changes local until the focused state/access checks and browser review pass. Continue through the driver and customer slices before treating Phase 1 as complete.

Existing files to consult:

- `TideCasa.Api/Features/RestaurantOrdering/RestaurantManagementStore.cs` — assignment, access, transitions and recorded payments.
- `TideCasa.Api/Features/RestaurantOrdering/RestaurantOrderingStore.cs` — order creation and protected customer tracking.
- `TideCasa.Blazor/Components/Pages/RestaurantOperations.razor` — current order desk.
- `TideCasa.Blazor/Features/RestaurantManagement/RestaurantManagementFlow.cs` — form actions and API calls.
- `TideCasa.Blazor/Components/Pages/RestaurantOrder.razor` and `.razor.cs` — customer receipt and manual refresh.
- `TideCasa.Blazor/Components/Pages/StaffTrainingWorkspace.razor` — existing restaurant-managed employee enrollment.
- `TideCasa.Contracts/RestaurantManagement.cs` and `RestaurantOrdering.cs` — shared contracts.
- `scripts/verify-restaurant-ordering.py`, `verify-friday-load.py`, `verify-multibar-load.py`, `verify-location-order-limit.py` and `verify-hosted-demo.py` — existing checks to assess and extend where appropriate.

## References

- [W3C Geolocation: requesting a position](https://www.w3.org/TR/geolocation/#request-a-position) — permissions, visible documents and position updates.
- [Google Maps URLs](https://developers.google.com/maps/documentation/urls/get-started) — cross-platform directions links without a Google API key.
- [Onfleet delivery capabilities](https://onfleet.com/blog/frequently-asked-questions-about-onfleet/) and [recipient tracking](https://support.onfleet.com/hc/en-us/articles/41314013074068-Notifications-and-Tracking-Page) — examples for the final optional integration phase.

References were reviewed in the preceding planning discussion. Provider capabilities, pricing and mobile platform requirements must be rechecked before committing their implementation.
