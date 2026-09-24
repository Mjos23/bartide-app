# Stripe browser loader

`pure.esm.js` is the unmodified `package/dist/pure.esm.js` from the published `@stripe/connect-js` 3.4.6 package. It is the browser component loader only. The C# API uses no Stripe SDK.

Source: https://registry.npmjs.org/@stripe/connect-js/-/connect-js-3.4.6.tgz

Package integrity (verified before extracting the single file): `sha512-fuC/U/ONDBMKwTBxKAnEBQH+m0x+Tze62/bV1ZDLT7oRnsJgddEnRi7FzdwIxz9pmFZhOJpSaikLIBw9g/1o1w==`

The MIT license is retained in `LICENSE.txt`, from https://github.com/stripe/connect-js/blob/master/LICENSE. Upstream: https://github.com/stripe/connect-js . Retrieved September 24, 2026.

This module loads the current Stripe-hosted secure components from `https://connect-js.stripe.com/v1.0/connect.js`. It is used only after the owner explicitly opens embedded setup. Stripe hosts the sensitive forms; no card, bank or identity fields are implemented in BarTide.
