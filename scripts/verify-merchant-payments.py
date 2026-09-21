"""Merchant integration against the official SDK, local fake Stripe, real API/SQLite.

All data and credentials are synthetic. The fake provider binds strictly to loopback;
processes run copied DLLs and never contact a real payment or identity service.
"""
import concurrent.futures
import copy
from datetime import datetime, timezone
import hashlib
import hmac
import http.cookiejar
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import threading
import time
import urllib.parse
import urllib.request
import uuid
import restaurant_test_support as s

s.RUN = s.ROOT / '.tools/merchant-payments-verification' / datetime.now(timezone.utc).strftime('%Y%m%d-%H%M%S-%f')
s.RUN.mkdir(parents=True); s.DB = s.RUN / 'synthetic.db'
check = s.check
SNAPSHOT = 'whsec_synthetic_connect_000000000'
THIN = 'whsec_synthetic_accounts_000000000'
PLATFORM = 'acct_syntheticplatform'
LOCK = threading.RLock()
CALLS, ACCOUNTS, SESSIONS, KEYS, REFUNDS = [], {}, {}, {}, {}
FAIL_ONCE = set()
BAD_PAYMENT = {}
BAD_CHARGE = {}


class StripeFake(BaseHTTPRequestHandler):
    def log_message(self, *args): pass
    def reply(self, status, body):
        self.send_response(status); self.send_header('Content-Type', 'application/json'); self.end_headers()
        self.wfile.write(json.dumps(body).encode())
    def handle_request(self):
        raw = self.rfile.read(int(self.headers.get('Content-Length', 0)))
        if self.headers.get('Content-Type', '').startswith('application/json'):
            body = json.loads(raw or b'{}')
        else:
            body = {k: v[0] for k, v in urllib.parse.parse_qs(raw.decode(), keep_blank_values=True).items()}
        path = urllib.parse.urlsplit(self.path).path
        account = self.headers.get('Stripe-Account')
        key = self.headers.get('Idempotency-Key')
        with LOCK:
            CALLS.append({'method': self.command, 'path': path, 'account': account, 'key': key, 'body': body})
            if self.headers.get('Authorization') != 'Bearer rk_test_synthetic_000000000':
                return self.reply(401, {'error': {'type': 'authentication_error', 'message': 'synthetic key'}})
            if path == '/v2/core/accounts' and self.command == 'POST':
                if key in KEYS: result = ACCOUNTS[KEYS[key]]
                else:
                    ident = 'acct_merchant' + str(len(ACCOUNTS) + 1)
                    result = {'id': ident, 'object': 'v2.core.account', 'livemode': False, 'closed': False,
                        'dashboard': 'full', 'identity': {'country': 'us'}, 'metadata': body.get('metadata'),
                        'configuration': {'merchant': {'applied': True, 'capabilities': {'card_payments': {'status': 'active'}}}},
                        'defaults': {'responsibilities': {'fees_collector': 'stripe', 'losses_collector': 'stripe'}}, 'requirements': {}}
                    ACCOUNTS[ident] = result; KEYS[key] = ident
                if 'account' in FAIL_ONCE:
                    FAIL_ONCE.remove('account'); return self.reply(500, {'error': {'type': 'api_error', 'message': 'uncertain create'}})
                return self.reply(200, result)
            if path.startswith('/v2/core/accounts/') and self.command == 'GET':
                return self.reply(200, ACCOUNTS[path.rsplit('/', 1)[-1]])
            if path == '/v2/core/account_links':
                return self.reply(200, {'object': 'v2.core.account_link', 'account': body['account'], 'livemode': False,
                    'url': 'https://connect.stripe.com/setup/synthetic', 'expires_at': '2026-09-21T00:00:00Z'})
            if path == '/v1/checkout/sessions' and self.command == 'POST':
                if key in KEYS: result = SESSIONS[KEYS[key]]
                else:
                    ident = 'cs_test_synthetic' + str(len(SESSIONS) + 1)
                    metadata = {k[9:-1]: v for k, v in body.items() if k.startswith('metadata[')}
                    total = sum(int(v) * int(body[k.replace('[price_data][unit_amount]', '[quantity]')])
                        for k, v in body.items() if k.endswith('[price_data][unit_amount]'))
                    result = {'id': ident, 'object': 'checkout.session', 'livemode': False, 'mode': 'payment',
                        'client_reference_id': body['client_reference_id'], 'metadata': metadata, 'amount_total': total,
                        'currency': 'usd', 'status': 'open', 'payment_status': 'unpaid', 'expires_at': int(body['expires_at']),
                        'url': 'https://checkout.stripe.com/c/pay/' + ident, '_account': account}
                    SESSIONS[ident] = result; KEYS[key] = ident
                if 'checkout' in FAIL_ONCE:
                    FAIL_ONCE.remove('checkout'); return self.reply(500, {'error': {'type': 'api_error', 'message': 'uncertain checkout'}})
                return self.reply(200, result)
            if path.startswith('/v1/checkout/sessions/'):
                ident = path.split('/')[4]; result = SESSIONS[ident]
                if result['_account'] != account: return self.reply(403, {'error': {'type': 'invalid_request_error', 'message': 'wrong account'}})
                if path.endswith('/expire'):
                    if 'expire_paid' in FAIL_ONCE: FAIL_ONCE.remove('expire_paid'); pay(ident)
                    elif result['status'] == 'open': result.update(status='expired', payment_status='unpaid')
                return self.reply(200, result)
            if path.startswith('/v1/payment_intents/'):
                ident = path.rsplit('/', 1)[-1]
                session = next(x for x in SESSIONS.values() if x.get('payment_intent') == ident)
                if session['_account'] != account: return self.reply(403, {'error': {'type': 'invalid_request_error', 'message': 'wrong account'}})
                amount = session['amount_total']; charge = 'ch_' + ident[3:]
                refunds = REFUNDS.get(charge, [])
                result = {'id': ident, 'object': 'payment_intent', 'livemode': False, 'status': 'succeeded', 'currency': 'usd',
                    'amount': amount, 'amount_received': amount, 'metadata': session['metadata'],
                    'latest_charge': {'id': charge, 'object': 'charge', 'livemode': False, 'status': 'succeeded', 'paid': True,
                        'captured': True, 'amount': amount, 'amount_captured': amount, 'currency': 'usd', 'payment_intent': ident,
                        'amount_refunded': sum(x['amount'] for x in refunds if x['status'] == 'succeeded'),
                        'payment_method_details': {'type': 'card', 'card': {'brand': 'visa', 'last4': '4242'}}}}
                result.update(BAD_PAYMENT)
                result['latest_charge'].update(BAD_CHARGE)
                return self.reply(200, result)
            if path == '/v1/refunds' and self.command == 'GET':
                charge = urllib.parse.parse_qs(urllib.parse.urlsplit(self.path).query)['charge'][0]
                return self.reply(200, {'object': 'list', 'data': REFUNDS.get(charge, []), 'has_more': False, 'url': '/v1/refunds'})
            return self.reply(404, {'error': {'type': 'invalid_request_error', 'message': 'Unknown local fixture path ' + path}})
    do_GET = do_POST = handle_request


