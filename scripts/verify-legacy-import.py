#!/usr/bin/env python3
"""Verify the offline importer using generated fixtures only; no customer data.

Required --report points to a NEW JSON file. Optional --dotnet and --api-dll
together run a local compiled-API health/startup check against synthetic data.
All fixture databases live in an owned temporary directory and are removed.
No provider, deployment, live database, existing credentials or SQL dump is used.
"""

from __future__ import annotations

import argparse
from contextlib import closing
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import socket
import sqlite3
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request


SCRIPT = Path(__file__).with_name("rehearse-legacy-import.py")
SPEC = importlib.util.spec_from_file_location("tide_import_rehearsal", SCRIPT)
IMPORTER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(IMPORTER)
FIXTURE_TIME = "2026-09-20T12:00:00.0000000+00:00"
PRIVATE_MARKERS = ("synthetic-owner-legacy", "synthetic-provider-owner", "owner@example.test",
                   "synthetic-tenant", "synthetic-course", "private-fixture-marker", "test-token-never-print")


class CheckFailure(Exception):
    pass


def check(value, name):
    if not value:
        raise CheckFailure(name)


def insert(connection, table, **values):
    columns = ','.join('"' + key + '"' for key in values)
    connection.execute('INSERT INTO "' + table + '" (' + columns + ') VALUES (' + ','.join('?' for _ in values) + ')', list(values.values()))


def fixture(path, scripts, populated=True):
    with closing(sqlite3.connect(path)) as database:
        database.execute("PRAGMA foreign_keys=ON")
        for script in scripts:
            database.executescript(script)
        if not populated:
            return
        insert(database, "bartide_auth_identities", provider_user_id="synthetic-provider-owner",
               app_user_id="synthetic-owner-legacy", verified_email="owner@example.test", created_at=FIXTURE_TIME)
        insert(database, "bartide_auth_identities", provider_user_id="synthetic-provider-staff",
               app_user_id="synthetic-staff-legacy", verified_email="staff@example.test", created_at=FIXTURE_TIME)
        token_hash = hashlib.sha256(b"test-token-never-print").hexdigest()
        insert(database, "bartide_auth_sessions", token_hash=token_hash, provider_user_id="synthetic-provider-owner", expires_at=2000000000)
        insert(database, "bartide_auth_legacy_claims", email="owner@example.test", legacy_user_id="synthetic-owner-legacy", approved=1)
        insert(database, "bartide_auth_limits", id="synthetic-limit-hash", count=2, expires_at=2000000000)
        insert(database, "bartide_customers", _rowid_=41, id="synthetic-tenant", slug="synthetic-only",
               email="owner@example.test", user_id="synthetic-owner-legacy", name="Synthetic Café 🌊",
               menu_json='{"items":[]}', version=47, enrollment_note="private-fixture-marker\x00end",
               created_at=FIXTURE_TIME, updated_at=FIXTURE_TIME, requested_plan="app", vertical="bartide")
        insert(database, "bartide_enhanced_members", id="synthetic-staff", tenant_id="synthetic-tenant",
               name="Synthetic Staff", email="staff@example.test", user_id="synthetic-staff-legacy", role="staff", created_at=FIXTURE_TIME)
        insert(database, "bartide_enhanced_shifts", id="synthetic-shift", tenant_id="synthetic-tenant",
               member_id="synthetic-staff", starts_at=FIXTURE_TIME, ends_at="2026-09-20T20:00:00Z", label="Fixture only")
        insert(database, "fit_courses", id="synthetic-course", tenant_id="synthetic-tenant", title="Synthetic onboarding",
               published=1, version=7, created_at=FIXTURE_TIME, updated_at=FIXTURE_TIME)
        insert(database, "fit_learners", id="synthetic-learner", tenant_id="synthetic-tenant", name="Synthetic Staff",
               email="staff@example.test", user_id="synthetic-staff-legacy", created_at=FIXTURE_TIME)
        insert(database, "fit_videos", id="synthetic-video", tenant_id="synthetic-tenant", object_key="synthetic/object",
               name="Synthetic clip", byte_size=12345, status="ready", created_at=FIXTURE_TIME)
        insert(database, "fit_lessons", id="synthetic-lesson", tenant_id="synthetic-tenant", course_id="synthetic-course",
               title="Synthetic lesson", video_kind="upload", video_source="synthetic-video", position=3, version=9,
               created_at=FIXTURE_TIME, updated_at=FIXTURE_TIME)
        insert(database, "fit_progress", id="synthetic-progress", tenant_id="synthetic-tenant", learner_id="synthetic-learner",
               lesson_id="synthetic-lesson", completed=1, updated_at=FIXTURE_TIME)
        insert(database, "tide_service_orders", id="synthetic-service-order", tenant_id="synthetic-tenant",
               environment="sandbox", status="paid", request_json='{"fixture":true}', initial_cents=60000,
               monthly_cents=5000, total_cents=65000, subscription_revision=11, subscription_status="active",
               session_id="synthetic-session", subscription_id="synthetic-subscription", paid_at=FIXTURE_TIME,
               created_at=FIXTURE_TIME, updated_at=FIXTURE_TIME)
        insert(database, "tide_service_invoices", id="synthetic-invoice", order_id="synthetic-service-order", kind="initial",
               amount_cents=65000, status="paid", paid_at=FIXTURE_TIME, refunded_cents=1500, refund_revision=4, updated_at=FIXTURE_TIME)
        insert(database, "tide_service_refunds", id="synthetic-refund", invoice_id="synthetic-invoice", charge_id="synthetic-charge",
               amount_cents=1500, status="succeeded", updated_at=FIXTURE_TIME)
        insert(database, "tide_service_refund_sync", invoice_id="synthetic-invoice", charge_id="synthetic-charge", revision=4,
               token=sqlite3.Binary(b"synthetic-binary\x00\xff"))
        insert(database, "tide_service_events", id="synthetic-event", order_id="synthetic-service-order", event_type="invoice.paid", processed_at=FIXTURE_TIME)
        insert(database, "tide_referral_profiles", id="synthetic-referral", user_id="synthetic-sales-legacy",
               email="sales@example.test", name="Synthetic Sales", introduction="Fixture only", status="active",
               code="SYNTHETIC", discount_percent=5, terms_version="fixture-v1", terms_accepted_at=FIXTURE_TIME,
               created_at=FIXTURE_TIME, updated_at=FIXTURE_TIME)
        insert(database, "tide_referral_sales", event_id="synthetic-referral-event", order_id="synthetic-service-order",
               profile_id="synthetic-referral", kind="initial", environment="sandbox", gross_cents=60000,
               refunded_cents=1500, commission_cents=11700, source_revision=4, paid_at=FIXTURE_TIME, updated_at=FIXTURE_TIME)
        database.commit()
        check(database.execute("PRAGMA foreign_key_check").fetchone() is None, "fixture_foreign_keys")


