"""Rehearse isolated Release recovery and measure a bounded Windows HTTP workload.

All providers are disabled. Inputs remain read-only; outputs use a unique synthetic
run directory. This does not demonstrate Linux, a memory limit, or Blazor circuits.
"""
from concurrent.futures import ThreadPoolExecutor
from contextlib import closing
import argparse
import ctypes
from ctypes import wintypes
from datetime import datetime, timezone
import hashlib
from html.parser import HTMLParser
import json
import os
from pathlib import Path
import platform
import shutil
import socket
import sqlite3
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request


ROOT = Path(__file__).resolve().parents[1]
SDK = ROOT / '.tools/dotnet-10.0.401/dotnet.exe'
OUTPUT_ROOT = ROOT / '.tools/release-recovery-verification'
PROCESSES, LOGS = [], []
RESULT = {'completed': False, 'checks': [], 'commands': [], 'limits': [
    'Windows portable Release processes; no Linux/container execution or hard memory/CPU limit.',
    'DOTNET_PROCESSOR_COUNT=1 is runtime configuration, not a host CPU quota or affinity cap.',
    'HTTP requests render static server pages; no SignalR circuits or authenticated owner workloads.',
    'Web uses an unconnected container API address; API and Web are tested independently.',
    'HTTPS forwarding is simulated only on loopback; no TLS/network/provider proof.',
    'Synthetic SQLite and DataProtection recovery only; no external object-store recovery.',
    'Key recovery preserves antiforgery decryption; in-memory login tickets intentionally do not survive restart.'
]}


def check(label, condition):
    RESULT['checks'].append({'check': label, 'passed': bool(condition)})
    if not condition:
        raise AssertionError(label)
    print('PASS ' + label, flush=True)


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def database_state(path):
    """Compare all rows, schema, migration journals and application history independently."""
    with closing(sqlite3.connect(path.resolve().as_uri() + '?mode=ro', uri=True)) as db:
        schema = db.execute('SELECT type,name,tbl_name,sql FROM sqlite_master ORDER BY type,name').fetchall()
        tables = {}
        for (name,) in db.execute("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name"):
            # SQLite identifiers come from the database itself; quote rather than interpolate raw names.
            identifier = '"' + name.replace('"', '""') + '"'
            rows = db.execute('SELECT * FROM ' + identifier).fetchall()
            encoded = sorted(json.dumps(row, ensure_ascii=True, separators=(',', ':'),
                default=lambda value: {'bytesHex': value.hex()}) for row in rows)
            tables[name] = {'rows': len(rows), 'sha256': hashlib.sha256('\n'.join(encoded).encode()).hexdigest()}
        return {'schemaSha256': hashlib.sha256(json.dumps(schema).encode()).hexdigest(), 'tables': tables,
                'integrity': db.execute('PRAGMA integrity_check').fetchall(),
                'foreignKeys': db.execute('PRAGMA foreign_key_check').fetchall(),
                'featureMigrations': db.execute('SELECT version,resource_name,sha256,applied_at FROM tide_feature_migrations ORDER BY version').fetchall()}


def demo_text_preferences(path):
    with closing(sqlite3.connect(path.resolve().as_uri() + '?mode=ro', uri=True)) as db:
        rows = db.execute('SELECT id,request_json FROM demo_requests ORDER BY id').fetchall()
    preferences = []
    for ident, payload in rows:
        item = {key.casefold(): value for key, value in json.loads(payload).items()}
        preferences.append((ident, item.get('cantext', False), item.get('phone', '')))
    return preferences


def backup(source, destination):
    args = [sys.executable, str(ROOT / 'deploy/digitalocean/backup-sqlite.py'), str(source), str(destination)]
    started = time.monotonic()
    process = subprocess.run(args, cwd=RUN, capture_output=True, text=True, timeout=60,
                             creationflags=subprocess.CREATE_NO_WINDOW)
    RESULT['commands'].append({'operation': 'consistent SQLite backup to a new path', 'exitCode': process.returncode,
        'seconds': round(time.monotonic() - started, 3), 'source': str(source), 'destination': str(destination)})
    check('SQLite backup helper succeeds: ' + destination.name, process.returncode == 0)


