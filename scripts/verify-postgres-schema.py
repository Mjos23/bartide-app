"""Verify the compiled PostgreSQL migrator against an owned loopback fixture.

No build or provider call. Every schema is uniquely named, recorded as owned only
after CREATE succeeds, and removed in finally. No pre-existing schema is modified.
The fixture password is read privately and omitted from evidence and diagnostics.
"""
import ctypes
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / '.tools/postgres-python'))
import psycopg
from psycopg import sql

RUN = ROOT / '.tools/postgres-schema-verification' / datetime.now().strftime('%Y%m%d-%H%M%S-%f')
SDK = ROOT / '.tools/dotnet-10.0.401/dotnet.exe'
SOURCE_DLL = ROOT / 'TideCasa.Api/bin/Debug/net10.0/TideCasa.Api.dll'
DLL = RUN / 'api-build/TideCasa.Api.dll'
MANIFEST_PATH = ROOT / 'TideCasa.Api/PostgresMigrations/manifest.json'
MANIFEST = json.loads(MANIFEST_PATH.read_text(encoding='utf-8'))
FIXTURE = json.loads((ROOT / '.tools/postgresql-17-test/fixture.json').read_text(encoding='utf-8-sig'))
PREFIX = 'tide_schema_' + uuid.uuid4().hex[:16]
OWNED, PROCESSES, RESULTS = [], [], []
HTTP = urllib.request.build_opener(urllib.request.ProxyHandler({}))
RUN.mkdir(parents=True, exist_ok=False)
def ignore_build_files(directory, names):
    return [name for name in names if name.endswith('.pdb') or
            (Path(directory).name == 'runtimes' and name != 'win-x64')]
shutil.copytree(SOURCE_DLL.parent, DLL.parent, ignore=ignore_build_files)
BUILD_SHA = hashlib.sha256(DLL.read_bytes()).hexdigest()
if os.name == 'nt':
    ctypes.windll.kernel32.SetErrorMode(0x0001 | 0x0002 | 0x8000)
if FIXTURE.get('host') != '127.0.0.1' or FIXTURE.get('database') != 'tide_test':
    raise RuntimeError('Only the isolated loopback PostgreSQL fixture is permitted.')


def redact(value):
    return str(value).replace(FIXTURE['password'], '[fixture password redacted]')


def save():
    value = {'checks': RESULTS, 'api_sha256': BUILD_SHA,
             'baseline_sha256': MANIFEST['migrations'][0]['sha256'],
             'processes': [{k: v for k, v in item.items() if k not in ('process', 'stream')} for item in PROCESSES],
             'owned_schemas': OWNED, 'fixture': 'isolated PostgreSQL loopback; no provider connections'}
    (RUN / 'results.json').write_text(json.dumps(value, indent=2), encoding='utf-8')


def check(name, condition):
    RESULTS.append({'check': name, 'passed': bool(condition)})
    save()
    print(('PASS ' if condition else 'FAIL ') + name, flush=True)
    if not condition:
        raise AssertionError(name)


def connect(schema=None):
    connection = psycopg.connect(host=FIXTURE['host'], port=FIXTURE['port'],
        user=FIXTURE['user'], password=FIXTURE['password'], dbname=FIXTURE['database'],
        connect_timeout=15, autocommit=True)
    if schema is not None:
        assert schema in OWNED and re.fullmatch(re.escape(PREFIX) + r'_[a-z0-9_]+', schema)
        connection.execute(sql.SQL('SET search_path TO {}').format(sql.Identifier(schema)))
    connection.execute("SET TIME ZONE 'UTC'")
    return connection


def create_schema(label):
    schema = PREFIX + '_' + label
    assert re.fullmatch(r'tide_schema_[a-f0-9]{16}_[a-z0-9_]+', schema) and len(schema) <= 55
    with connect() as db:
        db.execute(sql.SQL('CREATE SCHEMA {}').format(sql.Identifier(schema)))
    OWNED.append(schema)
    save()
    return schema


def rows(schema, statement, parameters=()):
    with connect(schema) as db:
        return db.execute(statement, parameters).fetchall()


def modify(schema, statement, parameters=()):
    with connect(schema) as db:
        db.execute(statement, parameters)


