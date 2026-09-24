# BarTide client onboarding and launch blueprint

September 24, 2026. Proposal grounded in the recovered application source and current Stripe documentation. This document does not implement or enable onboarding, payments, deployment, or publication. Existing behavior and commercial terms remain unchanged.

## Product outcome

A bar owner creates an account, works through a short saved checklist, invites a manager to help, connects payments, reviews a private version of their app, and approves its release. BarTide assembles the app from its maintained restaurant template and the client's approved information. Optional features create extra questions only when selected.

The default scope is the full browser/home-screen BarTide app, with ordering, payments and staff tools configurable. Events, rewards, training, delivery and store submissions can be deferred. A client never needs to supply an API key, edit code, choose hosting infrastructure, or diagnose a server error.

## What the source already provides

| Area | Existing foundation | Gap for the proposed experience |
| --- | --- | --- |
| Account | Signup, email verification, login, recovery and durable user bindings | Route verified clients directly to their unfinished setup |
| Workspace | Free private draft; business name, contact, city/state and notes; duplicate registration handling | Persistent section progress, precise launch requirements and one next action |
| Content | Business profile, categories, menu items, ingredient removals, media and private preparation | Structured address/hours, tenant branding, reviewable imports and a complete private preview |
| Operations | Tables/QR, pickup, delivery, tips, tax setting, fulfillment and staff roles | Several settings require an active app; allow preparation without opening public orders |
| Payments | Separate software billing and restaurant payments; custom .NET Stripe transport; Accounts v2; embedded and hosted onboarding | Merchant path is explicitly sandbox-only; real provider rehearsal and a separate live-mode implementation are still required |
| Launch | Platform-owner publication review, optimistic version checks and pause/resume | Comprehensive readiness evidence, client approval records, repeatable assembly and recovery |

This is source inspection, not fresh application or production verification. Prior verification reports remain historical evidence.

## Client journey

The account screen should always show the same six sections. Each shows Not started, In progress, Needs your attention, Waiting for verification, Ready, or Skipped where permitted. Every section saves to the server; clients can leave and resume on another device. Never display Saved before the save succeeds.

| Section | Details to request | Completion rule and responsibility |
| --- | --- | --- |
| 1. Your business | Display name; owner's name and verified account email; public contact phone/email; full venue address; country; time zone; weekly hours and closed days; existing website/social links if available | Owner establishes the workspace and confirms authority. Launch requires the public identity/contact details. Keep private owner email separate from public support email. Initial merchant scope remains US/USD. |
| 2. Your look and menu | Logo or explicit text-logo choice; style preset and accent color; short introduction; photos or explicit no-photo choice; menu categories; item names, descriptions, exact prices, availability and supported ingredient-removal choices | Owner or invited manager reviews the rendered result. Online ordering requires at least one available, priced item. Photo/PDF/spreadsheet imports are draft suggestions until the client confirms extracted names and prices. Never invent prices, ingredients or allergy claims. |
| 3. How you serve | Select menu-only, table ordering, pickup and/or delivery; table labels; public ordering phone; pickup instructions; tips preference; confirmed tax setting; staff-payment choice; delivery ZIPs, minimum, fee and capacity only when delivery is selected | Owner or manager confirms every selected service. Defaults keep order acceptance off. Require at least one usable payment path before accepting orders. Unsupported paid modifiers or complex tax requirements go to a scoped review. |
| 4. Your team and extras | Manager name/email and invitation; operating staff names/emails and roles when needed; who will receive and fulfill orders; optionally events, rewards and training materials | Owner grants manager access. Managers can prepare content and permitted operations. Banking, ownership, plan purchases and final release approval remain owner actions. Optional unfinished extras stay disabled and do not block the core launch. |
| 5. Your plan and getting paid | Separate cards for the BarTide package and the restaurant's guest payments. Package: server-generated quote and explicit terms acceptance. Guest payments: owner confirms saved business information and completes Stripe's secure form | The backend verifies software payment separately from merchant verification, card capability and payout capability. Sensitive banking/identity fields stay inside Stripe. Website/menu-only clients may defer guest payments. |
| 6. Preview and launch | Private phone/desktop preview; confirmed content and prices; practice customer/staff journey; requested launch time; selected launch scope; owner approval of the exact version | Show each blocking item with a direct fix link. The pilot retains the current BarTide review and schedule. Later automatic publication may act on an approved version only when all applicable checks pass. |