FAKE = ThreadingHTTPServer(('127.0.0.1', 0), StripeFake)
threading.Thread(target=FAKE.serve_forever, daemon=True).start()
tokens = {}
WEB = 'http://127.0.0.1:' + str(s.port())
forms_spec = importlib.util.spec_from_file_location('merchant_native_forms', Path(__file__).with_name('verify-management-web.py'))
forms_module = importlib.util.module_from_spec(forms_spec); forms_spec.loader.exec_module(forms_module)
Forms = forms_module.Forms


def web(client, path, fields=None, headers=None):
    raw = None if fields is None else urllib.parse.urlencode(fields).encode()
    hdrs = {} if fields is None else {'Content-Type': 'application/x-www-form-urlencoded', 'Origin': WEB}
    try: response = client.open(urllib.request.Request(WEB + path, data=raw, headers={**hdrs, **(headers or {})}), timeout=35)
    except urllib.error.HTTPError as error: response = error
    with response: return response.status, response.read().decode(), response.headers


def native_form(client, path, action):
    response = web(client, path)
    check('SSR page loads ' + path, response[0] == 200, response[:2])
    forms = [x for x in Forms(response[1]).forms if x['action'].endswith(action)]
    check('Native form exists ' + action, len(forms) == 1, response[:2])
    selected = forms[0]; selected['fields'] = {k: v or '' for k, v in selected['fields'].items()}
    return selected, response