def expect_state(name, expected, action):
    actual = None
    try:
        action()
    except psycopg.Error as error:
        actual = error.sqlstate
    check(name, actual == expected)


def free_port():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        return sock.getsockname()[1]


def start_api(label, schema):
    assert schema in OWNED
    check(label + ': compiled API unchanged', hashlib.sha256(DLL.read_bytes()).hexdigest() == BUILD_SHA)
    content = RUN / (label + '-content')
    content.mkdir()
    port = free_port()
    env = os.environ.copy()
    for key in list(env):
        upper = key.upper()
        if upper.startswith(('ASPNETCORE_', 'DOTNET_', 'STORAGE__', 'CONNECTIONSTRINGS__', 'REVERSEPROXY__', 'DATAPROTECTION__')) or any(
                token in upper for token in ('STRIPE', 'RESEND', 'SUPABASE', 'CLOUDFLARE', 'AUTH__',
                    'WEBPUSH__', 'NOTIFICATIONS__', 'MERCHANTPAYMENTS__', 'SERVICEBILLING__', 'MEDIA__')):
            env.pop(key)
    # Quote the private value using Npgsql connection-string quoting; never log it.
    password = '"' + FIXTURE['password'].replace('"', '""') + '"'
    connection = f'Host=127.0.0.1;Port={int(FIXTURE["port"])};Database=tide_test;Username={FIXTURE["user"]};Password={password};SSL Mode=Disable'
    env.update({'DOTNET_CLI_HOME': str(ROOT / '.tools/dotnet-home'),
        'DOTNET_CLI_TELEMETRY_OPTOUT': '1', 'DOTNET_SKIP_FIRST_TIME_EXPERIENCE': '1',
        'DOTNET_NOLOGO': '1', 'DOTNET_PROCESSOR_COUNT': '1', 'ASPNETCORE_ENVIRONMENT': 'Development',
        'DOTNET_DbgEnableMiniDump': '0', 'COMPlus_DbgEnableMiniDump': '0', 'DOTNET_EnableCrashReport': '0',
        'ASPNETCORE_URLS': f'http://127.0.0.1:{port}',
        'Storage__Provider': 'PostgreSql', 'Storage__PostgresSchema': schema,
        'Storage__CreatePostgresSchema': 'false', 'ConnectionStrings__Application': connection,
        'ReverseProxy__Provider': 'fixture-disabled',
        'Storage__DatabasePath': str(content / 'unused.db'),
        'Auth__Enabled': 'false', 'Auth__AllowLocalTestProvider': 'false', 'Auth__SupabaseUrl': '',
        'Auth__PublishableKey': '', 'Auth__PlatformOwnerUserId': '',
        'Stripe__CheckoutEnabled': 'false', 'Stripe__InvoicesEnabled': 'false', 'Stripe__Mode': 'sandbox',
        'ServiceBilling__CheckoutEnabled': 'false', 'ServiceBilling__LiveEnabled': 'false',
        'ServiceBilling__RestrictedKey': '', 'ServiceBilling__WebhookSecret': '', 'ServiceBilling__AllowLocalTestProvider': 'false',
        'MerchantPayments__Enabled': 'false', 'MerchantPayments__RestrictedKey': '',
        'WebPush__Enabled': 'false', 'Notifications__Mode': 'disabled',
        'Notifications__ApiKey': '', 'Media__Provider': 'disabled',
        'LOCALAPPDATA': str(content / 'local-profile'), 'APPDATA': str(content / 'roaming-profile')})
    path = RUN / (label + '.log')
    stream = path.open('w', encoding='utf-8')
    process = subprocess.Popen([str(SDK), str(DLL)], cwd=content, env=env,
        stdout=stream, stderr=subprocess.STDOUT, creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
    record = {'label': label, 'pid': process.pid, 'port': port, 'process': process,
              'stream': stream, 'log': path.name, 'started_at': time.monotonic()}
    PROCESSES.append(record)
    save()
    return record


def healthy(record):
    try:
        with HTTP.open(f'http://127.0.0.1:{record["port"]}/health', timeout=1) as response:
            return response.status == 200 and json.loads(response.read()).get('service') == 'tide-casa-api'
    except (OSError, urllib.error.URLError, json.JSONDecodeError):
        return False


def await_start(record, ready, message=None):
    process = record['process']
    deadline = time.monotonic() + 150
    is_ready = False
    while time.monotonic() < deadline and process.poll() is None:
        if healthy(record):
            is_ready = True
            break
        time.sleep(.1)
    record['startup_seconds'] = round(time.monotonic() - record['started_at'], 3)
    record['ready'] = is_ready
    record['startup_exit'] = process.poll()
    check(record['label'] + (': API healthy' if ready else ': API refuses startup'),
          is_ready and process.poll() is None if ready else not is_ready and process.poll() not in (None, 0))
    if message:
        record['stream'].flush()
        log = redact((RUN / record['log']).read_text(encoding='utf-8', errors='replace'))
        check(record['label'] + ': refusal identifies expected cause', message in log)


def stop(record):
    process = record.pop('process', None)
    if process is None:
        return
    if process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=10)
    record['exit_code'] = process.returncode
    record.pop('stream').close()
    path = RUN / record['log']
    path.write_text(redact(path.read_text(encoding='utf-8', errors='replace')), encoding='utf-8')
    save()


