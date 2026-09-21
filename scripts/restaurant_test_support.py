"""Exercise restaurant ordering through a real isolated API and SQLite database.

Only the identity-provider boundary is fake. All guests, venues, and orders are
synthetic; this does not contact Stripe, Supabase, the owner preview, or production.
Build TideCasa.Api first. Evidence and owned child-process logs stay under .tools.
"""
import base64
import concurrent.futures
import copy
from datetime import datetime, timezone
import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import socket
import sqlite3
import subprocess
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]
RUN = ROOT / '.tools/restaurant-management-verification' / (
    datetime.now(timezone.utc).strftime('%Y%m%d-%H%M%S-%f'))
RUN.mkdir(parents=True, exist_ok=False)
SDK = ROOT / '.tools/dotnet-10.0.401/dotnet.exe'
DB = RUN / 'synthetic.db'
KEY = 'sb_publishable_localverification000000000'
PASSWORD = 'synthetic-restaurant-password'
ALICE, BOB, STAFF, PLATFORM = [str(uuid.UUID(int=n)) for n in (301, 302, 303, 304)]
USERS = {name + '@example.invalid': {'id': ident, 'email': name + '@example.invalid',
    'email_confirmed_at': '2026-01-01T00:00:00Z', 'is_anonymous': False,
    'user_metadata': {'full_name': name.title() + ' Synthetic'}}
    for name, ident in [('alice', ALICE), ('bob', BOB), ('staff', STAFF), ('platform', PLATFORM)]}
TOKENS, RESULTS, PROCESSES, LOGS = {}, [], [], []
IP_LOCK, IP_COUNTER = threading.Lock(), 0
TABLE = 'a' * 64
SECOND_TABLE = 'b' * 64
FOREIGN_TABLE = 'c' * 64
DISABLED_TABLE = 'd' * 64
TABLE_ID, SECOND_TABLE_ID, FOREIGN_TABLE_ID, DISABLED_TABLE_ID = [str(uuid.UUID(int=n)) for n in (401, 402, 403, 404)]
PRIVATE = 'SYNTHETIC-PRIVATE-MARKER'
NOW = '2026-09-20T12:00:00Z'