def port():
    with socket.socket() as connection:
        connection.bind(('127.0.0.1', 0))
        return connection.getsockname()[1]


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args):
        return None


def request(base, route, data=None, secure=False, cookie=None, form=False):
    headers = {}
    if secure:
        headers['X-Forwarded-Proto'] = 'https'
    if data is not None:
        headers['Content-Type'] = 'application/x-www-form-urlencoded' if form else 'application/json'
        data = urllib.parse.urlencode(data).encode() if form else json.dumps(data).encode()
    if form:
        headers['Origin'] = base.replace('http://', 'https://')
    if cookie:
        # These are synthetic loopback cookies only. Forwarded HTTPS is not a TLS test.
        headers['Cookie'] = cookie
    started = time.perf_counter()
    try:
        response = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect()).open(
            urllib.request.Request(base + route, data=data, headers=headers), timeout=10)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        body = response.read().decode('utf-8')
        return response.status, body, response.headers, (time.perf_counter() - started) * 1000


def launch(project, base, extra):
    # Whitelist OS necessities; do not inherit provider credentials or application configuration.
    allowed = {'PATH', 'SYSTEMROOT', 'WINDIR', 'SYSTEMDRIVE', 'COMSPEC', 'TEMP', 'TMP', 'USERPROFILE',
               'APPDATA', 'LOCALAPPDATA', 'PROGRAMDATA', 'PROGRAMFILES', 'PROGRAMFILES(X86)',
               'HOMEDRIVE', 'HOMEPATH', 'OS', 'PROCESSOR_ARCHITECTURE'}
    env = {name: value for name, value in os.environ.items() if name.upper() in allowed}
    env.update({'ASPNETCORE_ENVIRONMENT': 'Production', 'DOTNET_ENVIRONMENT': 'Production',
        'ASPNETCORE_URLS': base, 'DOTNET_PROCESSOR_COUNT': '1', 'DOTNET_CLI_TELEMETRY_OPTOUT': '1',
        'DOTNET_SKIP_FIRST_TIME_EXPERIENCE': '1', 'Auth__Enabled': 'false', 'Auth__AllowLocalTestProvider': 'false',
        'Notifications__Mode': 'disabled', 'WebPush__Enabled': 'false', 'Media__Provider': 'disabled',
        'Media__AllowLocalStore': 'false', 'Stripe__CheckoutEnabled': 'false', 'Stripe__InvoicesEnabled': 'false',
        'ServiceBilling__LiveEnabled': 'false', 'ServiceBilling__CheckoutEnabled': 'false',
        'MerchantPayments__CheckoutEnabled': 'false', 'MerchantPayments__OnboardingEnabled': 'false', **extra})
    folder = PACKAGE / 'publish' / project
    log_path = RUN / (project + '-' + str(len(PROCESSES)) + '.log')
    log = log_path.open('w', encoding='utf-8'); LOGS.append(log)
    process = subprocess.Popen([str(SDK), str(folder / (project + '.dll'))], cwd=folder, env=env,
        stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW)
    PROCESSES.append(process)
    deadline = time.monotonic() + 60
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise RuntimeError(project + ' stopped; inspect its isolated startup log')
        try:
            if request(base, '/health')[0] == 200:
                check(project + ' starts in Production with isolated data',
                      'Hosting environment: Production' in log_path.read_text(encoding='utf-8'))
                return process
        except (OSError, urllib.error.URLError):
            pass
        time.sleep(.2)
    raise TimeoutError(project)


def stop(process):
    if process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            process.kill(); process.wait(timeout=10)


