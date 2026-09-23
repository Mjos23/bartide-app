# Pricing and email results release — September 23, 2026

This is a local release candidate. It has not been published, and the prepared Gmail test email has not been sent.

## Customer behavior

- New purchases charge $600 initially, or $900 with the existing optional $300 app-store support.
- Stripe Checkout collects a payment method and begins the $149 monthly subscription after a 30-day trial. The initial payment contains setup/add-ons only.
- Confirmed payment starts the 30-calendar-day private build period. Human launch approval still applies.
- Paid legacy $50 subscriptions and their invoice history retain their saved terms. Unpaid checkout consent for the old plan cannot resume under the new pricing.
- The public pages, service terms and both downloadable public flyers use the new pricing and timing.
- The owner-only email results dashboard records anonymous campaign visits and ordered link selections, with a separate test view. See `EMAIL-RESULTS.md` for scope and limits.

## Verification

Billing checks: 132 passed. Launch checks: 86 passed. PostgreSQL pricing migration checks: 14 passed, including populated legacy upgrade, repeat startup and checksum rejection. Populated SQLite upgrade preserves legacy order/invoice/refund relationships.

Email tracking: 19 SQLite, 19 PostgreSQL, 15 JavaScript and 28 integrated API/web checks passed. Independent browser review confirmed that opening the test landing, selecting Book a demo and then Purchase increments the test report and preserves the ordered pathway; production results stay separate. The local purchase page displayed $600 setup, $149 monthly after 30 days and a 30-day build.

The final eight-project solution build passed with zero warnings/errors after restoring the new checks project's dependencies. A fresh run against that combined build passed all 28 integrated email checks. Five migration-only startup/restart checks passed, including successful completion while the configured HTTP port was already occupied, confirming that the mode does not start a listener. Final build and packaging evidence are recorded under `output/pricing-email`. No real provider payment or test email is part of these checks.

## Reviewed deployment sequence

Publishing requires explicit approval. Keep marketing and the fictional demo isolated. Take a database backup before the pricing schema upgrade. The old API cannot restart against the new pricing ledger; rollback requires a forward-compatible build or the reviewed backup/restore procedure, not simply switching back to old binaries after new purchases exist.

1. Apply `deploy/app-platform/20260923-email-tracking.sql` as the schema owner to the existing marketing schema and API role only. It creates additive analytics tables with RLS and limited DML grants. Do not apply it to the fictional demo.
2. Run the new API artifact once with the correct isolated schema, a temporary schema-owner connection, its usual production settings, and `--Storage:MigrateOnly=true`. All schema initialization finishes, then the process exits before HTTP listeners or background workers start. This records the new pricing migration using its embedded checksums. Do not replace the live service's restricted role with the owner credential.
3. Deploy the same reviewed API/web candidate under the existing restricted runtime credentials. Preserve provider enablement flags, identities, hostnames and compute limits. No real charge or email is needed to publish.
4. Verify public pricing, health, owner access and test-campaign tracking. Keep paid legacy records intact. Gmail sending, delivery, opens and bounces are not automatically measured by the dashboard.
5. Stop for user approval before sending the prepared test email. After approval, verify the actual received message, From/Reply-To, authentication, CTA destination and test click pathway.

The email's tracked CTA is `https://bar.tide.casa/?campaign=7ae11d6ce57d4c1fb881ab6f84259bd0`. It opens the normal home page today, but the new tracking and pricing require publishing this release.
