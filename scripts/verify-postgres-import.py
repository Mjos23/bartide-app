"""Exercise the built migration CLI against isolated schemas on the local PG fixture.

No builds, provider calls, application database reads, or real contact records.
Passwords stay in private fixture/settings files and child-process environments.
Run with --tool <absolute path to TideCasa.MigrationTool.dll>.
"""
import argparse
from contextlib import closing
import hashlib
import json
import os
from pathlib import Path
import re
import runpy
import secrets
import shutil
import sqlite3
import subprocess
import sys
import traceback
import uuid

ROOT = Path(__file__).resolve().parents[1]
API = ROOT / "TideCasa.Api"
NOW = "2026-09-20T19:08:07.1234567-04:00"
MARKER = "SYNTHETIC-CONTACT-MUST-NOT-APPEAR"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--tool", required=True, type=Path)
    parser.add_argument("--fixture", type=Path, default=ROOT / ".tools/postgresql-17-test/fixture.json")
    parser.add_argument("--dotnet", default="dotnet")
    args = parser.parse_args()
    tool = args.tool.resolve(strict=True)
    fixture = json.loads(args.fixture.read_text(encoding="utf-8-sig"))
    assert fixture["host"] in ("127.0.0.1", "::1"), "Only the local PostgreSQL fixture is allowed"
    psql = ROOT / ".tools/postgresql-17-test/pgsql/bin/psql.exe"
    run_id = uuid.uuid4().hex[:12]
    output = ROOT / ".tools/postgres-import-verification" / run_id
    output.mkdir(parents=True)
    checks = []
    schemas = []
    roles = []
    env = dict(os.environ, PGPASSWORD=fixture["password"], PGSSLMODE="disable", PGCLIENTENCODING="UTF8")

    def sql(statement, *, tuples=True):
        command = [str(psql), "-X", "-v", "ON_ERROR_STOP=1", "-h", fixture["host"], "-p", str(fixture["port"]),
                   "-U", fixture["user"], "-d", fixture["database"]]
        if tuples:
            command += ["-A", "-t"]
        result = subprocess.run(command, input=statement, env=env, text=True, encoding="utf-8", capture_output=True, timeout=60)
        if result.returncode:
            raise RuntimeError("Local fixture SQL failed; provider details suppressed")
        return result.stdout.strip()

    def identifier(value):
        assert re.fullmatch(r"[a-z][a-z0-9_]{0,62}", value)
        return '"' + value + '"'

    def provision(label):
        schema = "tide_import_" + run_id + "_" + label
        sql("CREATE SCHEMA " + identifier(schema))
        schemas.append(schema)
        return schema

    def connection_value(value):
        return '"' + str(value).replace('"', '""') + '"'

    def settings(schema, user=None, password=None, host=None):
        values = {"Host": host or fixture["host"], "Port": fixture["port"], "Username": user or fixture["user"],
                  "Password": password or fixture["password"], "Database": fixture["database"],
                  "SSL Mode": "Disable", "Maximum Pool Size": 2}
        path = output / (schema + ("_restricted" if user else "") + ("_remote" if host else "") + ".private.json")
        path.write_text(json.dumps({"Storage": {"Provider": "PostgreSql", "PostgresSchema": schema},
            "ConnectionStrings": {"Application": ";".join(k + "=" + connection_value(v) for k, v in values.items())}}), encoding="utf-8")
        return path

    def command(source, configuration, apply=False, local=True):
        result = [args.dotnet, str(tool), "--source", str(source), "--settings", str(configuration)]
        if local:
            result.append("--local-test")
        if apply:
            result.append("--apply")
        return result

    def evaluate(label, result, expected):
        stdout = result.stdout
        (output / (label + ".process.json")).write_text(json.dumps({"exitCode": result.returncode,
            "stdoutCharacters": len(stdout), "stderrCharacters": len(result.stderr)}), encoding="utf-8")
        assert MARKER not in stdout + result.stderr and fixture["password"] not in stdout + result.stderr, "Sensitive output detected"
        assert not result.stderr.strip(), "Unexpected diagnostic output"
        data = json.loads(stdout)
        (output / (label + ".json")).write_text(json.dumps(data, indent=2), encoding="utf-8")
        assert expected(data), label + " returned an unexpected sanitized result"
        assert result.returncode == (1 if data["status"] == "failed" else 0), "Incorrect exit status"
        checks.append({"name": label, "passed": True})
        return data

    def run(label, source, configuration, expected, apply=False, local=True):
        result = subprocess.run(command(source, configuration, apply, local), text=True, encoding="utf-8", capture_output=True, timeout=120)
        return evaluate(label, result, expected)

    source = output / "synthetic.db"
    empty = output / "empty.db"
    bootstrap = runpy.run_path(str(API / "PostgresMigrations/generate_baseline.py"))["BOOTSTRAP"]
    db = sqlite3.connect(source)
    db.execute("PRAGMA foreign_keys=ON")
    for path in sorted((API / "Migrations").glob("*.sql")):
        db.executescript(path.read_text(encoding="utf-8"))
    db.executescript(bootstrap)
    for path in sorted((API / "FeatureMigrations").glob("*.sql")):
        db.executescript(path.read_text(encoding="utf-8"))
    db.commit()
    with closing(sqlite3.connect(empty)) as empty_db:
        db.backup(empty_db)
    manifest_bytes = (API / "Migrations/manifest.json").read_bytes()
    for migration in json.loads(manifest_bytes)["migrations"]:
        db.execute("INSERT INTO tide_schema_migrations VALUES(?,?,?,?,?,?)", (migration["version"], migration["resourceName"],
            migration["source"], migration["sha256"], hashlib.sha256(manifest_bytes).hexdigest(), NOW))
    for index, path in enumerate(sorted((API / "FeatureMigrations").glob("*.sql")), 1):
        db.execute("INSERT INTO tide_feature_migrations VALUES(?,?,?,?)", (index, "TideCasa.Api.FeatureMigrations." + path.name,
            hashlib.sha256(path.read_bytes()).hexdigest(), NOW))
    db.execute("""INSERT INTO bartide_customers(id,slug,email,name,menu_json,enrollment_note,created_at,updated_at)
        VALUES('fixture-customer','fixture','fixture@example.invalid',?,'{}','',?,?)""", (MARKER, NOW, NOW))
    for index in range(41):
        db.execute("""INSERT INTO tide_leads(id,name,business,email,phone,vertical,stage,notes,follow_up,tenant_id,version,updated_at,created_at)
            VALUES(?,?,?,?,?,'bartide','new',?,'',?,?,?,?)""", ("lead-" + str(index), MARKER, "Synthetic café 🐚 " + str(index),
            "fixture@example.invalid", "+15555550100", "Line 1\nLine 2 | café\tquoted \"text\"", "fixture-customer" if index == 0 else None,
            9223372036854775807 if index == 0 else index, NOW, NOW))
    db.execute("INSERT INTO bartide_auth_limits VALUES('range-min',-9223372036854775808,9223372036854775807)")
    db.execute("INSERT INTO bartide_auth_limits VALUES('range-max',9223372036854775807,-9223372036854775808)")
    db.execute("INSERT INTO demo_requests VALUES('request','payload','email','{}','requested','pending',?)", (NOW,))
    db.execute("INSERT INTO tide_sales_demo_links VALUES('request','lead-0',?)", (NOW,))
    db.execute("INSERT INTO tide_sales_history VALUES('history','lead-0','created','Synthetic','', 'operator',?)", (NOW,))
    db.execute("""INSERT INTO tide_service_orders(id,tenant_id,environment,request_json,initial_cents,monthly_cents,total_cents,app_stores,created_at,updated_at)
        VALUES('order','fixture-customer','sandbox','{}',12345,5000,17345,1,?,?)""", (NOW, NOW))
    db.commit()
    assert db.execute("PRAGMA integrity_check").fetchone() == ("ok",)
    assert not db.execute("PRAGMA foreign_key_check").fetchall()
    expected_rows = {row[0]: db.execute('SELECT * FROM "' + row[0] + '"').fetchall()
                     for row in db.execute("SELECT name FROM sqlite_schema WHERE type='table' AND name NOT GLOB 'sqlite_*'").fetchall()}
    db.close()
    original_hash = hashlib.sha256(source.read_bytes()).hexdigest()
    utf16_source = output / "synthetic-utf16.db"
    with closing(sqlite3.connect(utf16_source)) as utf16:
        utf16.execute("PRAGMA encoding='UTF-16le'")
        for path in sorted((API / "Migrations").glob("*.sql")):
            utf16.executescript(path.read_text(encoding="utf-8"))
        utf16.executescript(bootstrap)
        for path in sorted((API / "FeatureMigrations").glob("*.sql")):
            utf16.executescript(path.read_text(encoding="utf-8"))
        for table, rows in expected_rows.items():
            if rows:
                utf16.executemany("INSERT INTO " + identifier(table) + " VALUES(" + ",".join("?" for _ in rows[0]) + ")", rows)
        utf16.commit()
        assert utf16.execute("PRAGMA encoding").fetchone() == ("UTF-16le",)
        assert not utf16.execute("PRAGMA foreign_key_check").fetchall()

    try:
        main_schema = provision("main")
        main_settings = settings(main_schema)
        run("dry_run_empty_schema", source, main_settings, lambda r: r["baselineRequired"] and not r["committed"] and len(r["tables"]) == 76)
        assert sql("SELECT count(*) FROM information_schema.tables WHERE table_schema='" + main_schema + "'") == "0"
        checks.append({"name": "dry_run_created_no_tables", "passed": True})
        applied = run("apply_roundtrip", source, main_settings,
            lambda r: r["committed"] and all(t["matches"] for t in r["tables"]) and len(r["tables"]) == 76, apply=True)
        # Compare actual typed rows through a second driver, not just the tool's own hashes.
        query = " UNION ALL ".join("SELECT json_build_object('table','" + table + "','rows',COALESCE(json_agg(t),'[]'::json)) "
            "FROM (SELECT * FROM " + identifier(main_schema) + "." + identifier(table) + ") t" for table in expected_rows)
        # json_agg can include physical line breaks. Parse one complete JSON document.
        actual_tables = {item["table"]: item["rows"] for item in json.loads(sql("SELECT json_agg(payload) FROM (" + query + ") all_tables(payload)"))}
        with closing(sqlite3.connect(source)) as reference:
            for table, original in expected_rows.items():
                columns = [column[1] for column in reference.execute('PRAGMA table_info("' + table + '")')]
                expected = [dict(zip(columns, row)) for row in original]
                normalize = lambda values: sorted(json.dumps(v, sort_keys=True, ensure_ascii=False) for v in values)
                assert normalize(actual_tables[table]) == normalize(expected), "Independent row comparison failed"
        checks.append({"name": "independent_exact_76_table_rows_bigints_money_nulls_unicode_iso_history_41_leads", "passed": True})
        utf16_schema = provision("utf16")
        expected_digests = {table["name"]: table["sourceSha256"] for table in applied["tables"]}
        run("utf16_text_preserved_without_replacement", utf16_source, settings(utf16_schema),
            lambda r: r["committed"] and all(table["matches"] and table["sourceSha256"] == expected_digests[table["name"]]
                                            for table in r["tables"]), apply=True)
        run("reject_populated_rerun", source, main_settings, lambda r: r["errorCode"] == "destination_not_empty" and not r["committed"], apply=True)
        run("reject_populated_dry_run", source, main_settings, lambda r: r["errorCode"] == "destination_not_empty")

        invalid_schema = provision("invalid")
        invalid_settings = settings(invalid_schema)
        invalid_cases = [
            ("extra_table", "CREATE TABLE unexpected_table(id TEXT)", "source_table_inventory_mismatch"),
            ("extra_column", "ALTER TABLE tide_leads ADD COLUMN unexpected_column TEXT", "source_column_mapping_mismatch"),
            ("invalid_foreign_key", "UPDATE tide_leads SET tenant_id='missing' WHERE id='lead-0'", "source_foreign_key_check_failed"),
            ("lossy_integer", "UPDATE tide_leads SET version=1.5 WHERE id='lead-0'", "source_value_type_not_losslessly_supported"),
            ("invalid_utf8", "UPDATE tide_leads SET notes=CAST(X'80' AS TEXT) WHERE id='lead-0'", "source_invalid_unicode"),
            ("text_nul", "UPDATE tide_leads SET notes=char(0) WHERE id='lead-0'", "source_value_type_not_losslessly_supported")]
        for label, mutate, expected_error in invalid_cases:
            invalid = output / (label + ".db")
            shutil.copyfile(source, invalid)
            with closing(sqlite3.connect(invalid)) as bad:
                bad.execute(mutate)
                bad.commit()
            run(label, invalid, invalid_settings, lambda r, code=expected_error: r["errorCode"] == code, apply=True)
        assert sql("SELECT count(*) FROM information_schema.tables WHERE table_schema='" + invalid_schema + "'") == "0"
        checks.append({"name": "invalid_sources_never_initialized_target", "passed": True})
        sidecar = Path(str(source) + "-wal")
        sidecar.touch()
        try:
            run("reject_backup_sidecar", source, invalid_settings, lambda r: r["errorCode"] == "source_must_be_consistent_backup_without_sidecars")
        finally:
            sidecar.unlink()
        run("reject_remote_local_test", source, settings(invalid_schema, host="example.invalid"),
            lambda r: r["errorCode"] == "local_test_requires_literal_loopback")
        run("reject_non_tls_production_settings", source, invalid_settings,
            lambda r: r["errorCode"] == "configuration_or_validation_failed", local=False)

        # Restrict one late table after baseline creation: earlier inserted rows must roll back.
        rollback_schema = provision("rollback")
        rollback_settings = settings(rollback_schema)
        run("initialize_empty_fixture_baseline", empty, rollback_settings, lambda r: r["committed"], apply=True)
        run("dry_run_initialized_empty_destination", source, rollback_settings,
            lambda r: r["status"] == "dry_run_validated_empty_destination" and all(t["destinationRows"] == 0 for t in r["tables"]))
        role = "tide_import_role_" + run_id
        password = secrets.token_hex(24)
        sql("CREATE ROLE " + identifier(role) + " LOGIN PASSWORD '" + password + "'")
        roles.append(role)
        sql("GRANT USAGE ON SCHEMA " + identifier(rollback_schema) + " TO " + identifier(role) + "; "
            "GRANT ALL ON ALL TABLES IN SCHEMA " + identifier(rollback_schema) + " TO " + identifier(role) + "; "
            "REVOKE INSERT ON " + identifier(rollback_schema) + ".tide_schema_migrations FROM " + identifier(role))
        rollback = run("mid_import_permission_failure", source, settings(rollback_schema, user=role, password=password),
            lambda r: r["errorCode"] == "postgresql_rejected_operation" and not r["committed"] and r["rowsCopied"] > 41, apply=True)
        rollback_counts = sql(" UNION ALL ".join("SELECT count(*) FROM " + identifier(rollback_schema) + "." + identifier(table) for table in expected_rows))
        assert sum(map(int, rollback_counts.splitlines())) == 0
        checks.append({"name": "all_application_rows_rolled_back_after_write_failure", "passed": True})

        concurrent_schema = provision("concurrent")
        concurrent_settings = settings(concurrent_schema)
        processes = [subprocess.Popen(command(source, concurrent_settings, apply=True), stdout=subprocess.PIPE,
            stderr=subprocess.PIPE, text=True, encoding="utf-8") for _ in range(2)]
        statuses = []
        try:
            for index, process in enumerate(processes):
                stdout, stderr = process.communicate(timeout=120)
                data = evaluate("concurrent_" + str(index), subprocess.CompletedProcess([], process.returncode, stdout, stderr),
                    lambda r: r["committed"] or r["errorCode"] == "destination_not_empty" or
                              (r["errorCode"] == "destination_busy" and r["databaseErrorCode"] == "55P03"))
                statuses.append(data["committed"])
        finally:
            for process in processes:
                if process.poll() is None:
                    process.kill()
                    process.communicate(timeout=20)
        assert sorted(statuses) == [False, True]
        concurrent_counts = sql(" UNION ALL ".join("SELECT count(*) FROM " + identifier(concurrent_schema) + "." + identifier(table) for table in expected_rows))
        assert list(map(int, concurrent_counts.splitlines())) == [len(rows) for rows in expected_rows.values()]
        checks.append({"name": "concurrent_imports_commit_once", "passed": True})
        assert hashlib.sha256(source.read_bytes()).hexdigest() == original_hash
        assert not any(Path(str(source) + suffix).exists() for suffix in ("-wal", "-shm", "-journal"))
        checks.append({"name": "source_byte_identical_without_sidecars", "passed": True})
        summary = {"passed": True, "checks": checks, "tableCount": 76, "syntheticLeads": 41,
                   "sourceFileSha256": original_hash, "provider": "local PostgreSQL fixture only"}
        (output / "results.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
        print(json.dumps({"passed": True, "checks": len(checks), "resultPath": str(output / "results.json")}))
    finally:
        try:
            for schema in reversed(schemas):
                sql("DROP SCHEMA " + identifier(schema) + " CASCADE")
            for role in roles:
                sql("DROP ROLE " + identifier(role))
        finally:
            # Retain sanitized evidence and synthetic databases, remove credentials.
            for private in output.glob("*.private.json"):
                private.unlink()


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        frames = [{"function": frame.name, "line": frame.lineno} for frame in traceback.extract_tb(error.__traceback__)
                  if Path(frame.filename).name == Path(__file__).name]
        print(json.dumps({"passed": False, "errorType": type(error).__name__, "locations": frames,
            "details": "Suppressed to protect private fixture values"}))
        sys.exit(1)
