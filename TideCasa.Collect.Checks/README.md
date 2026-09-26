# Collect core and persistence checks

This independent console suite links the exact production Collect model and repository source files. It does not depend on the unfinished presentation layer. Checks run in batch order 001–008 and report individual failures before returning a nonzero exit code if any check fails.

Coverage includes tenant, role and debtor boundaries; invitation binding and replay; bounded atomic import; profile/evidence comparisons; review status; immutable offers and exact-cent agreements; revision concurrency; payment simulation and event ordering; explicit inquiry sharing snapshots; encrypted restart; and a labelled synthetic comparison benchmark.

Run from the source root using the installed .NET 10.0.401 SDK:

    dotnet run --project TideCasa.Collect.Checks/TideCasa.Collect.Checks.csproj --nologo

Use the existing private NuGet cache and CLI home when invoking the bundled SDK. No new packages beyond the existing Npgsql 10.0.3 dependency are introduced.

By default each run uses an isolated encrypted local directory under this project's .evidence directory, with a fixed business clock. It writes summary.json and synthetic-benchmark.json. Evidence contains only fictional test data. Do not commit generated bin, obj or .evidence directories.

Optional PostgreSQL execution reads the CollectCheckPostgres environment variable without printing it. It must point to a disposable loopback database whose name starts with collect_check_. Provision a fresh uniquely named database before running; the test deliberately refuses a remote host or ordinary application/test database. The repository owns its tide_collect schema inside that database. The suite does not provision or delete the database. The PostgreSQL branch checks actual encrypted ciphertext, revisions, expiry, restart and concurrency using the production repository.

Boundaries: no HTTP or browser coverage, document scanning, real customer onboarding, provider callback integration, operating compliance, backup of production data, or production scaling is claimed. The benchmark measures deterministic in-memory comparison only.

