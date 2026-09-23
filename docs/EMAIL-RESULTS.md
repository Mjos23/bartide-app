# BarTide email results

Owner dashboard: `/owner/email-results`. Access requires a fresh authenticated platform-owner account at both web and API boundaries. Test campaigns use a separate view and never contribute to production results.

The prepared test CTA is `https://bar.tide.casa/?campaign=7ae11d6ce57d4c1fb881ab6f84259bd0`. It opens the normal BarTide home page. The opaque campaign code contains no recipient identity and is not an authorization credential. The dashboard can create independent campaign links; creating one never sends an email.

## Measured behavior

An email landing creates one random visit, remembered by a host-only HttpOnly protected cookie for 30 minutes. Reopening the same campaign in that period reuses the visit. An allowlisted link selection records only its category, visit and server timestamp. Categories are home, pricing, demo, purchase, contact and sample. Consecutive repeats are deduplicated, with at most 20 selections per visit. Delegated JavaScript works across Blazor enhanced/interactive navigation; keepalive requests do not delay link navigation. Only approved Tide Casa/BarTide origins and the hosted demo root are recognized as external links. The recording request always stays on the original origin, and its protected cookie must accompany it.

Pathways follow clicks made on BarTide. A selection of a Tide Casa or demo-domain link is recorded, but subsequent activity on that other host is not joined. A demo selection is not a submitted request; a purchase selection is not payment evidence.

Sent, delivery, open, bounce and actual purchase-conversion metrics are unavailable. Gmail drafts/manual sends do not provide them. No tracking pixels, contact addresses, full target URLs, query strings or visitor IP addresses are stored by this feature. Scanners and automated visits can affect counts. Privacy controls, blocked cookies/JavaScript, rate limits or failed network requests can cause undercounting; this is directional traffic reporting. GPC/DNT requests are not tracked.

## Storage and deployment

The additive `EmailTrackingSchema` is independent of immutable baseline migrations. It auto-creates tables for local SQLite/development only. Production PostgreSQL startup refuses a missing extension; the schema owner must first review and apply `deploy/app-platform/20260923-email-tracking.sql` with the actual dedicated marketing schema and API role. The script denies public/anon/authenticated roles, enables RLS and grants only the API operations used here. Do not apply it to the separate public demo. The application then seeds exactly one test campaign idempotently.

Reports filter to 90 days. Counts cover all retained visits; pathway detail is bounded to the 20 most common paths among the 1,000 most recent visits in that period. Startup and new visits remove older anonymous visit/click rows. Capacity limits are 100 campaigns, 100,000 retained visits overall and 5,000 new visits per campaign per UTC day. Tracking failure does not prevent the home page loading. Public endpoints accept small bounded inputs and use the existing public-form rate limiter. Owners cannot supply custom redirect targets.

Rollback: restore the previous API/web release; retain these additive tables for the next deployment. No baseline rows or migration history are changed by this feature. Removing the tables would discard analytics and is a separate destructive action.

## Local verification

Run `dotnet run --project TideCasa.EmailTracking.Checks` for real SQLite storage checks, and pass the isolated loopback PostgreSQL fixture JSON path after `--` to run the same contract against PostgreSQL. The fixture creates and removes only its fresh `tide_email_check_...` schema. `node scripts/verify-email-click-paths.cjs` exercises navigation targets, trusted-origin boundaries, privacy opt-out and repeated script loading. After building API and Web, `python scripts/verify-email-results.py --hold` exercises the real web/API flow against an isolated SQLite database and a synthetic local identity provider, then retains an owner preview for up to 30 minutes. No real email/payment/identity providers are contacted. Complete a browser check of the owner report and a test landing → demo/purchase path before release. Do not send the prepared test email until the sender is configured, the website build is published and the user approves sending.
