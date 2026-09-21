"""Exercise the compiled API's real SchemaMigrator with synthetic, isolated databases.

Build TideCasa.Api first. This does not build, import customer data, contact providers,
or deploy. Every process is started sequentially and only this script's children are
stopped. Logs, database fixtures, exit codes and results remain under .tools.
"""
from contextlib import closing
import ctypes
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import socket
import sqlite3
import subprocess
import sys
import time
import traceback
import urllib.error
import urllib.request


ROOT = Path(__file__).resolve().parents[1]
RUN = ROOT / '.tools/migration-verification' / datetime.now().strftime('%Y%m%d-%H%M%S-%f')
SDK = ROOT / '.tools/dotnet-10.0.401/dotnet.exe'
DLL = Path(os.environ.get('TIDE_TEST_BUILD_ROOT', str(ROOT))) / 'TideCasa.Api/bin/Debug/net10.0/TideCasa.Api.dll'
MIGRATIONS = ROOT / 'TideCasa.Api/Migrations'
ORIGINAL = ROOT / 'BarTide-Independent-Hosting'
BASELINE = RUN / 'baseline.db'
BACKUP = RUN / 'synthetic-backup.db'
MANIFEST_BYTES = (MIGRATIONS / 'manifest.json').read_bytes()
MANIFEST = json.loads(MANIFEST_BYTES)
MANIFEST_HASH = hashlib.sha256(MANIFEST_BYTES).hexdigest()
EXPECTED_SOURCES = [
    'drizzle/0000_optimal_shaman.sql', 'drizzle/0001_wooden_wendell_rand.sql',
    'drizzle/0002_closed_rumiko_fujikawa.sql', 'drizzle/0003_jittery_epoch.sql',
    'drizzle/0004_even_sally_floyd.sql', 'drizzle/0005_acoustic_ego.sql',
    'drizzle/0006_perfect_union_jack.sql', 'drizzle/0007_spicy_vulcan.sql',
    'drizzle/0008_yielding_elektra.sql', 'db/independent/0001_auth.sql',
    'db/independent/0002_stripe.sql', 'db/independent/0003_contact.sql',
    'db/independent/0004_maintenance.sql', 'db/independent/0005_referrals.sql',
]
TABLE_PATTERN = re.compile(r'^\s*CREATE\s+TABLE(?:\s+IF\s+NOT\s+EXISTS)?\s+[`"]?([A-Za-z_][A-Za-z0-9_]*)', re.I | re.M)
results, cases, processes = [], [], []
current_case = 'setup'
RUN.mkdir(parents=True, exist_ok=False)

# Suppress interactive Windows crash reporting only for this test process and its
# children. Expected startup failures must return an exit code without a dialog.
if os.name == 'nt':
    ctypes.windll.kernel32.SetErrorMode(0x0001 | 0x0002 | 0x8000)


def save_progress():
    safe_processes = [{key: value for key, value in item.items() if key != '_process'} for item in processes]
    (RUN / 'progress.json').write_text(json.dumps({'checks': results, 'cases': cases, 'processes': safe_processes}, indent=2), encoding='utf-8')


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, location):
        return None


# Ignore ambient proxy settings: the only HTTP target in this harness is loopback.
HTTP = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())


def check(name, condition):
    results.append({'case': current_case, 'check': name, 'passed': bool(condition)})
    save_progress()
    print(('PASS ' if condition else 'FAIL ') + name, flush=True)
    if not condition:
        raise AssertionError(name)


def query(path, statement, values=()):
    with closing(sqlite3.connect(path, timeout=10)) as db:
        return db.execute(statement, values).fetchall()


def modify(path, statement, values=()):
    with closing(sqlite3.connect(path, timeout=10)) as db:
        with db:
            db.execute(statement, values)


def snapshot(path):
    """Compare logical schema and every synthetic row, not journal/file timestamps."""
    with closing(sqlite3.connect(path, timeout=10)) as db:
        schema = db.execute('SELECT type,name,tbl_name,sql FROM sqlite_schema ORDER BY type,name').fetchall()
        rows = {}
        for (table,) in db.execute("SELECT name FROM sqlite_schema WHERE type='table' ORDER BY name"):
            quoted = '"' + table.replace('"', '""') + '"'
            rows[table] = sorted(db.execute('SELECT * FROM ' + quoted).fetchall(), key=repr)
        return {'schema': schema, 'rows': rows}


def backup(source, target):
    # SQLite's backup API includes committed WAL contents; never copy a live .db alone.
    with closing(sqlite3.connect(source, timeout=10)) as src:
        with closing(sqlite3.connect(target, timeout=10)) as dst:
            src.backup(dst)


