# BarTide custom Stripe integration — implementation record

September 24, 2026. Local candidate only. Use the recovered source at `C:/Users/suffo/Documents/TideCasa-Recovery-20260921/reconstructed-source`.

## Implemented boundary

Merchant payments and Tide Casa software billing use the same standard-.NET transport primitives with separate configured credentials and contexts. Stripe.net has been removed from the API package references. No pricing, database schema, deployment specification, marketing, delivery or email-tracking code was changed.

- Every request carries Basic authentication (`server-key:`), the pinned `2026-08-26.dahlia` version, and its own account/idempotency headers. Merchant payment requests use the saved connected account; Accounts v2, AccountSessions and software billing use platform context.
- Accounts v2 requests use JSON; v1 uses form encoding. Hosted Checkout retains the card allowlist and Link restriction, server quotes, tips, saved request fingerprints and original keys.
- HTTP clients have a managed connection lifetime, a 20-second operation deadline, a 1 MiB response limit, no redirects/cookies and an exact Stripe API origin. Development fixtures require explicitly approved loopback configuration.
- Private exceptions retain bounded provider codes and request identifiers; public responses never relay provider bodies. GET requests can retry once. Merchant writes retry only throttling or an in-use idempotency key; uncertain creates remain with the existing durable recovery flow. Software billing retains one eligible idempotent-write retry. Long Retry-After values are deferred to recovery, never shortened. AccountSessions have no automatic write retry.
- Both webhook families and software billing verify exact payload bytes using HMAC-SHA256 and constant-time signature comparisons before JSON interpretation. Five-minute timestamp bounds include future timestamps, multiple v1 signatures support rotation, and duplicate JSON properties are rejected. Existing durable inboxes, contextual replay protection, recovery leases and current-provider-state reconciliation remain.
- Merchant payment mapping requires explicit sandbox mode, identity, types, totals, currency, status and card evidence. Refund pagination uses authored paths and validated cursors, with a maximum of 100 refunds. It never follows provider URLs.

## Owner experience

The payment page confirms the saved business name/email, preserves the same account across retries, displays verification, card acceptance, payout capability and BarTide checkout separately, and provides an explicit next action, last-check time, hosted fallback, Dashboard link and sandbox test-order link when allowed. Status failures retain the binding and present a stale, recoverable state. Redirects and component exits do not prove readiness.

The new authenticated `POST /api/v1/tenants/{id}/payments/connect/session` endpoint serves only an already-saved and freshly checked account. It never accepts an account selector. Initially enable only `account_onboarding` and `notification_banner`. Client secrets are returned with no-store, not saved or logged. The Blazor proxy requires the current owner session, same-origin POST and antiforgery validation, including on session renewal.

The Blazor component uses JavaScript interop and the official **browser-only** `@stripe/connect-js` 3.4.6 loader (MIT, integrity-verified, provenance alongside the asset). Sensitive forms stay on Stripe. Secrets remain in the browser callback and are not passed through Blazor circuit parameters or storage. Closing/leaving logs out the component session. Hosted fallback remains available after load errors.

## Configuration and external verification

Existing default-off configuration is unchanged. No key, webhook destination, account grant, live account or provider setting was created or edited.

| Setting | Purpose |
| --- | --- |
| Existing MerchantPayments RestrictedKey, PlatformAccountId and webhook secrets | Server-only sandbox credential/context and distinct endpoint signatures |
| Existing OnboardingEnabled | Allows hosted setup and saved binding |
| Existing CheckoutEnabled + CardsOnlyVerified | Separate card-checkout gate; these must not be inferred from onboarding |
| New PublishableKey | Matching sandbox browser key; never a server credential |
| New EmbeddedOnboardingEnabled + EmbeddedOnboardingVerified | Both required. Leave false until actual sandbox compatibility, component loading and account permission checks pass |
| ServiceBilling settings | Unchanged, including existing mode gates, consent and card-method configuration |

