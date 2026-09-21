# Reviewed SQLite to PostgreSQL transfer

This tool copies the 76 SQLite application tables, including both original migration ledgers, into an isolated PostgreSQL schema. It references the API's embedded PostgreSQL manifest. The new PostgreSQL ledger and encrypted Data Protection key table are not imported or replaced.

Use an absolute path to a consistent SQLite backup, with no `-wal`, `-shm` or `-journal` files. Stop application writers while taking the backup using SQLite's backup API. Do not use an ordinary file copy of a running WAL database. Keep the backup for rollback; this tool does not change it.

Create the intended `tide_*` schema separately and use a private JSON settings file outside source control:

```json
{
  "Storage": {
    "Provider": "PostgreSql",
    "PostgresSchema": "tide_casa"
  },
  "ConnectionStrings": {
    "Application": "<private PostgreSQL connection string with SSL Mode=VerifyFull>"
  }
}
```

The tool forces schema creation off, ignores other application settings sources, and requires verified TLS for ordinary runs. Never put credentials directly in command arguments or copied output.

```powershell
dotnet TideCasa.MigrationTool.dll --source C:\private\consistent-backup.db --settings C:\private\postgres-import.json
dotnet TideCasa.MigrationTool.dll --source C:\private\consistent-backup.db --settings C:\private\postgres-import.json --apply
```

The first command is read-only. It validates SQLite integrity, foreign keys, all expected table/column mappings and compatible values, then reads PostgreSQL metadata and application row counts. An empty preprovisioned schema reports `dry_run_validated_baseline_required`; no baseline is installed. An initialized empty target reports `dry_run_validated_empty_destination`. Neither result is a hosted deployment or production readiness claim.

`--apply` initializes or verifies the API's immutable PostgreSQL baseline, then takes the same writer advisory lock as the application and exclusive locks on all imported tables. Any populated application table rejects the transfer. It defers foreign keys, inserts typed and parameterized values, forces constraint validation, and compares all 76 row counts and canonical table digests before commit. Failed transfers roll back every imported row; the separately initialized, empty baseline may remain. A successful rerun is deliberately rejected rather than merged or overwritten.

TEXT values retain IDs, ISO timestamp text, JSON, whitespace, empty strings and Unicode exactly. The tool strictly decodes the original SQLite TEXT bytes using the database's UTF-8 or UTF-16 encoding, rejecting damaged bytes before the normal string reader can silently replace them. BIGINT values retain the complete signed 64-bit range. Unexpected SQLite value types, invalid Unicode, NUL text and NULL values incompatible with PostgreSQL required columns fail before copying. Digests encode field types and byte lengths and sort SHA-256 row digests, preserving duplicates without depending on row order or collation. Memory for hashing is proportional to one table's row count (32 digest bytes per row plus collection overhead).

Output is sanitized JSON containing only status, error codes, table names, row counts and digests. Raw database error messages, contact records, file paths and credentials are suppressed. Exit code is zero for a valid dry-run or verified commit and one for failure. A competing writer that holds a lock past the configured timeout produces `destination_busy` (PostgreSQL code `55P03`); stop the competing work and inspect the target before retrying. On a lost connection during commit, the server's commit outcome may be uncertain; inspect the destination privately before retrying. Never assume a failed client response proves the destination is empty.

For the existing local PostgreSQL fixture only, `--local-test` permits non-TLS configuration when the connection host is exactly `127.0.0.1` or `::1`. It still requires a preprovisioned isolated schema. The synthetic verifier runs the built CLI, does not build it or access providers, and creates/drops its own uniquely named local schemas and role:

```powershell
python scripts/verify-postgres-import.py --tool C:\absolute\TideCasa.MigrationTool.dll
```

The verifier uses only invented contact records. It exercises 41 leads, signed integer bounds, money, NULL/empty values, Unicode and original timestamp text, migration history, exact independent row comparison across all 76 tables, source rejection, no-write dry-runs, populated reruns, rollback after a write permission failure, simultaneous imports, and unchanged source bytes. This does not substitute for an approved consistent backup and read-only preflight of the actual production source and target.