class Provider(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def respond(self, status, body):
        self.send_response(status)
        self.send_header('Content-Type', 'application/json')
        self.end_headers()
        self.wfile.write(json.dumps(body).encode())

    def handle_request(self):
        if self.headers.get('apikey') != KEY:
            return self.respond(403, {})
        if self.headers.get('Transfer-Encoding', '').lower() == 'chunked':
            chunks = []
            while True:
                length = int(self.rfile.readline().split(b';', 1)[0].strip(), 16)
                if length == 0:
                    while self.rfile.readline().strip():
                        pass
                    break
                chunks.append(self.rfile.read(length))
                self.rfile.read(2)
            raw = b''.join(chunks)
        else:
            raw = self.rfile.read(int(self.headers.get('Content-Length', '0')))
        body = json.loads(raw or b'{}')
        path = urllib.parse.urlparse(self.path).path
        if path == '/auth/v1/token' and body.get('password') == PASSWORD:
            user = USERS.get(body.get('email'))
            if user:
                token = 'synthetic.' + base64.urlsafe_b64encode(uuid.uuid4().bytes).decode().rstrip('=') + '.signature'
                TOKENS[token] = user
                return self.respond(200, {'access_token': token, 'expires_in': 3600, 'user': user})
        if path == '/auth/v1/user':
            user = TOKENS.get(self.headers.get('Authorization', '').removeprefix('Bearer '))
            return self.respond(200 if user else 401, user or {})
        return self.respond(400, {})

    do_GET = do_POST = handle_request


def port():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        return sock.getsockname()[1]


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


PROVIDER = ThreadingHTTPServer(('127.0.0.1', 0), Provider)
threading.Thread(target=PROVIDER.serve_forever, daemon=True).start()
API_PORT = port()
API = 'http://127.0.0.1:' + str(API_PORT)


def call(path, body=None, token=None, headers=None):
    global IP_COUNTER
    with IP_LOCK:
        IP_COUNTER += 1
        # The isolated API trusts its loopback reverse proxy. Use documentation
        # addresses for separate synthetic clients, keeping functional tests from
        # exhausting another test's per-client rate-limit bucket.
        client_ip = '192.0.2.' + str(1 + (IP_COUNTER % 250))
    request_headers = {'X-Forwarded-For': client_ip, **(headers or {})}
    if token:
        request_headers['Authorization'] = 'Bearer ' + token
    encoded = None
    if body is not None:
        encoded = json.dumps(body).encode()
        request_headers['Content-Type'] = 'application/json'
    request = urllib.request.Request(API + path, data=encoded, headers=request_headers)
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    try:
        response = opener.open(request, timeout=40)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        text = response.read().decode('utf-8', errors='replace')
        try:
            parsed = json.loads(text)
        except json.JSONDecodeError:
            parsed = None
        return response.status, parsed, text, response.headers


def check(name, condition, detail=None):
    RESULTS.append({'check': name, 'passed': bool(condition)})
    print(('PASS ' if condition else 'FAIL ') + name, flush=True)
    if not condition:
        if detail is not None:
            # Results contain synthetic data only, never a provider credential.
            print('Synthetic failure detail: ' + str(detail)[:1800], flush=True)
        raise AssertionError(name)


def sql(statement, values=()):
    with sqlite3.connect(DB, timeout=20) as connection:
        return connection.execute(statement, values).fetchall()


def launch():
    env = os.environ.copy()
    for key in list(env):
        if any(word in key.upper() for word in ('STRIPE', 'RESEND', 'SUPABASE', 'CLOUDFLARE',
                                                  'AUTH__', 'ORDERING__', 'STORAGE__', 'SERVICEBILLING__', 'MERCHANTPAYMENTS__', 'NOTIFICATIONS__', 'MEDIA__')):
            env.pop(key)
    env.update({'ASPNETCORE_ENVIRONMENT': 'Development', 'ASPNETCORE_URLS': API,
        'Storage__DatabasePath': str(DB), 'Auth__Enabled': 'true', 'Auth__AllowLocalTestProvider': 'true',
        'Auth__SupabaseUrl': f'http://127.0.0.1:{PROVIDER.server_port}', 'Auth__PublishableKey': KEY,
        'Auth__PlatformOwnerUserId': 'supabase:' + PLATFORM,
        'Stripe__CheckoutEnabled': 'false', 'Stripe__InvoicesEnabled': 'false',
        'Ordering__PublicBaseUrl': 'https://ordering.example.invalid',
        'ReverseProxy__KnownClientProxy': '127.0.0.1'})
    log = (RUN / 'TideCasa.Api.log').open('w', encoding='utf-8')
    LOGS.append(log)
    proc = subprocess.Popen([str(SDK), str(Path(os.environ.get('TIDE_TEST_BUILD_ROOT', str(ROOT))) / 'TideCasa.Api/bin/Debug/net10.0/TideCasa.Api.dll')],
        cwd=ROOT / 'TideCasa.Api', env=env, stdout=log, stderr=subprocess.STDOUT,
        creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
    PROCESSES.append(proc)
    deadline = time.monotonic() + 180
    while time.monotonic() < deadline:
        if proc.poll() is not None:
            raise RuntimeError('Isolated API stopped; inspect ' + str(RUN / 'TideCasa.Api.log'))
        try:
            if call('/health')[0] == 200:
                return
        except (OSError, urllib.error.URLError):
            pass
        time.sleep(.5)
    raise TimeoutError('Isolated API startup timeout')


def menu():
    items = [{'id': 'dish-' + str(n), 'category': 'food', 'name': 'Synthetic dish ' + str(n),
        'description': 'A local test item', 'price_cents': 1001 if n == 1 else 1010 if n == 3 else 500,
        'price_label': None, 'available': True} for n in range(1, 23)]
    items += [{'id': 'market-price', 'category': 'food', 'name': 'Market price',
        'description': '', 'price_cents': None, 'price_label': 'Ask staff', 'available': True},
        {'id': 'sold-out', 'category': 'food', 'name': 'Sold out', 'description': '',
         'price_cents': 900, 'price_label': None, 'available': False},
        {'id': 'expensive', 'category': 'food', 'name': 'Catering item', 'description': '',
         'price_cents': 100000, 'price_label': None, 'available': True}]
    return {'schema': 'bartide-menu/1', 'venue': {'name': 'Synthetic Bistro', 'vertical': 'bartide',
        'area': 'Test', 'tagline': 'Local verification', 'hours_text': '', 'website_url': '',
        'service_note': '', 'currency': 'USD', 'private_test_marker': PRIVATE},
        'categories': [{'id': 'food', 'label': 'Food'}], 'items': items,
        'private_test_marker': PRIVATE}


def config():
    return {'enabled': True, 'accepting_orders': True, 'delivery_enabled': True,
        'tax_basis_points': 700, 'delivery_fee_cents': 333, 'delivery_minimum_cents': 1500,
        'delivery_capacity': 5, 'delivery_zips': ['33101'], 'prep_minutes': 25,
        'blocked_item_ids': ['dish-22'], 'pickup_instructions': 'Ask at the counter',
        'payment_instructions': 'Pay a member of staff',
        'private_test_marker': PRIVATE, 'preserve_future_config': {'nested': [1, True, 'keep']},
        'checkout': {'dine_in_enabled': True, 'pickup_enabled': True, 'pay_staff_enabled': True,
            'tips_enabled': True, 'preserve_future_checkout': {'keep': 'value'}, 'tables': [
                {'id': TABLE_ID, 'label': 'Table 1', 'token': TABLE, 'enabled': True},
                {'id': SECOND_TABLE_ID, 'label': 'Table 2', 'token': SECOND_TABLE, 'enabled': True},
                {'id': DISABLED_TABLE_ID, 'label': 'Table 3', 'token': DISABLED_TABLE, 'enabled': False}]}}


def seed(ident, owner=ALICE, status='active', enabled=True, vertical='bartide'):
    cfg = config()
    cfg['enabled'] = enabled
    if ident == 'foreign':
        cfg['checkout']['tables'] = [{'id': FOREIGN_TABLE_ID, 'label': 'Foreign Table',
                                      'token': FOREIGN_TABLE, 'enabled': True}]
    sql('INSERT INTO bartide_customers(id,slug,email,user_id,name,menu_json,version,status,enrollment_note,created_at,updated_at,vertical) '
        'VALUES (?,?,?,?,?,?,0,?,?,?,?,?)',
        (ident, ident, ident + '@example.invalid', 'supabase:' + owner, 'Synthetic ' + ident,
         json.dumps(menu()), status, PRIVATE, NOW, NOW, vertical))
    sql('INSERT INTO bartide_enhanced_configs(tenant_id,settings_json,version,updated_at) VALUES (?,?,0,?)',
        (ident, json.dumps(cfg), NOW))


def alter_config(mutator, tenant='bistro'):
    raw = json.loads(sql('SELECT settings_json FROM bartide_enhanced_configs WHERE tenant_id=?', (tenant,))[0][0])
    mutator(raw)
    sql('UPDATE bartide_enhanced_configs SET settings_json=?,version=version+1 WHERE tenant_id=?',
        (json.dumps(raw), tenant))


def quote(order=None, tenant='bistro', **overrides):
    request = copy.deepcopy(order) if order is not None else {
        'items': [{'itemId': 'dish-1', 'quantity': 1}], 'fulfillment': 'pickup', 'paymentMethod': 'staff'}
    request.update(overrides)
    response = call('/api/v1/restaurants/' + tenant + '/quote', request)
    return request, response


def valid_quote(order=None, tenant='bistro', **overrides):
    request, response = quote(order, tenant, **overrides)
    if response[0] != 200:
        raise AssertionError('Expected a usable synthetic quote: ' + str(response[:3]))
    return request, response[1]


def order_request(order=None, tenant='bistro', **overrides):
    selected, quoted = valid_quote(order, tenant)
    request = {'requestKey': str(uuid.uuid4()), 'trackingKey': uuid.uuid4().hex + uuid.uuid4().hex,
        'order': selected, 'quoteFingerprint': quoted['fingerprint'],
        'customerName': PRIVATE + ' Guest', 'phone': '+1 (305) 555-0100',
        'address': PRIVATE + ' 123 Test Street' if selected['fulfillment'] == 'delivery' else None,
        'note': PRIVATE + ' guest note'}
    request.update(overrides)
    return request


def submit(request, tenant='bistro'):
    return call('/api/v1/restaurants/' + tenant + '/orders', request)


def count_orders(tenant='bistro'):
    return sql('SELECT COUNT(*) FROM bartide_enhanced_orders WHERE tenant_id=?', (tenant,))[0][0]


def receipt_private(data):
    forbidden = {'customerName', 'customer_name', 'phone', 'address', 'note', 'trackingKey',
                 'tracking_key', 'requestKey', 'request_key', 'trackingHash', 'requestHash'}
    def safe(value):
        if isinstance(value, dict):
            return not (set(value) & forbidden) and all(safe(v) for v in value.values())
        return all(safe(v) for v in value) if isinstance(value, list) else True
    return safe(data) and PRIVATE not in json.dumps(data)


