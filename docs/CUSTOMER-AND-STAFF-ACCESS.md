# Customer and staff access

The ordering home now has **Get the App**, **My orders & rewards**, and **Staff login**. Installation instructions are unchanged. Customer ordering stays available without an account.

## Demonstrate Gulf Lantern

1. Open `https://demo.tide.casa/order/gulf-lantern` on the phone.
2. Choose My orders & rewards, then a fictional customer such as Avery. The shared demo uses existing sample identities without passwords.
3. Return to the menu and place a fictional Pay staff order. Its receipt says when saving to the signed-in account succeeded.
4. Open My orders & rewards to see saved orders and cooking progress. Active orders refresh every 30 seconds while visible; a failed refresh keeps the last checked status and shows a warning.
5. Choose My rewards. Customers request an earned reward and show it to staff; staff verify purchases and fulfill redemptions. Ordering alone does not award unverified points.
6. Choose Staff login, then Jordan for manager tools or a staff member for their restricted workspace. Customers cannot grant themselves team roles.

For private restaurants, the same entrances use the existing email/password sign-in, email verification and password recovery. A manager must add the employee's matching email and role through Team before that account receives staff access. Public sample-person switches remain restricted to the isolated fictional demo.

## Receipt ownership

New signed-in orders save automatically after placement. Guest ordering also saves a private receipt in this browser tab, for all fulfillment types. A guest can sign in from that receipt and explicitly choose Save my current order. Saving requires both authenticated identity and the receipt's random secret; matching a name or phone is never sufficient. An already claimed receipt cannot be moved to another account.

Saved account orders work on another signed-in device. The list is limited to the latest 30 from 90 days. Older pre-release guest orders are not retroactively assigned. The guest receipt itself stays in session storage, so a closed/cleared tab may lose an unsaved receipt. A failed automatic save does not place another order; the receipt offers account saving again.

The optional customer_user_id lives in existing order JSON. No schema migration, hosting-plan change, payment integration or new subscription is required. API reads filter by restaurant and authenticated user; no contact details or receipt secrets are returned in the customer list. Writes keep the existing serialized transaction/version checks. Web writes require the current cookie identity, same origin and antiforgery validation.

## Verification

`scripts/verify-customer-orders.py` exercises real API and web storage, authenticated customer isolation, restaurant isolation, receipt-proof validation, idempotency, staff status transitions, native form CSRF/origin protection, private cache headers and access entrances. Run with `--postgres` against the existing disposable local PostgreSQL fixture for the deployed database dialect. Phone-width browser rehearsal verifies navigation, automatic saving, account order retrieval and rewards/staff entry points.
