#!/usr/bin/env python3
"""Offline, exact-baseline SQLite import rehearsal; never an in-place migration.

Required: --source CHECKPOINTED-SNAPSHOT --output NEW-DATABASE --report NEW-JSON
All parent directories must exist. An offline snapshot has no -wal, -shm or
-journal sidecars. Stop/checkpoint the producer separately before supplying it;
this tool never opens a live database or discovers/export data from a provider.
Extra/missing tables, views, triggers and incompatible schema definitions are
rejected, not skipped. Only the 14 pinned, repository-owned SQL baselines execute.
Source rows, credentials and SQL error messages are never printed or reported.
The resulting database still contains the supplied private data: protect it and
the report appropriately. This is rehearsal tooling, not deployment approval.
"""

from __future__ import annotations

import argparse
from contextlib import closing
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import sqlite3
import stat
import sys
import tempfile


MANIFEST_SHA256 = "0faf463bbff15c66084eed0d547742da61f02daca60349e1660da5dc3d122d54"
BASELINE_DIRECTORY = Path(__file__).resolve().parents[1] / "TideCasa.Api" / "Migrations"
HISTORY = "tide_schema_migrations"
HISTORY_SQL = """CREATE TABLE tide_schema_migrations (
 version INTEGER PRIMARY KEY, resource_name TEXT NOT NULL UNIQUE,
 source_path TEXT NOT NULL, sha256 TEXT NOT NULL, manifest_sha256 TEXT NOT NULL,
 applied_at TEXT NOT NULL);"""
SIDECARS = ("-wal", "-shm", "-journal")


class RehearsalFailure(Exception):
    """Only constant, non-sensitive error codes may cross the CLI boundary."""


def require(condition, code):
    if not condition:
        raise RehearsalFailure(code)


def file_hash(path):
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def file_identity(path):
    value = path.stat()
    return (value.st_dev, value.st_ino, value.st_size, value.st_mtime_ns)


def no_sidecars(path):
    require(not any(os.path.lexists(str(path) + suffix) for suffix in SIDECARS),
            "offline_snapshot_required")


def validate_path_spelling(paths):
    if os.name == "nt":
        # Distinct paths and O_EXCL do not protect an existing file from having
        # an alternate data stream added. The drive/UNC anchor is the only
        # permitted location for a colon; reject before resolving or creating.
        require(not any(":" in part for path in paths for part in path.parts[1:]),
                "windows_stream_path_not_allowed")
        require(not any(os.path.isreserved(str(path)) for path in paths),
                "windows_reserved_path_not_allowed")


