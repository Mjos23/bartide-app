# Gulf Lantern on a phone

Customer menu: https://demo.tide.casa (opens the working ordering page).
People and perspectives: https://demo.tide.casa/sample-bar.

On phones, adding an item reveals a fixed cart bar with the current quantity and
total. **View cart & checkout** jumps directly to checkout; **Continue browsing**
returns to the menu without clearing selections. Desktop checkout stays beside
the menu while browsing. The perspective hub's menu photos link to their matching
orderable items. Existing QR links still open ordering directly.

Gulf Lantern is a fictional, shared practice bar. Pick a person to use the actual
customer, owner, general-manager, bartender, server, kitchen or driver screens.
Use **See additional views** at the bottom of the page to return to the selector. Sessions
last 30 minutes; choose a person again when a session expires. Two devices can
show customer ordering and the staff order board together. Refresh the board to
see activity from the other device.

On iPhone, open the address in Safari, tap Share, then Add to Home Screen. On
Android, open it in Chrome and use Install app or Add to home screen in the menu.
The shortcut opens the restaurant's ordering page. An internet connection is required.

## A short presentation

1. Scan the customer QR. Show the restaurant introduction, upcoming event and
   menu. Choose a dish and quantity, select Dine in, enter a sample table number,
   review the total and place a fictional order with Pay staff. No customer login
   is needed to order. Use fictional contact details.
2. Switch to Maya's server view. Open restaurant operations and accept the order.
3. Switch to Nico or Luis to demonstrate preparation and ready status. Return to
   Maya to record a fictional staff payment and complete the ready order.
4. Show Jordan's general-manager tools for shifts, messages, training, events and
   menu management. Casey is the business owner; no sample person is a Tide Casa
   platform administrator.
5. Return to Avery for rewards and event reservations.

Changes are shared with other visitors. Orders are practice records; no payment,
email, text or push notification is sent. The 18 photos and training video are
fixed in the public demo. Media upload/delete workflows remain available in
configured real business workspaces and in the private local test environment.
The demo has no live customer records or live payment/provider credentials.

## Hosting and isolation

The proposed separate DigitalOcean App Platform app uses two $5/month components
(API and Web), adding $10/month to the existing $10/month app hosting. Both use
Production configuration. Only Web is publicly routed; API listens on the app's
internal network. Source is shared with the real app, while demo mode defaults
off and validates its dedicated configuration before serving requests.

The existing PostgreSQL project contains a separate `tide_demo_gulf_lantern`
schema, an API login restricted to its application tables, and a separate Web
login restricted to its encrypted cookie-key storage. Both are denied access to
the live application and authentication schemas. A deployment restart preserves
sample records. Demo tokens are random, short lived and explicitly rejected by
normal app authentication. Demo routes exclude payments, enrollment, provider
webhooks, sending, sales-pipeline and platform-owner operations.

The private reviewed seed database and checksum are under `.tools/public-demo`.
They never enter the source ZIP or GitHub. Bundled media is under
`TideCasa.Api/DemoMedia`; the public fictional-person fixture is
`fixtures/gulf-lantern.json`. There is no public reset endpoint. An operator reset
requires a scoped backup and an explicit restore of only the demo schema.

## New ordering limit

Each location accepts at most **23 new orders per UTC clock minute**, shared
across all phones/IP addresses. The allowance resets at the next clock minute;
it is not a rolling 60-second window. Successful idempotent retries do not use
another slot. Failed order transactions roll the increment back. Each other
location retains its own allowance. The general request and phone-payment
guards are separate and remain enabled.

`scripts/verify-location-order-limit.py` checks SQLite and, with
`--provider postgres`, an isolated PostgreSQL fixture. Historical Friday and
multi-bar reports describe the former 10/IP-minute rule; they are preserved as
historical evidence, not current limit verification.

`scripts/verify-public-demo-auth.py` checks the isolated public-demo security
boundary. `scripts/verify-hosted-demo.py --base https://demo.tide.casa` checks
native forms, every fictional identity, the install manifest and the public hub.
See the private deployment evidence for the actual deployed source revision and
verification results; this guide alone is not proof of deployment.


## Restaurant-first QR presentation

The existing QR destination and demo root open /order/gulf-lantern. The customer sees Gulf Lantern branding, a compact restaurant introduction and patio illustration, the next published event, then orderable menu items. The event is optional, uses a two-second request budget and passes public prerendered state into the interactive view to avoid a second event fetch. Ended or cancelled events are not featured. The phone cart and table QR parameters retain their existing behavior. Add to home screen appears at the top so customers can keep the restaurant app on their phone. Other perspectives remain below the ordering experience. The home-screen manifest also starts at /order/gulf-lantern. Engineering Studio is parked separately and unavailable on public hosting.