def run_api(label, schema, ready=True, message=None):
    record = start_api(label, schema)
    try:
        await_start(record, ready, message)
    finally:
        stop(record)


def snapshot(schema):
    # All rows in this schema are synthetic. Keep only a digest in evidence.
    with connect(schema) as db:
        tables = db.execute("SELECT tablename FROM pg_tables WHERE schemaname=current_schema() ORDER BY tablename").fetchall()
        content = []
        for (table,) in tables:
            values = db.execute(sql.SQL('SELECT * FROM {}').format(sql.Identifier(table))).fetchall()
            content.append([table, sorted(values, key=repr)])
        return hashlib.sha256(json.dumps(content, ensure_ascii=False, default=str).encode()).hexdigest()


def profile_sql():
    return """INSERT INTO tide_referral_profiles
        (id,user_id,email,name,introduction,code,terms_version,terms_accepted_at,created_at,updated_at)
        VALUES (%s,%s,%s,'Synthetic profile','Synthetic test only',%s,'test-v1','2026-09-20T00:00:00Z','2026-09-20T00:00:00Z','2026-09-20T00:00:00Z')"""


def helper_checks(schema):
    with connect(schema) as db:
        for name, value, expected in [
            ('UTC instant', '2026-09-20T14:20:31.123456Z', '2026-09-20T14:20:31.123456+00:00'),
            ('offset instant', '2026-09-20T10:20:31.123456-04:00', '2026-09-20T14:20:31.123456+00:00'),
            ('DateTimeOffset roundtrip', '2026-09-20T14:20:31.1234560+00:00', '2026-09-20T14:20:31.123456+00:00'),
            ('ISO date', '2024-02-29', '2024-02-29T00:00:00+00:00')]:
            actual = db.execute('SELECT tide_iso_instant(%s)', (value,)).fetchone()[0]
            check('Timestamp helper: ' + name, actual.isoformat() == expected)
        rejected = [None, '', 'infinity', '-infinity', 'tomorrow', 'now', '2026-02-30', '2026-13-01',
                    '0000-01-01', '2026-01-01T24:00:00Z', '2026-01-01T23:59:60Z',
                    '2026-01-01T12:00:00', '2026-01-01T12:00:00+14:01', '09/20/2026', 'not a date']
        check('Timestamp helper rejects invalid and special inputs', all(
            db.execute('SELECT tide_iso_instant(%s) IS NULL', (value,)).fetchone()[0] for value in rejected))
        check('JSON helper preserves boolean versus numeric semantics',
            db.execute("SELECT tide_json(%s)->'enabled'=to_jsonb(true), tide_json(%s)->'enabled'=to_jsonb(true), tide_json(%s)->'enabled'=to_jsonb(1)",
                       ('{"enabled":true}', '{"enabled":1}', '{"enabled":1}')).fetchone() == (True, False, True))
        check('JSON helper preserves JSON null', db.execute("SELECT tide_json('null') IS NOT NULL AND tide_json('null')='null'::jsonb").fetchone()[0])
        invalid = [None, '', '{', '{"missing":}', '1e1000000', '"\\u0000"']
        check('JSON helper safely rejects malformed or unsupported input', all(
            db.execute('SELECT tide_json(%s) IS NULL', (value,)).fetchone()[0] for value in invalid))
        check('Helpers have fixed catalog search path and no elevated privileges', db.execute(
            "SELECT count(*) FROM pg_proc WHERE pronamespace=current_schema()::regnamespace AND proname IN ('tide_json','tide_iso_instant') AND proconfig=ARRAY['search_path=pg_catalog'] AND NOT prosecdef AND proisstrict").fetchone()[0] == 2)


