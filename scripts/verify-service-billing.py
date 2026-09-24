"""Exercise the real service-billing API/SQLite/standard .NET transport/Blazor against synthetic
identity and Stripe HTTP fixtures. No real Stripe account or payment is touched.
Build first; TIDE_TEST_BUILD_ROOT can select a compiled integration snapshot.
"""
import base64
import concurrent.futures
import copy
from datetime import datetime, timezone
import hashlib
import hmac
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
import restaurant_test_support as s

RUN = s.ROOT / '.tools/service-billing-verification' / datetime.now(timezone.utc).strftime('%Y%m%d-%H%M%S-%f')
RUN.mkdir(parents=True)
s.RUN, s.DB = RUN, RUN / 'synthetic.db'
spec = importlib.util.spec_from_file_location('billing_web_helpers', s.ROOT / 'scripts/verify-management-web.py')
w = importlib.util.module_from_spec(spec)
spec.loader.exec_module(w)
WEB, SECRET = w.WEB, 'whsec_tide_synthetic_signature_fixture'
VERSION = '2026-08-26.dahlia'
LOCK = threading.RLock()
OBJECTS, IDEMPOTENCY, REQUESTS = {}, {}, []
NONCARD = False
FAIL_ACCOUNT = False
SKIP_PAYMENT = False
ADVANCE_REFUND = None
OVERSIZE = False


def list_of(data):
    return {'object': 'list', 'data': copy.deepcopy(data), 'has_more': False}