def browser(person=None):
    client = urllib.request.build_opener(urllib.request.ProxyHandler({}), s.NoRedirect(), urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
    if person:
        form, _ = native_form(client, '/signin', '/auth/session/signin')
        response = web(client, form['action'], {**form['fields'], 'email': person + '@example.invalid', 'password': s.PASSWORD})
        check('Native sign in ' + person, response[0] in (302, 303), response[:2])
    return client


def ssr_checks():
    project = 'TideCasa.Blazor'; copied = s.RUN / project
    source = Path(os.environ.get('MERCHANT_ARTIFACTS', s.ROOT / '.tools/merchant-isolated-build')) / 'bin' / project / 'debug'
    shutil.copytree(source, copied, ignore=shutil.ignore_patterns("libSkiaSharp.pdb"))
    env = os.environ.copy()
    for name in list(env):
        if any(x in name.upper() for x in ('AUTH__', 'API__', 'STORAGE__', 'DATAPROTECTION__', 'SUPABASE', 'STRIPE', 'RESEND')): env.pop(name)
    env.update({'ASPNETCORE_ENVIRONMENT': 'Development', 'ASPNETCORE_URLS': WEB, 'Api__BaseUrl': s.API,
        'Auth__ApiBaseUrl': s.API, 'Auth__PublicBaseUrl': WEB, 'Auth__Enabled': 'true',
        'DataProtection__KeysPath': str(s.RUN / 'keys')})
    log = (s.RUN / 'web.log').open('w', encoding='utf-8'); s.LOGS.append(log)
    proc = subprocess.Popen([str(s.SDK), str(copied / (project + '.dll'))], cwd=s.ROOT / project, env=env,
        stdout=log, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
    s.PROCESSES.append(proc)
    for _ in range(300):
        if proc.poll() is not None: raise RuntimeError('Local Blazor fixture stopped')
        try:
            if web(browser(), '/health')[0] == 200: break
        except OSError: pass
        time.sleep(.2)
    owner_browser, other, anon = browser('alice'), browser('bob'), browser()
    check('Anonymous payment workspace requires sign in', web(anon, '/workspace/bistro/payments')[0] in (302, 303))
    check('Other owner cannot see payment workspace', web(other, '/workspace/bistro/payments')[0] == 403)
    form, page = native_form(owner_browser, '/workspace/bistro/payments', '/onboarding')
    check('Private setup page is not cached', 'no-store' in page[2].get('Cache-Control', ''))
    check('Owner setup explains separate restaurant funds and sandbox', 'connected Stripe account' in page[1] and 'No real charges' in page[1])
    missing = {k: v for k, v in form['fields'].items() if k != '__RequestVerificationToken'}
    check('Onboarding form rejects missing CSRF', web(owner_browser, form['action'], missing)[0] == 400)
    check('Onboarding form rejects foreign origin', web(owner_browser, form['action'], form['fields'], {'Origin': 'https://foreign.example.invalid'})[0] == 400)
    unchecked = {k: v for k, v in form['fields'].items() if k != 'confirm_us'}
    check('US confirmation required in native form', 'notice=invalid' in web(owner_browser, form['action'], unchecked)[2].get('Location', ''))
    response = web(owner_browser, form['action'], {**form['fields'], 'confirm_us': 'true'})
    check('Native form redirects only to hosted Stripe setup', response[0] in (302, 303) and response[2].get('Location', '').startswith('https://connect.stripe.com/'))
    response = web(owner_browser, '/merchant-payments/bistro/refresh')
    check('Expired hosted link refreshes after owner authentication', response[0] in (302, 303) and response[2].get('Location', '').startswith('https://connect.stripe.com/'))
    check('Anonymous hosted refresh requires sign in', '/signin?' in web(anon, '/merchant-payments/bistro/refresh')[2].get('Location', ''))
    page = web(anon, '/order/bistro')
    check('Public ordering offers sandbox phone checkout', page[0] == 200 and 'Secure card checkout' in page[1] and 'merchant-payments.js' in page[1])


def pay(session):
    SESSIONS[session].update(status='complete', payment_status='paid', payment_intent='pi_' + session[8:])


def launch(extra=None):
    copied = s.RUN / 'TideCasa.Api'
    if not copied.exists():
        source = Path(os.environ.get('MERCHANT_ARTIFACTS', s.ROOT / '.tools/merchant-isolated-build')) / 'bin/TideCasa.Api/debug'
        shutil.copytree(source, copied, ignore=shutil.ignore_patterns("libSkiaSharp.pdb"))
    env = os.environ.copy()
    for name in list(env):
        if any(x in name.upper() for x in ('STRIPE', 'RESEND', 'SUPABASE', 'CLOUDFLARE', 'AUTH__', 'ORDERING__', 'STORAGE__', 'MERCHANTPAYMENTS__', 'BILLING__')): env.pop(name)
    env.update({'ASPNETCORE_ENVIRONMENT': 'Development', 'ASPNETCORE_URLS': s.API,
        'Storage__DatabasePath': str(s.DB), 'Auth__Enabled': 'true', 'Auth__AllowLocalTestProvider': 'true',
        'Auth__SupabaseUrl': f'http://127.0.0.1:{s.PROVIDER.server_port}', 'Auth__PublishableKey': s.KEY,
        'Auth__PlatformOwnerUserId': 'supabase:' + s.PLATFORM, 'ReverseProxy__KnownClientProxy': '127.0.0.1',
        'MerchantPayments__RestrictedKey': 'rk_test_synthetic_000000000', 'MerchantPayments__PlatformAccountId': PLATFORM,
        'MerchantPayments__PublicBaseUrl': 'https://ordering.example.invalid', 'MerchantPayments__ApiBaseUrl': f'http://127.0.0.1:{FAKE.server_port}',
        'MerchantPayments__AllowLocalTestProvider': 'true', 'MerchantPayments__OnboardingEnabled': 'true',
        'MerchantPayments__CheckoutEnabled': 'true', 'MerchantPayments__CardsOnlyVerified': 'true',
        'MerchantPayments__ConnectWebhookSecret': SNAPSHOT, 'MerchantPayments__AccountWebhookSecret': THIN, **(extra or {})})
    output = (s.RUN / ('api-' + str(len(s.PROCESSES)) + '.log')).open('w', encoding='utf-8'); s.LOGS.append(output)
    proc = subprocess.Popen([str(s.SDK), str(copied / 'TideCasa.Api.dll')], cwd=s.ROOT / 'TideCasa.Api', env=env,
        stdout=output, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
    s.PROCESSES.append(proc)
    for _ in range(250):
        if proc.poll() is not None: raise RuntimeError('Local API stopped; see synthetic log')
        try:
            if s.call('/health')[0] == 200: return proc
        except OSError: pass
        time.sleep(.2)
    raise RuntimeError('API startup timeout')


def api(path, body=None, person=None, headers=None): return s.call(path, body, tokens.get(person), headers)
def owner(tenant='bistro', tail=''): return f'/api/v1/tenants/{tenant}/payments/connect' + tail
def onboard(tenant='bistro', person='alice', **updates):
    return api(owner(tenant, '/onboarding'), {'requestKey': str(uuid.uuid4()), 'confirmUsBusiness': True, **updates}, person)
def checkout(request=None, tenant='bistro', headers=None):
    request = request or s.order_request(paymentMethod='phone')
    return api(f'/api/v1/restaurants/{tenant}/checkout', request, headers=headers)
def new_order(**changes):
    order = {'items': [{'itemId': 'dish-1', 'quantity': 1}], 'fulfillment': 'pickup', 'paymentMethod': 'phone', 'tipPercent': 20, **changes}
    return s.order_request(order)
def track(result, request, cancel=False):
    return api('/api/v1/restaurants/bistro/checkout/' + ('cancel' if cancel else 'track'),
        {'orderId': result['receipt']['orderId'], 'trackingKey': request['trackingKey']})
def session_for(result):
    return s.sql('SELECT session_id FROM tide_restaurant_payment_attempts WHERE order_id=?', (result['receipt']['orderId'],))[0][0]
def operation(order_id, person='alice'):
    response = api('/api/v1/tenants/bistro/ordering/operations', person=person)
    return next(x for x in response[1]['orders'] if x['order']['receipt']['orderId'] == order_id)
def change_order(order_id, action, **changes):
    current = operation(order_id)
    return api('/api/v1/tenants/bistro/ordering/orders/' + order_id,
        {'expectedVersion': current['order']['version'], 'action': action, **changes}, 'alice')
def notify(session, **changes):
    payload = {'id': 'evt_' + uuid.uuid4().hex, 'object': 'event', 'api_version': '2026-08-26.dahlia',
        'type': 'checkout.session.completed', 'livemode': False, 'account': SESSIONS[session]['_account'],
        'data': {'object': SESSIONS[session]}, **changes}
    return signed('/api/stripe/connect/webhook', payload, SNAPSHOT)
def signed(path, payload, secret, age=0):
    raw = json.dumps(payload).encode(); stamp = int(time.time()) - age
    signature = hmac.new(secret.encode(), str(stamp).encode() + b'.' + raw, hashlib.sha256).hexdigest()
    req = urllib.request.Request(s.API + path, data=raw, headers={'Content-Type': 'application/json', 'Stripe-Signature': f't={stamp},v1={signature}'})
    try: response = urllib.request.build_opener(urllib.request.ProxyHandler({})).open(req, timeout=30)
    except urllib.error.HTTPError as error: response = error
    with response: return response.status, response.read().decode()


def run():
    proc = launch({'MerchantPayments__OnboardingEnabled': 'false'})
    for args in [('bistro',), ('foreign', s.BOB), ('draft', s.ALICE, 'draft'), ('basic', s.ALICE, 'active', False)]: s.seed(*args)
    for ident, person, role in [('merchant-kitchen', s.STAFF, 'kitchen'), ('merchant-driver', s.BOB, 'driver')]:
        s.sql('INSERT INTO bartide_enhanced_members(id,tenant_id,name,email,user_id,role,active,created_at) VALUES(?,?,?,?,?,?,1,?)',
            (ident, 'bistro', ident, ident + '@example.invalid', 'supabase:' + person, role, s.NOW))
    for person in ['alice', 'bob', 'staff']:
        response = s.call('/api/v1/auth/signin', {'email': person + '@example.invalid', 'password': s.PASSWORD})
        tokens[person] = response[1]['accessToken']
    check('Default-off owner status', api(owner(), person='alice')[1]['state'] == 'disabled')
    check('Default-off blocks provider writes', onboard()[0] == 503 and not CALLS)
    check('Default-off keeps phone menu disabled', not api('/api/v1/restaurants/bistro/menu')[1]['checkout']['phonePaymentAvailable'])
    proc.terminate(); proc.wait(); proc = launch()
    check('Anonymous owner endpoint denied', api(owner())[0] == 401)
    check('Cross-owner onboarding denied', onboard(person='bob')[0] == 403)
    check('Staff onboarding denied', onboard(person='staff')[0] == 403)
    check('Draft onboarding denied', onboard('draft')[0] == 404)
    check('Unenrolled onboarding denied', onboard('basic')[0] == 403)
    check('US business acknowledgment required', onboard(confirmUsBusiness=False)[0] == 400)
    FAIL_ONCE.add('account')
    check('Uncertain account creation retained', onboard()[0] == 503)
    response = onboard(); check('Account creation safely retries saved key', response[0] == 200 and len(ACCOUNTS) == 1, response)
    posts = [x for x in CALLS if x['path'] == '/v2/core/accounts' and x['method'] == 'POST']
    check('Account retry has immutable key and parameters', posts[0]['key'] == posts[1]['key'] and posts[0]['body'] == posts[1]['body'])
    body = posts[0]['body']; check('Accounts V2 full dashboard and Stripe fees/losses', body['dashboard'] == 'full' and body['defaults']['responsibilities'] == {'fees_collector': 'stripe', 'losses_collector': 'stripe'})
    check('Only merchant configuration requested', list(body['configuration']) == ['merchant'])
    account = next(iter(ACCOUNTS))
    check('Fresh account readiness', api(owner(), person='alice')[1]['phoneCheckoutAvailable'])
    check('Public phone availability enabled after readiness', api('/api/v1/restaurants/bistro/menu')[1]['checkout']['phonePaymentAvailable'])
    request = new_order(); response = checkout(request); check('Server quote reserves checkout', response[0] == 200, response[:3]); first = response[1]; session = session_for(first)
    check('Pending phone order withheld from kitchen', first['receipt']['status'] == 'awaiting_payment' and first['receipt']['paymentStatus'] == 'pending')
    create = [x for x in CALLS if x['path'] == '/v1/checkout/sessions' and x['method'] == 'POST'][-1]
    check('Direct charge uses immutable merchant account', create['account'] == account)
    check('Cards-only allowed methods serialized without static methods', create['body'].get('allowed_payment_method_types[0]') == 'card' and not any(k.startswith('payment_method_types') for k in create['body']))
    check('Checkout contains no platform fees or transfers', not any('application_fee' in k or 'transfer_data' in k for k in create['body']))
    check('Private receipt token is absent from provider payload', request['trackingKey'] not in json.dumps(create['body']))
    check('Server amount includes optional tip', SESSIONS[session]['amount_total'] == first['receipt']['quote']['totalCents'] and first['receipt']['quote']['tipCents'] > 0)
    again = checkout(request); check('Identical checkout replay returns same order/session', again[0] == 200 and again[1]['receipt']['orderId'] == first['receipt']['orderId'] and len(SESSIONS) == 1)
    before = len(CALLS)
    bad = api('/api/v1/restaurants/bistro/checkout/track', {'orderId': first['receipt']['orderId'], 'trackingKey': 'f'*64})
    check('Wrong private capability makes no provider calls', bad[0] == 404 and len(CALLS) == before)
    check('Cross-venue receipt denied', api('/api/v1/restaurants/foreign/checkout/track', {'orderId': first['receipt']['orderId'], 'trackingKey': request['trackingKey']})[0] == 404)
    ACCOUNTS[account]['configuration']['merchant']['capabilities']['card_payments']['status'] = 'inactive'
    check('Fresh disabled capability blocks new payment', checkout(new_order())[0] == 409)
    ACCOUNTS[account]['configuration']['merchant']['capabilities']['card_payments']['status'] = 'active'
    api(owner(), person='alice')
    pay(session); BAD_PAYMENT['amount_received'] = 1
    mismatch = track(first, request)
    check('Wrong captured amount cannot mark paid', mismatch[0] == 409 and s.sql('SELECT status FROM bartide_enhanced_orders WHERE id=?', (first['receipt']['orderId'],))[0][0] == 'awaiting_payment', mismatch[:3])
    BAD_PAYMENT.clear()
    for field, wrong in [('currency', 'eur'), ('livemode', True), ('metadata', {'purpose': 'different'})]:
        BAD_PAYMENT[field] = wrong
        check('Captured payment identity mismatch rejected ' + field, track(first, request)[0] == 409)
        BAD_PAYMENT.clear()
    BAD_CHARGE['payment_method_details'] = {'type': 'card', 'card': {'wallet': {'type': 'apple_pay'}}}
    check('Wallet charge cannot satisfy cards-only confirmation', track(first, request)[0] == 409)
    BAD_CHARGE.clear()
    check('Invalid signature rejected', signed('/api/stripe/connect/webhook', {}, THIN)[0] == 400)
    check('Expired signature rejected', signed('/api/stripe/connect/webhook', {}, SNAPSHOT, 600)[0] == 400)
    check('Live notification rejected', notify(session, livemode=True)[0] == 400)
    check('Foreign account notification rejected', notify(session, account='acct_foreignunknown')[0] == 400)
    check('API-version mismatch rejected', notify(session, api_version='2020-08-27')[0] == 400)
    check('Signed notification reconciles paid order', notify(session)[0] == 202)
    paid = track(first, request); check('Verified payment enters kitchen', paid[0] == 200 and paid[1]['receipt']['status'] == 'new' and paid[1]['state'] == 'paid', paid[:3])
    check('Readback calls remain bound to merchant account', all(x['account'] == account for x in CALLS if x['path'].startswith('/v1/')))
    check('Notification inbox stores no full guest payload', s.PRIVATE not in str(s.sql('SELECT object_id FROM tide_merchant_notification_inbox')))
    check('Owner accepts paid order before refund test', change_order(first['receipt']['orderId'], 'accepted')[0] == 200)
    check('Owner starts preparation before refund test', change_order(first['receipt']['orderId'], 'preparing')[0] == 200)
    charge = 'ch_' + SESSIONS[session]['payment_intent'][3:]; intent = SESSIONS[session]['payment_intent']
    REFUNDS[charge] = [{'id': 're_synthetic1', 'object': 'refund', 'charge': charge, 'payment_intent': intent, 'amount': 200, 'currency': 'usd', 'status': 'pending'}]
    refund = track(first, request); check('Pending refund read back without claiming completion', refund[1]['state'] == 'refund_pending' and refund[1]['pendingRefundCents'] == 200)
    check('Pending refund preserves fulfillment step while blocking preparation', refund[1]['receipt']['status'] == 'preparing' and operation(first['receipt']['orderId'])['allowedActions'] == [])
    REFUNDS[charge][0]['status'] = 'failed'; refund = track(first, request); check('Failed refund does not reduce payment', refund[1]['state'] == 'paid' and refund[1]['refundedCents'] == 0)
    check('Failed refund restores available action at unchanged fulfillment step', refund[1]['receipt']['status'] == 'preparing' and 'ready' in operation(first['receipt']['orderId'])['allowedActions'])
    REFUNDS[charge][0]['status'] = 'succeeded'; refund = track(first, request); check('Partial refund read back exactly', refund[1]['state'] == 'partially_refunded' and refund[1]['refundedCents'] == 200)
    REFUNDS[charge].append({'id': 're_synthetic2', 'object': 'refund', 'charge': charge, 'payment_intent': intent, 'amount': first['receipt']['quote']['totalCents']-200, 'currency': 'usd', 'status': 'succeeded'})
    refund = track(first, request); check('Full refund retains financial result without rewriting fulfillment', refund[1]['state'] == 'refunded' and refund[1]['receipt']['status'] == 'preparing')
    active = operation(first['receipt']['orderId'])
    check('Fully refunded active order exposes only owner close action', active['allowedActions'] == ['cancelled'])
    check('Kitchen cannot close refunded active order', operation(first['receipt']['orderId'], 'staff')['allowedActions'] == [] and api('/api/v1/tenants/bistro/ordering/orders/' + first['receipt']['orderId'], {'expectedVersion': active['order']['version'], 'action': 'cancelled'}, 'staff')[0] == 409)
    check('Owner explicitly closes fully refunded active order', change_order(first['receipt']['orderId'], 'cancelled')[0] == 200)
    check('Refund reconciliation cannot reopen owner-cancelled order', track(first, request)[1]['receipt']['status'] == 'cancelled')
    request2 = new_order(); FAIL_ONCE.add('checkout'); response = checkout(request2)
    check('Unknown checkout result retains reservation', response[0] == 503)
    proc.terminate(); proc.wait(); proc = launch()
    retry = checkout(request2); check('Restart safely reuses Stripe idempotency key', retry[0] == 200 and len(SESSIONS) == 2, retry[:3])
    canceled = track(retry[1], request2, True); check('Authoritative expiry releases unpaid reservation', canceled[1]['state'] == 'expired' and canceled[1]['receipt']['status'] == 'cancelled')
    request3 = new_order(); response3 = checkout(request3); FAIL_ONCE.add('expire_paid')
    race = track(response3[1], request3, True); check('Payment wins cancellation race', race[1]['state'] == 'paid' and race[1]['receipt']['status'] == 'new')
    request4 = new_order()
    with concurrent.futures.ThreadPoolExecutor(max_workers=5) as pool: races = list(pool.map(lambda _: checkout(request4), range(5)))
    ids = {x[1]['receipt']['orderId'] for x in races if x[0] == 200}
    check('Concurrent identical requests reserve one order', len(ids) == 1 and s.sql('SELECT COUNT(*) FROM tide_restaurant_payment_attempts WHERE order_id=?', (next(iter(ids)),))[0][0] == 1)
    check('No refund-creation payout or transfer provider calls', not any(x['method'] == 'POST' and ('refunds' in x['path'] or 'payouts' in x['path'] or 'transfers' in x['path']) for x in CALLS))
    # Simulate a webhook arriving after the provider created a session but before
    # its response was committed locally. The saved request, account and metadata
    # must be sufficient; return URLs and guest assertions do not prove payment.
    early_request = new_order(); early = checkout(early_request)[1]; early_session = session_for(early)
    s.sql("UPDATE tide_restaurant_payment_attempts SET session_id=NULL,state='creating',lease_key=NULL,lease_until=NULL WHERE order_id=?", (early['receipt']['orderId'],))
    pay(early_session)
    check('Early signed notification reconciles durable attempt metadata', notify(early_session)[0] == 202 and track(early, early_request)[1]['state'] == 'paid')
    stable_id = 'evt_' + uuid.uuid4().hex
    version_before = s.sql('SELECT version FROM bartide_enhanced_orders WHERE id=?', (early['receipt']['orderId'],))[0][0]
    check('Identical notification can be acknowledged repeatedly', notify(early_session, id=stable_id)[0] == 202 and notify(early_session, id=stable_id)[0] == 202)
    check('Duplicate paid events do not mutate restaurant order twice', s.sql('SELECT version FROM bartide_enhanced_orders WHERE id=?', (early['receipt']['orderId'],))[0][0] == version_before)
    check('Altered notification replay rejected', notify(early_session, id=stable_id, type='checkout.session.expired')[0] == 400)
    late_session = session_for(retry[1]); pay(late_session)
    check('Late captured payment preserves cancellation and stays out of kitchen', notify(late_session)[0] == 202 and track(retry[1], request2)[1]['receipt']['status'] == 'cancelled' and operation(retry[1]['receipt']['orderId'])['allowedActions'] == [])
    paused_request = new_order(); paused = checkout(paused_request)[1]; paused_session = session_for(paused)
    s.sql("UPDATE bartide_customers SET status='paused' WHERE id='bistro'"); pay(paused_session)
    check('Payment after business pause requires review', notify(paused_session)[0] == 202 and s.sql('SELECT status FROM bartide_enhanced_orders WHERE id=?', (paused['receipt']['orderId'],))[0][0] == 'paid_needs_review')
    s.sql("UPDATE bartide_customers SET status='active' WHERE id='bistro'")
    s.alter_config(lambda cfg: cfg.update(delivery_capacity=1))
    delivery_requests = [new_order(fulfillment='delivery', deliveryZip='33101', items=[{'itemId': 'dish-1', 'quantity': 2}]) for _ in range(2)]
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool: delivery_results = list(pool.map(checkout, delivery_requests))
    check('Unpaid checkout reserves delivery capacity atomically', sorted(x[0] for x in delivery_results) == [200, 409], delivery_results)
    success_index = next(i for i, result in enumerate(delivery_results) if result[0] == 200)
    check('Rejected delivery creates no extra payment attempt', delivery_results[1-success_index][1]['code'] == 'delivery_full')
    canceled = track(delivery_results[success_index][1], delivery_requests[success_index], True)
    next_delivery = checkout(delivery_requests[1-success_index])
    check('Verified cancellation makes delivery place available again', canceled[1]['state'] == 'expired' and next_delivery[0] == 200)
    delivered = next_delivery[1]; delivered_request = delivery_requests[1-success_index]
    delivered_id = delivered['receipt']['orderId']; delivered_session = session_for(delivered)
    pay(delivered_session); track(delivered, delivered_request)
    check('Owner assigns driver for paid delivery', change_order(delivered_id, 'assign-driver', driverId='merchant-driver')[0] == 200)
    for stage in ['accepted', 'preparing', 'ready', 'out_for_delivery', 'completed']:
        check('Paid delivery reaches ' + stage, change_order(delivered_id, stage)[0] == 200)
    delivery_charge = 'ch_' + SESSIONS[delivered_session]['payment_intent'][3:]
    REFUNDS[delivery_charge] = [{'id': 're_completed1', 'object': 'refund', 'charge': delivery_charge,
        'payment_intent': SESSIONS[delivered_session]['payment_intent'], 'amount': 200, 'currency': 'usd', 'status': 'pending'}]
    completed = track(delivered, delivered_request)
    check('Pending refund cannot reopen completed delivery', completed[1]['receipt']['status'] == 'completed' and completed[1]['state'] == 'refund_pending' and operation(delivered_id)['allowedActions'] == [])
    REFUNDS[delivery_charge][0]['status'] = 'failed'; completed = track(delivered, delivered_request)
    check('Failed pending refund keeps completed delivery terminal', completed[1]['receipt']['status'] == 'completed' and completed[1]['state'] == 'paid')
    REFUNDS[delivery_charge][0]['status'] = 'succeeded'; completed = track(delivered, delivered_request)
    check('Partial refund keeps completed delivery terminal', completed[1]['receipt']['status'] == 'completed' and completed[1]['state'] == 'partially_refunded')
    REFUNDS[delivery_charge].append({'id': 're_completed2', 'object': 'refund', 'charge': delivery_charge,
        'payment_intent': SESSIONS[delivered_session]['payment_intent'], 'amount': delivered['receipt']['quote']['totalCents'] - 200, 'currency': 'usd', 'status': 'succeeded'})
    completed = track(delivered, delivered_request)
    check('Full refund keeps completed delivery terminal', completed[1]['receipt']['status'] == 'completed' and completed[1]['state'] == 'refunded' and operation(delivered_id)['allowedActions'] == [])
    check('Refunded terminal delivery consumes no active queue or delivery capacity', s.sql("SELECT COUNT(*) FROM bartide_enhanced_orders WHERE id=? AND status NOT IN ('completed','cancelled')", (delivered_id,))[0][0] == 0)
    fresh_delivery = new_order(fulfillment='delivery', deliveryZip='33101', items=[{'itemId': 'dish-1', 'quantity': 2}])
    check('New delivery can reserve the freed slot after completed refund', checkout(fresh_delivery)[0] == 200)
    pending_requests = [new_order() for _ in range(4)]
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        pending_results = list(pool.map(lambda req: checkout(req, headers={'X-Forwarded-For': '203.0.113.91'}), pending_requests))
    check('Per-client pending checkouts cannot exceed three concurrently', sorted(x[0] for x in pending_results) == [200, 200, 200, 429], pending_results)
    old_request = new_order(); FAIL_ONCE.add('checkout'); check('Old-unknown fixture reserves safely', checkout(old_request)[0] == 503)
    old_id = s.sql('SELECT id FROM bartide_enhanced_orders WHERE request_key=?', (old_request['requestKey'],))[0][0]
    s.sql("UPDATE tide_restaurant_payment_attempts SET created_at='2020-01-01T00:00:00Z' WHERE order_id=?", (old_id,))
    count_before = len([x for x in CALLS if x['path'] == '/v1/checkout/sessions' and x['method'] == 'POST'])
    review = checkout(old_request)
    check('Unknown session beyond provider idempotency window requires review', review[0] == 409 and review[1]['code'] == 'merchant_review')
    check('Old unknown payment never creates another provider checkout', len([x for x in CALLS if x['path'] == '/v1/checkout/sessions' and x['method'] == 'POST']) == count_before)
    thin = {'id': 'evt_' + uuid.uuid4().hex, 'type': 'v2.core.account.updated', 'object': 'v2.core.event',
        'livemode': False, 'created': datetime.now(timezone.utc).isoformat(), 'context': PLATFORM,
        'related_object': {'id': account, 'type': 'v2.core.account', 'url': '/v2/core/accounts/' + account}}
    ACCOUNTS[account]['configuration']['merchant']['capabilities']['card_payments']['status'] = 'inactive'
    thin_result = signed('/api/stripe/accounts/webhook', thin, THIN)
    check('Thin account event fetches fresh capability state', thin_result[0] == 202 and s.sql('SELECT state FROM tide_merchant_accounts WHERE tenant_id=?', ('bistro',))[0][0] == 'onboarding', thin_result)
    check('Thin and snapshot signing secrets are isolated', signed('/api/stripe/accounts/webhook', thin, SNAPSHOT)[0] == 400)
    check('Thin event cannot select foreign account context', signed('/api/stripe/accounts/webhook', {**thin, 'context': 'acct_foreignaccount'}, THIN)[0] == 400)
    ACCOUNTS[account]['configuration']['merchant']['capabilities']['card_payments']['status'] = 'active'; api(owner(), person='alice')
    ssr_checks()
    proc.terminate(); proc.wait(); proc = launch({'MerchantPayments__RestrictedKey': 'rk_live_synthetic_000000000'})
    check('Live keys fail closed', api(owner(), person='alice')[1]['state'] == 'disabled')
    proc.terminate(); proc.wait(); proc = launch({'MerchantPayments__ApiBaseUrl': 'http://192.0.2.1:9999'})
    check('Non-loopback fake provider fails closed', api(owner(), person='alice')[1]['state'] == 'disabled')


if __name__ == '__main__':
    try: run()
    finally:
        (s.RUN / 'results.json').write_text(json.dumps(s.RESULTS, indent=2), encoding='utf-8')
        (s.RUN / 'provider-calls.json').write_text(json.dumps(CALLS, indent=2), encoding='utf-8')
        for proc in s.PROCESSES:
            if proc.poll() is None: proc.terminate(); proc.wait(timeout=20)
        for output in s.LOGS: output.close()
        FAKE.shutdown(); s.PROVIDER.shutdown()
        print('Evidence: ' + str(s.RUN), flush=True)