def constraint_checks(schema):
    modify(schema, profile_sql(), ('p-ascii', 'u-ascii', 'Owner@example.test', 'MiXeD'))
    expect_state('ASCII email duplicate is rejected', '23505', lambda: modify(schema, profile_sql(),
        ('p-email-dupe', 'u-email-dupe', 'owner@EXAMPLE.test', 'UNIQUE-1')))
    expect_state('ASCII referral code duplicate is rejected', '23505', lambda: modify(schema, profile_sql(),
        ('p-code-dupe', 'u-code-dupe', 'other@example.test', 'mixed')))
    modify(schema, profile_sql(), ('p-unicode-upper', 'u-unicode-upper', 'Ä@example.test', 'Ä'))
    modify(schema, profile_sql(), ('p-unicode-lower', 'u-unicode-lower', 'ä@example.test', 'ä'))
    check('SQLite NOCASE keeps non-ASCII case distinctions', rows(schema,
        "SELECT count(*) FROM tide_referral_profiles WHERE id IN ('p-unicode-upper','p-unicode-lower')")[0][0] == 2)
    child = "INSERT INTO tide_referral_reviews(id,profile_id,reviewer_id,status,discount_percent,created_at) VALUES(%s,%s,'synthetic-reviewer','pending',0,'2026-09-20T00:00:00Z')"
    expect_state('Foreign keys enforce immediately by default', '23503', lambda: modify(schema, child, ('r-immediate', 'p-missing')))
    with connect(schema) as db:
        with db.transaction():
            db.execute('SET CONSTRAINTS ALL DEFERRED')
            db.execute(child, ('r-deferred', 'p-deferred'))
            db.execute(profile_sql(), ('p-deferred', 'u-deferred', 'deferred@example.test', 'DEFERRED'))
            db.execute('SET CONSTRAINTS ALL IMMEDIATE')
    check('Explicitly deferred forward reference commits with its parent', rows(schema,
        "SELECT count(*) FROM tide_referral_reviews WHERE id='r-deferred' AND profile_id='p-deferred'")[0][0] == 1)
    def dangling():
        with connect(schema) as db:
            with db.transaction():
                db.execute('SET CONSTRAINTS ALL DEFERRED')
                db.execute(child, ('r-dangling', 'p-absent'))
    expect_state('Deferred unresolved reference rejects commit', '23503', dangling)
    check('Rejected deferred transaction leaves no child', rows(schema,
        "SELECT count(*) FROM tide_referral_reviews WHERE id='r-dangling'")[0][0] == 0)
    check('All 87 FKs are valid, deferrable and initially immediate', rows(schema,
        "SELECT count(*) FROM pg_constraint WHERE connamespace=current_schema()::regnamespace AND contype='f' AND condeferrable AND NOT condeferred AND convalidated")[0][0] == 87)


