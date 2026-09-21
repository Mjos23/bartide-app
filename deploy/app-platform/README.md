# App Platform launch preparation

This folder contains a two-service template for the PostgreSQL-backed application. The local migration, encrypted key storage, restricted database roles and internal forwarding have been exercised successfully. Account setup, Linux builds and hosted verification remain required. The template itself does not deploy anything.

- [app.template.yaml](app.template.yaml): API and Web, one fixed 512 MiB container each, narrow public webhook route prefixes, runtime-secret placeholders and inactive external features.
- [Dockerfile.api](Dockerfile.api) and [Dockerfile.web](Dockerfile.web): separate .NET 10 multi-stage images using the existing SDK version, port 8080 and the non-root runtime user.

The $5 containers give a **$10/month compute baseline**. Each includes 50 GiB outbound allowance; excess transfer is $0.02/GiB and allowances pool at team level. Tax, existing Supabase/R2 usage, registry charges and any added resources are separate. No managed database, worker, dedicated IP, Caddy container or additional replica is selected. The $20 ceiling is not an automatic provider billing stop. [DigitalOcean pricing](https://docs.digitalocean.com/products/app-platform/details/pricing/).

## Current facts and remaining account checks

September 21 update: the actual DigitalOcean proposal validated the two-service configuration at $10/month, and app `bb80369f-0289-44b6-9f25-d84386e9babc` was created in the verified default project. Both Linux images built and started against Supabase. HTTP readiness probes failed; switching to TCP readiness made deployment `9b20c973-da4c-4924-bc26-177eb2ec07f5` active. Public `/health` returns 200, but normal requests are rejected by the ingress boundary and require diagnosis before launch. Optional `ReverseProxy__Diagnostics=true` logs rejection reasons and transport peers without credential, cookie, body or client-header values. It defaults off. TCP readiness is not proof of public HTTP correctness.

The provider requires prefix route matching, rejects `internal_ports` duplicating `http_port`, and allows `disable_edge_cache: true` only after a custom domain is added. This template reflects those observed constraints. Application authorization, webhook signatures and no-store responses remain in place; real edge path normalization and cache behavior require hosted checks. [Platform limits](https://docs.digitalocean.com/products/app-platform/details/limits/).

Supabase remote rehearsal, independent native backup restoration, restricted runtime roles, Production API startup and encrypted Web key recovery have passed. The `tide_casa` baseline is provisioned, while its 76 application tables remain empty pending final source refresh and migration. Protected evidence and credentials remain outside the source archive. The following preparation notes describe the original account inspection; use the launch checkpoint for current state.

Authenticated inspection confirmed Supabase project `eerdotgmssernhnhggqw` (BarTide), Free plan, AWS `us-west-2`, approximately 26 MB of 500 MB database space. The session pooler is `aws-0-us-west-2.pooler.supabase.com:5432`; observed backend connection use was 7/60. The database password, remote restricted runtime roles, remote schema provisioning and off-host recovery remain pending. Do not deploy the administrator account as a runtime role. The template proposes `sfo` to be geographically near the existing database, subject to actual App Platform availability and measured latency.

DigitalOcean is signed in and its primary payment method is verified. No app has been provisioned. The private GitHub repository is `Mjos23/bartide-app`; DigitalOcean access is prepared for that repository only and awaits confirmation. Cloudflare currently serves `tide.casa` and `bar.tide.casa` through existing Worker custom domains. This template intentionally has no `domains` block; it uses the generated App Platform hostname first. Do not remove the old Worker attachments or alter the current DNS as part of template preparation.

## Final-source requirement

The workspace is not a Git repository, and it contains unfinished demo changes. **Do not deploy the workspace or an earlier ZIP by inference.** The release owner must preserve unfinished work outside a clean, reviewed source export and prove the intended final routes/assets. Both Dockerfiles default `RELEASE_SOURCE_REVIEWED` to `false` and refuse to build until it is explicitly set to `true` for that export. This is a release-content check, not a request for another user approval.

The export needs the final API, Web, Contracts and Domain projects, `global.json`, relevant build-property files, the root `.dockerignore`, and this deployment folder. If migration introduces another project or shared file, its reference and build-context inclusion must also be reviewed. Exclude local databases, keys, credentials, runtime environment files, `.tools`, `bin`, `obj`, exports and unrelated legacy application trees. The existing `.dockerignore` protects Docker context; it does **not** protect a Git push. Review the actual Git file list separately and use an explicit source allowlist.

Choose one delivery route after account/source inspection:

1. **Git build:** place the reviewed export in an authorized private repository, retain its commit hash, and replace both `github.repo`/`github.branch` placeholders with that same reviewed source. Keep `deploy_on_push: false` initially. App Platform uses the root context and the two Dockerfiles; build-time `RELEASE_SOURCE_REVIEWED=true` enables the already-reviewed build. Repository creation/push and access grants are separate external actions, not performed here.
2. **Container build:** build the same export as two `linux/amd64` images using these Dockerfiles and the explicit review argument, test the images, then publish to an authorized supported registry. Replace each `github`, `source_dir` and `dockerfile_path` block with its `image` source, preferably pinned by digest; do not specify both `tag` and `digest`. App Platform supports DOCR, GHCR and Docker Hub. Inspect registry access and pricing before use. No build, download or push was performed here.

Example build commands for the release owner, **not executed**:

```text
docker build --platform linux/amd64 --build-arg RELEASE_SOURCE_REVIEWED=true -f deploy/app-platform/Dockerfile.api -t bartide-api:reviewed .
docker build --platform linux/amd64 --build-arg RELEASE_SOURCE_REVIEWED=true -f deploy/app-platform/Dockerfile.web -t bartide-web:reviewed .
```

A local ZIP is not an App Platform source by itself. The supported source/image step remains necessary. [App creation](https://docs.digitalocean.com/products/app-platform/how-to/create-apps/), [Dockerfile builds](https://docs.digitalocean.com/products/app-platform/reference/dockerfile/), [monorepo contexts](https://docs.digitalocean.com/products/app-platform/how-to/deploy-from-monorepo/), [container registries](https://docs.digitalocean.com/products/app-platform/how-to/deploy-from-container-images/).

## Runtime contract with the application owner

These settings are implemented in the current source and exercised locally. Their presence in YAML is not proof of hosted operation:

| Setting | API | Web | Requirement |
| --- | --- | --- | --- |
| `Storage__Provider=PostgreSql` | Yes | Yes | Durable PostgreSQL; never silently fall back to SQLite. |
| `ConnectionStrings__Application` | Runtime secret | Runtime secret | Existing Supabase IPv4 session pooler, verified TLS and explicit pool limits. Prefer component-specific least-privilege roles when supported by the implementation. |
| `Storage__PostgresSchema=tide_casa` | Yes | Yes | Isolated reviewed schema; do not install into or alter live application/auth schemas. |
| `DataProtection__Provider=PostgreSql` | No | Yes | Shared durable key repository. |
| `DataProtection__EncryptionKey` | No | Runtime secret | Stable base64 encoding of 32 random bytes; encrypts stored key XML. Preserve separately in protected recovery material. Never regenerate on restart. |
| `ReverseProxy__Provider=DigitalOceanAppPlatform` | Yes | Yes | Provider-specific ingress scheme/client-IP handling, with spoofing tests. |
| `ReverseProxy__InternalToken` | Runtime secret | Runtime secret | Same independent random token on both components; validates the internal Web-to-API hop. |
| `Api__BaseUrl=http://api:8080/` | No | Yes | Matches the current Production URL validator and API internal port. |

API pool maximum **5** and Web pool maximum **2** are initial planning values, not confirmed provider allowances. Put them in the respective secret connection strings with `Minimum Pool Size=0`. Budget at least two overlapping deployment generations (14 potential application connections), plus migration/administration and existing application usage; compare with the actual Supabase backend and pooler limits. Reduce limits if necessary and test bounded queuing/timeouts. Avoid constructing a connection string from a guessed host or username.

A shape for the private connection string is `Host=<verified session pooler>;Port=5432;Database=postgres;Username=<verified pooled role>;Password=<secret>;SSL Mode=VerifyFull;Root Certificate=/app/certs/supabase-prod-ca-2021.crt;Minimum Pool Size=0;Maximum Pool Size=<reviewed limit>;Timeout=15;Command Timeout=30;Application Name=<component>`. Both API and Web require the root-certificate setting. Verify the host's certificate chain and the final driver's accepted settings. Never use `Trust Server Certificate=true` to bypass a failed certificate check. Root handles schema selection and persistence implementation. [Supabase connection modes](https://supabase.com/docs/guides/database/connecting-to-postgres), [Npgsql connection parameters](https://www.npgsql.org/doc/connection-string-parameters.html).

Both Docker images include Supabase's public Root 2021 CA at that path. This is a public certificate, not a private key; it is trusted only by the configured database connection, with no operating-system trust change. The download URL is defined in [Supabase's dashboard source](https://github.com/supabase/supabase/blob/master/apps/studio/hooks/custom-content/custom-content.json); the production certificate is [published by Supabase](https://supabase-downloads.s3-ap-southeast-1.amazonaws.com/prod/ssl/prod-ca-2021.crt). The reviewed file SHA-256 is `700723581420dd1ac98fd7e9ac529f0ef210eadcaf87fc868a3ad7d114c2f3b7`. The source packager allows this exact certificate and verifies its hash. The local session-pooler test passed the certificate/hostname boundary and reached database authentication after default trust failed; the actual hosted Npgsql connection remains a deployment gate. [Supabase SSL verification](https://supabase.com/docs/guides/platform/ssl-enforcement).

App Platform has no persistent volumes and container disk is disposable. The Dockerfiles create only private scratch space under `/tmp/bartide`; neither creates a durable `/data` nor a key volume. PostgreSQL mode defaults media staging to temporary scratch space. R2 remains the intended private object store, but its object bytes do not replace durable PostgreSQL media metadata and authorization. [Platform storage limits](https://docs.digitalocean.com/products/app-platform/details/limits/).

The existing `bartide-production-private` R2 bucket was verified by metadata reads in account `51e1ec9e2e9afa464c3e7175662f18e3`; public access is disabled. Runtime credentials are pending. Configure lowercase `Media__Provider=r2`, `Media__R2__AccountId`, `Media__R2__Bucket`, and the bucket-scoped `AccessKeyId`/`SecretAccessKey` runtime secrets. Use Object Read & Write restricted to this bucket. The connector did not demonstrate token-management access. A configured client is not proof of remote access: exercise authenticated upload, retrieval, range reads, deletion, tenant denial and scratch cleanup before enabling uploads publicly.

All credentials are `type: SECRET`, `scope: RUN_TIME` placeholders. Add real values through the protected provider flow after account inspection; do not paste them into the tracked template, build arguments, logs, source ZIP or browser code. Restrict console/configuration access because runtime secrets are available to those administrators. `${APP_DOMAIN}` and `${APP_URL}` are provider bindings, not secrets. Confirm that the bound service billing origin has no trailing slash. [Environment variables and bindings](https://docs.digitalocean.com/products/app-platform/how-to/use-environment-variables/).

## Public ingress and private API

The API is configured for Web at `http://api:8080/`. Its HTTP port also serves internal traffic; it must not be duplicated in `internal_ports`. Public ingress matches the following narrow prefixes and preserves the full path. Application endpoint routing and signature validation determine which webhook requests are accepted:

| Public path | Source endpoint | Purpose |
| --- | --- | --- |
| `/api/v1/webhooks/stripe/service` | API ServiceBillingEndpoints | Platform service-billing Stripe notifications |
| `/api/stripe/connect/webhook` | API MerchantPaymentsEndpoints | Connected-account snapshot notifications |
| `/api/stripe/accounts/webhook` | API MerchantPaymentsEndpoints | Account thin notifications |

The Web service receives `/` and paths outside those prefixes. There is no catch-all `/api` route. Public `/api/v1/auth`, owner/API data paths and API health must therefore not reach the API component. This does not remove authorization or Stripe signature checks. A wrong-method webhook request may receive 405; an invalid signed POST must fail without recording a successful event. No cross-origin browser API access is assumed. Verify edge normalization, unmatched webhook suffixes, trailing slashes and encoded paths on the real platform. The component health check probes `/health` directly; its success alone does not prove database readiness.

When adding custom domains later, update `AllowedHosts` on both components to enumerate the generated hostname and verified custom hostnames. Retain `api` internally. Current source includes `api.tide.casa` in its defaults, but this plan does not create or expose that separate host. Public webhook URLs may use the chosen verified primary Web domain. [App spec ingress and ports](https://docs.digitalocean.com/products/app-platform/reference/app-spec/), [internal routing](https://docs.digitalocean.com/products/app-platform/how-to/manage-internal-routing/).

## HTTPS and original client identity

App Platform terminates public TLS and forwards HTTP to port 8080. DigitalOcean documents original client IP in **`do-connecting-ip`**; its `x-forwarded-for` identifies the ingress server. The provider-specific middleware is implemented in `SharedHosting/AppPlatformIngress.cs`; Web adds its private forwarding credential through `AppPlatformApiHandler`. Do not copy Compose's fixed proxy addresses or trust arbitrary inbound `X-Forwarded-*` values. The final trust contract must distinguish platform ingress from the Web-to-API hop, preserve the parsed client address there, and reject spoofed/duplicate/malformed values without giving clients control of the request scheme or rate-limit identity. Both components require the same independent runtime secret `ReverseProxy__InternalToken`, 43–200 non-whitespace characters, generated once using a cryptographic random source.

Prove HTTPS detection, secure-cookie/origin validation, two-client rate-limit separation, forged forwarding-header rejection and Web-to-API attribution. Observe what the platform overwrites when a caller supplies `do-connecting-ip`; documentation alone does not prove the trust boundary. Do not enable `ASPNETCORE_FORWARDEDHEADERS_ENABLED` or clear known-proxy restrictions as an unexplained shortcut. [DigitalOcean client-IP documentation](https://docs.digitalocean.com/support/where-can-i-find-the-client-ip-address-of-a-request-connecting-to-my-app/).

The specification disables edge caching for dynamic services. Confirm `/_blazor` negotiation and `wss://` upgrades, interactive navigation, idle/reconnect and redeploy behavior. Keep a single Web instance until any future circuit/session scaling strategy is proven. Durable Data Protection keys do not persist the current in-memory ticket store or server circuits; restart sign-outs still need explicit acceptance or a session-store change. [Edge settings](https://docs.digitalocean.com/products/app-platform/how-to/configure-edge-settings/), [Blazor hosting](https://learn.microsoft.com/en-us/aspnet/core/blazor/host-and-deploy/server/?view=aspnetcore-10.0).

## Preflight before the first app creation

1. The DigitalOcean primary payment method is verified. Complete the single-repository source authorization and inspect app/project/region availability and the actual total. Do not add a managed database, paid dedicated IP or extra service to get past setup.
2. Verify existing Supabase project, pooled TLS connection, restricted role, `tide_casa` isolation, pool headroom and restorable backup. Confirm any impact on the old live site. A Free plan has no automatic backups and may pause when inactive; storage below quota is not a recovery plan. [Supabase pricing](https://supabase.com/pricing).
3. Retain local migration evidence and finish the remaining feature suites. The synthetic importer passed 25 checks and a rehearsal of the real backup matched all 76 tables, 158 rows, 41 prospects and owner bindings. Restricted-role runtime, encrypted keys and ingress checks also passed locally. Revalidate the actual remote destination, preserve originals and compare imported identifiers, tenant ownership and ledger totals; no migration is performed by this folder.
4. Select the reviewed source, resolve every relevant `REPLACE_` value, and validate the resulting app spec through the actual platform's validation mechanism without creating an app first where possible. `RELEASE_SOURCE_REVIEWED` changes only after source selection. Keep immutable source/image identifiers and a recoverable prior version.
5. Exercise both final Linux images under **512 MiB per-container limits**. Record cold-start and migration memory, steady idle usage, native image-processing peaks, representative Web circuits, database pool usage, tail latency, reconnects and restarts. Verify writable scratch paths and disk cleanup. No safe user count has been measured. If memory is insufficient, propose the measured minimum before changing the selected plan; upgrading one component to 1 GiB makes compute $15, both $20 before extras.

## Staged provider and domain sequence

Create the application only after the account/source/storage checks above, then verify the generated HTTPS hostname first. Keep authentication, R2, mail, push and payment-creation switches off until their own credentials and required behavior are verified. The template allows signed webhook plumbing to be configured while checkout remains off.

Service billing supports sandbox/live modes behind explicit flags. The current merchant implementation accepts test keys and fixes its environment to `test`; **merchant live checkout cannot be enabled by filling a live key into this template**. Root must finish the live-payment adaptation before any merchant live launch. Independently verify the selected Stripe account, cards-only payment configuration, destination mode, endpoint signing secrets, notification version, replay handling and reconciliation. Do not reuse the old site's signing secret as an assumption.

The current Worker also has a D1 application database; the preview SQLite database is not the complete migration source. A protected native D1 export and preview backup were reconciled in a separate local candidate. Its PostgreSQL rehearsal passed for all 76 tables, 215 rows and 44 leads, with exact counts/value digests, independent ownership checks, source preservation and cleanup. It retains unique rows from both sources and explicit account bindings; it does not infer ownership of legacy customers. D1 migration history remains separate source provenance rather than invented rows in the new application's ledger. Private backups and reconciliation evidence stay outside this repository.

After real-host auth/cookie/tenant/upload/restore/Stripe tests pass, establish a brief controlled write cutover, refresh both source snapshots and compare records again before the final import. Rehearsal snapshots are not a guarantee that the old live site has received no later changes. Review the exact Cloudflare Worker custom-domain and DNS cutover with the old state recorded. Update public origins, allowed hosts, Supabase redirect settings and Stripe destinations for the final domain as one reviewed cutover. Verify both hostnames over HTTPS, retain rollback instructions and avoid two application writers. Traffic rollback must also preserve any records created in PostgreSQL after launch. No domain, Stripe destination or provider resource was changed by this preparation task.

## Verification boundary

Prepared locally: the app template, two Dockerfiles and this runbook. Thirty-one local text-based contract checks passed for service count/size, routes, ports, secret scope, source guards, configuration names, Docker user/SDK and local links. Source route names and URL validation were checked against current code; the coordinating task confirmed the new persistence/proxy configuration names. A full YAML parse was not run because the bundled Python lacks a YAML parser; no dependency was installed. Official App Platform schema/pricing/storage/network documentation was reviewed, but platform schema validation remains required. This is not an account-validated spec or a successful image build. No build, app process, registry push, provider mutation, DNS change or purchase was performed in this task.
