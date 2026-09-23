# Delivery phase two and menu customization

Scope: Gulf Lantern at demo.tide.casa. The main Tide Casa deployment and hosting sizes remain unchanged. Later native driver-app and provider work is paused.

## Ordering

Photos open full descriptions, ingredients, quantity and Add/Update cart controls. Customers can leave off listed ingredients, add an item request and add an order-wide request. Choices apply to every serving of the same item in a cart, as the dialog explains; separate variants are not separate lines in this release. Prices remain unchanged. Staff see the choices in their order views. All 20 fictional ingredient lists are editable through Menu and settings.

Events replaces What's on; Popular items replaces For the table. Category links jump to appetizers, food, drinks and alcohol-free items. Built by BarTide links to bar.tide.casa. The fixed cart and Add to home screen remain.

## Location sharing

Managers enable the optional feature from the Delivery desk. An assigned driver acknowledges and collects the order, then explicitly starts sharing. Customers see only their delivery through their private receipt. Managers see a batch of their restaurant's locations.

The public shared demo uses server-generated fictional St Pete Beach positions. It never requests device GPS; both API validation and the demo database policy restrict it to simulated data. Real foreground GPS code is available for private restaurant deployments, but is not enabled on production tide.casa in this release. Consenting private drivers must complete a physical-phone pilot before customer rollout.

Real mode requests browser location permission only after the driver's action. Hiding the screen, switching to navigation or locking the phone stops sharing and requires another start. Pending requests cannot restart it after a hide/show cycle. There is no background service, ETA promise or offline replay.

Views show update age, accuracy and a map. Points are out of date at 60 seconds and hidden after five minutes. Only one latest point per driver/restaurant is stored, separately from order history. Uploads are limited to one per 15 seconds (the UI uses 16 seconds); boards and customer locations refresh about every 30 seconds while visible. GPS and status use separate rate-limit buckets from new orders. The 23 orders/minute/location admission cap is unchanged.

Active membership, assignment and session are checked on reads and writes. One customer leg per driver in a restaurant can be shared. End delivery, cancel, reassign, deactivate staff or disable delivery/location sharing removes the session. Old sequence numbers, stale/future captures and invalid coordinates are rejected. An offline stop might not arrive; age labels and expiry still apply. The cleanup runs each minute: expired storage disappears within five minutes plus one cleanup interval while the service runs. No route history is retained.

## Maps

Visible OpenStreetMap tiles use browser caching and attribution, without prefetch or offline download. Exact markers stay in the app; coarse tile requests go to OpenStreetMap. Opening the larger-map link intentionally sends that location to OpenStreetMap. The tile URL is centralized in delivery-location.js for later replacement. Public tiles have no SLA; order workflows continue if tiles fail. No paid service was added.

References: https://www.w3.org/TR/geolocation/#request-a-position and https://operations.osmfoundation.org/policies/tiles/ .

## Storage and rollback

DeliveryLocationSchema.cs defines a separate additive table; the immutable baseline is unchanged. Local development creates it. Hosted deployments require the schema owner to provision it before releasing the API. The API cannot create tables or expand privileges. The public demo additionally has row-level security restricted to tenant gulf-lantern and simulated=1, with only its existing isolated API role granted table access. Web, anon and authenticated roles have no access.

Before a private deployment, review and provision the equivalent table with a private API role policy; the shared-demo-only policy must not be copied unchanged. Enable it only for the consenting pilot restaurant.

For rollback, disable location sharing first to remove active points, then deploy the preceding reviewed source. Older code tolerates the additional table. Optional menu fields remain compatible with earlier records.

## Verification and limits

Real API/storage tests cover modifications, quote fingerprints, idempotency, permissions, freshness, replay, stop/expiry, one active leg and tenant isolation on SQLite and PostgreSQL. Synthetic-mode checks reject device coordinates. Node tests with synthetic DOM/GPS cover pending-start hide/show, permission denial, expired authentication and simulation-mode mismatch. Browser checks at 390px exercise customization, fixed checkout, customer maps and driver simulation.

Physical iOS/Android GPS, lock-screen and poor-network field trials remain for consenting private pilot accounts; they are not claimed as verified. Phases three and four remain paused.