def main():
    schema = create_schema('baseline')
    run_api('blank-install', schema)
    tables = {row[0] for row in rows(schema, 'SELECT tablename FROM pg_tables WHERE schemaname=current_schema()')}
    check('All 77 application tables plus new migration ledger exist', tables == {t['name'] for t in MANIFEST['tables']} | {'tide_postgres_migrations'})
    ledger = rows(schema, 'SELECT * FROM tide_postgres_migrations')
    check('Ledger records embedded script and manifest checksums', len(ledger) == 1 and ledger[0][0] == 1 and
        ledger[0][1] == MANIFEST['migrations'][0]['resourceName'] and ledger[0][2] == MANIFEST['migrations'][0]['sha256'] and
        ledger[0][3] == hashlib.sha256(MANIFEST_PATH.read_bytes()).hexdigest())
    check('Legacy SQLite provenance ledgers are left empty', rows(schema,
        'SELECT (SELECT count(*) FROM tide_schema_migrations),(SELECT count(*) FROM tide_feature_migrations)')[0] == (0, 0))
    check('No auto-generated identities or sequences exist', rows(schema,
        "SELECT count(*) FROM pg_class WHERE relnamespace=current_schema()::regnamespace AND relkind='S'")[0][0] == 0)
    modify(schema, 'INSERT INTO tide_data_protection_keys VALUES (%s,%s,%s,%s)',
        ('synthetic-key-id', 'Synthetic opaque blob', 'ciphertext-placeholder-not-a-real-key', '2026-09-20T00:00:00.0000000-04:00'))
    for value in (-9223372036854775808, 9223372036854775807):
        modify(schema, 'INSERT INTO tide_feature_migrations VALUES (%s,%s,%s,%s)',
            (value, 'synthetic-explicit-' + str(value), '0' * 64, '2026-09-20T00:00:00.0000000Z'))
    check('Signed 64-bit values and explicit legacy IDs retain exact range', [r[0] for r in rows(schema,
        'SELECT version FROM tide_feature_migrations ORDER BY version')] == [-9223372036854775808, 9223372036854775807])
    before = snapshot(schema)
    run_api('unchanged-rerun', schema)
    check('Repeated startup preserves every synthetic row and migration timestamp', snapshot(schema) == before and rows(schema, 'SELECT * FROM tide_postgres_migrations') == ledger)
    helper_checks(schema)
    constraint_checks(schema)
    before = snapshot(schema)
    modify(schema, "UPDATE tide_postgres_migrations SET sha256=repeat('f',64)")
    run_api('history-drift', schema, False, 'history or immutable checksums differ')
    modify(schema, 'UPDATE tide_postgres_migrations SET sha256=%s', (ledger[0][2],))
    check('History refusal performs no application writes', snapshot(schema) == before)
    modify(schema, "ALTER TABLE tide_leads ALTER COLUMN next_action SET DEFAULT 'unexpected-default'")
    run_api('default-drift', schema, False, 'recorded PostgreSQL schema has drifted')
    modify(schema, "ALTER TABLE tide_leads ALTER COLUMN next_action SET DEFAULT ''")
    modify(schema, 'DROP INDEX demo_requests_email_time')
    modify(schema, 'CREATE INDEX demo_requests_email_time ON demo_requests(created_at,email_hash)')
    run_api('index-drift', schema, False, 'recorded PostgreSQL schema has drifted')
    modify(schema, 'DROP INDEX demo_requests_email_time')
    modify(schema, 'CREATE INDEX demo_requests_email_time ON demo_requests(email_hash,created_at)')
    original_helper = rows(schema, "SELECT pg_get_functiondef('tide_json(text)'::regprocedure)")[0][0]
    modify(schema, """CREATE OR REPLACE FUNCTION tide_json(input_text TEXT) RETURNS JSONB
        LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE SECURITY INVOKER SET search_path=pg_catalog
        AS $$ BEGIN RETURN NULL; END; $$""")
    run_api('function-drift', schema, False, 'recorded PostgreSQL schema has drifted')
    modify(schema, original_helper)
    modify(schema, 'DROP TABLE tide_data_protection_keys')
    run_api('missing-table', schema, False, "application table 'tide_data_protection_keys' is missing")

    untracked = create_schema('untracked')
    modify(untracked, 'CREATE TABLE fixture_existing(id BIGINT PRIMARY KEY)')
    modify(untracked, 'INSERT INTO fixture_existing VALUES (42)')
    run_api('untracked-schema', untracked, False, 'not empty and has no migration ledger')
    check('Untracked schema and data remain untouched', rows(untracked, 'SELECT * FROM fixture_existing') == [(42,)] and
        rows(untracked, 'SELECT count(*) FROM pg_tables WHERE schemaname=current_schema()')[0][0] == 1)

    rollback = create_schema('rollback')
    # The final ledger INSERT fails after all baseline DDL/validation ran. This is a
    # test-owned fault-injection guard, not a production trigger or code change.
    modify(rollback, """CREATE TABLE tide_postgres_migrations (
        version BIGINT NOT NULL PRIMARY KEY, resource_name TEXT COLLATE "C" NOT NULL UNIQUE,
        sha256 TEXT COLLATE "C" NOT NULL, manifest_sha256 TEXT COLLATE "C" NOT NULL,
        schema_sha256 TEXT COLLATE "C" NOT NULL, applied_at TEXT COLLATE "C" NOT NULL,
        CONSTRAINT fixture_reject_baseline CHECK(version<>1))""")
    run_api('baseline-rollback', rollback, False, 'fixture_reject_baseline')
    check('Late baseline failure rolls back all 77 tables and both helpers', rows(rollback,
        "SELECT count(*) FROM pg_tables WHERE schemaname=current_schema()")[0][0] == 1 and rows(rollback,
        'SELECT count(*) FROM tide_postgres_migrations')[0][0] == 0 and rows(rollback,
        'SELECT count(*) FROM pg_proc WHERE pronamespace=current_schema()::regnamespace')[0][0] == 0)

    locked = create_schema('lock')
    with connect(locked) as db:
        with db.transaction():
            db.execute('SELECT pg_advisory_xact_lock(hashtext(current_database()),hashtext(current_schema()))')
            record = start_api('advisory-lock', locked)
            waiting = False
            deadline = time.monotonic() + 7
            while time.monotonic() < deadline and not waiting:
                waiting = rows(locked, """SELECT count(*) FROM pg_locks WHERE locktype='advisory' AND NOT granted
                    AND classid::bigint=(hashtext(current_database())::bigint & 4294967295)
                    AND objid::bigint=(hashtext(current_schema())::bigint & 4294967295)""")[0][0] == 1
                if not waiting:
                    time.sleep(.1)
            check('Startup demonstrably waits on the exact application writer advisory lock', waiting and record['process'].poll() is None and not healthy(record) and
                rows(locked, 'SELECT count(*) FROM pg_tables WHERE schemaname=current_schema()')[0][0] == 0)
        try:
            await_start(record, True)
        finally:
            stop(record)
    check('Fixture stayed on PostgreSQL 17', rows(locked, 'SHOW server_version_num')[0][0].startswith('17'))