class StripeFixture(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def respond(self, status, body):
        data = json.dumps(body).encode()
        self.send_response(status)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def handle_request(self):
        global ADVANCE_REFUND
        raw = self.rfile.read(int(self.headers.get('Content-Length', 0))).decode()
        parsed = urllib.parse.urlsplit(self.path)
        query = urllib.parse.parse_qs(parsed.query)
        body = {key: values[0] for key, values in urllib.parse.parse_qs(raw).items()}
        path, method = parsed.path, self.command
        with LOCK:
            REQUESTS.append({'path': path, 'method': method, 'body': body, 'version': self.headers.get('Stripe-Version'),
                'agent': self.headers.get('User-Agent'), 'account': self.headers.get('Stripe-Account'), 'key': self.headers.get('Idempotency-Key')})
            if self.headers.get('Authorization') != 'Basic ' + base64.b64encode(b'rk_test_tide_local_fixture:').decode() or self.headers.get('Stripe-Version') != VERSION or self.headers.get('Stripe-Account'):
                return self.respond(403, {'error': {'message': 'Synthetic fixture credentials/context invalid', 'type': 'invalid_request_error'}})
            if path == '/v1/account':
                if OVERSIZE:
                    return self.respond(200, {'id': 'acct_fixture', 'object': 'account', 'oversized_fixture': 'x' * (1024 * 1024)})
                return self.respond(200, {'id': 'acct_wrong' if FAIL_ACCOUNT else 'acct_fixture', 'object': 'account'})
            if path == '/v1/payment_method_configurations/pmc_fixture':
                return self.respond(200, {'id': 'pmc_fixture', 'object': 'payment_method_configuration', 'active': True,
                    'card': {'available': True, 'display_preference': {'value': 'on'}},
                    'link': {'available': True, 'display_preference': {'value': 'on' if NONCARD else 'off'}}})
            ident = path.rsplit('/', 1)[-1]
            if method == 'POST':
                idem = self.headers.get('Idempotency-Key')
                if not idem:
                    return self.respond(400, {'error': {'message': 'Missing idempotency', 'type': 'invalid_request_error'}})
                if idem in IDEMPOTENCY:
                    old = IDEMPOTENCY[idem]
                    if old['raw'] != raw or old['path'] != path:
                        return self.respond(409, {'error': {'message': 'Changed idempotency parameters', 'type': 'idempotency_error'}})
                    return self.respond(200, OBJECTS[old['id']])
                if path == '/v1/checkout/sessions':
                    # Reproduce the live account's Managed Payments default.
                    if body.get('managed_payments[enabled]') != 'false':
                        return self.respond(400, {'error': {'message': 'payment_method_configuration conflicts with default Managed Payments', 'type': 'invalid_request_error'}})
                    ident = 'cs_test_' + uuid.uuid4().hex
                    total = sum(int(value) for key, value in body.items() if key.endswith('[unit_amount]') and (not body.get('subscription_data[trial_period_days]') or key.replace('[unit_amount]', '[recurring][interval]') not in body))
                    item = {'id': ident, 'object': 'checkout.session', 'livemode': False, 'metadata': {
                        key[len('metadata['):-1]: value for key, value in body.items() if key.startswith('metadata[')},
                        'client_reference_id': body['client_reference_id'], 'mode': body['mode'], 'currency': 'usd', 'amount_total': total,
                        'payment_method_types': ['card'], 'payment_method_configuration_details': {'id': body['payment_method_configuration']},
                        'status': 'open', 'payment_status': 'unpaid', 'url': 'https://checkout.stripe.com/c/pay/' + ident}
                    OBJECTS[ident] = item
                elif path.endswith('/expire'):
                    ident = path.split('/')[-2]
                    OBJECTS[ident]['status'] = 'expired'
                elif path.startswith('/v1/subscriptions/'):
                    OBJECTS[ident]['cancel_at_period_end'] = body.get('cancel_at_period_end') == 'true'
                else:
                    return self.respond(400, {'error': {'message': 'Unexpected mutation in fixture', 'type': 'invalid_request_error'}})
                IDEMPOTENCY[idem] = {'path': path, 'raw': raw, 'id': ident}
                return self.respond(200, OBJECTS[ident])
            if path == '/v1/checkout/sessions':
                return self.respond(200, list_of([v for v in OBJECTS.values() if v['object'] == 'checkout.session' and v.get('subscription') == query.get('subscription', [None])[0]]))
            if path == '/v1/invoice_payments':
                entries = [v for v in OBJECTS.values() if v['object'] == 'invoice_payment']
                if 'invoice' in query:
                    entries = [v for v in entries if v['invoice'] == query['invoice'][0]]
                if 'payment[payment_intent]' in query:
                    entries = [v for v in entries if v['payment']['payment_intent'] == query['payment[payment_intent]'][0]]
                return self.respond(200, list_of([] if SKIP_PAYMENT else entries))
            if path == '/v1/refunds':
                return self.respond(200, list_of([v for v in OBJECTS.values() if v['object'] == 'refund' and v['charge'] == query['charge'][0]]))
            if ident in OBJECTS:
                if ident == ADVANCE_REFUND:
                    old = copy.deepcopy(OBJECTS[ident])
                    OBJECTS[ident]['status'] = 'succeeded'
                    ADVANCE_REFUND = None
                    return self.respond(200, old)
                return self.respond(200, OBJECTS[ident])
            return self.respond(404, {'error': {'message': 'Synthetic object absent', 'type': 'invalid_request_error'}})

    do_GET = do_POST = handle_request


STRIPE = ThreadingHTTPServer(('127.0.0.1', 0), StripeFixture)
threading.Thread(target=STRIPE.serve_forever, daemon=True).start()


def settle(session_id, amount=None, status='paid', renewal=False):
    with LOCK:
        session = OBJECTS[session_id]
        suffix = uuid.uuid4().hex
        sub = session.get('subscription') or 'sub_' + suffix
        customer = session.get('customer') or 'cus_' + suffix
        invoice, intent, charge, payment = ['in_' + suffix, 'pi_' + suffix, 'ch_' + suffix, 'inpay_' + suffix]
        amount = amount if amount is not None else 14900 if renewal else session['amount_total']
        if not renewal:
            session.update(status='complete', payment_status='paid', subscription=sub, customer=customer, invoice=invoice)
            OBJECTS[sub] = {'id': sub, 'object': 'subscription', 'livemode': False, 'metadata': session['metadata'], 'customer': customer,
                'status': 'trialing', 'trial_start': int(time.time()) - 2592000, 'trial_end': int(time.time()), 'cancel_at_period_end': False, 'items': list_of([{'id': 'si_' + suffix, 'quantity': 1,
                    'current_period_end': int(time.time()) + 2592000,
                    'price': {'currency': 'usd', 'unit_amount': 14900, 'recurring': {'interval': 'month', 'interval_count': 1}}}])}
        OBJECTS[sub]['latest_invoice'] = invoice
        OBJECTS[invoice] = {'id': invoice, 'object': 'invoice', 'livemode': False, 'customer': customer, 'currency': 'usd',
            'parent': {'subscription_details': {'subscription': sub}}, 'total': amount, 'amount_due': amount,
            'amount_paid': amount if status == 'paid' else 0, 'amount_remaining': 0 if status == 'paid' else amount,
            'status': status, 'status_transitions': {'paid_at': int(time.time()) if status == 'paid' else None},
            'billing_reason': 'subscription_cycle' if renewal else 'subscription_create', 'hosted_invoice_url': 'https://invoice.stripe.com/i/' + invoice}
        OBJECTS[payment] = {'id': payment, 'object': 'invoice_payment', 'livemode': False, 'invoice': invoice,
            'currency': 'usd', 'status': 'paid', 'amount_paid': amount, 'payment': {'type': 'payment_intent', 'payment_intent': intent}}
        OBJECTS[intent] = {'id': intent, 'object': 'payment_intent', 'livemode': False, 'customer': customer, 'currency': 'usd',
            'amount_received': amount, 'status': 'succeeded', 'latest_charge': charge}
        OBJECTS[charge] = {'id': charge, 'object': 'charge', 'livemode': False, 'customer': customer, 'currency': 'usd', 'payment_intent': intent,
            'amount': amount, 'amount_captured': amount, 'paid': True, 'captured': True, 'payment_method_details': {'type': 'card'}}
        return invoice, sub, charge, intent


def refund(charge, amount, status):
    ident = 're_' + uuid.uuid4().hex
    OBJECTS[ident] = {'id': ident, 'object': 'refund', 'charge': charge, 'payment_intent': OBJECTS[charge]['payment_intent'],
        'amount': amount, 'currency': 'usd', 'status': status}
    return ident


def event(ident, kind='invoice.paid', event_id=None, overrides=None, stale=False, bad=False):
    value = {'id': event_id or 'evt_' + uuid.uuid4().hex, 'object': 'event', 'api_version': VERSION, 'livemode': False,
        'type': kind, 'data': {'object': {'id': ident, 'object': OBJECTS[ident]['object']}}, **(overrides or {})}
    raw = json.dumps(value, separators=(',', ':')).encode()
    timestamp = int(time.time()) - (1000 if stale else 0)
    signature = hmac.new(SECRET.encode(), str(timestamp).encode() + b'.' + raw, hashlib.sha256).hexdigest()
    headers = {'Content-Type': 'application/json', 'Stripe-Signature': f't={timestamp},v1=' + ('0' * 64 if bad else signature)}
    req = urllib.request.Request(s.API + '/api/v1/webhooks/stripe/service', data=raw, headers=headers)
    try:
        response = urllib.request.build_opener(urllib.request.ProxyHandler({}), s.NoRedirect()).open(req, timeout=100)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        return response.status, response.read().decode(), value['id']


def launch(project, address, extra, suffix=''):
    snapshot = RUN / 'assemblies' / project
    if not snapshot.exists():
        shutil.copytree(Path(os.environ.get('TIDE_TEST_BUILD_ROOT', str(s.ROOT))) / project / 'bin/Debug/net10.0', snapshot, ignore=shutil.ignore_patterns("libSkiaSharp.pdb"))
    env = os.environ.copy()
    for name in list(env):
        if any(word in name.upper() for word in ('STRIPE', 'SERVICEBILLING', 'MERCHANT', 'RESEND', 'SUPABASE', 'CLOUDFLARE', 'AUTH__', 'ORDERING__', 'STORAGE__', 'API__', 'DATAPROTECTION__', 'MEDIA__')): env.pop(name)
    env.update({'ASPNETCORE_ENVIRONMENT': 'Development', 'ASPNETCORE_URLS': address,
                'Stripe__CheckoutEnabled': 'false', 'Stripe__InvoicesEnabled': 'false',
                'ServiceBilling__RestrictedKey': '', 'ServiceBilling__WebhookSecret': '', 'ServiceBilling__LiveEnabled': 'false',
                'ServiceBilling__CheckoutEnabled': 'false', 'ServiceBilling__CardsOnlyVerified': 'false', **extra})
    log = (RUN / (project + suffix + '.log')).open('w', encoding='utf-8')
    s.LOGS.append(log)
    process = subprocess.Popen([str(s.SDK), str(snapshot / (project + '.dll'))], cwd=s.ROOT / project,
        env=env, stdout=log, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
    s.PROCESSES.append(process)
    deadline = time.monotonic() + 120
    while time.monotonic() < deadline:
        if process.poll() is not None: raise RuntimeError('Fixture startup failed: ' + project)
        try:
            with urllib.request.build_opener(urllib.request.ProxyHandler({})).open(address + '/health', timeout=2) as response:
                if response.status == 200: return process
        except (OSError, urllib.error.URLError): pass
        time.sleep(.25)
    raise TimeoutError('Fixture startup failed')


def api(tenant, suffix='', body=None, token=None):
    return s.call('/api/v1/tenants/' + tenant + '/billing' + suffix, body, token or OWNER)


def checkout(tenant, stores=False, code=None, **overrides):
    quote = api(tenant, '/quote', {'appStores': stores, 'referralCode': code})
    if quote[0] != 200: raise AssertionError('Fixture quote: ' + str(quote[:3]))
    body = {'requestId': str(uuid.uuid4()), 'appStores': stores, 'referralCode': code,
        'quoteFingerprint': quote[1]['fingerprint'], 'termsVersion': quote[1]['termsVersion'], 'acceptedTerms': True, **overrides}
    return api(tenant, '/checkout', body), body


def session_for(order):
    return s.sql('SELECT session_id FROM tide_service_orders WHERE id=?', (order,))[0][0]


def action(tenant, order, name, confirmed=True):
    return api(tenant, '/orders/' + order + '/' + name, {'requestId': str(uuid.uuid4()), 'confirmed': confirmed})


def rejects_production_fixture():
    if os.name == 'nt':
        import ctypes
        ctypes.windll.kernel32.SetErrorMode(0x0001 | 0x0002)
    address = 'http://127.0.0.1:' + str(s.port())
    env = os.environ.copy()
    for name in list(env):
        if any(word in name.upper() for word in ('STRIPE', 'SERVICEBILLING', 'MERCHANT', 'RESEND', 'SUPABASE', 'CLOUDFLARE', 'AUTH__', 'ORDERING__', 'STORAGE__', 'API__', 'DATAPROTECTION__', 'MEDIA__')): env.pop(name)
    env.update({'ASPNETCORE_ENVIRONMENT': 'Production', 'ASPNETCORE_URLS': address, 'Auth__Enabled': 'false',
        'Storage__DatabasePath': str(RUN / 'production-guard.db'), 'ServiceBilling__DevelopmentApiBase': f'http://127.0.0.1:{STRIPE.server_port}',
        'ServiceBilling__AllowLocalTestProvider': 'true', 'ServiceBilling__RestrictedKey': 'rk_test_tide_local_fixture'})
    output = RUN / 'production-fixture-guard.log'
    expected = 'A local Stripe fixture requires explicit Development configuration'
    with output.open('w', encoding='utf-8') as log:
        process = subprocess.Popen([str(s.SDK), str(RUN / 'assemblies/TideCasa.Api/TideCasa.Api.dll')], cwd=s.ROOT / 'TideCasa.Api',
            env=env, stdout=log, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
        s.PROCESSES.append(process)
        deadline = time.monotonic() + 20
        while process.poll() is None and time.monotonic() < deadline and expected not in output.read_text(encoding='utf-8'): time.sleep(.1)
        listening = False
        try:
            with urllib.request.build_opener(urllib.request.ProxyHandler({})).open(address + '/health', timeout=1) as response: listening = response.status == 200
        except (OSError, urllib.error.URLError): pass
        if process.poll() is None: process.terminate(); process.wait(timeout=10)
    s.check('Production cannot select local Stripe fixture', expected in output.read_text(encoding='utf-8') and not listening)


def run():
    global OWNER, NONCARD, FAIL_ACCOUNT, SKIP_PAYMENT, ADVANCE_REFUND, OVERSIZE
    settings = {'Storage__DatabasePath': str(s.DB), 'Auth__Enabled': 'true', 'Auth__AllowLocalTestProvider': 'true',
        'Auth__SupabaseUrl': f'http://127.0.0.1:{s.PROVIDER.server_port}', 'Auth__PublishableKey': s.KEY,
        'Auth__PlatformOwnerUserId': 'supabase:' + s.PLATFORM, 'ReverseProxy__KnownClientProxy': '127.0.0.1',
        'ServiceBilling__Environment': 'sandbox', 'ServiceBilling__RestrictedKey': 'rk_test_tide_local_fixture',
        'ServiceBilling__WebhookSecret': SECRET, 'ServiceBilling__AccountId': 'acct_fixture', 'ServiceBilling__PublicOrigin': 'https://tide.example.invalid',
        'ServiceBilling__PaymentMethodConfigurationId': 'pmc_fixture', 'ServiceBilling__CheckoutEnabled': 'true', 'ServiceBilling__CardsOnlyVerified': 'true',
        'ServiceBilling__AllowLocalTestProvider': 'true', 'ServiceBilling__DevelopmentApiBase': f'http://127.0.0.1:{STRIPE.server_port}', 'ServiceBilling__GuestCheckoutEnabled': 'true'}
    process = launch('TideCasa.Api', s.API, settings)
    for tenant in ['basic', 'stores', 'referral', 'dbfail', 'recover', 'concurrent', 'legacy', 'review', 'wrong', 'web']:
        s.seed(tenant, status='draft')
    s.seed('foreign', s.BOB, 'draft')
    s.seed('paused', status='paused')
    tokens = {person: s.call('/api/v1/auth/signin', {'email': person + '@example.invalid', 'password': s.PASSWORD})[1]['accessToken'] for person in ['alice', 'bob', 'staff']}
    OWNER = tokens['alice']
    root = '/api/v1/tenants/basic/billing'
    s.check('Anonymous billing denied', s.call(root)[0] == 401)
    for person in ['bob', 'staff']:
        s.check(person + ' cannot read another owner billing', api('basic', token=tokens[person])[0] == 403)
    s.check('Paused owner can see billing', api('paused')[0] == 200 and not api('paused')[1]['canPurchase'])
    for stores, expected in [(False, 60000), (True, 90000)]:
        q = api('basic', '/quote', {'appStores': stores})
        s.check('Server prices first charge ' + str(expected), q[0] == 200 and q[1]['firstPaymentCents'] == expected and q[1]['monthlyCents'] == 14900)
    s.sql("INSERT INTO tide_referral_profiles(id,user_id,email,name,introduction,status,code,discount_percent,terms_version,terms_accepted_at,created_at,updated_at) VALUES(?,?,?,?,?,'active',?,10,'fixture',?,?,?)",
          ('rep', 'supabase:' + s.BOB, 'bob@example.invalid', 'Synthetic Representative', 'Synthetic', 'SAVE10', s.NOW, s.NOW, s.NOW))
    q = api('referral', '/quote', {'appStores': True, 'referralCode': 'save10'})
    s.check('Referral discounts setup and add-on, never maintenance', q[1]['firstPaymentCents'] == 81000 and q[1]['monthlyCents'] == 14900 and q[1]['discountCents'] == 9000)
    s.check('Self referral rejected by verified identity', api('foreign', '/quote', {'referralCode': 'SAVE10'}, tokens['bob'])[0] == 400)
    before = len(IDEMPOTENCY)
    s.check('Unchecked recurring terms rejected', checkout('basic', acceptedTerms=False)[0][0] == 400)
    s.check('Stale price consent rejected', checkout('basic', quoteFingerprint='0' * 64)[0][0] == 409)
    NONCARD = True
    s.check('Unexpected enabled payment method fails closed', checkout('basic')[0][0] == 503)
    NONCARD = False
    FAIL_ACCOUNT = True
    s.check('Wrong provider account fails closed', checkout('basic')[0][0] == 422)
    FAIL_ACCOUNT = False
    OVERSIZE = True
    s.check('Provider response body is bounded', checkout('basic')[0][0] == 503)
    OVERSIZE = False
    s.check('Rejected requests create no provider checkout', len(IDEMPOTENCY) == before)
    result, body = checkout('basic', amountCents=1)
    s.check('Basic checkout created through SDK', result[0] == 200, result[:3])
    basic = result[1]['orderId']; basic_session = session_for(basic)
    s.check('Forged client price ignored', OBJECTS[basic_session]['amount_total'] == 60000)
    saved = next(v for v in IDEMPOTENCY.values() if v['id'] == basic_session)
    wire = urllib.parse.parse_qs(saved['raw'])
    s.check('A 149-dollar monthly line and one-time setup', wire['line_items[0][price_data][unit_amount]'] == ['14900'] and wire['line_items[0][price_data][recurring][interval]'] == ['month'] and wire['line_items[1][price_data][unit_amount]'] == ['60000'] and not any('recurring' in k and '[0]' not in k for k in wire))
    s.check('Trusted returns and reviewed method configuration only', wire['success_url'][0].startswith('https://tide.example.invalid/workspace/basic/') and 'payment_method_types' not in wire and not any(x in key for key in wire for x in ['transfer_data', 'application_fee', 'automatic_tax']))
    s.check('Maintenance starts 30 days after checkout payment with a saved card', wire['subscription_data[trial_period_days]'] == ['30'] and wire['payment_method_collection'] == ['always'])
    s.check('Repeated checkout returns same provider session', api('basic', '/checkout', body)[1]['orderId'] == basic and len([x for x in OBJECTS.values() if x['object'] == 'checkout.session']) == 1)
    s.check('Changing saved options requires discard', checkout('basic', True)[0][0] == 409)
    s.check('Resume requires explicit confirmation', action('basic', basic, 'resume-checkout', False)[0] == 400)
    s.check('Resume saved checkout succeeds', action('basic', basic, 'resume-checkout')[0] == 200)
    original_url = OBJECTS[basic_session]['url']
    OBJECTS[basic_session]['url'] = 'https://evil.example.invalid/fake-checkout'
    s.check('Provider hosted URL must remain exact Stripe origin', action('basic', basic, 'resume-checkout')[0] == 503)
    OBJECTS[basic_session]['url'] = original_url
    review, _ = checkout('review'); review_id = review[1]['orderId']
    s.sql("UPDATE tide_service_orders SET session_id=NULL,created_at='2020-01-01T00:00:00Z' WHERE id=?", (review_id,))
    attempts_before = len([r for r in REQUESTS if r['method'] == 'POST' and r['path'] == '/v1/checkout/sessions'])
    s.check('Uncertain checkout beyond idempotency window requires review', action('review', review_id, 'resume-checkout')[0] == 409 and len([r for r in REQUESTS if r['method'] == 'POST' and r['path'] == '/v1/checkout/sessions']) == attempts_before)
    add, _ = checkout('stores', True); add_id = add[1]['orderId']
    s.check('App-store add-on belongs only to first charge', OBJECTS[session_for(add_id)]['amount_total'] == 90000)
    s.check('Discard unpaid checkout verified and stored', action('stores', add_id, 'discard-checkout')[0] == 200 and s.sql('SELECT status FROM tide_service_orders WHERE id=?', (add_id,))[0][0] == 'expired')
    s.check('Discard permits newly consented options', checkout('stores', False)[0][0] == 200)
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        competing = list(pool.map(lambda _: checkout('concurrent')[0], range(2)))
    s.check('Concurrent identical checkout has one durable order', all(r[0] == 200 for r in competing) and len({r[1]['orderId'] for r in competing}) == 1, competing)
    s.check('Provider idempotency prevents duplicate concurrent sessions', sum(1 for v in OBJECTS.values() if v['object'] == 'checkout.session' and v['metadata']['tide_tenant_id'] == 'concurrent') == 1)
    s.sql("CREATE TRIGGER fail_session BEFORE UPDATE OF session_id ON tide_service_orders WHEN NEW.tenant_id='dbfail' BEGIN SELECT RAISE(ABORT,'synthetic fault'); END")
    failed, request = checkout('dbfail')
    s.check('Database failure after Stripe success stays unconfirmed', failed[0] == 503 and s.sql("SELECT session_id FROM tide_service_orders WHERE tenant_id='dbfail'")[0][0] is None)
    failed_id = s.sql("SELECT id FROM tide_service_orders WHERE tenant_id='dbfail'")[0][0]
    s.sql('DROP TRIGGER fail_session')
    s.check('Uncertain checkout resumes using same provider request', action('dbfail', failed_id, 'resume-checkout')[0] == 200 and sum(v['object'] == 'checkout.session' and v.get('metadata', {}).get('tide_tenant_id') == 'dbfail' for v in OBJECTS.values()) == 1)
    s.sql("INSERT INTO bartide_payment_orders(id,tenant_id,environment,channel,status,payment_status,amount_cents,currency,request_json,hosted_url,created_at,updated_at) VALUES('historical','legacy','sandbox','checkout','paid','paid',60000,'usd','{}','https://evil.example.invalid',?,?)", (s.NOW, s.NOW))
    history = api('legacy')[1]
    s.check('Historical one-time price preserved and unsafe URL suppressed', history['historicalOrders'][0]['amountCents'] == 60000 and history['historicalOrders'][0]['url'] is None and not history['canPurchase'])
    s.check('Historical payment blocks duplicate subscription', checkout('legacy')[0][0] == 409)
    s.seed('legacy-service', status='draft')
    legacy, _ = checkout('legacy-service'); legacy_id = legacy[1]['orderId']; legacy_session = session_for(legacy_id)
    snapshot = json.loads(s.sql('SELECT request_json FROM tide_service_orders WHERE id=?', (legacy_id,))[0][0])
    snapshot['termsVersion'] = '2026-09-maintenance-v1'
    s.sql('UPDATE tide_service_orders SET monthly_cents=5000,total_cents=65000,request_json=? WHERE id=?', (json.dumps(snapshot), legacy_id))
    OBJECTS[legacy_session]['amount_total'] = 65000
    s.check('Unpaid legacy consent cannot resume at changed terms', action('legacy-service', legacy_id, 'resume-checkout')[0] == 409)
    legacy_invoice, legacy_sub, _, _ = settle(legacy_session)
    OBJECTS[legacy_sub]['items']['data'][0]['price']['unit_amount'] = 5000
    s.check('Already completed legacy checkout keeps its original 650-dollar total and 50-dollar rate', event(legacy_invoice)[0] == 200 and s.sql('SELECT status,monthly_cents,total_cents FROM tide_service_orders WHERE id=?', (legacy_id,))[0] == ('paid', 5000, 65000))
    legacy_renewal, _, _, _ = settle(legacy_session, amount=5000, renewal=True)
    s.check('Legacy 50-dollar renewals still reconcile', event(legacy_renewal)[0] == 200 and s.sql('SELECT amount_cents FROM tide_service_invoices WHERE id=?', (legacy_renewal,))[0][0] == 5000)

    ref, _ = checkout('referral', True, 'SAVE10'); ref_id = ref[1]['orderId']; ref_session = session_for(ref_id)
    invoice, sub, charge, intent = settle(ref_session)
    for title, kwargs in [('Bad signature', {'bad': True}), ('Stale signature', {'stale': True}), ('Wrong event version', {'overrides': {'api_version': '2020-01-01'}}), ('Wrong live context', {'overrides': {'livemode': True}}), ('Connect event on service endpoint', {'overrides': {'account': 'acct_foreign'}})]:
        s.check(title + ' rejected', event(invoice, **kwargs)[0] == 400)
    s.check('Rejected webhooks have no financial side effects', s.sql('SELECT COUNT(*) FROM tide_service_invoices WHERE order_id=?', (ref_id,))[0][0] == 0)
    OBJECTS[sub]['trial_end'] += 3600
    s.check('A new subscription with any delay other than 30 days is rejected', event(invoice)[0] == 422)
    OBJECTS[sub]['trial_end'] -= 3600
    SKIP_PAYMENT = True
    bad_payment = event(invoice)
    s.check('Manual-paid invoice without captured chain rejected', bad_payment[0] == 422 and not s.sql('SELECT id FROM tide_service_invoices WHERE id=?', (invoice,)))
    SKIP_PAYMENT = False
    OBJECTS[charge]['captured'] = False
    s.check('Uncaptured charge cannot mark service paid', event(invoice)[0] == 422)
    OBJECTS[charge]['captured'] = True
    OBJECTS[intent]['customer'] = 'cus_foreign'
    s.check('Foreign customer in payment chain rejected', event(invoice)[0] == 422)
    OBJECTS[intent]['customer'] = OBJECTS[sub]['customer']
    OBJECTS[invoice]['total'] -= 1
    s.check('Invoice amount mismatch rejected before ledger mutation', event(invoice)[0] == 422)
    OBJECTS[invoice]['total'] += 1
    OBJECTS[charge]['payment_method_details']['type'] = 'us_bank_account'
    s.check('Captured non-card payment cannot activate service', event(invoice)[0] == 422)
    OBJECTS[charge]['payment_method_details']['type'] = 'card'
    s.sql("CREATE TRIGGER fail_commission BEFORE INSERT ON tide_referral_sales BEGIN SELECT RAISE(ABORT,'synthetic commission fault'); END")
    failed_event = event(invoice)
    s.check('Commission write failure rolls back payment and event', failed_event[0] == 503 and not s.sql('SELECT id FROM tide_service_invoices WHERE id=?', (invoice,)) and not s.sql('SELECT id FROM tide_service_events WHERE id=?', (failed_event[2],)) and s.sql('SELECT status FROM tide_service_orders WHERE id=?', (ref_id,))[0][0] == 'pending')
    s.check('Failed event remains durable for retry', s.sql('SELECT state FROM tide_service_event_inbox WHERE event_id=?', (failed_event[2],))[0][0] == 'pending')
    s.sql('DROP TRIGGER fail_commission')
    retry = event(invoice, event_id=failed_event[2])
    s.check('Identical signed event retry commits captured invoice', retry[0] == 200 and s.sql('SELECT status FROM tide_service_orders WHERE id=?', (ref_id,))[0][0] == 'paid', retry)
    s.check('Sandbox verified purchase never starts a live build', s.sql("SELECT status FROM bartide_customers WHERE id='referral'")[0][0] == 'draft')
    s.check('Initial invoice credits setup only; monthly commission waits for payment', s.sql('SELECT kind,gross_cents,commission_cents FROM tide_referral_sales WHERE order_id=? ORDER BY kind', (ref_id,)) == [('initial', 81000, 16200)])
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        duplicates = list(pool.map(lambda _: event(invoice, event_id=failed_event[2]), range(2)))
    s.check('Concurrent signed replay has one ledger and event', all(r[0] == 200 for r in duplicates) and s.sql('SELECT COUNT(*) FROM tide_referral_sales WHERE order_id=?', (ref_id,))[0][0] == 1 and s.sql('SELECT COUNT(*) FROM tide_service_events WHERE id=?', (failed_event[2],))[0][0] == 1)
    pending_refund = refund(charge, 8600, 'pending')
    s.check('Pending refund reconciles independently of payment', event(pending_refund, 'refund.created')[0] == 200 and s.sql('SELECT status,refunded_cents,refund_pending_cents FROM tide_service_invoices WHERE id=?', (invoice,))[0] == ('paid', 0, 8600))
    OBJECTS[pending_refund]['status'] = 'failed'
    s.check('Failed refund clears pending without reducing earned commission', event(pending_refund, 'refund.failed')[0] == 200 and s.sql('SELECT refund_pending_cents,refund_failed_cents FROM tide_service_invoices WHERE id=?', (invoice,))[0] == (0, 8600) and s.sql('SELECT SUM(commission_cents) FROM tide_referral_sales WHERE order_id=?', (ref_id,))[0][0] == 16200)
    successful = refund(charge, 8600, 'succeeded')
    s.check('Setup refund adjusts only the setup commission', event(successful, 'refund.updated')[0] == 200 and s.sql('SELECT kind,refunded_cents,commission_cents FROM tide_referral_sales WHERE order_id=? ORDER BY kind', (ref_id,)) == [('initial', 8600, 14480)])
    renew, _, renew_charge, _ = settle(ref_session, renewal=True, status='open')
    OBJECTS[sub]['status'] = 'past_due'
    s.check('Failed renewal records open invoice without a new commission', event(renew, 'invoice.payment_failed')[0] == 200 and s.sql('SELECT status FROM tide_service_invoices WHERE id=?', (renew,))[0][0] == 'open' and s.sql('SELECT COUNT(*) FROM tide_referral_sales WHERE order_id=?', (ref_id,))[0][0] == 1)
    OBJECTS[renew].update(status='paid', amount_paid=14900, amount_remaining=0, status_transitions={'paid_at': int(time.time())})
    OBJECTS[sub]['status'] = 'active'
    s.check('Recovered renewal is exactly 149 dollars and ten percent commission', event(renew)[0] == 200 and s.sql('SELECT gross_cents,commission_cents FROM tide_referral_sales WHERE event_id=?', (renew + ':maintenance',))[0] == (14900, 1490))
    racing_refund = refund(renew_charge, 500, 'pending')
    ADVANCE_REFUND = racing_refund
    s.check('Later succeeded refund list cannot regress to earlier pending snapshot', event(racing_refund, 'refund.updated')[0] == 200 and s.sql('SELECT refunded_cents,refund_pending_cents FROM tide_service_invoices WHERE id=?', (renew,))[0] == (500, 0) and s.sql('SELECT commission_cents FROM tide_referral_sales WHERE event_id=?', (renew + ':maintenance',))[0][0] == 1440)
    s.sql("UPDATE bartide_customers SET status='paused' WHERE id='referral'")
    s.check('Paused owner can cancel at paid period end', action('referral', ref_id, 'cancel-renewal')[0] == 200 and s.sql('SELECT cancel_at_period_end FROM tide_service_orders WHERE id=?', (ref_id,))[0][0] == 1 and OBJECTS[sub]['cancel_at_period_end'])
    s.check('Cancellation does not create refund or immediate cancellation', OBJECTS[sub]['status'] == 'active' and not any(r['method'] == 'POST' and ('refund' in r['path'] or r['body'].get('cancel_at_period_end') == 'false') for r in REQUESTS))
    # A late initial invoice event still refreshes the current subscription status.
    OBJECTS[sub]['status'] = 'canceled'
    s.check('Out-of-order initial event retains authoritative canceled status', event(invoice)[0] == 200 and s.sql('SELECT subscription_status FROM tide_service_orders WHERE id=?', (ref_id,))[0][0] == 'canceled')
    s.check('Paid checkout cannot be discarded', action('referral', ref_id, 'discard-checkout')[0] == 409)
    # Recover provider success before local session binding using signed subscription association.
    rec, _ = checkout('recover'); rec_id = rec[1]['orderId']; rec_session = session_for(rec_id)
    rec_invoice, rec_sub, rec_charge, _ = settle(rec_session)
    refund(rec_charge, 60000, 'succeeded')
    s.sql('UPDATE tide_service_orders SET session_id=NULL WHERE id=?', (rec_id,))
    s.check('Refund-before-paid event recovers missing session via subscription', event(rec_charge, 'charge.refunded')[0] == 200 and session_for(rec_id) == rec_session and s.sql('SELECT refunded_cents FROM tide_service_invoices WHERE id=?', (rec_invoice,))[0][0] == 60000)
    s.check('Custom transport uses pinned stable version without Connect header', all(r['version'] == VERSION and not r['account'] for r in REQUESTS) and all(r['agent'] == 'TideCasa-StripeHttp/1.0' for r in REQUESTS))
    # SSR forms share the real API session and CSRF protections.
    launch('TideCasa.Blazor', WEB, {'Api__BaseUrl': s.API, 'Auth__AllowLocalHttp': 'true', 'DataProtection__KeyPath': str(RUN / 'keys')})
    owner_browser = w.login('alice')
    forms, page = w.forms_for(owner_browser, '/workspace/web/billing?appStores=true')
    form = w.find_form(forms, '/checkout')
    s.check('Checkout add-on query changes reviewed total only', '$900.00' in page[1] and '$149 each month' in page[1] and not s.sql("SELECT id FROM tide_service_orders WHERE tenant_id='web'"))
    consent = [c for c in form['controls'] if c.get('name') == 'acceptedTerms'][0]
    s.check('Renewal consent initially unchecked and required', 'checked' not in consent and 'required' in consent)
    s.check('No raw billing secret in rendered page', SECRET not in page[1] and 'rk_test_' not in page[1] and OWNER not in page[1])
    s.check('Missing antiforgery rejected', w.post(owner_browser, form, {'acceptedTerms': 'true'}, remove=['__RequestVerificationToken'])[0] == 400)
    s.check('Cross-origin checkout rejected', w.post(owner_browser, form, {'acceptedTerms': 'true'}, headers={'Origin': 'https://evil.example.invalid'})[0] == 400)
    s.check('Unchecked native checkout cannot create provider session', 'notice=invalid' in w.post(owner_browser, form)[2].get('Location', '') and not s.sql("SELECT id FROM tide_service_orders WHERE tenant_id='web'"))
    posted = w.post(owner_browser, form, {'acceptedTerms': 'true'})
    s.check('Consented native checkout redirects to hosted Stripe only', posted[0] == 302 and posted[2].get('Location', '').startswith('https://checkout.stripe.com/c/pay/'), posted[:2])
    web_id = s.sql("SELECT id FROM tide_service_orders WHERE tenant_id='web'")[0][0]
    s.sql('UPDATE tide_service_orders SET session_id=NULL WHERE id=?', (web_id,))
    forms, resumed_page = w.forms_for(owner_browser, '/workspace/web/billing')
    resume = w.find_form(forms, '/resume-checkout')
    s.check('Uncertain saved checkout offers owner recovery', '$900.00' in resumed_page[1] and 'confirmed' not in resume['fields'])
    s.check('Native resume reuses provider session', w.post(owner_browser, resume, {'confirmed': 'true'})[0] == 302)
    foreign_browser = w.login('bob')
    s.check('Foreign signed-in user cannot render owner billing', w.web('/workspace/web/billing', client=foreign_browser)[0] == 403)
    s.check('Returned checkout shows pending refresh without marking paid', 'http-equiv="refresh"' in w.web('/workspace/web/billing?checkout=returned', client=owner_browser)[1] and s.sql('SELECT status FROM tide_service_orders WHERE id=?', (web_id,))[0][0] == 'pending')
    guest_purchase_checks(tokens)
    process.terminate(); process.wait(timeout=10)
    settings['ServiceBilling__CheckoutEnabled'] = 'false'
    process = launch('TideCasa.Api', s.API, settings, '-checkout-off')
    # Fresh sessions are registered in durable SQLite and survive this API restart.
    s.check('Disabling checkout preserves authenticated billing history', not api('basic')[1]['checkoutAvailable'])
    s.check('Flag-off rejects new checkout', checkout('wrong')[0][0] == 503)
    s.check('Disabling checkout also disables public guest creation', not s.call('/api/v1/service-purchases/options')[1]['checkoutAvailable'] and s.call('/api/v1/service-purchases/checkout', {'checkoutKey': 'd'*64, 'plan': 'business', 'appStores': False, 'acceptedTerms': True, 'termsVersion': '2026-09-maintenance-v2'})[0] == 503)
    s.check('Flag-off still verifies signed notifications', event(rec_invoice)[0] == 200)
    s.check('Flag-off still permits known subscription cancellation', action('recover', rec_id, 'cancel-renewal')[0] == 200)
    s.check('All provider writes remain only checkout, expiry and period-end subscription update', all(r['method'] == 'GET' or r['path'] == '/v1/checkout/sessions' or r['path'].endswith('/expire') or r['path'].startswith('/v1/subscriptions/') for r in REQUESTS))
    process.terminate(); process.wait(timeout=10)
    settings = {k: v for k, v in settings.items() if not k.startswith('ServiceBilling__')}
    launch('TideCasa.Api', s.API, settings, '-default-off')
    request_count = len(REQUESTS)
    s.check('No configured credentials defaults checkout off', not api('basic')[1]['checkoutAvailable'] and checkout('wrong')[0][0] == 503 and len(REQUESTS) == request_count)
    s.check('Default-off history remains available', api('referral')[0] == 200 and len(api('referral')[1]['orders'][0]['invoices']) == 2)
    rejects_production_fixture()


def guest_purchase_checks(tokens):
    root = '/api/v1/service-purchases'
    s.check('Guest payment options require no account', s.call(root + '/options')[1]['checkoutAvailable'])
    guest = w.browser()
    forms, page = w.forms_for(guest, '/purchase/restaurant?appStores=true')
    form = w.find_form(forms, '/guest-checkout')
    s.check('Anonymous purchase shows total, add-on and payment before account', page[0] == 200 and '$900' in page[1] and 'Create your account' in page[1] and form['fields']['appStores'] == 'true' and '/start/restaurant' not in page[1])
    s.check('Purchase page cannot be cached or leak return reference externally', 'no-store' in page[2].get('Cache-Control', '') and page[2].get('Referrer-Policy') == 'same-origin')
    s.check('Guest payment consent is required and initially unchecked', all('checked' not in c and 'required' in c for c in form['controls'] if c.get('name') == 'acceptedTerms'))
    before = len([r for r in REQUESTS if r['path'] == '/v1/checkout/sessions' and r['method'] == 'POST'])
    s.check('Guest payment rejects missing CSRF', w.post(guest, form, {'acceptedTerms': 'true'}, remove=['__RequestVerificationToken'])[0] == 400)
    s.check('Guest payment rejects cross-origin requests', w.post(guest, form, {'acceptedTerms': 'true'}, headers={'Origin': 'https://wrong.invalid'})[0] == 400)
    s.check('Guest payment rejects absent renewal consent', 'notice=confirmation' in w.post(guest, form)[2].get('Location', ''))
    s.check('Rejected forms made no provider writes', len([r for r in REQUESTS if r['path'] == '/v1/checkout/sessions' and r['method'] == 'POST']) == before)
    posted = w.post(guest, form, {'acceptedTerms': 'true'})
    s.check('Guest checkout opens hosted payment without signing in', posted[0] == 302 and posted[2].get('Location', '').startswith('https://checkout.stripe.com/'), posted)
    session = posted[2]['Location'].rsplit('/', 1)[1]
    order, tenant = s.sql('SELECT id,tenant_id FROM tide_service_orders WHERE session_id=?', (session,))[0]
    checkout_request = [r for r in REQUESTS if r['path'] == '/v1/checkout/sessions' and r['method'] == 'POST'][-1]['body']
    s.check('Checkout expiry leaves margin below Stripe maximum for clock skew', all(1800 < int(r['body']['expires_at']) - time.time() < 23.5 * 3600 for r in REQUESTS if r['path'] == '/v1/checkout/sessions' and r['method'] == 'POST'))
    s.check('Stripe collects billing email and returns to post-payment account step', 'customer_email' not in checkout_request and checkout_request['success_url'].endswith('/purchase/complete/' + order + '/{CHECKOUT_SESSION_ID}') and '/signin' not in checkout_request['success_url'])
    s.check('Guest package has no owner or business details before payment', s.sql('SELECT user_id,status FROM bartide_customers WHERE id=?', (tenant,))[0] == (None, 'draft') and OBJECTS[session]['amount_total'] == 90000)
    repeated = w.post(guest, form, {'acceptedTerms': 'true'})
    s.check('Repeated guest click resumes same provider session', repeated[2].get('Location') == posted[2].get('Location') and len(s.sql('SELECT id FROM tide_service_orders WHERE tenant_id=?', (tenant,))) == 1)
    s.check('Changing an open checkout cannot create a second charge', 'notice=guest_options_saved' in w.post(guest, form, {'acceptedTerms': 'true', 'appStores': 'false'})[2].get('Location', '') and len(s.sql('SELECT id FROM tide_service_orders WHERE tenant_id=?', (tenant,))) == 1)
    returned = '/purchase/complete/' + order + '/' + session
    status_path = root + '/' + order + '/' + session
    details = {'businessName': 'Synthetic Guest Bar', 'contactName': 'Guest Buyer', 'area': 'Test City'}
    s.check('Browser return does not mark a payment paid', 'Confirming your payment' in w.web(returned, client=guest)[1] and s.call(status_path)[1]['status'] == 'pending')
    s.check('Unpaid purchase cannot be claimed even by verified account', s.call(status_path + '/claim', details, tokens['staff'])[0] == 409)
    s.check('Different session reference cannot read or claim purchase', s.call(root + '/' + order + '/cs_test_wrong')[0] == 404)
    invoice, sub, charge, _ = settle(session)
    OBJECTS[session]['customer_details'] = {'email': 'staff@example.invalid'}
    s.check('Settlement alone still waits for verified webhook evidence', s.call(status_path)[1]['status'] == 'pending')
    s.check('Guest invoice verifies through existing signed billing pipeline', event(invoice)[0] == 200 and s.call(status_path)[1]['status'] == 'paid')
    public = s.call(status_path)[1]
    s.check('Anonymous purchase status exposes no buyer email or identity', 'staff@example.invalid' not in json.dumps(public) and 'tenantId' not in public)
    s.check('Paid purchase cannot be claimed anonymously', s.call(status_path + '/claim', details)[0] == 401)
    s.check('Foreign verified email cannot steal paid purchase', s.call(status_path + '/claim', details, tokens['alice'])[0] == 403 and s.sql('SELECT user_id FROM bartide_customers WHERE id=?', (tenant,))[0][0] is None)
    forms, paid_page = w.forms_for(guest, returned)
    s.check('Only verified payment reveals account creation next step', 'Test payment confirmed.' in paid_page[1] and '/signup?return_to=' in paid_page[1])
    buyer = w.login('staff')
    forms, buyer_page = w.forms_for(buyer, returned)
    claim = w.find_form(forms, '/claim')
    s.check('Business details are collected after payment and account verification', 'Tell us about your business.' in buyer_page[1] and claim['fields']['orderId'] == order)
    s.check('Account connection rejects missing CSRF', w.post(buyer, claim, details, remove=['__RequestVerificationToken'])[0] == 400)
    connected = w.post(buyer, claim, details)
    s.check('Verified checkout email connects paid purchase without another payment', connected[0] == 302 and connected[2].get('Location') == '/workspace/' + tenant + '/billing' and s.sql('SELECT user_id,name FROM bartide_customers WHERE id=?', (tenant,))[0] == ('supabase:' + s.STAFF, 'Synthetic Guest Bar'), connected)
    s.check('Repeated account connection is idempotent', s.call(status_path + '/claim', details, tokens['staff'])[0] == 200)
    s.check('Buyer can open invoices and renewal management after connection', w.web('/workspace/' + tenant + '/billing', client=buyer)[0] == 200 and api(tenant, token=tokens['staff'])[1]['orders'][0]['status'] == 'paid')
    resumed = w.post(guest, form, {'acceptedTerms': 'true'})
    s.check('Returning buyer cannot accidentally pay the same purchase again', resumed[2].get('Location') == returned)
    reset_key = 'c' * 64
    req = {'checkoutKey': reset_key, 'plan': 'business', 'appStores': False, 'acceptedTerms': True, 'termsVersion': '2026-09-maintenance-v2'}
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        attempts = list(pool.map(lambda _: s.call(root + '/checkout', req), range(2)))
    s.check('Concurrent anonymous checkout is idempotent', all(x[0] == 200 for x in attempts) and len({x[1]['orderId'] for x in attempts}) == 1, attempts)
    reset_order = attempts[0][1]['orderId']; reset_session = session_for(reset_order)
    s.check('Base guest price is 600 dollars with service deferred', OBJECTS[reset_session]['amount_total'] == 60000)
    s.check('Closing unpaid checkout verifies provider expiry', s.call(root + '/discard', {'checkoutKey': reset_key})[0] == 200 and OBJECTS[reset_session]['status'] == 'expired' and s.sql('SELECT status FROM tide_service_orders WHERE id=?', (reset_order,))[0][0] == 'expired')
    s.check('No email or account data accepted as a purchase credential', s.call(root + '/checkout', {**req, 'checkoutKey': 'staff@example.invalid'})[0] == 404)
    s.check('BarTide payment pages consolidate to the return domain', w.web('/purchase/restaurant?appStores=true', headers={'Host': 'bar.tide.casa'})[2].get('Location') == 'https://tide.casa/purchase/restaurant?appStores=true')


try:
    run()
finally:
    for process in reversed(s.PROCESSES):
        if process.poll() is None:
            process.terminate()
            try: process.wait(timeout=10)
            except subprocess.TimeoutExpired: process.kill(); process.wait(timeout=10)
    for log in s.LOGS: log.close()
    STRIPE.shutdown(); s.PROVIDER.shutdown()
    (RUN / 'results.json').write_text(json.dumps(s.RESULTS, indent=2), encoding='utf-8')
    (RUN / 'provider-requests.json').write_text(json.dumps(REQUESTS, indent=2), encoding='utf-8')
    print('Evidence: ' + str(RUN), flush=True)
