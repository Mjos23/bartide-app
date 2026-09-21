"""Validate the deployment draft with official portable tools, without deployment.

Downloads only pinned, SHA-256-verified vendor executables into .tools. Does not
install a Docker daemon, change PATH/trust stores, run containers or contact ACME.
"""
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import subprocess
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]
HOST = ROOT / 'deploy/digitalocean'
RUN = ROOT / '.tools/deployment-config-verification' / datetime.now(timezone.utc).strftime('%Y%m%d-%H%M%S-%f')
RUN.mkdir(parents=True)
BIN = ROOT / '.tools/deployment-validation-tools'
BIN.mkdir(exist_ok=True)
ASSETS = [
    {'name': 'caddy_2.11.4_windows_amd64.zip', 'url': 'https://github.com/caddyserver/caddy/releases/download/v2.11.4/caddy_2.11.4_windows_amd64.zip',
     'sha256': '1708333f79e274c7697285afe6d592ab39314e0b131e9ec6bea08ad27df62ebf'},
    {'name': 'docker-compose-windows-x86_64.exe', 'url': 'https://github.com/docker/compose/releases/download/v5.5.0/docker-compose-windows-x86_64.exe',
     'sha256': '51e1e61195f3616896265487ed64551095f3bd27ac7fbd5758d3538c3bfa1b19'},
]
result = {'completed': False, 'checks': [], 'commands': [], 'tools': ASSETS,
          'limits': 'Windows config parsing/provision validation only; no container start, Linux, ACME, public TLS, host or provider changes.'}


def check(label, condition):
    result['checks'].append({'check': label, 'passed': bool(condition)})
    if not condition:
        raise AssertionError(label)
    print('PASS ' + label, flush=True)


def fetch(asset):
    destination = BIN / asset['name']
    if not destination.exists():
        request = urllib.request.Request(asset['url'], headers={'User-Agent': 'TideCasa-local-verification'})
        with urllib.request.urlopen(request, timeout=90) as response, destination.open('xb') as output:
            while block := response.read(1024 * 1024):
                output.write(block)
    if hashlib.sha256(destination.read_bytes()).hexdigest() != asset['sha256']:
        raise ValueError('Vendor asset checksum mismatch: ' + asset['name'])
    return destination


def run(label, command, env):
    process = subprocess.run(list(map(str, command)), cwd=HOST, env=env, capture_output=True, text=True,
                             timeout=60, creationflags=subprocess.CREATE_NO_WINDOW)
    (RUN / (label + '.stdout')).write_text(process.stdout, encoding='utf-8')
    (RUN / (label + '.stderr')).write_text(process.stderr, encoding='utf-8')
    result['commands'].append({'label': label, 'command': list(map(str, command)), 'exitCode': process.returncode})
    check(label + ' exits 0', process.returncode == 0)
    return process.stdout


try:
    with ThreadPoolExecutor(max_workers=2) as pool:
        files = list(pool.map(fetch, ASSETS))
    check('Both vendor assets match their published SHA-256', True)
    with zipfile.ZipFile(files[0]) as archive:
        # Read one known executable; never extract archive-supplied paths.
        caddy_bytes = archive.read('caddy.exe')
        (BIN / 'caddy.exe').write_bytes(caddy_bytes)
    caddy, compose = BIN / 'caddy.exe', files[1]
    result['caddyExecutableSha256'] = hashlib.sha256(caddy_bytes).hexdigest()
    env = os.environ.copy()
    placeholders = {}
    for line in (HOST / '.env.example').read_text().splitlines():
        if line and not line.startswith('#'):
            key, value = line.split('=', 1); placeholders[key] = value
    for key in list(env):
        if key.upper().startswith(('COMPOSE_', 'DOCKER_')) or key in placeholders:
            env.pop(key)
    # Use only reviewed examples, never an operator's external runtime environment.
    env.update(placeholders)
    env.update({'XDG_DATA_HOME': str(RUN / 'caddy-data'), 'XDG_CONFIG_HOME': str(RUN / 'caddy-config'), 'APPDATA': str(RUN / 'appdata')})
    result['versions'] = {'caddy': run('caddy-version', [caddy, 'version'], env).strip(),
                          'compose': run('compose-version', [compose, 'version'], env).strip()}
    command = [compose, '--env-file', HOST / '.env.example', '-f', HOST / 'compose.yaml', 'config']
    run('compose-quiet', [*command, '--quiet'], env)
    configuration = json.loads(run('compose-render', [*command, '--format', 'json'], env))
    services = configuration['services']
    check('Exactly API, Web and proxy services', set(services) == {'api', 'web', 'caddy'})
    check('Only proxy publishes host ports', bool(services['caddy'].get('ports')) and not services['api'].get('ports') and not services['web'].get('ports'))
    api = services['api']['environment']
    for key in ('ServiceBilling__CheckoutEnabled', 'ServiceBilling__LiveEnabled', 'MerchantPayments__CheckoutEnabled',
                'MerchantPayments__OnboardingEnabled', 'WebPush__Enabled', 'Auth__AllowLocalTestProvider', 'Media__AllowLocalStore'):
        check('Resolved configuration keeps ' + key + ' off', str(api[key]).lower() == 'false')
    check('Resolved email sending disabled', api['Notifications__Mode'] == 'disabled')
    check('Resolved examples contain blank private credentials', all(not value for key, value in api.items() if any(part in key for part in ('RestrictedKey', 'WebhookSecret', 'SecretAccessKey', 'VapidPrivateKey', 'ResendApiKey'))))
    for name in ('api', 'web'):
        check(name + ' uses Production for both selectors', services[name]['environment']['DOTNET_ENVIRONMENT'] == services[name]['environment']['ASPNETCORE_ENVIRONMENT'] == 'Production')
        check(name + ' root filesystem read-only', services[name]['read_only'] is True)
    run('caddy-validate', [caddy, 'validate', '--config', HOST / 'caddy/Caddyfile', '--adapter', 'caddyfile'], env)
    adapted = json.loads(run('caddy-adapt', [caddy, 'adapt', '--config', HOST / 'caddy/Caddyfile', '--adapter', 'caddyfile'], env))
    check('Caddy admin API disabled', adapted['admin']['disabled'] is True)
    http = adapted['apps']['http']
    check('Caddy uses unprivileged internal listener ports', http['http_port'] == 8080 and http['https_port'] == 8443)
    targets = []
    def visit(value):
        if isinstance(value, dict):
            if value.get('handler') == 'reverse_proxy':
                targets.extend(x['dial'] for x in value['upstreams'])
            for child in value.values(): visit(child)
        elif isinstance(value, list):
            for child in value: visit(child)
    visit(adapted)
    check('Caddy routes to the two private services', sorted(targets) == ['api:8080', 'web:8080'])
    result['completed'] = True
finally:
    (RUN / 'results.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
    print('Deployment config evidence: ' + str(RUN), flush=True)
