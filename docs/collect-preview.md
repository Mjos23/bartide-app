# Collect: fictional pilot release

Collect is the Bangel Debt Analytics product at `collect.tide.casa`. The intended service lets debtors join independently for free, find participating creditors, choose whether to share a financial-profile snapshot, and negotiate with a creditor's own office. Creditors/collection clients pay $1,500 for the build and $199 monthly; the existing first-month convention makes the initial amount $1,699. The business is based in Clearwater, Florida and intends United States coverage. This release does not establish nationwide operational eligibility.

## Current boundary

This is a working **fictional preview**, plus the existing verified email account enrollment. No real debts, uploaded financial files, debt payments, consumer fees, creditor subscriptions, collection communications or real creditor activation are accepted. A real sign-in opens an honest account-ready page. It never inherits a fictional creditor role. There is no configuration switch that makes the simulated financial workspace live.

The creditor evaluates submitted documents. Planned document intake includes paystubs, bills and handwritten budgets. `document-reviewed` is displayed as **Creditor reviewed**; the platform does not certify authenticity, make credit decisions, estimate ability to pay, or guarantee a negotiated outcome. The earlier proposal for independent source verification is superseded by this human-review decision.

## Implemented experience

- `/` or `/collect`: two-door homepage, creditors on the left and debtors on the right; stacked phone entrances include quick audience links. Each door leads to its existing office or free-registration route. The fee calculator appears below and runs entirely in the browser without storage or requests.
- `/collect/signup`, `/collect/signin`: existing global verified-email identity, host-only session cookies, same-origin/antiforgery forms and fresh API identity checks.
- `/collect/profile`: fictional independent debtor profile, kept separate from case data.
- `/collect/directory` and `/c/{slug}`: fictional creditor discovery and office preview. Searching discloses no profile. An inquiry may include an explicit, immutable profile snapshot.
- `/client` and `/collect/client`: client pricing, branding preview, bounded CSV import, authority/creditor distinction and invitation contracts. Custom preview office addresses are session-specific; this does not publish a real public client office.
- `/collect/case/{id}`: itemization, sample notice, disputes/support, preferences, profile, generated sample documents, human review, offer/counter/withdrawal and exact-term acceptance.
- `/collect/agreement/{id}`: printable fictional agreement with immutable parties, payee, amounts, terms and exact-cent installment schedule.
- Payments: explicit simulation; server-selected amount and creditor, separate pending/succeeded/settled/returned/refunded states, event replay/order handling. No real provider, webhook, card capture, wallet, automatic debit or transfer.
- `/collect/analytics`: signed creditor-reviewed minus reported monthly income differences; zero denominator, missing evidence, expired review and incompatible gross/net basis remain visible.

## Architecture and isolation

Existing API/restaurant behavior is retained. Web adds an isolated Collect middleware/renderer and rules module. Collect routes in production require the Collect host. Restaurant membership and metadata never grant a Collect role. Public examples cannot become real memberships. The preview role selector is intentionally limited to the caller's own fictional workspace.

Preview state is persisted under `tide_collect.preview_workspaces` using authenticated Data Protection encryption, bound to its workspace ID. The production key ring is already durably persisted and encrypted in PostgreSQL. No financial fields or document content are stored as searchable plaintext. Optimistic revision updates cover mutations. Document access is recorded separately in `preview_access_log`, so viewing a file does not stale an open form. Access records cascade when the preview is deleted. Generated sample downloads are safe text attachments; no arbitrary file bytes are accepted.

Limits: 500 concurrent preview workspaces, 24-hour access lifetime, expired records removed on subsequent preview creation; at most 100 cases/workspace, 25 CSV rows/import, 12 document metadata records/case, 100 offers/case, 2 MiB encrypted workspace envelope. Mutation and download rate limits apply. Idle expiration hides a workspace immediately; physical expired-row cleanup is opportunistic rather than a scheduled retention service. Development has an encrypted-file alternative; production never silently falls back to ephemeral disk.