if __name__ == '__main__':
    failed = False
    try:
        main()
    except Exception as error:
        failed = True
        RESULTS.append({'check': 'Harness completion', 'passed': False, 'error_type': type(error).__name__,
                        'error': redact(str(error))})
        print('FAIL ' + type(error).__name__ + ': ' + redact(str(error)), flush=True)
    finally:
        for record in PROCESSES:
            stop(record)
        for schema in reversed(OWNED):
            try:
                assert re.fullmatch(re.escape(PREFIX) + r'_[a-z0-9_]+', schema)
                with connect() as db:
                    db.execute(sql.SQL('DROP SCHEMA {} CASCADE').format(sql.Identifier(schema)))
            except Exception as error:
                failed = True
                RESULTS.append({'check': 'Owned schema cleanup', 'passed': False, 'error_type': type(error).__name__})
        if not any(item['check'] == 'Owned schema cleanup' and not item['passed'] for item in RESULTS):
            with connect() as db:
                remaining = db.execute('SELECT nspname FROM pg_namespace WHERE nspname::text=ANY(%s)', (OWNED,)).fetchall()
            check('All test-owned PostgreSQL schemas removed', not remaining)
        # Remove only this verified, disposable runtime copy after all owned processes stop.
        disposable = DLL.parent.resolve()
        assert disposable == RUN.resolve() / 'api-build'
        assert RUN.parent.resolve() == (ROOT / '.tools/postgres-schema-verification').resolve()
        shutil.rmtree(disposable)
        save()
    print(json.dumps({'passed': sum(item['passed'] for item in RESULTS),
        'failed': sum(not item['passed'] for item in RESULTS), 'results': str(RUN / 'results.json')}), flush=True)
    sys.exit(1 if failed else 0)
