"""Verify the local .NET slice using isolated, synthetic data and no provider keys.
Build the API and Blazor projects first. Requires Python 3 and the workspace SDK.
No deployment, outbound email, Stripe calls, or original-app data access.
"""
import concurrent.futures
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]
BUILD_ROOT = Path(os.environ.get('TIDE_TEST_BUILD_ROOT', str(ROOT)))
SDK = ROOT / '.tools/dotnet-10.0.401/dotnet.exe'
RUN = ROOT / '.tools/verification' / time.strftime('%Y%m%d-%H%M%S')
RUN.mkdir(parents=True, exist_ok=False)
processes, logs, results = [], [], []

def port():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        return sock.getsockname()[1]

api_port, web_port = port(), port()
API, WEB = f'http://127.0.0.1:{api_port}', f'http://127.0.0.1:{web_port}'

def call(base, path, data=None, method=None, headers=None):
    req = urllib.request.Request(base + path,
        data=None if data is None else json.dumps(data).encode(),
        headers={'Content-Type': 'application/json', **(headers or {})}, method=method)
    try:
        response = urllib.request.urlopen(req, timeout=30)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        body = response.read().decode('utf-8', errors='replace')
        return response.status, body, response.headers

def check(name, condition):
    results.append({'check': name, 'passed': bool(condition)})
    print(('PASS ' if condition else 'FAIL ') + name, flush=True)
    if not condition:
        raise AssertionError(name)