These limits describe a pilot. A 10,000-row deterministic in-memory variance check is not a database-load, large-portfolio, or predictive-model benchmark.

## Fee comparison basis

The creditor headline is “Stop relying on a middleman. See for yourself.” It names Beyond Finance in the supporting comparison without asserting that the company mishandles customer data. The debtor headline is “Debt relief shouldn’t leave you deeper in debt.” A linked [CFPB explanation](https://www.consumerfinance.gov/ask-cfpb/what-is-a-debt-relief-program-and-how-do-i-know-if-i-should-use-one-en-1457/) describes debt-settlement risks if payments stop, including added fees/interest, credit damage and lawsuits. The page distinguishes settlement from a consolidation loan and does not claim every provider causes financial ruin.

The homepage uses Beyond Finance's own [program pricing](https://www.beyondfinance.com/program/), checked September 26, 2026: typical program fees of 15–25% of enrolled debt, varying by debt and state, charged on a success basis after an accepted offer and a payment toward it. The default is an illustrative 25%; 30% is an editable hypothetical rate, not a claim about their usual pricing. At $20,000 and 25%, the calculated program fee is $5,000 versus Collect's $0 debtor access. The comparison does not assume equal settlement outcomes, include all repayment costs, or allege misconduct by the competitor. Beyond provides a managed service; Collect intends to provide direct-negotiation tools.

## Before real financial intake

Real intake is **not implemented in this release**. Required follow-on work includes separate durable real-user profiles/disclosures and client authorization storage; creditor onboarding/authority and public office activation; private PDF/image upload with quarantine, malware scanning, encrypted object storage, consent and document access controls; staff MFA; controlled document review and customer clarification messages; state-specific notices/dispute/communications handling; retention/deletion and incident operations; production backup restoration and readiness review. Do not reclassify preview fixtures as live data or expose the preview role selector to real records.

Payments also need an eligible processor and creditor merchant onboarding with authenticated, idempotent provider events and operational reconciliation. Stripe's [prohibited-business list](https://stripe.com/legal/restricted-businesses) includes debt collection agencies and debt settlement/negotiation businesses. This implementation does not route that activity through a restaurant Stripe account or enable client service billing through the existing restaurant checkout.

Federal and applicable state collection rules require a market-aware implementation: [CFPB Regulation F](https://www.consumerfinance.gov/rules-policy/regulations/1006/), [CFPB federal and state protections overview](https://www.consumerfinance.gov/ask-cfpb/what-laws-limit-what-debt-collectors-can-say-or-do-en-329/), [Florida consumer collection statutes](https://flsenate.gov/Laws/Statutes/2026/Chapter559/Part_VI). This preview makes no legal-compliance certification.

## Verification and release

Build API and Web with the pinned .NET SDK. `TideCasa.Collect.Checks` links the exact rules/repository files; its README describes isolated PostgreSQL tests. `scripts/verify-collect-http.py` refuses non-loopback targets. `scripts/verify-auth.py` exercises existing identity/restaurant behavior plus Collect enrollment; set `TIDE_TEST_DOTNET` when the SDK lives outside the repository.

Deploy from a frozen manifest based on production commit `0701e99fe407f8aa771cdd80d0b34aa4d74ea2b1`; do not deploy unrelated recovered source. Preserve service sizing, secrets, ingress and existing domains. Add the Collect alias/AllowedHosts and selected reviewed source branch. Before enabling the hosted preview, the database owner must apply `deploy/app-platform/20260926-collect-preview.sql`. Production only probes the preprovisioned tables; it never creates a schema or falls back to disk. The restricted Web role receives access only to the two isolated preview tables. A storage failure returns a Collect error without stopping restaurant hosting. Development can create its isolated schema; set `Collect:CreateSchema=false` to rehearse restricted-role behavior. Confirm a synthetic workspace can be created, revisited and deleted after deployment. Roll back the app spec/source to the saved prior version if needed; retain new isolated preview tables for investigation rather than deleting unrelated data.