Do not force clients through a single unskippable linear form. They can upload a menu while Stripe reviews the business. A manager may prepare content while the owner handles billing. Required fields depend on selected features, and optional sections must offer a clear Do this later choice.

Existing purchase-first customers should claim the verified purchase through the existing flow and land in the same setup checklist, without paying again or creating a second workspace.

## Stripe's role

BarTide owns the public business profile, menu, media, operating settings, staff permissions, app assembly and release. Stripe owns its verification form, identity/bank information, account requirements and payment processing. Reuse confirmed public information where the supported API allows; do not silently overwrite verified legal identity with marketing edits.

Keep two visibly different payment concepts:

- **Your BarTide plan:** existing $600 setup, $149 monthly maintenance beginning 30 days after the initial purchase, optional $300 store submission support, and existing referral/consent rules. Workspace registration remains free.
- **Your customers' payments:** direct charges to the restaurant's saved connected account, using the existing account model and Dashboard access. Do not introduce an extra transaction fee or migrate software-billing customer records as part of onboarding.

Use the existing custom server transport and official browser component. An authenticated, owner-bound server endpoint chooses the saved connected account and creates the short-lived embedded session; the browser never chooses an arbitrary account. Preserve hosted onboarding as a fallback.

Show Business verification, Card acceptance, Payout readiness and BarTide checkout independently, with a last-checked time and next action. Unknown or stale information cannot satisfy a launch gate. A return from Stripe or component exit is not proof that verification completed. Process the appropriate Accounts v2 requirement/capability events and connected-account payment events with their existing signature, environment and tenant binding checks; reconcile current provider state before enabling payments.

Existing-account attachment is not currently implemented. Do not label a new connection as importing an existing Stripe account or payment history. Networked onboarding can reuse some business information; that is a different function.

The Stripe implementation planner was consulted in the existing sandbox context for this proposal. Its SaaS/direct-charge/embedded-onboarding direction fits the existing integration. Accepting that planning guide did not change any account or grant permission to migrate billing or activate services.

## Backend structure

Use one versioned onboarding record per existing tenant and a maintained restaurant template. Do not create a new codebase, hosting service, or compilation pipeline for every signup. Standard releases remain software deployments; a client launch configures and publishes a tenant within the application. Start with the existing hosted slug-based address. Custom domains need their own ownership, routing and HTTPS workflow.

| Proposed record/service | Required information or behavior |
| --- | --- |
| Onboarding draft | Tenant, schema version, revision, section answers, selected features, missing-field paths, last editor and save time; references to existing domain records rather than duplicate payment/menu truth |
| Preparation access | Explicit tenant-scoped owner/manager grants and invitation acceptance; allow approved setup work while draft/building without granting public order access |
| Readiness evaluator | Stable check code, affected section/feature, status, actor responsible, safe explanation, fix action, evidence version and checked time |
| Assembly job | Tenant + input revision + template version + job type as a deduplication identity; durable state, attempts, lease, next retry, safe failure code and correlation ID |
| Release snapshot | Immutable versions of profile, menu, branding, assets and selected operational settings; preview and tests use these same versions |
| Approval record | Approving owner, exact release version, chosen scope, launch timing, applicable terms version and approval time |
| Publication record | Approved release, publication result, health evidence and previous release reference; preserve orders and payment records during rollback |

Suggested API surface, additive to existing features: read setup and next actions; save one section with an expected revision; validate readiness; request/resume assembly; read job state; inspect authenticated preview; approve the exact release; publish through the internal release worker. These are proposed interfaces, not endpoints that already exist.

