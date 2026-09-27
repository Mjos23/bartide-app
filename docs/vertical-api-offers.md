# Public API offers — September 27, 2026

BarTide, Law, Accounting, Tee Times, Fit and Driver advertise a $150 one-time API/front-end build and $199/month maintenance, both due initially ($349). Optional store-submission support is $300 once ($649 initially). Eligible software offers use the existing Tide Casa business Stripe service checkout. No new customer charge, subscription, price object, webhook, or tenant access was created by this release.

Law, Accounting and Golf retain their independent Sites projects, databases, storage and protected portals. Their public pages link to the central service checkout; paying for a build does not grant access to private legal, accounting or clubhouse records.

Collect, Fit and Driver public documents are embedded in the existing .NET web service. `VerticalMarketing` serves only the exact public Fit/Driver root hosts (plus development-only `/fitness` and `/delivery` aliases). All private driver, customer and business routes retain existing authorization. `order.tide.casa` remains the customer portal. No new always-on service or compute size is required.

Collect shows **planned pricing only**. Its checkout, real financial intake, creditor activation and live collection payments remain closed pending payment-provider approval and service readiness. Fictional preview entrances retain CSRF and session checks. The replacement public page intentionally uses the common API-offer structure in place of the former competitor fee calculator; the private preview workflows remain intact.

Fit is a scoped software-build offer. Its separate browser-local member prototype is not represented as a finished membership API. Authentication, private member data, paid entitlements and provider integrations need an agreed implementation scope. The public illustration is a concept.

Driver describes existing dispatch and accepted-hire workflows. Software billing is separate from driver compensation. No driver-payment readiness flag or financial workflow was activated. Actual payouts continue to depend on their separate configuration and verification.

Public stack explanations link directly to https://learn.tide.casa/csharp/ and https://learn.tide.casa/javascript/. Courses are free to access without login. The account, authorization and certification records of business portals are unrelated to course access.

The public HTML files in `TideCasa.Blazor/MarketingPages` are release source, not runtime React applications. They share `wwwroot/vertical-api.css` and a small pricing-selector script. C# serves the documents; all payments use the existing server-authoritative checkout.
