# BarTide application source

> Current rights: all rights reserved by MJ Physics Engineering / Michael Bangel. No new license is granted by this revision. Prior grants and third-party notices remain effective; see the root LICENSE and LICENSING.md.


ASP.NET Core API and Blazor Web, with PostgreSQL durable storage for DigitalOcean App Platform.
This is a source candidate, not a claim of a completed production launch.

Build with the .NET SDK version in global.json. App Platform Dockerfiles are under deploy/app-platform.
The deployment template contains placeholders and keeps payment creation disabled until provider verification.
Configure private runtime settings in the hosting provider; never commit credentials or databases.

The migration tool defaults to read-only planning and requires explicit --apply for transactional transfer.
Preserve a consistent source backup and verify the imported values before changing production traffic.
See docs/postgresql-schema.md and deploy/app-platform/README.md for persistence/deployment details.

The canceled new restaurant simulations, local data, prospect records, private keys and credentials are excluded.
Existing application demonstrations and public assets are retained.
