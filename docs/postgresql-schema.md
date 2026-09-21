# PostgreSQL baseline and SQLite compatibility

The PostgreSQL baseline creates the existing 76 application tables, plus `tide_data_protection_keys` for encrypted Web key-ring storage. `PostgresSchemaMigrator` creates its separate `tide_postgres_migrations` ledger. It imports no application records and never manufactures SQLite migration history.

The schema inventory is in `TideCasa.Api/PostgresMigrations/manifest.json`. It records all 664 columns, 62 CHECK constraints, 87 foreign keys, 37 non-primary UNIQUE constraints/indexes, and 71 named indexes. The existing fourteen legacy SQL files are verified against their immutable manifest; nine feature SQL files and the three runtime-created tables are also included. The generator folds SQLite ALTER statements in an in-memory database and reads only schema metadata. It does not open the preview database.

## Installation and history

`ApplicationDatabase` must open an already provisioned, isolated application schema, normally `tide_casa`. The migrator requires that schema to be the sole explicit `search_path` entry. It never creates, modifies, or grants access to `public`, `auth`, `storage`, or provider-owned schemas.

Installation uses one ReadCommitted transaction. Before reading history or schema state, it acquires the same transaction advisory lock as application writers:

```sql
SELECT pg_advisory_xact_lock(hashtext(current_database()),hashtext(current_schema()));
```

The baseline SQL, schema validation, and ledger insert commit together. A failure rolls the entire installation back. An untracked nonempty schema is rejected; table names alone are never accepted as proof of migration history. Repeated initialization validates the existing version, resource name, script SHA-256, manifest SHA-256, and schema fingerprint without applying the script again.

Only the initial baseline DDL command has a bounded 120-second command timeout. This follows an importer fixture failure at the previous 20-second deadline before any data rows were copied. Ordinary metadata/application queries keep their 20-second command deadline, and advisory/database lock acquisition keeps the 10-second lock timeout. This execution bound does not alter the immutable SQL or manifest.

The fingerprint contains catalog definitions, never application values: tables, columns and defaults, constraints, indexes, noninternal triggers, and the two parsing helpers. Missing tables/columns, invalid indexes, changed definitions, unexpected triggers, or checksum/history drift prevent startup. Separate importer bookkeeping tables are outside this baseline fingerprint; they do not relax checks on application tables.

This is one atomic version-1 baseline. Subsequent schema changes require a new reviewed migration and corresponding migrator support; do not regenerate version 1 after its first deployment. PostgreSQL may render catalog definitions differently after a major-version upgrade. Rehearse the upgrade and review any fingerprint mismatch rather than rewriting the ledger to suppress it. The ledger is a consistency check, not a signature against someone with database-owner access.

## Data representation

| SQLite representation | PostgreSQL representation | Compatibility decision |
| --- | --- | --- |
| TEXT IDs, timestamps and JSON | TEXT with `C` collation | Import exact strings; no timezone normalization or JSON reserialization. |
| INTEGER flags, money, counts and versions | BIGINT | Preserve signed 64-bit values. Flags retain existing integer semantics and CHECK constraints. |
| Text primary keys | TEXT NOT NULL PRIMARY KEY | PostgreSQL enforces non-null primary keys. Preflight must reject any permissive legacy SQLite NULL key. |
| UNIQUE/partial indexes | UNIQUE constraints or corresponding indexes | Preserve indexed columns, order, and predicates. |
| Foreign keys | Same columns and actions; DEFERRABLE INITIALLY IMMEDIATE | Ordinary application statements check immediately. An atomic importer may explicitly defer references until commit. |
| Referral email/code `NOCASE` uniqueness | ASCII-folding expression indexes | Preserve SQLite's ASCII case-insensitive behavior without introducing Unicode case folding. |

The referral expression is `translate(column, 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz') COLLATE "C"`. Equality lookups must use the same expression on both sides when they require SQLite NOCASE semantics. The expression index does not change ordinary text equality. The two affected columns are `tide_referral_profiles.email` and `tide_referral_profiles.code`.

There are **no AUTOINCREMENT tables** in these 23 source scripts. Only the two legacy history `version` columns use SQLite INTEGER PRIMARY KEY rowid aliases; application migration writes supply their values explicitly. They become BIGINT primary keys with no implicit sequence or identity. The import must preserve those explicit versions, exclude SQLite internal tables/rowids, and must not invent sequence values. No sequence reset is required for this baseline.

