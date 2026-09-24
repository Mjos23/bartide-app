# BarTide sandbox readiness

This diagnostic uses the same .NET transport and merchant configuration validation as the application. It never changes configuration, enables features, creates accounts or sessions, charges cards, refunds money, registers webhooks or follows provider-supplied URLs. The selected sandbox is `acct_1UHOd4QkpV0WCm81`.

## Run the check

Build `TideCasa.Stripe.Checks` first. From the recovered source directory, run:

```powershell
./scripts/verify-stripe-sandbox.ps1
```

This examines application configuration only. To also authenticate the configured sandbox key and check Accounts v2 read access:

```powershell
./scripts/verify-stripe-sandbox.ps1 -Probe
```

The probe sends at most two distinct GET requests, with the existing bounded GET retry policy: `/v1/account` followed by `/v2/core/accounts?limit=1`. It stops if the key's account does not match the selected sandbox. It refuses live keys, other platform IDs and local-provider overrides. Missing server configuration prevents all network access.

Exit 2 means prerequisites are missing or a read could not be verified; exit 0 confirms only the reported configuration/read checks. It never reports the payment flow as verified. The report contains no key values, signing secrets, customer data or raw provider errors. Its default location is `output/stripe-api/sandbox-preflight.json`; `-ReportPath` selects another file.

## Configuration sources

The check follows the application's relevant settings order: API `appsettings.json`, the selected environment's appsettings file, the API's existing user secrets in Development, then environment variables. `DOTNET_ENVIRONMENT` takes precedence over `ASPNETCORE_ENVIRONMENT`; the default is Production. No command-line secret values are accepted. Use the existing secure configuration mechanism; do not commit keys or place them in this document.

Required merchant settings are:

| Setting | Requirement |
| --- | --- |
| `MerchantPayments:RestrictedKey` | An existing `rk_test_` restricted key belonging to the selected sandbox |
| `MerchantPayments:PlatformAccountId` | `acct_1UHOd4QkpV0WCm81` |
| `MerchantPayments:PublicBaseUrl` | Owner portal origin only; HTTPS, or explicitly local Development |
| `MerchantPayments:PublishableKey` | Matching sandbox browser key; ownership remains a real-component check |
| `MerchantPayments:ConnectWebhookSecret` | The connected-account snapshot endpoint's own signing secret |
| `MerchantPayments:AccountWebhookSecret` | The Accounts v2 thin-event endpoint's separate signing secret |
| `MerchantPayments:ApiBaseUrl` | Unset for actual Stripe checks |

Leave the existing feature gates off until their respective rehearsals pass. The diagnostic reports `OnboardingEnabled`, `CheckoutEnabled` plus `CardsOnlyVerified`, and `EmbeddedOnboardingEnabled` plus `EmbeddedOnboardingVerified`; it cannot enable them or prove earlier recorded verification flags are accurate. Secret presence alone does not prove write permissions or webhook delivery.

## September 24 findings

Read-only connector inventory confirmed the selected sandbox and found no open connected accounts. Both listed event destinations are platform software-billing snapshot destinations on `2026-08-26.dahlia`; one is enabled and one disabled. No Accounts v2 thin-event destination was present. They were not modified or repurposed. Old descriptions on billing destinations do not change BarTide's current software pricing.

No merchant settings are configured in the current API/Blazor appsettings files or current process environment. The fresh diagnostic report records the effective configuration, including Development user secrets when selected. The connector authorizes its own requests; it does not provide the application with a restricted key, a browser publishable key or endpoint signing secrets.

The connector advertises `2026-08-26.preview`. The app keeps the approved `2026-08-26.dahlia` pin. Read-only connector results therefore do not establish that the app's exact wire contract works with its own permissions. A real account creation, AccountSession, Checkout and webhook rehearsal is still required after configuration is available.

## Existing Stripe accounts

Stripe's current [Accounts v2 limitations](https://docs.stripe.com/connect/accounts-v2) require Accounts v1 for OAuth authentication. [Connect OAuth](https://docs.stripe.com/connect/oauth-reference) also requires an application client ID and registered callback, and restricts accounts controlled by another platform. None of those prerequisites is verified here. Existing-account attachment stays unavailable; the current Accounts v2 ownership checks remain intact.

The implementation record describes the required connection provenance, single-use state, account/mode/owner binding and deauthorization handling before introducing that flow. There is no OAuth endpoint or migration hidden behind the UI. Networked onboarding can reuse business information without attaching an existing account's payment history.

## Remaining real sandbox rehearsal

With the selected sandbox's application credentials and required endpoint configuration available, verify: account create/resume and separate capability states; hosted and embedded onboarding/renewal; table, pickup and delivery orders with tips; allowed cards and disallowed wallets; cancellation, declined cards and 3DS; duplicate and out-of-order notifications; uncertain-response/restart recovery; and refund status readback. Record evidence against the pinned version before enabling the corresponding verification gates. Deployment remains a separate authorized action.