The selected sandbox `acct_1UHOd4QkpV0WCm81` was inventoried and read without mutation. Its Accounts v2 list was empty. No local merchant restricted key or publishable key is configured in application settings/environment/user secrets. The connector's API schema advertises `2026-08-26.preview`; it does not prove the app's pinned dahlia contract. Documentation and the previously installed 52.4.2 types informed mapping, but **real dahlia account creation, hosted/embedded onboarding, Checkout, authentication challenges and webhooks remain unverified**. No real Stripe payment was attempted. Local synthetic fixture success is not provider end-to-end success.

## Existing-account authorization remains unavailable

Stripe documents OAuth as an Accounts v1 case and limits connecting accounts controlled by another platform. The current Accounts v2 provenance requires BarTide-owned metadata. There is no verified OAuth client/callback configuration or reviewed cross-model binding in this source. The portal therefore explicitly says that existing-account attachment is unavailable; networked onboarding may reuse business information but does not migrate payment history.

No OAuth exchange/callback or provenance schema was introduced. Before enabling it, verify the supported account model and permissions, then review a separate connection record with immutable environment/account/authorization provenance. Implement expiring, owner-and-tenant-bound server-side state, an exact callback allowlist, a single atomic consume, scope/mode checks and deauthorization handling. An uncertain OAuth exchange must never be automatically replayed. Existing payment attempts must retain their original account binding through any future replacement/revocation. These prerequisites prevent substituting relaxed metadata checks for authorization.

## Verification and rollback

The [verification report](../output/stripe-api/VERIFICATION.md) records the passing automated checks, the nine-project build with zero warnings/errors, browser observations, exact source diffs and hashes. The baseline merchant and service-billing suites were run before replacing their boundaries. The only authentication expectation changes in those fixtures are intentional Basic wire assertions; the existing regressions remain.

The follow-up adds a [read-only sandbox readiness command](BARTIDE-STRIPE-SANDBOX-READINESS.md), using the application's actual transport and configuration validation. It checks existing configuration and optionally performs two GET requests; it never enables gates or creates provider resources. Both normal and Development checks currently stop before network access because merchant configuration is missing. Fresh connector inventory found no connected accounts and no Accounts v2 thin-event destination. Additional local regressions cover interrupted network responses, exact-request retry behavior, invalid/duplicate/over-limit refund pages, and correct two-page refund totals with a deliberately untrusted provider pagination URL.

The original versions of touched files are preserved under `.tools/stripe-api/original/`, with `output/stripe-api/original-manifest.json`. Source diffs and the final file manifest identify new files. No application/database migration is needed for this candidate. To abandon it locally, compare current hashes to the final manifest, restore only this task's original files, and remove only its listed added files after checking for subsequent edits; never overwrite the recovered tree wholesale. Existing payment attempts, account bindings and inboxes retain their formats and keys.

Before any future release, rehearse the selected sandbox with its actual restricted permissions: account creation/resume, pending/overdue requirements, separate payout readiness, embedded sessions and hosted fallback, cards/wallet policy, table/pickup/delivery orders with tips, cancellation/3DS/failure, lost-response/restart recovery, duplicate/out-of-order notifications, and refund readback. Require separate deployment approval. No additional hosting is needed.

## Provider references

- [Authentication](https://docs.stripe.com/api/authentication)
- [Accounts v2](https://docs.stripe.com/connect/accounts-v2), including payout capability and OAuth limitations
- [SaaS onboarding and AccountSession interoperability](https://docs.stripe.com/connect/saas/tasks/onboard)
- [AccountSession API](https://docs.stripe.com/api/account_sessions/create)
- [Connect browser loader](https://github.com/stripe/connect-js)
- [OAuth reference](https://docs.stripe.com/connect/oauth-reference), including non-idempotent code exchange
- [Original approved blueprint](BARTIDE-STRIPE-API-BLUEPRINT-20260924.md)