`tide_schema_migrations` and `tide_feature_migrations` are structurally present but empty after a new PostgreSQL installation. A reviewed import copies their actual SQLite records and checksums as provenance. They are not PostgreSQL execution ledgers. `tide_postgres_migrations` records only the new PostgreSQL baseline and must never be replaced with SQLite history.

## Safe query parsing

Stored strings remain unchanged. Query ports use two schema-local, invoker-rights helpers with `search_path=pg_catalog`:

- `tide_iso_instant(text)` accepts ISO `YYYY-MM-DD` dates as UTC midnight, or a full ISO timestamp with seconds, optional one-to-seven fractional digits, and an explicit `Z` or `±HH:MM` offset (up to 14:00). A `T`, `t`, or space can separate date and time. Invalid calendar dates and non-ISO input return SQL NULL. Timezone-less full timestamps, leap-second syntax, `24:00`, and PostgreSQL special words such as `now`, `tomorrow`, and `infinity` are intentionally rejected. Existing API event/shift writers use `DateTimeOffset.ToString("O")`, which this grammar accepts. PostgreSQL comparisons have microsecond precision; the original seven-digit text remains intact. The helper is conservatively STABLE because timestamp casts can depend on settings.
- `tide_json(text)` returns JSONB for valid JSON, and SQL NULL for malformed/unsupported input or out-of-range JSON numbers. SQL NULL returns SQL NULL; the valid JSON literal `null` remains JSONB null. It is IMMUTABLE. Callers explicitly distinguish JSON booleans from numbers when preserving existing enabled/disabled rules.

Both helpers catch only PostgreSQL data exceptions; cancellation, resource failures, and unrelated server errors remain visible. Neither changes stored data or uses dynamic SQL. See PostgreSQL's [function volatility guidance](https://www.postgresql.org/docs/current/xfunc-volatility.html), [error categories](https://www.postgresql.org/docs/current/errcodes-appendix.html), and [PL/pgSQL exception behavior](https://www.postgresql.org/docs/current/plpgsql-control-structures.html#PLPGSQL-ERROR-TRAPPING).

## Import and verification boundaries

Before any live import, separately verify SQLite integrity/foreign keys, expected schema and histories, PostgreSQL-representable cell types, non-null primary keys, text encoding and absence of embedded NUL characters. PostgreSQL rejects text containing NUL; do not silently trim or replace it. Reject invalid source records and report affected table/column metadata without dumping private values. This baseline does not perform that preflight or import.

Use the manifest's dependency `importOrder`, or an atomic transaction with `SET CONSTRAINTS ALL DEFERRED` and a forced constraint check before commit. Preserve IDs, nullable values, text, integer ranges, and all source history rows. Compare per-table counts and value digests after copying. Keep the source read-only and retain rollback capability; deploying the baseline does not authorize discarding SQLite or enabling providers.

Fresh schema-only checks reproduced the SQL and manifest byte-for-byte from immutable source DDL, verified 77 tables and 87 deferrable/immediate FKs, checked the dependency order, and confirmed no data INSERTs or schema provisioning statements in the baseline.

`python scripts/verify-postgres-schema.py` then passed **56 checks, zero failures** against the isolated loopback PostgreSQL 17.11 fixture. [Retained results](../.tools/postgres-schema-verification/20260920-221613-822421/results.json) record the exact API and baseline hashes. The run used one filtered, immutable API build copy and `DOTNET_PROCESSOR_COUNT=1`; all ten owned API processes exited, all four owned schemas were removed, and the runtime copy was removed. No live provider database was used.

That run exercised actual API blank installation and rerun; exact synthetic row/history preservation including signed BIGINT extrema; ISO/JSON helper edge cases; ASCII-only uniqueness; immediate and explicitly deferred foreign keys; startup refusal after history, default, index, function, and missing-table changes; preservation of an untracked schema; rollback of all 77 tables and both helpers when the final ledger write was deliberately rejected; and `pg_locks` evidence that startup waited on the exact application-writer advisory key before installing.

Two earlier harness runs are retained separately: one selected local media without its required opt-in (corrected to disabled), and one timed out during host/fixture contention. The latter's only retained owned schema was subsequently removed and that cleanup recorded. The successful run used reduced CPU demand and a longer readiness budget. These are functional/recovery checks, not a latency, container-memory, or concurrency-capacity measurement. Full SQLite-to-PostgreSQL import fidelity, production/Supabase permissions and TLS, and broader application behavior remain separate coordinating-task checks.