class OwnedFile:
    """Delete/write only the exclusively created inode at the recorded path."""

    def __init__(self, path, fd):
        self.path = path.resolve(strict=True)
        value = os.fstat(fd)
        self.identity = (value.st_dev, value.st_ino)
        os.close(fd)

    @classmethod
    def create(cls, path):
        fd = os.open(path, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
        return cls(path, fd)

    @classmethod
    def temporary(cls, parent):
        fd, name = tempfile.mkstemp(prefix=".tide-rehearsal-", suffix=".sqlite", dir=parent)
        return cls(Path(name), fd)

    def assert_owned(self):
        require(not self.path.is_symlink() and self.path.resolve(strict=True) == self.path,
                "artifact_identity_changed")
        value = self.path.stat()
        require(stat.S_ISREG(value.st_mode) and
                (value.st_dev, value.st_ino) == self.identity, "artifact_identity_changed")

    def remove(self):
        if not os.path.lexists(self.path):
            return
        self.assert_owned()
        self.path.unlink()

    def write_report(self, report):
        self.assert_owned()
        fd = os.open(self.path, os.O_WRONLY)
        with os.fdopen(fd, "wb") as stream:
            value = os.fstat(stream.fileno())
            require((value.st_dev, value.st_ino) == self.identity, "artifact_identity_changed")
            stream.truncate(0)
            stream.write((json.dumps(report, indent=2, sort_keys=True) + "\n").encode("utf-8"))
            stream.flush()
            os.fsync(stream.fileno())


def configure(connection, readonly=False):
    connection.execute("PRAGMA trusted_schema=OFF")
    connection.execute("PRAGMA foreign_keys=ON")
    connection.execute("PRAGMA busy_timeout=1000")
    if hasattr(connection, "setconfig"):
        connection.setconfig(sqlite3.SQLITE_DBCONFIG_DEFENSIVE, True)
    if readonly:
        connection.execute("PRAGMA query_only=ON")
    return connection


def open_database(path, readonly=False):
    # Immutable is permitted only for the explicitly supplied offline snapshot.
    options = "?mode=ro&immutable=1" if readonly else "?mode=rw"
    return configure(sqlite3.connect(path.as_uri() + options, uri=True, isolation_level=None), readonly)


def load_baseline():
    raw = (BASELINE_DIRECTORY / "manifest.json").read_bytes()
    require(hashlib.sha256(raw).hexdigest() == MANIFEST_SHA256, "baseline_manifest_changed")
    manifest = json.loads(raw)
    entries = manifest["migrations"]
    require(manifest["formatVersion"] == 1 and len(entries) == 14, "baseline_manifest_invalid")
    scripts = []
    filenames = []
    for version, entry in enumerate(entries, 1):
        prefix = "TideCasa.Api.Migrations."
        require(entry["version"] == version and entry["resourceName"].startswith(prefix),
                "baseline_manifest_invalid")
        filename = entry["resourceName"][len(prefix):]
        require(Path(filename).name == filename and filename.endswith(".sql"), "baseline_manifest_invalid")
        raw_sql = (BASELINE_DIRECTORY / filename).read_bytes()
        require(hashlib.sha256(raw_sql).hexdigest() == entry["sha256"], "baseline_sql_changed")
        scripts.append(raw_sql.decode("utf-8-sig"))
        filenames.append(filename)
    require(set(filenames) == {p.name for p in BASELINE_DIRECTORY.glob("*.sql")}, "baseline_files_changed")
    return entries, scripts


def execute_baseline(connection, scripts):
    # executescript commits an existing transaction; execute complete statements
    # individually to keep schema, history and imported rows in one transaction.
    for script in scripts:
        statement = ""
        for character in script:
            statement += character
            if character == ";" and sqlite3.complete_statement(statement):
                connection.execute(statement)
                statement = ""
        if sql_tokens(statement):
            require(sqlite3.complete_statement(statement + ";"), "baseline_sql_incomplete")
            connection.execute(statement)


TOKEN = re.compile(r"\s+|--[^\n]*(?:\n|$)|/\*[\s\S]*?\*/|'(?:''|[^'])*'|\"(?:\"\"|[^\"])*\"|`(?:``|[^`])*`|\[[^\]]*\]|[A-Za-z_][A-Za-z_0-9$]*|[0-9]+(?:\.[0-9]+)?|.")


def sql_tokens(sql):
    result = []
    for match in TOKEN.finditer(sql or ""):
        token = match.group()
        if token.isspace() or token.startswith(("--", "/*")):
            continue
        if token.startswith("'"):
            result.append(("literal", token))
        elif token.startswith(('"', "`", "[")):
            quote = token[0]
            body = token[1:-1]
            if quote in ('"', "`"):
                body = body.replace(quote * 2, quote)
            result.append(("identifier", body.casefold()))
        elif re.fullmatch(r"[A-Za-z_][A-Za-z_0-9$]*", token):
            result.append(("identifier", token.casefold()))
        else:
            result.append(("symbol", token))
    while result and result[-1] == ("symbol", ";"):
        result.pop()
    return tuple(result)


def identifier(name):
    return '"' + name.replace('"', '""') + '"'


def schema_description(connection, omit_history=False):
    objects = connection.execute("SELECT type,name,tbl_name,sql FROM sqlite_schema WHERE name NOT GLOB 'sqlite_*'").fetchall()
    require(not any(row[0] not in ("table", "index") for row in objects), "unsupported_schema_object")
    result = {}
    table_flags = {row[1]: tuple(row[4:6]) for row in connection.execute("PRAGMA table_list") if row[0] == "main"}
    for kind, name, table, sql in objects:
        if omit_history and (name == HISTORY or table == HISTORY):
            continue
        entry = [kind, table, sql_tokens(sql)]
        if kind == "table":
            columns = connection.execute("PRAGMA table_xinfo(" + identifier(name) + ")").fetchall()
            require(all(row[6] == 0 for row in columns) and
                    not any(row[1].casefold() in ("rowid", "_rowid_", "oid") for row in columns),
                    "unsupported_columns")
            entry += [columns,
                      connection.execute("PRAGMA foreign_key_list(" + identifier(name) + ")").fetchall(),
                      sorted(tuple(row[1:]) for row in connection.execute("PRAGMA index_list(" + identifier(name) + ")")),
                      table_flags[name]]
            for index in connection.execute("PRAGMA index_list(" + identifier(name) + ")"):
                entry.append((index[1], connection.execute("PRAGMA index_xinfo(" + identifier(index[1]) + ")").fetchall()))
            # Index enumeration order is not schema semantics.
            entry[7:] = sorted(entry[7:])
        result[name] = entry
    return result


def validate_schema(connection, expected, omit_history=False):
    require(schema_description(connection, omit_history) == expected, "schema_mismatch")


def validate_integrity(connection):
    require(connection.execute("PRAGMA integrity_check").fetchall() == [("ok",)], "integrity_check_failed")
    require(connection.execute("PRAGMA foreign_key_check").fetchone() is None, "foreign_key_check_failed")


def columns_for(connection, table):
    return [row[1] for row in connection.execute("PRAGMA table_xinfo(" + identifier(table) + ")")]


def read_rows(connection, table, columns):
    selected = ','.join(identifier(column) for column in columns)
    return connection.execute("SELECT _rowid_," + selected + " FROM " + identifier(table) + " ORDER BY _rowid_")


def copy_rows(source, destination, tables):
    for table in tables:
        columns = columns_for(source, table)
        sql = "INSERT INTO " + identifier(table) + "(_rowid_," + ','.join(map(identifier, columns)) + ") VALUES(" + ','.join('?' for _ in range(len(columns) + 1)) + ")"
        cursor = read_rows(source, table, columns)
        while batch := cursor.fetchmany(128):
            destination.executemany(sql, batch)


def encoded_row(row):
    # Storage classes matter: integer 1 and real 1.0 must not compare equal.
    encoded = []
    for value in row:
        if value is None:
            encoded.append(["null"])
        elif isinstance(value, int):
            encoded.append(["integer", str(value)])
        elif isinstance(value, float):
            encoded.append(["real", value.hex()])
        elif isinstance(value, str):
            encoded.append(["text", value])
        elif isinstance(value, bytes):
            encoded.append(["blob", value.hex()])
        else:
            raise RehearsalFailure("unsupported_storage_class")
    return json.dumps(encoded, ensure_ascii=True, separators=(",", ":")).encode("ascii")


def compare_contents(source, destination, tables):
    report = {}
    for table in tables:
        columns = columns_for(source, table)
        left, right = read_rows(source, table, columns), read_rows(destination, table, columns)
        digest, count = hashlib.sha256(), 0
        while True:
            a, b = left.fetchone(), right.fetchone()
            require((a is None) == (b is None), "content_count_mismatch")
            if a is None:
                break
            raw = encoded_row(a)
            require(raw == encoded_row(b), "content_mismatch")
            digest.update(len(raw).to_bytes(8, "big"))
            digest.update(raw)
            count += 1
        report[table] = {"rows": count, "sha256": digest.hexdigest()}
    return report


def add_history(connection, entries):
    connection.execute(HISTORY_SQL)
    applied_at = datetime.now(timezone.utc).isoformat()
    connection.executemany("INSERT INTO tide_schema_migrations VALUES(?,?,?,?,?,?)", [
        (entry["version"], entry["resourceName"], entry["source"], entry["sha256"], MANIFEST_SHA256, applied_at)
        for entry in entries])


def validate_history(connection, entries):
    actual = connection.execute("SELECT version,resource_name,source_path,sha256,manifest_sha256 FROM tide_schema_migrations ORDER BY version").fetchall()
    expected = [(e["version"], e["resourceName"], e["source"], e["sha256"], MANIFEST_SHA256) for e in entries]
    require(actual == expected, "migration_history_mismatch")


def source_unchanged(source, before_identity, before_hash):
    no_sidecars(source)
    require(file_identity(source) == before_identity and file_hash(source) == before_hash,
            "source_changed_during_rehearsal")


def rehearse(source_path, output_path, report_path):
    owned_output = owned_report = snapshot = None
    success, stage = False, "paths"
    result = {"format_version": 1, "status": "failed"}
    try:
        requested = [Path(value).absolute() for value in (source_path, output_path, report_path)]
        validate_path_spelling(requested)
        source, output, report = [path.resolve(strict=False) for path in requested]
        require(len({os.path.normcase(str(path)) for path in (source, output, report)}) == 3,
                "paths_must_be_distinct")
        artifact_paths = [os.path.normcase(str(path)) for path in (source, output, report)]
        require(not any(candidate == base + suffix for candidate in artifact_paths
                        for base in artifact_paths for suffix in SIDECARS), "artifact_sidecar_path_conflict")
        require(source.is_file(), "source_file_required")
        require(not any(os.path.lexists(path) for path in (requested[1], requested[2], output, report)),
                "destination_already_exists")
        require(output.parent.is_dir() and report.parent.is_dir(), "parent_directory_required")
        no_sidecars(output)
        owned_report = OwnedFile.create(report)
        stage = "baseline"
        entries, scripts = load_baseline()
        with closing(configure(sqlite3.connect(":memory:", isolation_level=None))) as reference:
            execute_baseline(reference, scripts)
            expected = schema_description(reference)
            tables = sorted(name for name, shape in expected.items() if shape[0] == "table")
            require(len(tables) == 46, "baseline_table_count_changed")
        stage = "source_validation"
        no_sidecars(source)
        before_identity, before_hash = file_identity(source), file_hash(source)
        with closing(open_database(source, readonly=True)) as source_connection:
            validate_schema(source_connection, expected)
            validate_integrity(source_connection)
            snapshot = OwnedFile.temporary(output.parent)
            with closing(open_database(snapshot.path)) as snapshot_writer:
                source_connection.backup(snapshot_writer)
        source_unchanged(source, before_identity, before_hash)
        stage = "import"
        owned_output = OwnedFile.create(output)
        with closing(open_database(snapshot.path, readonly=True)) as frozen, closing(open_database(output)) as destination:
            validate_schema(frozen, expected)
            validate_integrity(frozen)
            destination.execute("BEGIN IMMEDIATE")
            try:
                destination.execute("PRAGMA defer_foreign_keys=ON")
                execute_baseline(destination, scripts)
                add_history(destination, entries)
                copy_rows(frozen, destination, tables)
                stage = "verification"
                validate_schema(destination, expected, omit_history=True)
                validate_history(destination, entries)
                validate_integrity(destination)
                table_report = compare_contents(frozen, destination, tables)
                source_unchanged(source, before_identity, before_hash)
                destination.commit()
            except BaseException:
                destination.rollback()
                raise
        source_unchanged(source, before_identity, before_hash)
        owned_output.assert_owned()
        result = {"format_version": 1, "status": "passed", "table_count": len(tables),
                  "migration_count": len(entries), "manifest_sha256": MANIFEST_SHA256,
                  "source_sha256": before_hash, "output_sha256": file_hash(output),
                  "checks": {"source_unchanged": "passed", "exact_schema": "passed",
                             "exact_typed_content_and_rowids": "passed", "foreign_keys": "passed",
                             "integrity": "passed", "migration_history": "passed"},
                  "tables": table_report}
        owned_report.write_report(result)
        success = True
    except RehearsalFailure as error:
        result = {"format_version": 1, "status": "failed", "stage": stage, "error_code": str(error)}
    except (KeyboardInterrupt, SystemExit):
        result = {"format_version": 1, "status": "failed", "stage": stage, "error_code": "interrupted"}
    except Exception:
        # sqlite/OSError messages may contain paths, SQL or values. Never emit them.
        result = {"format_version": 1, "status": "failed", "stage": stage, "error_code": "operation_failed"}
    finally:
        cleanup_failed = False
        if snapshot is not None:
            try:
                snapshot.remove()
            except Exception:
                cleanup_failed = True
                success = False
        # A later snapshot-cleanup failure also invalidates success. Evaluate
        # output cleanup afterwards, so it cannot leave a failed artifact behind.
        if not success and owned_output is not None:
            try:
                owned_output.remove()
            except Exception:
                cleanup_failed = True
        if cleanup_failed:
            result = {"format_version": 1, "status": "failed", "stage": "cleanup",
                      "error_code": "owned_artifact_cleanup_incomplete"}
            success = False
        if not success and owned_report is not None:
            try:
                owned_report.write_report(result)
            except Exception:
                try:
                    owned_report.remove()
                except Exception:
                    pass
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--report", required=True, type=Path)
    args = parser.parse_args()
    result = rehearse(args.source, args.output, args.report)
    # Full per-table hashes live in the report; console remains compact.
    print(json.dumps({key: value for key, value in result.items() if key != "tables"}, sort_keys=True))
    return 0 if result["status"] == "passed" else 1


if __name__ == "__main__":
    sys.exit(main())