def free_port():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        return sock.getsockname()[1]


def stop(record):
    process = record.pop('_process', None)
    if process is None:
        return
    if process.poll() is None:
        record['stopped_by_harness'] = True
        process.terminate()
        try:
            process.wait(timeout=20)
        except subprocess.TimeoutExpired:
            record['killed_after_timeout'] = True
            process.kill()
            process.wait(timeout=10)
    record['exit_code'] = process.returncode
    save_progress()


def run_api(label, path, expect_ready, while_running=None):
    if any(record.get('_process') and record['_process'].poll() is None for record in processes):
        raise RuntimeError('Sequential process invariant violated')
    port = free_port()
    content_root = RUN / (label + '-content-root')
    content_root.mkdir()
    env = os.environ.copy()
    for key in list(env):
        upper = key.upper()
        if any(word in upper for word in ('STRIPE', 'RESEND', 'SUPABASE', 'CLOUDFLARE', 'AUTH__', 'WEBPUSH__', 'NOTIFICATIONS__', 'MERCHANTPAYMENTS__', 'SERVICEBILLING__', 'MEDIA__')) or upper.startswith(('ASPNETCORE_', 'DOTNET_', 'STORAGE__')):
            env.pop(key)
    env.update({
        'DOTNET_CLI_HOME': str(ROOT / '.tools/dotnet-home'),
        'DOTNET_CLI_TELEMETRY_OPTOUT': '1', 'DOTNET_SKIP_FIRST_TIME_EXPERIENCE': '1',
        'DOTNET_NOLOGO': '1', 'ASPNETCORE_ENVIRONMENT': 'Development',
        'DOTNET_DbgEnableMiniDump': '0', 'COMPlus_DbgEnableMiniDump': '0',
        'DOTNET_EnableCrashReport': '0',
        'ASPNETCORE_URLS': f'http://127.0.0.1:{port}',
        'Storage__DatabasePath': str(path), 'Auth__Enabled': 'false',
        'Auth__AllowLocalTestProvider': 'false', 'Auth__SupabaseUrl': '',
        'Auth__PublishableKey': '', 'Auth__PlatformOwnerUserId': '',
        'Stripe__CheckoutEnabled': 'false', 'Stripe__InvoicesEnabled': 'false',
        'Stripe__Mode': 'sandbox',
        'WebPush__Enabled': 'false', 'Notifications__Mode': 'disabled',
    })
    record = {'label': label, 'database': path.name, 'expected': 'healthy' if expect_ready else 'startup refusal',
              'log': label + '.log', 'stopped_by_harness': False}
    processes.append(record)
    with (RUN / record['log']).open('w', encoding='utf-8') as log:
        process = subprocess.Popen([str(SDK), str(DLL)], cwd=content_root, env=env,
            stdout=log, stderr=subprocess.STDOUT,
            creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        record['_process'] = process
        record['pid'] = process.pid
        save_progress()
        ready = False
        started = time.monotonic()
        try:
            deadline = started + 180
            while time.monotonic() < deadline:
                if process.poll() is not None:
                    break
                try:
                    with HTTP.open(f'http://127.0.0.1:{port}/health', timeout=2) as response:
                        payload = json.loads(response.read())
                        ready = response.status == 200 and payload.get('service') == 'tide-casa-api'
                        if ready:
                            break
                except (OSError, urllib.error.URLError, json.JSONDecodeError):
                    pass
                time.sleep(.15)
            record['startup_seconds'] = round(time.monotonic() - started, 3)
            record['ready'] = ready
            record['startup_exit_code'] = process.poll()
            if expect_ready:
                check(label + ': real API startup completes', ready and process.poll() is None)
                if while_running:
                    while_running()
            else:
                check(label + ': API refuses startup with nonzero exit', not ready and process.poll() not in (None, 0))
        finally:
            stop(record)
    return (RUN / record['log']).read_text(encoding='utf-8', errors='replace')


def source_hashes():
    check('Manifest has exactly the 14 reviewed sources in order', MANIFEST['formatVersion'] == 1
          and [entry['source'] for entry in MANIFEST['migrations']] == EXPECTED_SOURCES)
    expected_tables = set()
    for version, entry in enumerate(MANIFEST['migrations'], 1):
        filename = entry['resourceName'].removeprefix('TideCasa.Api.Migrations.')
        original = (ORIGINAL / entry['source']).read_bytes()
        copied = (MIGRATIONS / filename).read_bytes()
        check('Exact original bytes and SHA256: ' + entry['source'], entry['version'] == version
              and copied == original and hashlib.sha256(copied).hexdigest() == entry['sha256'])
        expected_tables.update(TABLE_PATTERN.findall(copied.decode('utf-8-sig')))
    check('Baseline defines exactly 46 application tables', len(expected_tables) == 46)
    return expected_tables


def populate():
    stamp = '2026-09-20T00:00:00Z'
    with closing(sqlite3.connect(BASELINE, timeout=10)) as db:
        db.execute('PRAGMA foreign_keys=ON')
        with db:
            db.execute("INSERT INTO bartide_customers(id,slug,email,user_id,name,menu_json,status,enrollment_note,created_at,updated_at) VALUES (?,?,?,?,?,'{}','draft','',?,?)",
                       ('synthetic-tenant', 'synthetic-tenant', 'owner@example.invalid', 'synthetic-user', 'Synthetic business', stamp, stamp))
            db.execute('INSERT INTO fit_courses(id,tenant_id,title,created_at,updated_at) VALUES (?,?,?,?,?)',
                       ('synthetic-course', 'synthetic-tenant', 'Synthetic onboarding', stamp, stamp))
            db.execute('INSERT INTO fit_learners(id,tenant_id,name,email,user_id,created_at) VALUES (?,?,?,?,?,?)',
                       ('synthetic-learner', 'synthetic-tenant', 'Synthetic learner', 'learner@example.invalid', 'synthetic-learner-user', stamp))
            db.execute('INSERT INTO fit_lessons(id,tenant_id,course_id,title,video_kind,video_source,created_at,updated_at) VALUES (?,?,?,?,?,?,?,?)',
                       ('synthetic-lesson', 'synthetic-tenant', 'synthetic-course', 'Synthetic lesson', 'external', 'https://example.invalid/video', stamp, stamp))
            db.execute('INSERT INTO fit_progress(id,tenant_id,learner_id,lesson_id,completed,updated_at) VALUES (?,?,?,?,1,?)',
                       ('synthetic-progress', 'synthetic-tenant', 'synthetic-learner', 'synthetic-lesson', stamp))
            db.execute('INSERT INTO fit_videos(id,tenant_id,object_key,name,byte_size,status,created_at) VALUES (?,?,?,?,?,?,?)',
                       ('synthetic-video', 'synthetic-tenant', 'synthetic-only/video.mp4', 'Synthetic object reference', 12, 'ready', stamp))
            db.execute('INSERT INTO bartide_auth_identities(provider_user_id,app_user_id,verified_email,created_at) VALUES (?,?,?,?)',
                       ('synthetic-provider', 'synthetic-user', 'owner@example.invalid', stamp))
            db.execute('INSERT INTO bartide_auth_sessions(token_hash,provider_user_id,expires_at) VALUES (?,?,?)',
                       (hashlib.sha256(b'not-a-real-token').hexdigest(), 'synthetic-provider', 1))
            db.execute("INSERT INTO demo_requests(id,payload_hash,email_hash,request_json,created_at) VALUES ('synthetic-demo','synthetic-hash','synthetic-email-hash','{}',?)", (stamp,))


def fresh_and_idempotent(expected_tables):
    run_api('fresh', BASELINE, True)
    tables = {name for (name,) in query(BASELINE, "SELECT name FROM sqlite_schema WHERE type='table' AND name NOT GLOB 'sqlite_*'")}
    feature_scripts = sorted((ROOT / 'TideCasa.Api/FeatureMigrations').glob('*.sql'))
    feature_tables = set()
    for source in feature_scripts:
        feature_tables.update(TABLE_PATTERN.findall(source.read_text(encoding='utf-8-sig')))
    check('Real migrator preserves all 46 baseline tables and adds only reviewed feature tables and history',
          tables == expected_tables | feature_tables | {'tide_schema_migrations', 'tide_feature_migrations', 'demo_requests'})
    feature_history = query(BASELINE, 'SELECT version,resource_name,sha256 FROM tide_feature_migrations ORDER BY version')
    check('Feature history matches the exact ordered source checksums', feature_history == [
        (index, 'TideCasa.Api.FeatureMigrations.' + source.name, hashlib.sha256(source.read_bytes()).hexdigest())
        for index, source in enumerate(feature_scripts, 1)])
    history = query(BASELINE, 'SELECT version,resource_name,source_path,sha256,manifest_sha256 FROM tide_schema_migrations ORDER BY version')
    expected = [(entry['version'], entry['resourceName'], entry['source'], entry['sha256'], MANIFEST_HASH) for entry in MANIFEST['migrations']]
    check('Compiled embedded resources record every exact source and manifest checksum', history == expected)
    check('Fresh baseline has no foreign-key violations', query(BASELINE, 'PRAGMA foreign_key_check') == [])
    populate()
    before = snapshot(BASELINE)
    run_api('idempotent-first-restart', BASELINE, True)
    check('First restart preserves all schema, migration timestamps and synthetic rows', snapshot(BASELINE) == before)
    run_api('idempotent-second-restart', BASELINE, True)
    check('Second restart remains exactly idempotent', snapshot(BASELINE) == before)


def refusal(label, mutate, diagnostic, copy_baseline=True):
    path = RUN / (label + '.db')
    if copy_baseline:
        backup(BASELINE, path)
    mutate(path)
    before = snapshot(path)
    log = run_api(label, path, False)
    check(label + ': expected migrator reason is recorded', diagnostic in log)
    check(label + ': original schema and rows remain unchanged', snapshot(path) == before)


def unknown_table(path):
    modify(path, 'CREATE TABLE unknown_legacy(id TEXT PRIMARY KEY, note TEXT NOT NULL)')
    modify(path, 'INSERT INTO unknown_legacy VALUES (?,?)', ('sentinel', 'synthetic original state'))


def late_failure():
    path = RUN / 'late-migration-collision.db'
    # Only demo_requests is an allowed preexisting table. A view collides with the
    # fourteenth script after all earlier scripts have executed in its transaction.
    demo_ddl = query(BASELINE, "SELECT sql FROM sqlite_schema WHERE name='demo_requests'")[0][0]
    modify(path, demo_ddl)
    modify(path, "INSERT INTO demo_requests(id,payload_hash,email_hash,request_json,created_at) VALUES ('sentinel','synthetic','synthetic','{}','2026-09-20')")
    modify(path, "CREATE VIEW tide_referral_profiles AS SELECT 'synthetic-original-view' AS id")
    before = snapshot(path)
    log = run_api('late-migration-collision', path, False)
    check('Late failure identifies the fourteenth-script view collision', 'view' in log.lower() and ('tide_referral_profiles' in log or 'views may not be indexed' in log.lower()))
    check('Late failure rolls back all earlier schema and history while preserving original view/demo row', snapshot(path) == before)
    check('Failed baseline leaves neither application tables nor migration history',
          query(path, "SELECT name FROM sqlite_schema WHERE type='table' AND name NOT GLOB 'sqlite_*'") == [('demo_requests',)])
    # Repair only this disposable fixture; then prove the failed attempt left it reusable.
    modify(path, 'DROP VIEW tide_referral_profiles')
    run_api('late-failure-retry', path, True)
    check('Clean retry commits all 14 migrations once', query(path, 'SELECT COUNT(*) FROM tide_schema_migrations') == [(14,)])
    check('Existing demo request survives successful baseline installation', query(path, 'SELECT id FROM demo_requests') == [('sentinel',)])


def backup_restore():
    before = snapshot(BASELINE)
    run_api('online-backup-source', BASELINE, True, lambda: backup(BASELINE, BACKUP))
    check('SQLite online backup preserves complete populated database', snapshot(BACKUP) == before)
    # Diverge a disposable working copy, then restore from the backup using SQLite.
    restored = RUN / 'restored.db'
    backup(BASELINE, restored)
    modify(restored, "UPDATE bartide_customers SET name='synthetic changed after backup'")
    modify(restored, 'DELETE FROM fit_progress')
    check('Restore fixture actually diverges from the backup', snapshot(restored) != before)
    backup(BACKUP, restored)
    check('SQLite restore recovers schema, history and every synthetic row', snapshot(restored) == before)
    check('Restored database passes SQLite integrity check', query(restored, 'PRAGMA integrity_check') == [('ok',)])
    check('Restored relationship graph passes foreign-key check', query(restored, 'PRAGMA foreign_key_check') == [])
    joined = query(restored, '''SELECT t.id,c.id,l.id,u.id,p.completed,v.object_key
        FROM bartide_customers t JOIN fit_courses c ON c.tenant_id=t.id
        JOIN fit_lessons l ON l.course_id=c.id AND l.tenant_id=t.id
        JOIN fit_progress p ON p.lesson_id=l.id AND p.tenant_id=t.id
        JOIN fit_learners u ON u.id=p.learner_id AND u.tenant_id=t.id
        JOIN fit_videos v ON v.tenant_id=t.id''')
    check('Tenant/course/lesson/learner/progress/object-reference relationships survive restore',
          joined == [('synthetic-tenant', 'synthetic-course', 'synthetic-lesson', 'synthetic-learner', 1, 'synthetic-only/video.mp4')])
    run_api('restored-restart', restored, True)
    check('Restored populated database starts with exact idempotent state', snapshot(restored) == before)


def run_case(name, action):
    global current_case
    current_case = name
    started = time.monotonic()
    record = {'case': name}
    try:
        action()
        record['passed'] = True
    except Exception as error:
        record['passed'] = False
        record['error_type'] = type(error).__name__
        record['error'] = str(error)
        (RUN / (name + '-failure.txt')).write_text(traceback.format_exc(), encoding='utf-8')
        print('CASE FAILED ' + name + ': ' + str(error), flush=True)
    record['seconds'] = round(time.monotonic() - started, 3)
    cases.append(record)


try:
    check('Portable SDK and previously compiled API exist', SDK.is_file() and DLL.is_file())
    expected_tables = source_hashes()
    run_case('fresh-and-idempotent', lambda: fresh_and_idempotent(expected_tables))
    if not cases[-1]['passed']:
        raise RuntimeError('Baseline prerequisite failed; dependent fixtures are not meaningful')
    run_case('unknown-table', lambda: refusal('unknown-table', unknown_table, 'have no migration history', False))
    run_case('untracked-legacy', lambda: refusal('untracked-legacy', lambda p: modify(p, 'DROP TABLE tide_schema_migrations'), 'have no migration history'))
    run_case('empty-history', lambda: refusal('empty-history', lambda p: modify(p, 'DELETE FROM tide_schema_migrations'), 'have no recorded baseline'))
    run_case('partial-history', lambda: refusal('partial-history', lambda p: modify(p, 'DELETE FROM tide_schema_migrations WHERE version=14'), 'baseline is incomplete or unknown'))
    run_case('unknown-history-version', lambda: refusal('unknown-history-version', lambda p: modify(p, 'UPDATE tide_schema_migrations SET version=99 WHERE version=14'), 'checksums differ'))
    run_case('script-checksum-drift', lambda: refusal('script-checksum-drift', lambda p: modify(p, "UPDATE tide_schema_migrations SET sha256=? WHERE version=7", ('0' * 64,)), 'checksums differ'))
    run_case('manifest-checksum-drift', lambda: refusal('manifest-checksum-drift', lambda p: modify(p, "UPDATE tide_schema_migrations SET manifest_sha256=? WHERE version=1", ('0' * 64,)), 'checksums differ'))
    run_case('missing-table', lambda: refusal('missing-table', lambda p: modify(p, 'DROP TABLE tide_referral_reviews'), 'recorded application table is missing'))
    run_case('feature-checksum-drift', lambda: refusal('feature-checksum-drift', lambda p: modify(p, "UPDATE tide_feature_migrations SET sha256=? WHERE version=1", ('0' * 64,)), 'feature migration differs'))
    run_case('missing-feature-table', lambda: refusal('missing-feature-table', lambda p: modify(p, 'DROP TABLE tide_staff_lesson_progress'), 'recorded feature table is missing'))
    run_case('feature-version-gap', lambda: refusal('feature-version-gap', lambda p: modify(p, 'UPDATE tide_feature_migrations SET version=99 WHERE version=1'), 'feature migration differs'))
    run_case('foreign-key-orphan', lambda: refusal('foreign-key-orphan', lambda p: modify(p,
        "INSERT INTO fit_courses(id,tenant_id,title,created_at,updated_at) VALUES ('synthetic-orphan','missing-synthetic-tenant','Synthetic orphan','2026-09-20','2026-09-20')"), 'failed its foreign-key check'))
    run_case('late-transaction-rollback', late_failure)
    run_case('populated-backup-restore', backup_restore)
finally:
    for record in processes:
        stop(record)
    report = {'created_at_utc': datetime.now(timezone.utc).isoformat(), 'compiled_api': str(DLL),
              'compiled_api_sha256': hashlib.sha256(DLL.read_bytes()).hexdigest() if DLL.is_file() else None,
              'isolation': 'Synthetic databases only; auth and Stripe disabled; no provider calls or deployment.',
              'checks': results, 'cases': cases, 'processes': processes,
              'passed': bool(cases) and all(item['passed'] for item in results + cases),
              'limitations': ['Exercises the installed schema baseline, not a production data import.',
                              'Backup retains synthetic object references; it does not copy or verify R2 object contents.',
                              'Known startup refusal cases retain their deliberately damaged synthetic fixtures.']}
    (RUN / 'results.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print('Verification artifacts: ' + str(RUN), flush=True)

print(f"{sum(item['passed'] for item in results)}/{len(results)} checks and {sum(item['passed'] for item in cases)}/{len(cases)} cases passed.", flush=True)
sys.exit(0 if report['passed'] else 1)
