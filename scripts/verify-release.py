"""Publish current source and smoke-test isolated Production-mode processes on Windows.

Uses only cached dependencies. Does not deploy, enable providers or test Linux/TLS.
"""
from contextlib import closing
from datetime import datetime, timezone
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import socket
import sqlite3
import subprocess
import sys
import time
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
SDK = ROOT / '.tools/dotnet-10.0.401/dotnet.exe'
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--existing-package', type=Path, help='Retry smoke checks on an unchanged, already published source manifest.')
args = parser.parse_args()
RUN = args.existing_package.resolve(strict=True) if args.existing_package else ROOT / '.tools/release-verification' / datetime.now(timezone.utc).strftime('%Y%m%d-%H%M%S-%f')
assert RUN.is_relative_to((ROOT / '.tools/release-verification').resolve())
RUN.mkdir(parents=True, exist_ok=bool(args.existing_package))
# Reuse established isolated caches to avoid another full native-runtime copy.
ARTIFACTS = ROOT / '.tools/release-verification/20260920-215242-401180/artifacts'
RESULT = {'completed': False, 'checks': [], 'commands': [], 'limits': 'Windows portable Release only; no Linux/container/TLS/capacity/provider delivery or dependency vulnerability audit.'}
PROCESSES, LOGS = [], []


def source_hashes():
    files = [ROOT / 'global.json', ROOT / 'TideCasa.slnx']
    for directory in ['TideCasa.Api', 'TideCasa.Blazor', 'TideCasa.Contracts', 'TideCasa.Domain', 'deploy']:
        files += [p for p in (ROOT / directory).rglob('*') if p.is_file() and not {'bin', 'obj', 'App_Data', '__pycache__'} & set(p.relative_to(ROOT / directory).parts)]
    return {str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(files)}


def check(label, condition):
    RESULT['checks'].append({'check': label, 'passed': bool(condition)})
    if not condition:
        raise AssertionError(label)
    print('PASS ' + label, flush=True)


def command(label, args, env):
    with (RUN / (label + '.log')).open('w', encoding='utf-8') as log:
        result = subprocess.run(args, cwd=ROOT, env=env, stdout=log, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW)
    RESULT['commands'].append({'label': label, 'args': list(map(str, args)), 'exitCode': result.returncode})
    check(label + ' exit 0', result.returncode == 0)


def port():
    with socket.socket() as s:
        s.bind(('127.0.0.1', 0))
        return s.getsockname()[1]


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args):
        return None


def request(url, data=None, secure_proxy=False):
    headers = {'Content-Type': 'application/json'} if data is not None else {}
    if secure_proxy:
        headers['X-Forwarded-Proto'] = 'https'
    try:
        response = urllib.request.build_opener(NoRedirect).open(urllib.request.Request(url, data=data, headers=headers), timeout=5)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        return response.status, response.read().decode('utf-8'), dict(response.headers)


def launch(project, base, extra):
    env = os.environ.copy()
    for key in list(env):
        if any(word in key.upper() for word in ('STRIPE', 'RESEND', 'SUPABASE', 'CLOUDFLARE', 'AUTH__', 'MEDIA__', 'STORAGE__', 'NOTIFICATIONS__', 'WEBPUSH__', 'SERVICEBILLING__', 'MERCHANTPAYMENTS__', 'API__', 'DATAPROTECTION__', 'ORDERING__', 'ASPNETCORE_', 'DOTNET_ENVIRONMENT')):
            env.pop(key)
    env.update({'ASPNETCORE_ENVIRONMENT': 'Production', 'DOTNET_ENVIRONMENT': 'Production', 'ASPNETCORE_URLS': base,
                'Auth__Enabled': 'false', 'Auth__AllowLocalTestProvider': 'false', 'Notifications__Mode': 'disabled',
                'WebPush__Enabled': 'false', 'Stripe__CheckoutEnabled': 'false', 'Stripe__InvoicesEnabled': 'false',
                'ServiceBilling__LiveEnabled': 'false', 'ServiceBilling__CheckoutEnabled': 'false',
                'MerchantPayments__CheckoutEnabled': 'false', 'MerchantPayments__OnboardingEnabled': 'false', **extra})
    folder = RUN / 'publish' / project
    log = (RUN / (project + '.startup.log')).open('w', encoding='utf-8'); LOGS.append(log)
    process = subprocess.Popen([str(SDK), str(folder / (project + '.dll'))], cwd=folder, env=env, stdin=subprocess.DEVNULL,
                               stdout=log, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW)
    PROCESSES.append(process)
    until = time.monotonic() + 60
    while time.monotonic() < until:
        if process.poll() is not None:
            raise RuntimeError(project + ' stopped at startup; see log')
        try:
            if request(base + '/health')[0] == 200:
                check(project + ' Release starts in Production mode', 'Hosting environment: Production' in (RUN / (project + '.startup.log')).read_text(encoding='utf-8'))
                return
        except (OSError, urllib.error.URLError):
            pass
        time.sleep(.2)
    raise TimeoutError(project)


