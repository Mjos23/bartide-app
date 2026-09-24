# Delivery phase one — Gulf Lantern release

September 22, 2026. This release contains the optional delivery workflow and the requested ordering layout improvements. Deploy first to the isolated fictional Gulf Lantern demo. Later delivery phases are paused.

## Run a demo

1. Open the restaurant menu at https://demo.tide.casa/. The cart shortcut stays visible even when empty. Quantity controls sit over larger menu photographs. Tap View cart & checkout to reach checkout.
2. Add food meeting the $15 demo delivery minimum. Select Delivery, ZIP 33706, Pay staff and fictional contact/address details. Review and send. Keep this private receipt open in its tab.
3. In another tab open https://demo.tide.casa/sample-bar#perspectives and try Jordan's manager view. Open Orders and fulfillment, then Deliveries. Assign Zoe, accept the order, prepare it and mark ready.
4. Switch to Zoe's view. Open Orders and fulfillment to reach My deliveries. Acknowledge the job; select Collected when ready, then Confirm delivered at the handoff. Directions opens the supplied delivery address in Google Maps. Use only fictional details in the public shared demo and do not drive to the fictional address.
5. The customer's receipt updates while visible, normally every 15 seconds. Delivery does not record payment. The manager may record a payment already received separately. For the demo this represents fictional payment only.

The shared demo is for rehearsal, so another visitor can also use its fictional identities. Ordinary restaurant workspaces retain their real account and tenant permissions.

## Slice 1: settings and states

The manager enables Enhanced delivery desk in Menu and settings. A public restaurant contact phone is optional there. Existing settings and unknown JSON fields remain intact. The demo leaves the contact phone blank and links customers to the fictional manager view for help.

No table or schema migration is required. The restaurant configuration adds `delivery_workflow_enabled` and `contact_phone`. A delivery adds an allowlisted progress object in the existing order JSON. Its driver name, assignment, acknowledgement, collection and delivery timestamps are public only through the protected receipt. Internal actor IDs, notes and customer contact/address fields are never added to that receipt.

Delivery status `delivered` means physically handed over with payment outstanding. It frees delivery capacity while retaining the unpaid order for staff reconciliation. After recorded payment, the order becomes `completed`. An already paid order completes when the driver confirms the handoff.

## Slice 2: staff actions

`RestaurantDeliveryWorkflow.cs` defines legal transitions. `RestaurantManagementStore.cs` still checks restaurant membership, assigned driver, active membership and expected order version. Assignment requires an active driver linked to a verified sign-in account. Reassignment requires a reason, resets acknowledgement/collection and removes the former driver's authority. Manager handoff overrides require a reason.

The manager adds a driver through the existing team workflow, then shares the workspace link. This release does not send invitation emails. Problems enter Needs attention; a manager records their resolution or reassigns/cancels using permitted actions. Neither reporting a problem nor confirming a delivery refunds or collects money.

Updates use optimistic versions. A duplicate/stale action returns a conflict and the UI asks staff to reload; it cannot duplicate confirmation history. The delivery desk retains the existing 200-order read limit and manual refresh for new jobs.

## Slice 3: customer updates and recovery

`RestaurantOrder.Delivery.cs` and `delivery-status.js` request only the protected receipt. Polls run at most once every 15 seconds, avoid overlap, pause while hidden, back off to 30/60 seconds on failures and stop after delivery/cancellation/completion. The page shows last-change and last-check times and an error instead of claiming an old status is fresh.

Delivery receipts are saved in that tab's session storage using the existing checkout recovery helper. Reload uses the order ID and secret tracking key. An uncertain submission retries the same request key. Clearing browser data, ending the tab session or using another phone does not automatically transfer the receipt. Customers can also use Check order status, including for payment reconciliation after polling stops.

## Slice 4: ordering layout

Quantity controls overlay each menu photo; photos fill their menu column. The fixed cart shows item count and the current quote, or clearly labels an unconfirmed subtotal. It remains visible across phone and desktop widths, including at zero items. The existing Add to home screen entry and the additional demo perspectives below ordering remain available.

## Verification and release boundary

Fresh builds: API and Web, zero errors/warnings. Focused real-API delivery suite: 89 checks on SQLite and 89 on isolated PostgreSQL, including 20 fictional restaurants with four concurrent workers. Both runs passed assignment, acknowledgment, reassignment, revoked access, stale writes, exactly-once confirmation, protected tracking, payment separation, capacity release and feature-off recovery. Existing management and ordering suites passed 75 and 147 checks.

Browser rehearsal covers manager → driver → customer, automatic status reads, receipt reload, and the cart/photo layout at 390px and desktop widths. These are correctness and browser-layout checks, not a measured production traffic limit or a physical-phone field trial. Deployment evidence and the exact source archive are stored locally with the release.

## Disable/recover

Disable Enhanced delivery desk to stop new enhanced deliveries. Existing orders with delivery progress retain this workflow so they can finish and reconcile. Keep this compatible application version until those orders resolve: reverting to old code cannot safely interpret the new delivered-but-unpaid state. Preserve backups; do not delete order JSON or roll back database contents to disable the feature.

The production Tide Casa app and hosting sizes remain separate. No GPS, external messaging, mobile app, provider integration or new subscription is included. Resume later phases only after the user chooses to continue.