Existing feature stores remain authoritative. Onboarding should call their validation and authorization rules through shared application services. A menu or operating-setting edit must update or invalidate affected readiness evidence, preview versions and approvals. Never auto-publish edits that were made after approval.

Use durable jobs/outbox records in the existing database and a bounded worker within the existing hosting footprint first. Persist work before acknowledging it, retry temporary failures with backoff, and move exhausted or ambiguous operations to review. Reuse payment-specific recovery logic instead of retrying uncertain charges or creating new Stripe accounts. No additional hosting spend is assumed; the recovery record's existing $20/month compute cap remains a constraint to verify before infrastructure changes.

## Launch gates and failure handling

Evaluate gates for the selected scope; a percentage-complete meter alone cannot authorize release.

| Gate | Evidence required |
| --- | --- |
| Ownership and enrollment | Verified account binding, owner authority, verified qualifying software purchase and accepted terms |
| Content | Required profile complete, assets accessible, approved menu/categories/prices, selected template renders on phone and desktop |
| Service readiness | Enabled fulfillment modes valid, tax explicitly confirmed, usable payment path and an accountable order recipient; unused modes off |
| Merchant payments, when selected | Correct tenant/account/environment; fresh requirements and card/payout capability evidence; actual live implementation and required feature verification complete |
| Application checks | Preview works; links/media load; tenant access checks pass; selected customer and staff flows pass in isolated practice mode |
| Approval and timing | Current release matches owner approval; pilot BarTide review recorded; scheduled/build-policy conditions satisfied |
| Publication | Approved release activated once; public URL and selected features pass health checks; previous release retained |

For the pilot, require both card and payout capability readiness for a launch that advertises online payments. This is a proposed BarTide operational rule, not a claim that Stripe always requires both to create a charge. Keep the underlying statuses separate.

If payment verification is pending, continue content preparation. Offer an explicit owner choice to launch menu-only, or with a verified staff-payment mode where supported. Record approval of that reduced scope; never silently downgrade what the owner approved. If they require online payments at launch, keep publication waiting.

If custom-domain setup is pending, offer the hosted address as a separately approved scope. Never claim a custom domain is ready before ownership, routing and HTTPS pass. Native store submissions remain a separate assisted service with outside review timelines.

Automatic debugging means detecting missing/invalid information, checking the assembled app, retrying safe transient work, reconciling provider status and routing specific failures to the right person. It does not mean automatically rewriting production code or guaranteeing that every defect fixes itself.

Clients see actions such as Confirm two menu prices, Finish Stripe verification, or We are checking your app. BarTide sees the failed check, input/template versions, redacted diagnostic context, attempt history and controlled retry action. Post-launch failures pause the affected feature or restore a prior release according to a reviewed policy; financial records are never rolled back with site content.

## Changes that must be made deliberately

1. **Preparation versus publication:** merchant authorization currently requires `status='active'`; ordering settings also load only active venues. Account discovery binds ordinary manager invitations only for active workspaces. Introduce preparation access before attempting an end-to-end onboarding flow. Preserve the active-only public ordering boundary.
2. **Live payments:** merchant options, account reads, Checkout and persistence are sandbox-specific. Live support is an implementation and validation step, not just replacing a test key. Keep test/live account bindings, events, attempts and credentials separate; never promote a sandbox result into live evidence.
3. **Release timing:** current terms and launch code impose a 30-day build period for the current plan and platform-owner review with the customer. Start with automatic preparation and assisted publication. To introduce earlier/automatic publication, change terms, consent/versioning, eligibility and review behavior together, with a clear rule for existing customers. The $149 billing start remains unchanged unless separately decided.
4. **Readiness depth:** current launch review checks the scheduled dates and presence of named menu items. It does not prove branding, correct prices, working payments, fulfillment readiness, domain readiness or a passing release test.
5. **Content assembly:** a media upload is source material, not a finished menu or branded app. Add structured review and a reusable tenant theme/preview layer. Unsupported imports or customization requests must be visible review items.