def independent_rows(path):
    # Deliberately independent from the importer's encoder/comparison logic.
    result = {}
    with closing(sqlite3.connect(path.as_uri() + "?mode=ro", uri=True)) as database:
        database.execute("PRAGMA trusted_schema=OFF")
        tables = [row[0] for row in database.execute("SELECT name FROM sqlite_schema WHERE type='table' AND name NOT GLOB 'sqlite_*' AND name NOT IN ('tide_schema_migrations','demo_requests') ORDER BY name")]
        for table in tables:
            rows = database.execute('SELECT _rowid_,* FROM "' + table + '" ORDER BY _rowid_').fetchall()
            result[table] = [[(type(value).__name__, repr(value)) for value in row] for row in rows]
    return result


def run_cli(source, output, report):
    run = subprocess.run([sys.executable, str(SCRIPT), "--source", str(source), "--output", str(output), "--report", str(report)],
                         capture_output=True, text=True, timeout=30, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    check(not run.stderr, "cli_no_raw_errors")
    for marker in PRIVATE_MARKERS:
        check(marker not in run.stdout, "cli_no_private_values")
    try:
        result = json.loads(run.stdout)
    except (ValueError, TypeError):
        raise CheckFailure("cli_json_result") from None
    check(run.returncode == (0 if result.get("status") == "passed" else 1), "cli_exit_status")
    return result


def api_startup(dotnet, api_dll, database, original_rows):
    # Health checks only. Production avoids user-secrets loading; blank provider
    # configuration prevents a supplied environment from enabling providers.
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        port = probe.getsockname()[1]
    environment = {key: value for key, value in os.environ.items()
                   if not key.lower().startswith(("supabase", "stripe", "auth__", "resend", "connectionstrings__", "storage__", "reverseproxy__", "servicebilling__", "merchantpayments__", "notifications__", "media__"))}
    environment.update({"ASPNETCORE_ENVIRONMENT": "Production", "DOTNET_ENVIRONMENT": "Production",
                        "ASPNETCORE_URLS": "http://127.0.0.1:" + str(port), "AllowedHosts": "127.0.0.1;localhost",
                        "Storage__DatabasePath": str(database), "Supabase__Url": "", "Supabase__PublishableKey": "",
                        "Supabase__AnonKey": "", "Supabase__ServiceRoleKey": "", "Auth__PlatformOwnerUserId": "",
                        "Auth__Enabled": "false", "Auth__SupabaseUrl": "", "Auth__PublishableKey": "",
                        "Auth__AllowLocalTestProvider": "false",
                        "DOTNET_CLI_TELEMETRY_OPTOUT": "1", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1"})
    with subprocess.Popen([str(dotnet), str(api_dll)], cwd=api_dll.parent, env=environment,
                          stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                          creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0)) as process:
        try:
            ready = False
            deadline = time.monotonic() + 25
            opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
            while process.poll() is None and time.monotonic() < deadline:
                try:
                    with opener.open("http://127.0.0.1:" + str(port) + "/health", timeout=1) as response:
                        ready = response.status == 200
                    if ready:
                        break
                except (OSError, urllib.error.URLError):
                    time.sleep(0.15)
            check(ready, "compiled_api_accepts_import")
        finally:
            if process.poll() is None:
                process.terminate()
            try:
                process.wait(timeout=8)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=8)
    # Startup now applies separately reviewed feature migrations. Compare every
    # imported baseline table and typed row exactly; extra feature tables are
    # verified by the migration suite and are not imported customer records.
    after = independent_rows(database)
    check(set(original_rows).issubset(after), "api_preserves_imported_tables")
    check({table: after[table] for table in original_rows} == original_rows, "api_preserves_imported_rows")
    with closing(sqlite3.connect(database)) as connection:
        entries, _ = IMPORTER.load_baseline()
        IMPORTER.validate_history(connection, entries)


def verify(directory, dotnet=None, api_dll=None):
    checks, case_count = [], 0
    entries, scripts = IMPORTER.load_baseline()
    source = directory / "synthetic-source.sqlite"
    fixture(source, scripts)
    original_hash, original_identity = IMPORTER.file_hash(source), IMPORTER.file_identity(source)
    original_rows = independent_rows(source)
    output, report = directory / "imported.sqlite", directory / "imported.json"
    result = run_cli(source, output, report)
    check(result["status"] == "passed", "populated_import_success")
    saved_report = json.loads(report.read_text(encoding="utf-8"))
    check(saved_report["table_count"] == 46 and saved_report["migration_count"] == 14, "all_baselines_present")
    check(independent_rows(output) == original_rows, "independent_exact_typed_rows")
    check(sum(table["rows"] for table in saved_report["tables"].values()) == sum(map(len, original_rows.values())), "all_row_counts")
    check(IMPORTER.file_hash(source) == original_hash and IMPORTER.file_identity(source) == original_identity, "source_bytes_and_metadata_unchanged")
    check(not any(os.path.exists(str(source) + suffix) for suffix in IMPORTER.SIDECARS), "source_no_sidecar_writes")
    for marker in PRIVATE_MARKERS + (hashlib.sha256(b"test-token-never-print").hexdigest(), str(source)):
        check(marker not in report.read_text(encoding="utf-8"), "report_no_private_values")
    with closing(sqlite3.connect(output)) as connection:
        IMPORTER.validate_history(connection, entries)
        check(connection.execute("SELECT _rowid_,version,user_id FROM bartide_customers").fetchone() ==
              (41, 47, "synthetic-owner-legacy"), "stable_rowid_version_identity")
        check(connection.execute("SELECT initial_cents,monthly_cents,total_cents,subscription_revision FROM tide_service_orders").fetchone() ==
              (60000, 5000, 65000, 11), "prices_and_revisions_preserved")
        check(connection.execute("SELECT count(*) FROM fit_progress p JOIN fit_lessons l ON p.lesson_id=l.id JOIN fit_courses c ON l.course_id=c.id JOIN fit_learners r ON p.learner_id=r.id JOIN bartide_enhanced_members m ON m.user_id=r.user_id WHERE m.tenant_id=c.tenant_id AND p.completed=1").fetchone()[0] == 1, "staff_course_relationships")
        check(connection.execute("SELECT typeof(token) FROM tide_service_refund_sync").fetchone()[0] == "blob", "blob_storage_class_preserved")
        check(connection.execute("PRAGMA foreign_key_check").fetchone() is None and connection.execute("PRAGMA integrity_check").fetchall() == [("ok",)], "output_integrity")
    checks += ["populated_import", "46_table_counts", "14_exact_migrations", "independent_typed_content", "source_bytes_unchanged",
               "source_metadata_unchanged", "no_source_sidecar_writes", "reports_redacted", "rowids_versions_identity",
               "financial_amounts_revisions", "staff_course_relationships", "blob_storage_class", "output_integrity"]
    case_count += 1

    # Existing output/report and alias collisions must not mutate either file.
    existing_hash = IMPORTER.file_hash(output)
    refused_report = directory / "existing-output.json"
    failure = run_cli(source, output, refused_report)
    check(failure["error_code"] == "destination_already_exists" and not refused_report.exists() and IMPORTER.file_hash(output) == existing_hash, "existing_output_preserved")
    absent_output = directory / "existing-report.sqlite"
    report_hash = IMPORTER.file_hash(report)
    failure = run_cli(source, absent_output, report)
    check(failure["error_code"] == "destination_already_exists" and not absent_output.exists() and IMPORTER.file_hash(report) == report_hash, "existing_report_preserved")
    failure = run_cli(source, source, directory / "samepath.json")
    check(failure["error_code"] == "paths_must_be_distinct" and IMPORTER.file_hash(source) == original_hash, "same_path_refused")
    checks += ["existing_output_preserved", "existing_report_preserved", "same_path_refused"]
    case_count += 3
    for name, conflict_report in (("source_sidecar", Path(str(source) + "-wal")),
                                  ("output_sidecar", directory / "sidecar-conflict.sqlite-journal")):
        target = directory / "sidecar-conflict.sqlite"
        failure = run_cli(source, target, conflict_report)
        check(failure["error_code"] == "artifact_sidecar_path_conflict" and
              not target.exists() and not conflict_report.exists() and
              IMPORTER.file_hash(source) == original_hash, name + "_path_refused")
        checks.append(name + "_path_refused")
        case_count += 1
    if os.name == "nt":
        for position, name in enumerate(("source", "output", "report")):
            target, diagnosis = directory / ("ads-" + name + ".sqlite"), directory / ("ads-" + name + ".json")
            stream = Path(str(source) + ":rehearsal-" + name)
            parameters = [source, target, diagnosis]
            parameters[position] = stream
            failure = run_cli(*parameters)
            check(failure["error_code"] == "windows_stream_path_not_allowed" and
                  not target.exists() and not diagnosis.exists() and not stream.exists() and
                  IMPORTER.file_hash(source) == original_hash and IMPORTER.file_identity(source) == original_identity,
                  "ads_" + name + "_refused_without_source_mutation")
            checks.append("ads_" + name + "_refused_without_mutation")
            case_count += 1

    def failed_case(name, change=None, scripts_override=None, error=None, raw=False):
        nonlocal case_count
        candidate, target, diagnosis = [directory / (name + suffix) for suffix in (".source.sqlite", ".output.sqlite", ".report.json")]
        if raw:
            candidate.write_bytes(b"synthetic non-database fixture")
        elif scripts_override is not None:
            fixture(candidate, scripts_override, populated=False)
        else:
            shutil.copyfile(source, candidate)
        if change:
            change(candidate)
        initial_hash, initial_identity = IMPORTER.file_hash(candidate), IMPORTER.file_identity(candidate)
        failure = run_cli(candidate, target, diagnosis)
        check(failure["status"] == "failed" and not target.exists(), name + "_refused_no_output")
        if error:
            check(failure["error_code"] == error, name + "_reason")
        check(IMPORTER.file_hash(candidate) == initial_hash and IMPORTER.file_identity(candidate) == initial_identity, name + "_source_unchanged")
        check(json.loads(diagnosis.read_text())["status"] == "failed", name + "_safe_report")
        check(not any(directory.glob(".tide-rehearsal-*")), name + "_snapshot_cleanup")
        checks.extend([name + "_refused", name + "_source_unchanged", name + "_cleanup"])
        case_count += 1

    def alter(sql):
        def apply(path):
            with closing(sqlite3.connect(path)) as connection:
                connection.executescript(sql)
        return apply

    failed_case("missing_table", alter("DROP TABLE tide_tasks"), error="schema_mismatch")
    failed_case("unknown_table", alter("CREATE TABLE unknown_private_records(id TEXT)"), error="schema_mismatch")
    failed_case("changed_column", alter("ALTER TABLE bartide_auth_limits ADD COLUMN unexpected TEXT"), error="schema_mismatch")
    failed_case("changed_index", alter("DROP INDEX tide_service_invoice_order"), error="schema_mismatch")
    failed_case("extra_index", alter("CREATE INDEX unexpected_index ON fit_courses(title)"), error="schema_mismatch")
    changed = [sql.replace("monthly_cents=5000", "monthly_cents=5001") for sql in scripts]
    check(changed != scripts, "check_constraint_fixture_changed")
    failed_case("changed_check", scripts_override=changed, error="schema_mismatch")
    changed_fk = [sql.replace("tenant_id TEXT NOT NULL REFERENCES bartide_customers(id)",
                             "tenant_id TEXT NOT NULL REFERENCES fit_courses(id)") for sql in scripts]
    check(changed_fk != scripts, "foreign_key_fixture_changed")
    failed_case("changed_foreign_key", scripts_override=changed_fk, error="schema_mismatch")
    failed_case("orphan_foreign_key", alter("PRAGMA foreign_keys=OFF; UPDATE fit_progress SET lesson_id='synthetic-missing'"), error="foreign_key_check_failed")
    failed_case("view", alter("CREATE VIEW private_fixture_view AS SELECT * FROM bartide_auth_sessions"), error="unsupported_schema_object")
    failed_case("trigger", alter("CREATE TRIGGER private_fixture_trigger AFTER INSERT ON bartide_customers BEGIN SELECT RAISE(ABORT,'private-fixture-marker'); END"), error="unsupported_schema_object")
    failed_case("uncheckpointed_snapshot", lambda path: Path(str(path) + "-wal").write_bytes(b""), error="offline_snapshot_required")
    failed_case("invalid_database", raw=True, error="operation_failed")

    # Inject a failure after real inserts to prove transaction rollback and owned
    # artifact cleanup, without exposing a production fault-injection option.
    original_copy = IMPORTER.copy_rows
    for name in ("partial_copy_failure", "content_difference"):
        target, diagnosis = directory / (name + ".sqlite"), directory / (name + ".json")
        def injected(frozen, destination, tables):
            if name == "partial_copy_failure":
                original_copy(frozen, destination, tables[:1])
                raise IMPORTER.RehearsalFailure("synthetic_copy_failure")
            original_copy(frozen, destination, tables)
            destination.execute("UPDATE bartide_customers SET version=version+1")
        IMPORTER.copy_rows = injected
        try:
            failure = IMPORTER.rehearse(source, target, diagnosis)
        finally:
            IMPORTER.copy_rows = original_copy
        check(failure["error_code"] == ("synthetic_copy_failure" if name == "partial_copy_failure" else "content_mismatch"), name + "_detected")
        check(not target.exists() and not any(Path(str(target) + suffix).exists() for suffix in IMPORTER.SIDECARS), name + "_output_rolled_back")
        check(not any(directory.glob(".tide-rehearsal-*")) and IMPORTER.file_hash(source) == original_hash, name + "_source_and_cleanup")
        checks += [name + "_detected", name + "_rollback_cleanup", name + "_source_unchanged"]
        case_count += 1

    original_remove = IMPORTER.OwnedFile.remove
    withheld_snapshots = []
    target, diagnosis = directory / "late-cleanup.sqlite", directory / "late-cleanup.json"
    def failed_snapshot_removal(artifact):
        if artifact.path.name.startswith(".tide-rehearsal-"):
            withheld_snapshots.append(artifact)
            raise OSError("synthetic cleanup failure")
        original_remove(artifact)
    IMPORTER.OwnedFile.remove = failed_snapshot_removal
    try:
        failure = IMPORTER.rehearse(source, target, diagnosis)
    finally:
        IMPORTER.OwnedFile.remove = original_remove
        for artifact in withheld_snapshots:
            original_remove(artifact)
    check(failure["error_code"] == "owned_artifact_cleanup_incomplete" and not target.exists(), "late_cleanup_removes_verified_output")
    check(json.loads(diagnosis.read_text())["error_code"] == "owned_artifact_cleanup_incomplete", "late_cleanup_report")
    check(IMPORTER.file_hash(source) == original_hash and not any(directory.glob(".tide-rehearsal-*")), "late_cleanup_source_unchanged")
    checks += ["late_cleanup_removes_output", "late_cleanup_report", "late_cleanup_source_unchanged"]
    case_count += 1

    if dotnet and api_dll:
        api_startup(dotnet, api_dll, output, original_rows)
        checks += ["compiled_api_accepts_import", "compiled_api_preserves_data", "compiled_api_preserves_history"]
        case_count += 1
    return {"format_version": 1, "status": "passed", "data": "synthetic_only", "case_count": case_count,
            "check_count": len(checks), "checks": [{"name": name, "status": "passed"} for name in checks],
            "compiled_api_startup": "passed" if dotnet else "not_requested",
            "script_sha256": {"rehearse-legacy-import.py": IMPORTER.file_hash(SCRIPT),
                              "verify-legacy-import.py": IMPORTER.file_hash(Path(__file__).resolve())}}


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--report", required=True, type=Path)
    parser.add_argument("--dotnet", type=Path)
    parser.add_argument("--api-dll", type=Path)
    args = parser.parse_args()
    if bool(args.dotnet) != bool(args.api_dll):
        parser.error("--dotnet and --api-dll must be supplied together")
    owned_report, temporary = None, None
    result = {"format_version": 1, "status": "failed", "data": "synthetic_only"}
    try:
        requested = args.report.absolute()
        IMPORTER.validate_path_spelling([requested])
        report = requested.resolve(strict=False)
        check(not os.path.lexists(requested) and not os.path.lexists(report), "report_already_exists")
        check(report.parent.is_dir(), "report_parent_required")
        owned_report = IMPORTER.OwnedFile.create(report)
        temp_parent = Path(tempfile.gettempdir()).resolve(strict=True)
        temporary = Path(tempfile.mkdtemp(prefix="tide-import-verify-", dir=temp_parent)).resolve(strict=True)
        temporary_identity = IMPORTER.file_identity(temporary)[:2]
        result = verify(temporary, args.dotnet.resolve(strict=True) if args.dotnet else None,
                        args.api_dll.resolve(strict=True) if args.api_dll else None)
    except (CheckFailure, IMPORTER.RehearsalFailure) as error:
        result["error_code"] = str(error)
    except (KeyboardInterrupt, SystemExit):
        result["error_code"] = "interrupted"
    except Exception:
        result["error_code"] = "verification_operation_failed"
    finally:
        if temporary is not None:
            try:
                check(not temporary.is_symlink() and temporary.resolve(strict=True) == temporary and
                      temporary.parent == temp_parent and temporary.name.startswith("tide-import-verify-") and
                      IMPORTER.file_identity(temporary)[:2] == temporary_identity, "temporary_identity_changed")
                shutil.rmtree(temporary)
            except Exception:
                result = {"format_version": 1, "status": "failed", "error_code": "fixture_cleanup_incomplete"}
        if owned_report is not None:
            try:
                owned_report.write_report(result)
            except Exception:
                result = {"format_version": 1, "status": "failed", "error_code": "report_write_failed"}
                try:
                    owned_report.remove()
                except Exception:
                    pass
    print(json.dumps({key: value for key, value in result.items() if key != "checks"}, sort_keys=True))
    return 0 if result["status"] == "passed" else 1


if __name__ == "__main__":
    sys.exit(main())