class FormToken(HTMLParser):
    def __init__(self):
        super().__init__()
        self.tokens = []

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if tag == 'input' and attrs.get('name') == '__RequestVerificationToken':
            self.tokens.append(attrs['value'])


def key_hashes(path):
    return {item.name: sha(item) for item in path.glob('key-*.xml')}


class MemoryCounters(ctypes.Structure):
    _fields_ = [('cb', wintypes.DWORD), ('PageFaultCount', wintypes.DWORD)] + [
        (name, ctypes.c_size_t) for name in ('PeakWorkingSetSize', 'WorkingSetSize', 'QuotaPeakPagedPoolUsage',
        'QuotaPagedPoolUsage', 'QuotaPeakNonPagedPoolUsage', 'QuotaNonPagedPoolUsage',
        'PagefileUsage', 'PeakPagefileUsage', 'PrivateUsage')]


def memory(pid):
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    psapi = ctypes.WinDLL('psapi', use_last_error=True)
    kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    psapi.GetProcessMemoryInfo.argtypes = [wintypes.HANDLE, ctypes.POINTER(MemoryCounters), wintypes.DWORD]
    psapi.GetProcessMemoryInfo.restype = wintypes.BOOL
    handle = kernel.OpenProcess(0x0400 | 0x0010, False, pid)
    if not handle:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        counters = MemoryCounters()
        counters.cb = ctypes.sizeof(counters)
        if not psapi.GetProcessMemoryInfo(handle, ctypes.byref(counters), counters.cb):
            raise ctypes.WinError(ctypes.get_last_error())
        return {'workingSetBytes': counters.WorkingSetSize, 'privateBytes': counters.PrivateUsage,
                'peakWorkingSetBytes': counters.PeakWorkingSetSize}
    finally:
        kernel.CloseHandle(handle)


def measure_workload(api_process, web_process, api_base, web_base, quote):
    endpoints = [
        ('api-health', api_base, '/health', None, False, 200),
        ('api-menu', api_base, '/api/v1/restaurants/bistro/menu', None, False, 200),
        ('api-quote', api_base, '/api/v1/restaurants/bistro/quote', quote, False, 200),
        ('api-private-sales', api_base, '/api/v1/owner/sales', None, False, 401),
        ('api-private-launch', api_base, '/api/v1/owner/launch-review', None, False, 401),
        ('web-restaurant', web_base, '/restaurant', None, True, 200),
        ('web-enhanced-demo', web_base, '/enhanced-demo', None, True, 200),
        ('web-signin', web_base, '/signin', None, True, 200),
        ('web-sales-css', web_base, '/sales-pipeline.css', None, True, 200),
        ('web-private-sales', web_base, '/owner/sales', None, True, 302),
    ]
    samples, responses, sampling_errors = [], [], []
    done = threading.Event()
    began = time.monotonic()

    def sample():
        while not done.is_set():
            try:
                samples.append({'elapsedSeconds': round(time.monotonic() - began, 3),
                    'api': memory(api_process.pid), 'web': memory(web_process.pid)})
            except Exception as error:
                sampling_errors.append(type(error).__name__)
                return
            done.wait(.1)

    def invoke(item):
        label, base, route, data, secure, expected = item
        status, _, _, elapsed = request(base, route, data, secure)
        return {'endpoint': label, 'status': status, 'expected': expected, 'elapsedMs': round(elapsed, 2)}

    sampler = threading.Thread(target=sample, daemon=True)
    sampler.start()
    try:
        with ThreadPoolExecutor(max_workers=2) as pool:
            for _ in range(30):
                responses.extend(pool.map(invoke, endpoints))
                time.sleep(.5)
        done.wait(3)
    finally:
        done.set(); sampler.join(timeout=5)
    check('Memory sampler completes without errors', not sampling_errors and bool(samples) and not sampler.is_alive())
    check('All 300 bounded workload responses match expected public/private behavior',
          len(responses) == 300 and all(item['status'] == item['expected'] for item in responses))
    workload = {'requests': len(responses), 'rounds': 30, 'httpWorkers': 2, 'sampleIntervalMs': 100,
        'seconds': round(time.monotonic() - began, 3), 'samples': samples, 'responses': responses,
        'baseline': samples[0], 'final': samples[-1]}
    workload['peak'] = {name: {metric: max(sample[name][metric] for sample in samples)
        for metric in ('workingSetBytes', 'privateBytes', 'peakWorkingSetBytes')} for name in ('api', 'web')}
    workload['combinedPeakSampledPrivateBytes'] = max(sample['api']['privateBytes'] + sample['web']['privateBytes'] for sample in samples)
    workload['combinedPeakSampledWorkingSetBytes'] = max(sample['api']['workingSetBytes'] + sample['web']['workingSetBytes'] for sample in samples)
    (RUN / 'workload-memory.json').write_text(json.dumps(workload, indent=2), encoding='utf-8')
    RESULT['memorySummary'] = {key: value for key, value in workload.items() if key not in ('samples', 'responses')}