def launch(project, listen_port, extra):
    env = os.environ.copy()
    # Do not inherit provider credentials into the synthetic test processes.
    for key in list(env):
        if any(word in key.upper() for word in ['STRIPE', 'RESEND', 'SUPABASE', 'CLOUDFLARE', 'SERVICEBILLING__', 'MERCHANTPAYMENTS__', 'NOTIFICATIONS__', 'MEDIA__', 'AUTH__']):
            env.pop(key)
    env.update({'ASPNETCORE_ENVIRONMENT': 'Development',
        'ASPNETCORE_URLS': f'http://127.0.0.1:{listen_port}',
        'DOTNET_CLI_TELEMETRY_OPTOUT': '1', 'Stripe__CheckoutEnabled': 'false',
        'Stripe__InvoicesEnabled': 'false', 'Stripe__Mode': 'sandbox', 'Notifications__Mode': 'disabled', 'Auth__Enabled': 'false', **extra})
    logfile = (RUN / (project + '.log')).open('w', encoding='utf-8')
    logs.append(logfile)
    dll = BUILD_ROOT / project / 'bin/Debug/net10.0' / (project + '.dll')
    process = subprocess.Popen([str(SDK), str(dll)], cwd=ROOT / project,
        env=env, stdout=logfile, stderr=subprocess.STDOUT,
        creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
    processes.append(process)
    deadline = time.monotonic() + 180
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise RuntimeError(project + ' stopped. Inspect its isolated verification log.')
        try:
            if call(f'http://127.0.0.1:{listen_port}', '/health')[0] == 200:
                return process
        except (OSError, urllib.error.URLError):
            pass
        time.sleep(0.5)
    raise RuntimeError(project + ' did not start before the local timeout.')

request = {'id': str(uuid.uuid4()), 'name': 'Local verification',
    'business': 'Synthetic test only', 'email': 'booking@example.invalid',
    'phone': '', 'city': '', 'businessType': 'bartide',
    'preferredTimes': 'Tuesday afternoon', 'timeZone': 'Eastern Time',
    'goals': 'QA only', 'contactWebsite': ''}

try:
    domain = subprocess.run([str(SDK), str(BUILD_ROOT / 'TideCasa.Domain.Checks/bin/Debug/net10.0/TideCasa.Domain.Checks.dll')], capture_output=True, text=True, timeout=120, creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
    check('42 independent customer reward domain checks', domain.returncode == 0 and 'PASS: 42 customer reward domain checks.' in domain.stdout)
    api = launch('TideCasa.Api', api_port, {'Storage__DatabasePath': str(RUN / 'synthetic.db')})
    web = launch('TideCasa.Blazor', web_port, {'Api__BaseUrl': API + '/',
        'DataProtection__KeysPath': str(RUN / 'keys')})
    check('API and separate Blazor health endpoints', call(API, '/health')[0] == 200 and call(WEB, '/health')[0] == 200)
    status, body, _ = call(API, '/openapi/v1.json')
    check('Versioned reusable API is documented', status == 200 and '/api/v1/demo-requests' in json.loads(body)['paths'])
    for stores, first in [(False, 65000), (True, 95000)]:
        status, body, _ = call(API, '/api/v1/pricing/quote', {'appStores': stores, 'setupCents': 1, 'discountCents': 99999})
        quote = json.loads(body)
        check(f'Server-owned quote: stores={stores}', status == 200 and quote['firstPaymentCents'] == first and quote['monthlyCents'] == 5000 and quote['setupCents'] == 60000 and quote['discountCents'] == 0 and quote['termsVersion'] == '2026-09-maintenance-v1')
    check('Unvalidated referral never silently applied', call(API, '/api/v1/pricing/quote', {'referralCode': 'NOT-CONNECTED'})[0] == 503)
    status, body, headers = call(API, '/api/v1/demo-requests', request)
    receipt = json.loads(body)
    check('Booking is saved without exposing contact data', status == 201 and receipt['id'] == request['id'] and receipt['status'] == 'requested' and 'email' not in receipt and 'not confirmed' in receipt['message'])
    check('Booking response is not cached', headers.get('Cache-Control') == 'no-store')
    check('Identical booking retry is idempotent', call(API, '/api/v1/demo-requests', request)[0] == 200)
    check('Changed payload cannot reuse a booking id', call(API, '/api/v1/demo-requests', {**request, 'name': 'Changed'})[0] == 409)
    invalid = [('missing id', {'id': str(uuid.UUID(int=0))}), ('bad email', {'email': 'invalid'}),
        ('blank name', {'name': '   '}), ('honeypot', {'contactWebsite': 'https://example.invalid'}),
        ('oversized text', {'goals': 'x' * 1601}), ('invalid business type', {'businessType': 'anything'}),
        ('null optional field', {'phone': None}), ('control character', {'name': 'bad\x01name'})]
    for label, replacement in invalid:
        payload = {**request, 'id': str(uuid.uuid4()), **replacement}
        check('Booking rejects ' + label, call(API, '/api/v1/demo-requests', payload)[0] == 400)
    for _ in range(2):
        check('Within email submission allowance', call(API, '/api/v1/demo-requests', {**request, 'id': str(uuid.uuid4())})[0] == 201)
    check('Per-email repeated submissions are limited', call(API, '/api/v1/demo-requests', {**request, 'id': str(uuid.uuid4())})[0] == 429)
    check('Saved bookings have no public listing', call(API, '/api/v1/demo-requests')[0] in (404, 405))
    concurrent_request = {**request, 'id': str(uuid.uuid4()), 'email': 'concurrency@example.invalid'}
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        codes = list(pool.map(lambda _: call(API, '/api/v1/demo-requests', concurrent_request)[0], range(4)))
    check('Concurrent retries create exactly one record', sorted(codes) == [200, 200, 200, 201])
    for path, features in [('/', 'id="included"'), ('/restaurant', 'bt-home-features')]:
        status, html, _ = call(WEB, path)
        (RUN / ('home.html' if path == '/' else 'restaurant.html')).write_text(html, encoding='utf-8')
        lower = html.lower()
        # Both templates put their first rewards feature ahead of the pricing section.
        reward = lower.find('customer rewards')
        pricing = lower.find('id="pricing"')
        check('Features before pricing at ' + path, status == 200 and 0 <= reward < pricing)
        check('No app-store promotion or demo-version label at ' + path, 'app-store' not in lower and 'app store' not in lower and 'demo version' not in lower)
        check('Book a demo linked at ' + path, '/book-a-demo' in html)
    status, html, _ = call(WEB, '/', headers={'Host': 'bar.tide.casa'})
    (RUN / 'restaurant-host.html').write_text(html, encoding='utf-8')
    check('Restaurant hostname selects BarTide', status == 200 and '<title>BarTide' in html
          and 'Your bar.' in html and 'Their kind of night.' in html)
    for path in ['/book-a-demo', '/contact']:
        status, html, _ = call(WEB, path)
        check('Booking form renders at ' + path, status == 200 and 'Request my demo' in html and 'When works for you?' in html)
    status, html, _ = call(WEB, '/purchase/restaurant')
    check('Checkout renders API total and optional stores with next step', status == 200 and '$650' in html and '$300 once' in html and '/start/restaurant?appStores=false' in html and 'Continue with your business' in html)
    for asset in ['/brand.css', '/tide-casa/homepage.css', '/tide-casa/assets/tide-casa-logo.png', '/assets/coastal-dining.jpg', '/app.js', '/_framework/blazor.web.js']:
        check('Public asset ' + asset, call(WEB, asset)[0] == 200)
    # Restart only our child API process to prove persistence beyond process memory.
    api.terminate()
    api.wait(timeout=30)
    api = launch('TideCasa.Api', api_port, {'Storage__DatabasePath': str(RUN / 'synthetic.db')})
    check('Booking persists across API restart', call(API, '/api/v1/demo-requests', request)[0] == 200)
finally:
    for process in processes:
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=30)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=10)
    for logfile in logs:
        logfile.close()
    (RUN / 'results.json').write_text(json.dumps(results, indent=2), encoding='utf-8')
    print('Verification artifacts: ' + str(RUN), flush=True)
print(f'{len(results)} checks passed. No provider calls or deployment.', flush=True)
