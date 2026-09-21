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
BUILD_ROOT = Path(os.environ.get('TIDE_TEST_BUILD_ROOT', str(ROOT)))
RUN = ROOT / '.tools/restaurant-ordering-verification' / (
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
    proc = subprocess.Popen([str(SDK), str(BUILD_ROOT / 'TideCasa.Api/bin/Debug/net10.0/TideCasa.Api.dll')],
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


try:
    launch()
    for args in [('bistro',), ('foreign', BOB), ('paused', ALICE, 'paused'),
                 ('draft', ALICE, 'draft'), ('disabled', ALICE, 'active', False),
                 ('other-vertical', ALICE, 'active', True, 'fit-tide'), ('capacity',),
                 ('legacy-menu',), ('contact-validation',)]:
        seed(*args)
    sql('INSERT INTO bartide_enhanced_members(id,tenant_id,name,email,user_id,role,active,created_at) '
        'VALUES (?,?,?,?,?,?,1,?)',
        ('staff-test', 'bistro', 'Synthetic staff', 'staff@example.invalid', 'supabase:' + STAFF, 'kitchen', NOW))
    owner_tokens = {}
    for person in ('alice', 'bob', 'staff', 'platform'):
        response = call('/api/v1/auth/signin', {'email': person + '@example.invalid', 'password': PASSWORD})
        check('Synthetic ' + person + ' signs in through registered auth boundary', response[0] == 200, response[:3])
        owner_tokens[person] = response[1]['accessToken']
    owner = owner_tokens['alice']

    response = call('/api/v1/restaurants/bistro/menu')
    public = response[1]
    check('Public menu loads without a customer login', response[0] == 200, response[:3])
    check('Public menu is allowlisted and contains no owner/private configuration',
        set(public) == {'slug', 'name', 'menuVersion', 'currency', 'categories', 'items', 'checkout', 'table'}
        and PRIVATE not in response[2] and 'tables' not in public['checkout'] and public['table'] is None)
    check('Phone payment stays unavailable and tips are enabled',
        public['checkout']['phonePaymentAvailable'] is False and public['checkout']['tipsEnabled'] is True)
    check('Menu translates category and integer cents correctly',
        public['categories'][0] == {'id': 'food', 'name': 'Food'} and
        next(item for item in public['items'] if item['id'] == 'dish-1')['priceCents'] == 1001)
    for length in (101, 160):
        legacy_menu = menu()
        legacy_label = 'Ask staff: ' + 'x' * (length - len('Ask staff: '))
        next(item for item in legacy_menu['items'] if item['id'] == 'market-price')['price_label'] = legacy_label
        sql('UPDATE bartide_customers SET menu_json=?,version=version+1 WHERE id=?',
            (json.dumps(legacy_menu), 'legacy-menu'))
        response = call('/api/v1/restaurants/legacy-menu/menu')
        check('Legacy menu preserves a ' + str(length) + '-character price label', response[0] == 200 and
            next(item for item in response[1]['items'] if item['id'] == 'market-price')['priceLabel'] == legacy_label,
            response[:3])
        check('Legacy price-label response remains private-data allowlisted at ' + str(length) + ' characters',
            PRIVATE not in response[2] and 'tables' not in response[1]['checkout'])
    for tenant in ('missing', 'paused', 'draft', 'other-vertical'):
        check('Public menu rejects unavailable venue ' + tenant,
            call('/api/v1/restaurants/' + tenant + '/menu')[0] == 404)
    response = call('/api/v1/restaurants/disabled/menu')
    check('Disabled ordering keeps the public menu visible with checkout closed',
        response[0] == 200 and response[1]['checkout']['acceptingOrders'] is False)
    check('Disabled enrollment cannot accept an order quote', quote(tenant='disabled')[1][0] == 409)
    response = call('/api/v1/restaurants/bistro/menu?table=' + TABLE)
    check('Valid table QR resolves only its own table', response[0] == 200 and
        response[1]['table']['id'] == TABLE_ID and response[1]['table']['label'] == 'Table 1')
    for label, token in [('foreign', FOREIGN_TABLE), ('disabled', DISABLED_TABLE), ('unknown', 'e' * 64)]:
        check('Table link rejects ' + label + ' token',
            call('/api/v1/restaurants/bistro/menu?table=' + token)[0] == 404)
    qr = call('/api/v1/restaurants/bistro/tables/' + TABLE + '/qr')
    check('Enabled table generates SVG QR', qr[0] == 200 and '<svg' in qr[2]
        and qr[3].get('Content-Type', '').startswith('image/svg+xml'))
    other_host = call('/api/v1/restaurants/bistro/tables/' + TABLE + '/qr?url=https://attacker.example.invalid',
        headers={'Host': 'localhost:' + str(API_PORT), 'X-Forwarded-Host': 'attacker.example.invalid'})
    check('QR destination ignores caller Host and URL query', other_host[0] == 200 and other_host[2] == qr[2])
    check('Disabled and foreign table QR cannot be generated',
        all(call('/api/v1/restaurants/bistro/tables/' + token + '/qr')[0] == 404
            for token in (FOREIGN_TABLE, DISABLED_TABLE)))

    selected, no_tip = valid_quote()
    check('Default no-tip quote uses authoritative cents and pre-tip tax',
        (no_tip['subtotalCents'], no_tip['taxCents'], no_tip['deliveryFeeCents'], no_tip['tipCents'], no_tip['totalCents'])
        == (1001, 70, 0, 0, 1071) and no_tip['canSubmit'] is True)
    for percent, expected in [(15, 150), (20, 200), (25, 250)]:
        _, value = valid_quote(tipPercent=percent)
        check(str(percent) + '% tip is rounded once on the menu subtotal',
            value['tipCents'] == expected and value['taxCents'] == 70 and value['totalCents'] == 1071 + expected)
    _, midpoint = valid_quote(items=[{'itemId': 'dish-3', 'quantity': 1}], tipPercent=15)
    check('Exact half-cent tip rounds up once', midpoint['tipCents'] == 152 and midpoint['taxCents'] == 71)
    _, custom = valid_quote(customTipCents=123)
    check('Custom tip cents are exact and excluded from tax', custom['tipCents'] == 123 and custom['totalCents'] == 1194)
    _, zero = valid_quote(customTipCents=0)
    check('Explicit zero custom tip remains zero', zero['tipCents'] == 0)
    _, full = valid_quote(customTipCents=1001)
    check('Custom tip may equal the subtotal', full['tipCents'] == 1001)
    _, high = valid_quote(items=[{'itemId': 'expensive', 'quantity': 1}], customTipCents=50000)
    check('Custom tip accepts the $500 absolute boundary', high['tipCents'] == 50000)
    for label, overrides in [('negative tip', {'customTipCents': -1}),
        ('tip exceeds subtotal', {'customTipCents': 1002}),
        ('tip exceeds $500', {'items': [{'itemId': 'expensive', 'quantity': 1}], 'customTipCents': 50001}),
        ('unsupported preset', {'tipPercent': 18}),
        ('preset and custom together', {'tipPercent': 15, 'customTipCents': 100}),
        ('invalid fulfillment', {'fulfillment': 'curb'}),
        ('invalid payment', {'paymentMethod': 'paid'}),
        ('dine-in missing table', {'fulfillment': 'dine-in'}),
        ('pickup table injection', {'tableToken': TABLE}),
        ('pickup ZIP injection', {'deliveryZip': '33101'}),
        ('unsupported delivery ZIP', {'fulfillment': 'delivery', 'deliveryZip': '99999',
                                     'items': [{'itemId': 'dish-1', 'quantity': 2}]}),
        ('delivery below minimum', {'fulfillment': 'delivery', 'deliveryZip': '33101'})]:
        request, rejected = quote(**overrides)
        check('Quote rejects ' + label, rejected[0] == 400, rejected[:3])
    for label, items in [('empty cart', []), ('duplicate item', [{'itemId': 'dish-1', 'quantity': 1}] * 2),
        ('missing item', [{'itemId': 'missing', 'quantity': 1}]),
        ('market-price item', [{'itemId': 'market-price', 'quantity': 1}]),
        ('sold-out item', [{'itemId': 'sold-out', 'quantity': 1}]),
        ('blocked online item', [{'itemId': 'dish-22', 'quantity': 1}]),
        ('zero quantity', [{'itemId': 'dish-1', 'quantity': 0}]),
        ('quantity over 20', [{'itemId': 'dish-1', 'quantity': 21}]),
        ('over 20 lines', [{'itemId': 'dish-' + str(i), 'quantity': 1} for i in range(1, 22)]),
        ('over 50 units', [{'itemId': 'dish-' + str(i), 'quantity': 17} for i in range(1, 4)])]:
        expected = 409 if label in ('missing item', 'market-price item', 'sold-out item', 'blocked online item') else 400
        check('Quote rejects ' + label, quote(items=items)[1][0] == expected)
    _, max_units = valid_quote(items=[{'itemId': 'dish-1', 'quantity': 20},
        {'itemId': 'dish-2', 'quantity': 20}, {'itemId': 'dish-3', 'quantity': 10}])
    check('Boundary of 50 units with maximum item quantity remains usable', len(max_units['lines']) == 3)
    selected, injected = valid_quote(items=[{'itemId': 'dish-1', 'quantity': 1, 'unitCents': 1}],
        subtotalCents=1, totalCents=1)
    check('Browser-supplied prices cannot lower an order total', injected['totalCents'] == 1071
        and injected['lines'][0]['unitCents'] == 1001)
    dine_in, dine_quote = valid_quote(fulfillment='dine-in', tableToken=TABLE, tipPercent=20)
    check('Dine-in quote includes resolved table and no delivery fee',
        dine_quote['tableLabel'] == 'Table 1' and dine_quote['deliveryFeeCents'] == 0)
    delivery, delivery_quote = valid_quote(fulfillment='delivery', deliveryZip='33101',
        items=[{'itemId': 'dish-1', 'quantity': 2}], tipPercent=15)
    # 2002 subtotal + 333 fee; 7% tax = 163.45 -> 163; 15% tip = 300.3 -> 300.
    check('Delivery taxes food plus delivery and tips food only',
        (delivery_quote['subtotalCents'], delivery_quote['deliveryFeeCents'], delivery_quote['taxCents'],
         delivery_quote['tipCents'], delivery_quote['totalCents']) == (2002, 333, 163, 300, 2798))

    for label, selection, contact_phone in [('dine-in without a phone', dine_in, ''),
        ('seven-digit phone', None, '5550100'),
        ('fifteen-digit phone', None, '+123456789012345'),
        ('phone with allowed separators', None, '+1 (305) 555.0100'),
        ('phone with a dash', delivery, '+1 305-555-0100')]:
        request = order_request(selection, tenant='contact-validation', phone=contact_phone)
        response = submit(request, 'contact-validation')
        check('Checkout accepts ' + label, response[0] == 201 and receipt_private(response[1]), response[:3])
    for label, selection, contact_phone in [('pickup without a phone', None, ''),
        ('delivery without a phone', delivery, ''), ('six-digit phone', None, '555010'),
        ('sixteen-digit phone', None, '+1234567890123456'), ('punctuation-only phone', None, '() . - ()'),
        ('multiple plus signs', None, '++13055550100'), ('embedded plus sign', None, '1305+5550100'),
        ('letter in phone', None, '305555010x'), ('tab in phone', None, '305\t5550100')]:
        request = order_request(selection, tenant='contact-validation', phone=contact_phone)
        before_validation = count_orders('contact-validation')
        response = submit(request, 'contact-validation')
        check('Checkout rejects ' + label, response[0] == 400 and
            count_orders('contact-validation') == before_validation, response[:3])
    multiline_note = (PRIVATE + '\nNo onions\r\nSauce\ton side\n').ljust(500, 'x')
    response = submit(order_request(tenant='contact-validation', note=multiline_note), 'contact-validation')
    check('Checkout accepts a 500-character note with CR LF and tab', response[0] == 201, response[:3])
    persisted_note = json.loads(sql('SELECT payload_json FROM bartide_enhanced_orders WHERE id=?',
        (response[1]['orderId'],))[0][0])['note']
    check('Multiline note persists intact but stays out of public receipt',
        persisted_note == multiline_note and receipt_private(response[1]))
    for label, note in [('501-character note', 'x' * 501), ('other control character in note', 'No onions\x01')]:
        before_validation = count_orders('contact-validation')
        response = submit(order_request(tenant='contact-validation', note=note), 'contact-validation')
        check('Checkout rejects ' + label, response[0] == 400 and
            count_orders('contact-validation') == before_validation, response[:3])

    phone, unavailable = valid_quote(paymentMethod='phone')
    check('Phone quote clearly cannot submit before merchant Stripe is ready',
        unavailable['canSubmit'] is False and bool(unavailable['unavailableReason']))
    before = count_orders()
    response = submit(order_request(phone))
    check('Phone submission creates no order and returns conflict',
        response[0] == 409 and count_orders() == before, response[:3])
    for label, request in [('delivery needs address', order_request(delivery, address='')),
        ('pickup rejects delivery address', order_request(address='Unwanted address')),
        ('contact name required', order_request(customerName='')),
        ('valid contact phone required', order_request(phone='invalid')),
        ('tracking key must be unguessable', order_request(trackingKey='tiny')),
        ('legacy tracking format is read-only', order_request(trackingKey=str(uuid.uuid4()) + str(uuid.uuid4()))),
        ('request key required', order_request(requestKey=''))]:
        response = submit(request)
        check('Submission rejects ' + label, response[0] == 400 and count_orders() == before, response[:3])

    original = order_request(dine_in)
    response = submit(original)
    receipt = response[1]
    check('Pay-staff order is created pending acceptance and remains unpaid',
        response[0] == 201 and receipt['status'] == 'new' and receipt['paymentStatus'] == 'unpaid', response[:3])
    check('Receipt does not expose customer contact, note or tracking credentials', receipt_private(receipt))
    row = sql('SELECT status,fulfillment,payload_json,tracking_hash FROM bartide_enhanced_orders WHERE id=?',
              (receipt['orderId'],))[0]
    stored = json.loads(row[2])
    check('Submitted table, tip and legacy unpaid status persist durably',
        row[0:2] == ('new', 'dine-in') and stored['tip_cents'] == 200 and
        stored['table_id'] == TABLE_ID and stored['table_label'] == 'Table 1' and
        stored['payment_status'] == 'unpaid' and stored['payment_method'] == 'staff')
    check('Tracking credential is hashed and absent from stored payload',
        row[3] != original['trackingKey'] and original['trackingKey'] not in row[2])
    response = submit(original)
    check('Same request key retry returns original receipt without duplicate',
        response[0] == 200 and response[1] == receipt and count_orders() == before + 1)
    for label, mutate in [
        ('tip', lambda r: r['order'].update(tipPercent=25)),
        ('table', lambda r: r['order'].update(tableToken=SECOND_TABLE)),
        ('payment choice', lambda r: r['order'].update(paymentMethod='phone')),
        ('contact name', lambda r: r.update(customerName='Changed Synthetic Guest')),
        ('phone', lambda r: r.update(phone='3055550101')),
        ('note', lambda r: r.update(note='Different synthetic note')),
        ('tracking secret', lambda r: r.update(trackingKey=uuid.uuid4().hex + uuid.uuid4().hex))]:
        changed = copy.deepcopy(original)
        mutate(changed)
        response = submit(changed)
        check('Idempotency key conflicts when changing ' + label,
            response[0] == 409 and count_orders() == before + 1, response[:3])
    response = call('/api/v1/restaurants/bistro/track',
        {'orderId': receipt['orderId'], 'trackingKey': original['trackingKey']})
    check('Correct private tracking key returns allowlisted current receipt',
        response[0] == 200 and response[1] == receipt and receipt_private(response[1]))
    for label, tenant, tracking_key in [('wrong secret', 'bistro', uuid.uuid4().hex * 2),
                                      ('different venue', 'foreign', original['trackingKey'])]:
        response = call('/api/v1/restaurants/' + tenant + '/track',
            {'orderId': receipt['orderId'], 'trackingKey': tracking_key})
        check('Tracking rejects ' + label, response[0] == 404 and PRIVATE not in response[2])

    stale = order_request()
    old_menu = menu()
    changed_menu = menu()
    changed_menu['items'][0]['price_cents'] = 1101
    sql('UPDATE bartide_customers SET menu_json=?,version=version+1 WHERE id=?',
        (json.dumps(changed_menu), 'bistro'))
    before = count_orders()
    response = submit(stale)
    check('Price change invalidates the prior quote fingerprint without creating an order',
        response[0] == 409 and count_orders() == before, response[:3])
    sql('UPDATE bartide_customers SET menu_json=?,version=version+1 WHERE id=?',
        (json.dumps(old_menu), 'bistro'))
    invalid_fingerprint = order_request(quoteFingerprint='0' * 64)
    response = submit(invalid_fingerprint)
    check('Forged quote fingerprint is rejected without order creation', response[0] == 409 and count_orders() == before)
    blocked = order_request()
    alter_config(lambda c: c['blocked_item_ids'].append('dish-1'))
    response = submit(blocked)
    check('Submission rechecks current item availability', response[0] in (400, 409) and count_orders() == before)
    alter_config(lambda c: c['blocked_item_ids'].remove('dish-1'))
    paused = order_request()
    alter_config(lambda c: c.update(accepting_orders=False))
    response = submit(paused)
    check('Submission respects a venue closing orders after quoting', response[0] == 409 and count_orders() == before)
    alter_config(lambda c: c.update(accepting_orders=True))
    alter_config(lambda c: c['checkout'].update(tips_enabled=False))
    check('Merchant tips-off prevents customer tip injection', quote(tipPercent=15)[1][0] == 400)
    alter_config(lambda c: c['checkout'].update(tips_enabled=True))
    for flag, request in [('pickup_enabled', {'fulfillment': 'pickup'}),
                          ('pay_staff_enabled', {'paymentMethod': 'staff'})]:
        alter_config(lambda c, name=flag: c['checkout'].update({name: False}))
        check('Quote honors merchant ' + flag + ' gate', quote(**request)[1][0] == 400)
        alter_config(lambda c, name=flag: c['checkout'].update({name: True}))
    alter_config(lambda c: c['checkout'].update(dine_in_enabled=False))
    check('Dine-in disabled makes existing table link unavailable',
        quote(fulfillment='dine-in', tableToken=TABLE)[1][0] == 404)
    alter_config(lambda c: c['checkout'].update(dine_in_enabled=True))
    alter_config(lambda c: c.update(tax_basis_points=None))
    check('Unconfirmed tax configuration cannot accept orders', quote()[1][0] == 409)
    alter_config(lambda c: c.update(tax_basis_points=700))

    delivered_request = order_request(delivery)
    delivered = submit(delivered_request)
    check('Delivery submission persists its own order without a table association', delivered[0] == 201 and
        delivered[1]['quote']['tableLabel'] is None and delivered[1]['paymentStatus'] == 'unpaid')
    changed_address = copy.deepcopy(delivered_request)
    changed_address['address'] = PRIVATE + ' 456 Changed Street'
    before_delivery_retry = count_orders()
    check('Idempotency key also binds the delivery address', submit(changed_address)[0] == 409 and
        count_orders() == before_delivery_retry)

    # Ported historical rows lack the new receipt/tip/table properties. Preserve
    # those rows byte-for-byte and prove the public read adapter applies defaults.
    historical = copy.deepcopy(stored)
    historical_id = str(uuid.uuid4())
    historical_key = str(uuid.uuid4()) + str(uuid.uuid4())
    historical.update(id=historical_id, number='HISTORICAL', fulfillment='pickup')
    for name in ('dotnet_receipt', 'tip_cents', 'table_id', 'table_label', 'payment_method'):
        historical.pop(name, None)
    historical['total_cents'] = historical['subtotal_cents'] + historical['tax_cents'] + historical['delivery_fee_cents']
    historical_json = json.dumps(historical)
    sql('INSERT INTO bartide_enhanced_orders(id,tenant_id,request_key,request_hash,tracking_hash,payload_json,status,fulfillment,version,created_at,updated_at) '
        'VALUES (?,?,?,?,?,?,?,?,0,?,?)',
        (historical_id, 'bistro', str(uuid.uuid4()), 'synthetic-history-hash', hashlib.sha256(historical_key.encode()).hexdigest(),
         historical_json, 'new', 'pickup', NOW, NOW))
    historical_response = call('/api/v1/restaurants/bistro/track',
        {'orderId': historical_id, 'trackingKey': historical_key})
    check('Legacy 72-character UUID-pair tracking key resolves its hashed historical order',
        len(historical_key) == 72 and historical_response[0] == 200, historical_response[:3])
    check('Legacy orders remain readable with no tip and no table', historical_response[0] == 200 and
        historical_response[1]['quote']['tipCents'] == 0 and historical_response[1]['quote']['tableLabel'] is None and
        historical_response[1]['quote']['paymentMethod'] == 'staff' and receipt_private(historical_response[1]))
    for label, candidate in [('wrong UUID pair', str(uuid.uuid4()) + str(uuid.uuid4())),
                             ('truncated UUID pair', historical_key[:-1]), ('extended UUID pair', historical_key + 'a')]:
        response = call('/api/v1/restaurants/bistro/track', {'orderId': historical_id, 'trackingKey': candidate})
        check('Historical tracking rejects ' + label, response[0] == 404 and PRIVATE not in response[2])
    check('Reading a legacy order never rewrites its stored payload',
        sql('SELECT payload_json FROM bartide_enhanced_orders WHERE id=?', (historical_id,))[0][0] == historical_json)

    path = '/api/v1/tenants/bistro/ordering'
    check('Anonymous owner controls require authentication', call(path)[0] == 401)
    for person in ('bob', 'staff'):
        check(person.title() + ' cannot read owner ordering details', call(path, token=owner_tokens[person])[0] == 403)
        response = call(path + '/tables', {'expectedVersion': 0, 'label': 'Unauthorized'}, token=owner_tokens[person])
        check(person.title() + ' cannot create venue tables', response[0] == 403)
    workspace = call(path, token=owner)
    check('Active owner can view private customer details for fulfillment', workspace[0] == 200 and
        workspace[1]['canManage'] and any(o['customerName'].startswith(PRIVATE) for o in workspace[1]['orders']))
    check('Explicit configured platform owner can access ordering', call(path, token=owner_tokens['platform'])[0] == 200)
    for tenant in ('paused', 'draft', 'disabled', 'other-vertical'):
        check('Owner ordering rejects unavailable venue ' + tenant,
            call('/api/v1/tenants/' + tenant + '/ordering', token=owner)[0] == (403 if tenant == 'disabled' else 404))
    version = workspace[1]['configVersion']
    cfg_before = json.loads(sql('SELECT settings_json FROM bartide_enhanced_configs WHERE tenant_id=?', ('bistro',))[0][0])
    response = call(path + '/tables', {'expectedVersion': version, 'label': 'Patio 4'}, token=owner)
    check('Owner table creation increments configuration version', response[0] == 200 and
        response[1]['configVersion'] == version + 1 and len(response[1]['tables']) == 4, response[:3])
    created = next(t for t in response[1]['tables'] if t['label'] == 'Patio 4')
    check('Server assigns opaque unique table identity and token',
        created['enabled'] is True and bool(created['id']) and len(created['token']) >= 32 and
        created['token'] not in (TABLE, SECOND_TABLE, DISABLED_TABLE))
    cfg_after = json.loads(sql('SELECT settings_json FROM bartide_enhanced_configs WHERE tenant_id=?', ('bistro',))[0][0])
    check('Table changes preserve unknown outer and checkout configuration',
        cfg_after['preserve_future_config'] == cfg_before['preserve_future_config'] and
        cfg_after['checkout']['preserve_future_checkout'] == cfg_before['checkout']['preserve_future_checkout'] and
        cfg_after['private_test_marker'] == PRIVATE)
    check('Stale owner form cannot overwrite newer table settings',
        call(path + '/tables', {'expectedVersion': version, 'label': 'Stale'}, token=owner)[0] == 409)
    version += 1
    check('Table labels are case-insensitively unique',
        call(path + '/tables', {'expectedVersion': version, 'label': 'pAtIo 4'}, token=owner)[0] == 409)
    check('A foreign table ID cannot be toggled in this tenant',
        call(path + '/tables/' + FOREIGN_TABLE_ID, {'expectedVersion': version, 'enabled': False}, token=owner)[0] == 404)
    response = call(path + '/tables/' + created['id'], {'expectedVersion': version, 'enabled': False}, token=owner)
    check('Owner can disable a table with current version', response[0] == 200 and
        response[1]['configVersion'] == version + 1 and
        next(t for t in response[1]['tables'] if t['id'] == created['id'])['enabled'] is False)
    check('Disabled generated QR stops resolving immediately',
        call('/api/v1/restaurants/bistro/menu?table=' + created['token'])[0] == 404 and
        call('/api/v1/restaurants/bistro/tables/' + created['token'] + '/qr')[0] == 404)
    check('Stale table toggle is rejected',
        call(path + '/tables/' + created['id'], {'expectedVersion': version, 'enabled': True}, token=owner)[0] == 409)
    sql('UPDATE bartide_customers SET user_id=? WHERE id=?', ('supabase:' + BOB, 'bistro'))
    check('Table mutation rechecks durable ownership after sign-in',
        call(path + '/tables', {'expectedVersion': version + 1, 'label': 'Revoked owner'}, token=owner)[0] == 403)
    sql('UPDATE bartide_customers SET user_id=? WHERE id=?', ('supabase:' + ALICE, 'bistro'))

    seed('table-limit')
    alter_config(lambda c: c['checkout'].update(tables=[{'id': str(uuid.UUID(int=n)),
        'label': 'Capacity table ' + str(n), 'token': format(n, '064x'), 'enabled': True}
        for n in range(1000, 1099)]), tenant='table-limit')
    table_limit_path = '/api/v1/tenants/table-limit/ordering/tables'
    response = call(table_limit_path, {'expectedVersion': 1, 'label': 'Final allowed table'}, token=owner)
    check('Owner may create the 100th table', response[0] == 200 and len(response[1]['tables']) == 100)
    response = call(table_limit_path, {'expectedVersion': 2, 'label': 'Too many tables'}, token=owner)
    check('Owner cannot exceed the 100-table boundary', response[0] == 400 and
        sql('SELECT version FROM bartide_enhanced_configs WHERE tenant_id=?', ('table-limit',))[0][0] == 2)

    concurrent_request = order_request()
    before = count_orders()
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        responses = list(pool.map(submit, [copy.deepcopy(concurrent_request) for _ in range(4)]))
    check('Concurrent identical retries create exactly one order',
        sorted(r[0] for r in responses) == [200, 200, 200, 201] and count_orders() == before + 1 and
        len({r[1]['orderId'] for r in responses}) == 1, [r[:2] for r in responses])

    # Capacity applies only to active delivery orders. Existing pickup/dine-in
    # orders below must not consume delivery slots; concurrent deliveries do.
    alter_config(lambda c: c.update(delivery_capacity=1), tenant='capacity')
    pickup_request = order_request(tenant='capacity')
    dine_request = order_request(dine_in, tenant='capacity')
    check('Capacity fixture accepts pickup and dine-in orders',
        submit(pickup_request, 'capacity')[0] == 201 and submit(dine_request, 'capacity')[0] == 201)
    deliveries = [order_request(delivery, tenant='capacity') for _ in range(2)]
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        responses = list(pool.map(lambda r: submit(r, 'capacity'), deliveries))
    check('Concurrent deliveries respect one delivery slot independent of other fulfillment modes',
        sorted(r[0] for r in responses) == [201, 409] and count_orders('capacity') == 3,
        [r[:2] for r in responses])
    sql('UPDATE bartide_enhanced_orders SET status=? WHERE tenant_id=? AND fulfillment=?',
        ('completed', 'capacity', 'delivery'))
    check('Completed delivery releases its capacity slot', submit(order_request(delivery, tenant='capacity'), 'capacity')[0] == 201)

    # Reuse a valid synthetic payload while arranging exactly 99 active rows;
    # only the final two writes are performed concurrently through the API.
    sample = sql('SELECT payload_json FROM bartide_enhanced_orders WHERE tenant_id=? LIMIT 1', ('capacity',))[0][0]
    active = sql("SELECT COUNT(*) FROM bartide_enhanced_orders WHERE tenant_id=? AND status NOT IN ('completed','cancelled')", ('capacity',))[0][0]
    for n in range(99 - active):
        ident = str(uuid.uuid4())
        sql('INSERT INTO bartide_enhanced_orders(id,tenant_id,request_key,request_hash,tracking_hash,payload_json,status,fulfillment,version,created_at,updated_at) '
            'VALUES (?,?,?,?,?,?,?, ?,0,?,?)',
            (ident, 'capacity', ident, 'synthetic-hash', 'synthetic-tracking-hash', sample, 'new', 'pickup', NOW, NOW))
    requests = [order_request(tenant='capacity') for _ in range(2)]
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        responses = list(pool.map(lambda r: submit(r, 'capacity'), requests))
    check('Concurrent submissions enforce the 100 active-order cap transactionally',
        sorted(r[0] for r in responses) == [201, 409] and
        sql("SELECT COUNT(*) FROM bartide_enhanced_orders WHERE tenant_id=? AND status NOT IN ('completed','cancelled')", ('capacity',))[0][0] == 100,
        [r[:2] for r in responses])
finally:
    for proc in PROCESSES:
        if proc.poll() is None:
            proc.terminate()
            try:
                proc.wait(timeout=30)
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.wait(timeout=10)
    PROVIDER.shutdown()
    PROVIDER.server_close()
    for log in LOGS:
        log.close()
    (RUN / 'results.json').write_text(json.dumps(RESULTS, indent=2), encoding='utf-8')
    print('Restaurant ordering evidence: ' + str(RUN), flush=True)

print(str(len(RESULTS)) + ' restaurant-ordering checks passed; synthetic local data only.', flush=True)