def main():
    check('Windows memory API available', os.name == 'nt')
    RESULT['runtime'] = {'os': platform.platform(), 'processorCountSetting': 1,
                         'package': str(PACKAGE), 'fixture': str(FIXTURE)}
    prior = json.loads((PACKAGE / 'results.json').read_text(encoding='utf-8'))
    check('Input Release package previously completed verification', prior['completed'])
    expected = json.loads((PACKAGE / 'source-hashes.json').read_text(encoding='utf-8'))
    RESULT['baselineSourceManifestSha256'] = sha(PACKAGE / 'source-hashes.json')
    RESULT['currentSourceDifferencesFromBaseline'] = [path for path, value in expected.items()
        if not (ROOT / path).is_file() or sha(ROOT / path) != value]
    # The integration owner may improve current source concurrently. Measurements
    # describe this immutable published baseline, never those later changes.
    binary_hashes = {name: sha(PACKAGE / 'publish' / name / (name + '.dll')) for name in ('TideCasa.Api', 'TideCasa.Blazor')}
    check('Release binaries match recorded publish hashes', all(binary_hashes[name] == prior['packages'][name]['assemblySha256'] for name in binary_hashes))
    RESULT['assemblySha256'] = binary_hashes
    fixture_hash = sha(FIXTURE)
    baseline = database_state(FIXTURE)
    text_preferences = demo_text_preferences(FIXTURE)
    if ARGS.require_demo_text_fixture:
        check('Recovery fixture includes opted-in and non-opted-in demo requests',
              any(opted and phone for _, opted, phone in text_preferences)
              and any(not opted for _, opted, _ in text_preferences))
    check('Synthetic source has sound integrity and foreign keys', baseline['integrity'] == [('ok',)] and baseline['foreignKeys'] == [])
    check('Synthetic source exercises sales and history recovery', all(baseline['tables'][name]['rows'] > 0
        for name in ('bartide_customers', 'tide_leads', 'tide_sales_history', 'tide_sales_operations', 'demo_requests', 'tide_sales_demo_links')))
    check('Synthetic source has all nine migration records', [row[0] for row in baseline['featureMigrations']] == list(range(1, 10)))
    snapshot, restored = RUN / 'snapshot.db', RUN / 'restored.db'
    backup(FIXTURE, snapshot)
    check('Backup preserves every table, schema, migration record and history row', database_state(snapshot) == baseline)
    backup(snapshot, restored)
    check('New-path restore preserves every table, schema, migration record and history row', database_state(restored) == baseline)
    RESULT['database'] = {'sourceSha256': fixture_hash, 'bytes': FIXTURE.stat().st_size, **baseline}
    api_base = 'http://127.0.0.1:' + str(port())
    web_base = 'http://127.0.0.1:' + str(port())
    api = launch('TideCasa.Api', api_base, {'Storage__DatabasePath': str(restored)})
    check('Production API startup preserves restored data and migration journal', database_state(restored) == baseline)
    check('Recovered demo text preferences remain attached to their exact request phone',
          demo_text_preferences(restored) == text_preferences)
    menu = request(api_base, '/api/v1/restaurants/bistro/menu')
    menu_json = json.loads(menu[1])
    check('Restored public synthetic menu is usable', menu[0] == 200 and menu_json['slug'] == 'bistro'
        and len(menu_json['items']) >= 20 and any(item['id'] == 'dish-1' and item['priceCents'] == 1001 for item in menu_json['items']))
    check('Public menu omits private fixture fields', 'private_test_marker' not in menu[1] and 'privateNote' not in menu[1])
    quote = {'items': [{'itemId': 'dish-1', 'quantity': 1}], 'fulfillment': 'pickup', 'paymentMethod': 'staff'}
    quoted = request(api_base, '/api/v1/restaurants/bistro/quote', quote)
    price = json.loads(quoted[1])
    check('Restored quote calculates the saved menu price and tax', quoted[0] == 200 and price['subtotalCents'] == 1001
          and price['taxCents'] == 70 and price['totalCents'] == 1071 and price['canSubmit'])
    for route in ('/api/v1/owner/sales', '/api/v1/owner/launch-review', '/api/v1/tenants/bistro/ordering'):
        reply = request(api_base, route)
        check('Restored API keeps anonymous private route inaccessible: ' + route,
              reply[0] == 401 and 'no-store' in reply[2].get('Cache-Control', ''))
    check('Restored API rejects anonymous sales mutation', request(api_base, '/api/v1/owner/sales', {})[0] == 401)
    active_keys, saved_keys, restored_keys = RUN / 'synthetic-keys-original', RUN / 'synthetic-keys-backup', RUN / 'synthetic-keys-restored'
    web_settings = {'Api__BaseUrl': 'http://api:8080/', 'DataProtection__KeysPath': str(active_keys)}
    web = launch('TideCasa.Blazor', web_base, web_settings)
    signin = request(web_base, '/signin', secure=True)
    parser = FormToken(); parser.feed(signin[1])
    cookies = [value.split(';', 1)[0] for value in signin[2].get_all('Set-Cookie', []) if '.AspNetCore.Antiforgery.' in value]
    check('Synthetic production form creates antiforgery token, cookie and key ring',
          signin[0] == 200 and len(parser.tokens) == 1 and len(cookies) == 1 and bool(key_hashes(active_keys)))
    fields = {'__RequestVerificationToken': parser.tokens[0], 'return_to': '/account'}
    original_signout = request(web_base, '/auth/session/signout', fields, True, cookies[0], True)
    check('Synthetic form token works before recovery', original_signout[0] == 302
        and original_signout[2].get('Location') == '/signin?notice=signed-out')
    stop(web)
    shutil.copytree(active_keys, saved_keys)
    shutil.copytree(saved_keys, restored_keys)
    check('Key backup and new-path restore preserve exact synthetic bytes', key_hashes(active_keys) == key_hashes(saved_keys) == key_hashes(restored_keys))
    web = launch('TideCasa.Blazor', web_base, {**web_settings, 'DataProtection__KeysPath': str(restored_keys)})
    recovered_signout = request(web_base, '/auth/session/signout', fields, True, cookies[0], True)
    check('Recovered key ring decrypts pre-restart antiforgery form', recovered_signout[0] == 302
        and recovered_signout[2].get('Location') == '/signin?notice=signed-out')
    private = request(web_base, '/owner/sales', secure=True)
    check('Restored frontend keeps owner sales private', private[0] == 302 and '/signin' in private[2].get('Location', '')
          and 'no-store' in private[2].get('Cache-Control', ''))
    RESULT['keyRecovery'] = {'keyCount': len(key_hashes(restored_keys)), 'preRestartAntiforgeryAccepted': True,
                             'loginTicketPersistence': 'Not claimed: ServerTicketStore is in-memory and restart ends sessions.'}
    measure_workload(api, web, api_base, web_base, quote)
    check('Read-only workload preserves all restored application data and histories', database_state(restored) == baseline)
    check('Disabled notification worker makes no delivery attempts', baseline['tables']['tide_demo_notifications'] == database_state(restored)['tables']['tide_demo_notifications'])
    stop(web)
    web = launch('TideCasa.Blazor', web_base, {**web_settings, 'DataProtection__KeysPath': str(RUN / 'synthetic-keys-empty-control')})
    lost_keys = request(web_base, '/auth/session/signout', fields, True, cookies[0], True)
    check('Negative control rejects old antiforgery token without restored keys', lost_keys[0] == 400 and 'form has expired' in lost_keys[1])
    stop(web); stop(api)
    backup(restored, RUN / 'post-run-snapshot.db')
    check('Post-run consistent backup still matches original fixture', database_state(RUN / 'post-run-snapshot.db') == baseline)
    api = launch('TideCasa.Api', api_base, {'Storage__DatabasePath': str(RUN / 'post-run-snapshot.db')})
    check('Second recovered API restart serves the same public menu', request(api_base, '/api/v1/restaurants/bistro/menu')[1] == menu[1])
    check('Repeated recovered startup preserves all data and migration checksums', database_state(RUN / 'post-run-snapshot.db') == baseline)
    check('Repeated recovery preserves opted-in and default email scheduling choices',
          demo_text_preferences(RUN / 'post-run-snapshot.db') == text_preferences)
    check('Original fixture input remains byte-for-byte unchanged', sha(FIXTURE) == fixture_hash)
    check('Release assemblies remain unchanged', all(sha(PACKAGE / 'publish' / name / (name + '.dll')) == value for name, value in binary_hashes.items()))
    check('Explicit paths prevent package App_Data creation', all(not (PACKAGE / 'publish' / name / 'App_Data').exists() for name in binary_hashes))
    RESULT['completed'] = True


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--package', type=Path, required=True, help='Verified release run directory containing publish and results.json.')
    parser.add_argument('--fixture', type=Path, required=True, help='Completed synthetic sales verifier database; never the normal preview database.')
    parser.add_argument('--require-demo-text-fixture', action='store_true', help='Require both texting and email-only requests in the recovery fixture.')
    ARGS = parser.parse_args()
    PACKAGE, FIXTURE = ARGS.package.resolve(strict=True), ARGS.fixture.resolve(strict=True)
    if not PACKAGE.is_relative_to(ROOT / '.tools/release-verification'):
        parser.error('Package must be an existing local release verification run.')
    if not FIXTURE.is_relative_to(ROOT / '.tools/sales-pipeline-verification') or FIXTURE.name != 'synthetic.db':
        parser.error('Fixture must be a synthetic sales verification database.')
    if not json.loads((FIXTURE.parent / 'completion.json').read_text(encoding='utf-8'))['completed']:
        parser.error('The source synthetic sales run must have completed.')
    RUN = OUTPUT_ROOT / datetime.now(timezone.utc).strftime('%Y%m%d-%H%M%S-%f')
    RUN.mkdir(parents=True, exist_ok=False)
    try:
        main()
    except BaseException as error:
        RESULT['errorType'] = type(error).__name__
        raise
    finally:
        for process in reversed(PROCESSES):
            stop(process)
        for log in LOGS:
            log.close()
        RESULT['processesStopped'] = all(process.poll() is not None for process in PROCESSES)
        (RUN / 'results.json').write_text(json.dumps(RESULT, indent=2), encoding='utf-8')
        print('Recovery evidence: ' + str(RUN), flush=True)