try:
    before = source_hashes()
    if args.existing_package:
        prior = json.loads((RUN / 'results.json').read_text(encoding='utf-8'))
        (RUN / ('prior-results-' + datetime.now(timezone.utc).strftime('%H%M%S-%f') + '.json')).write_text(json.dumps(prior, indent=2), encoding='utf-8')
        RESULT['commands'] = prior['commands']
        check('Retried package still matches source', before == json.loads((RUN / 'source-hashes.json').read_text(encoding='utf-8')))
        check('Both retained Release publishes exited 0', all(any(c['label'] == p + '-publish' and c['exitCode'] == 0 for c in RESULT['commands']) for p in ('TideCasa.Api', 'TideCasa.Blazor')))
    else:
        (RUN / 'source-hashes.json').write_text(json.dumps(before, indent=2), encoding='utf-8')
    env = os.environ.copy()
    env.update({'DOTNET_CLI_HOME': str(ROOT / '.tools/dotnet-home'), 'NUGET_PACKAGES': str(ROOT / '.tools/nuget-packages'),
                'DOTNET_CLI_TELEMETRY_OPTOUT': '1', 'DOTNET_SKIP_FIRST_TIME_EXPERIENCE': '1'})
    # Native debug symbols are not needed by the package. Runtime DLL/SO assets remain intact.
    targets = RUN / 'omit-native-debug-symbols.targets'
    targets.write_text('''<Project>
  <Target Name="OmitNativeDebugSymbolsFromCopy" AfterTargets="ResolveReferences">
    <ItemGroup><ReferenceCopyLocalPaths Remove="@(ReferenceCopyLocalPaths)" Condition="'%(ReferenceCopyLocalPaths.Filename)%(ReferenceCopyLocalPaths.Extension)' == 'libSkiaSharp.pdb'" /></ItemGroup>
  </Target>
  <Target Name="OmitNativeDebugSymbolsFromPublish" AfterTargets="ComputeFilesToPublish">
    <ItemGroup><ResolvedFileToPublish Remove="@(ResolvedFileToPublish)" Condition="'%(ResolvedFileToPublish.Filename)%(ResolvedFileToPublish.Extension)' == 'libSkiaSharp.pdb'" /></ItemGroup>
  </Target>
</Project>''', encoding='utf-8')
    for project in ('TideCasa.Api', 'TideCasa.Blazor'):
        project_file = ROOT / project / (project + '.csproj')
        common = ['--artifacts-path', str(ARTIFACTS / project), '-p:NuGetAudit=false']
        if not args.existing_package:
            command(project + '-restore-cached', [str(SDK), 'restore', str(project_file), *common, '--source', str(ROOT / '.tools/nuget-packages'), '--disable-parallel', '--nologo'], env)
            command(project + '-publish', [str(SDK), 'publish', str(project_file), '-c', 'Release', *common, '-o', str(RUN / 'publish' / project),
                    '--no-restore', '--disable-build-servers', '--nologo', '-p:UseAppHost=false', '-p:UseSharedCompilation=false',
                    '-p:CustomAfterMicrosoftCommonTargets=' + str(targets), '-m:1'], env)
        files = [p for p in (RUN / 'publish' / project).rglob('*') if p.is_file()]
        forbidden = [p for p in files if p.suffix.lower() in ('.db', '.sqlite', '.pem', '.key', '.env') or p.name.lower().startswith(('secrets', 'credentials')) or p.name.lower().startswith('key-') and p.suffix.lower() == '.xml']
        check(project + ' package has no private data/key filenames', not forbidden)
        check(project + ' package omits large native debug symbols', not any(p.name == 'libSkiaSharp.pdb' for p in files))
        RESULT.setdefault('packages', {})[project] = {'files': len(files), 'bytes': sum(p.stat().st_size for p in files),
            'assemblySha256': hashlib.sha256((RUN / 'publish' / project / (project + '.dll')).read_bytes()).hexdigest()}
    check('Source unchanged during publishing', before == source_hashes())
    api, web = 'http://127.0.0.1:' + str(port()), 'http://127.0.0.1:' + str(port())
    db = RUN / 'synthetic.db'
    launch('TideCasa.Api', api, {'Storage__DatabasePath': str(db)})
    for route in ('/api/v1/owner/sales', '/api/v1/owner/launch-review'):
        check('API anonymous GET denied: ' + route, request(api + route)[0] == 401)
    check('API anonymous sales POST denied', request(api + '/api/v1/owner/sales', b'{}')[0] == 401)
    with closing(sqlite3.connect(db)) as connection:
        check('Release installs all nine feature migrations', connection.execute('SELECT COUNT(*), MAX(version) FROM tide_feature_migrations').fetchone() == (9, 9))
        check('Release database integrity and foreign keys', connection.execute('PRAGMA integrity_check').fetchall() == [('ok',)] and connection.execute('PRAGMA foreign_key_check').fetchall() == [])
    # Production rejects plain HTTP loopback. These static page/privacy checks use the
    # approved container URL; they do not claim to exercise an API/Web network connection.
    launch('TideCasa.Blazor', web, {'Api__BaseUrl': 'http://api:8080/', 'DataProtection__KeysPath': str(RUN / 'keys')})
    # Simulate the trusted loopback proxy's scheme for secure-cookie rendering; no TLS claim.
    for route, expected in (('/restaurant', 'BarTide'), ('/enhanced-demo', 'Six tools. One connected business.'), ('/sales-pipeline.css', '.sales-'), ('/account-workspaces.css', '.account-')):
        response = request(web + route, secure_proxy=True)
        check('Published frontend serves ' + route, response[0] == 200 and expected in response[1])
    private = request(web + '/owner/sales', secure_proxy=True)
    check('Published owner sales page stays private', private[0] == 302 and '/signin' in private[2].get('Location', '') and 'no-store' in private[2].get('Cache-Control', ''))
    check('Published frontend logs have no application errors', 'fail:' not in (RUN / 'TideCasa.Blazor.startup.log').read_text(encoding='utf-8'))
    RESULT['frontendBoundary'] = 'Static page and anonymous privacy checks with simulated trusted-loopback HTTPS forwarding; container API URL not connected.'
    with (RUN / 'hosting-results.json').open('w', encoding='utf-8') as output:
        hosting = subprocess.run([sys.executable, str(ROOT / 'scripts/verify-hosting.py')], stdout=output, stderr=subprocess.STDOUT, cwd=ROOT)
    check('Hosting source and synthetic backup suite exits 0', hosting.returncode == 0)
    RESULT['runtimeTools'] = {name: shutil.which(name) for name in ('docker', 'caddy', 'wsl')}
    RESULT['completed'] = True
finally:
    for process in reversed(PROCESSES):
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=15)
            except subprocess.TimeoutExpired:
                process.kill(); process.wait(timeout=10)
    for log in LOGS:
        log.close()
    RESULT['processesStopped'] = all(p.poll() is not None for p in PROCESSES)
    (RUN / 'results.json').write_text(json.dumps(RESULT, indent=2), encoding='utf-8')
    print('Release evidence: ' + str(RUN), flush=True)