## Implementation order and acceptance evidence

| Slice | Deliverable | Evidence needed before calling it complete |
| --- | --- | --- |
| 1. Saved setup | One six-section client checklist, versioned answers, required/optional rules, exact next actions; retain current publication behavior | New owner signs up and resumes after logout; failed saves stay unsaved; duplicate submits create one workspace; stale edits are rejected; another tenant cannot read the draft |
| 2. Private preparation | Narrow owner/manager draft access and safe settings preparation; role-appropriate team collaboration | Invited manager edits permitted sections before launch; cannot change banking, billing, ownership or approve release; public routes/orders stay unavailable |
| 3. Payment integration | Embed existing payment workflow, preserve separate plan billing, complete actual sandbox configuration and provider rehearsal | Create/resume one account; pending requirements; separate payouts/cards; session expiry and hosted fallback; webhook duplicates/out-of-order events; declined cards, 3DS and restart recovery |
| 4. App assembly | Tenant branding, reviewed menu ingestion, authenticated versioned preview, isolated practice order and readiness report | Approved input produces the expected app; no invented content; mobile checks; guest/staff practice works without real charges or production orders; restart resumes one job |
| 5. Assisted pilot | Paid client journey through approval and current-policy publication, plus health checks and support view | One complete rehearsal with no direct database editing; no public exposure before approval; exact approved release goes live; failed publication is recoverable |
| 6. Controlled automation | Separately verified live merchant support and versioned release-policy change; automate standard launches after owner approval | Live/test separation, current payment evidence, eligible policy/terms, exactly-once publication effect, edit-after-approval invalidation, rollback and exception routing |

Use and extend existing workspace, auth, merchant-payment, service-billing and launch-review checks when implementing these slices. This planning task did not execute them and does not claim a new passing build.

## Source anchors and provider references

- [Workspace registration](../TideCasa.Api/Features/Accounts/WorkspaceRegistrationStore.cs), [current start screen](../TideCasa.Blazor/Components/Pages/StartWorkspace.razor), [workspace permissions](../TideCasa.Api/Features/Accounts/WorkspaceAccessStore.cs).
- [Business/menu contracts](../TideCasa.Contracts/RestaurantManagement.cs), [ordering contracts](../TideCasa.Contracts/RestaurantOrdering.cs), [operating settings](../TideCasa.Api/Features/RestaurantOrdering/RestaurantManagementStore.cs).
- [Merchant authorization](../TideCasa.Api/Features/MerchantPayments/MerchantPaymentsStore.cs), [sandbox configuration](../TideCasa.Api/Features/MerchantPayments/MerchantPaymentOptions.cs), [payment page](../TideCasa.Blazor/Components/Pages/WorkspacePayments.razor).
- [Service terms](../TideCasa.Blazor/Components/Pages/ServiceTerms.razor), [payment reconciliation](../TideCasa.Api/Features/ServiceBilling/ServiceBillingReconciliation.cs), [launch rules](../TideCasa.Api/Features/LaunchReview/LaunchReviewStore.cs), [publication UI](../TideCasa.Blazor/Components/Pages/OwnerLaunchReview.razor).
- [Existing Stripe implementation record](BARTIDE-STRIPE-IMPLEMENTATION-20260924.md) and [sandbox readiness](BARTIDE-STRIPE-SANDBOX-READINESS.md).
- Stripe [SaaS onboarding](https://docs.stripe.com/connect/saas/tasks/onboard): hosted/embedded collection, account requirements, return behavior and Accounts v2 events.
- Stripe [embedded onboarding](https://docs.stripe.com/connect/embedded-onboarding): secure verification forms and continued requirement collection.
- Stripe [Accounts v2](https://docs.stripe.com/connect/accounts-v2): account model and interoperability limitations.
- Stripe [webhooks](https://docs.stripe.com/webhooks): signatures, delivery behavior and duplicate handling.
- Apple [App Review Guidelines](https://developer.apple.com/app-store/review/guidelines/): separate review and suitability requirements for native distribution.
