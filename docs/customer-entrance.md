# Customer entrance

The customer home is `https://order.tide.casa/`. `/customer` provides the same entrance on the existing hosts. Nearby restaurants remain at `/restaurants`, ranked only by distance. Owners must enable their listing and set a valid location to appear.

Customer account pages are `/customer/signin`, `/customer/signup`, `/customer/verify-email`, `/customer/forgot-password`, `/customer/reset-password`, and `/customer/account`. The generic auth routes also use customer branding on the customer host or when returning to a restaurant/customer route. Business and driver return paths remain supported.

There is one existing Supabase identity and stable BarTide user ID across all restaurants. Signup uses the existing email-code verification and creates no authenticated session before verification. Customers who joined through a restaurant use the same email/password centrally. Cookies remain secure, HttpOnly and host-only, so a different host can require another sign-in with the same credentials. No shared-domain cookie or token handoff is introduced.

Restaurant menu and published restaurant app signup links return to the originating restaurant. Valid 64-character lowercase hexadecimal table tokens survive authentication. Other order query parameters, duplicated table tokens and external redirect targets are discarded. Joining a restaurant's rewards is a separate existing action; eligibility and automatic paid-purchase rules are unchanged.

Deployment adds `order.tide.casa` as an alias on the existing App Platform app and to runtime `AllowedHosts`. Its DNS-only CNAME points to the existing app ingress. Existing domains, service sizes, private API routes, data and identity settings are retained. No migration or new paid service is required.

Verification: build the Blazor project; run `scripts/verify-auth.py` (which includes `customer_entry_checks.py`) and `scripts/verify-restaurant-discovery.py`. These use isolated synthetic identity/API/database fixtures. Check mobile and desktop layouts, public HTTPS, canonical URLs, customer account redirects, auth form context and unchanged business/driver entry pages. Production verification must not create real users, orders or payments.
