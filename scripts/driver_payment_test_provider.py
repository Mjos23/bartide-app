"""Loopback-only, synthetic Stripe destination-charge boundary for driver checks.

No real keys or provider calls. The fixture validates pinned API version, platform
context, encoding and exact idempotency replay. Imported by workflow checks too.
"""
import base64
import copy
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import threading
import urllib.parse


KEY = 'rk_test_synthetic_driver_000000000'
SECRET = 'whsec_synthetic_driver_000000000'


class DriverStripeFake:
    def __init__(self):
        self.lock = threading.RLock()
        self.calls, self.accounts, self.sessions, self.intents, self.transfers, self.fees = [], {}, {}, {}, {}, {}
        self.keys, self.fail_once, self.failures = {}, set(), {}
        self.link_url = 'https://connect.stripe.com/setup/synthetic_driver'
        fixture = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass

            def reply(self, status, body):
                self.send_response(status)
                self.send_header('Content-Type', 'application/json')
                self.end_headers()
                self.wfile.write(json.dumps(body).encode())

            def handle_request(self):
                raw = self.rfile.read(int(self.headers.get('Content-Length', 0)))
                content_type = self.headers.get('Content-Type', '')
                body = json.loads(raw or b'{}') if content_type.startswith('application/json') else {
                    k: v[0] for k, v in urllib.parse.parse_qs(raw.decode(), keep_blank_values=True).items()}
                path = urllib.parse.urlsplit(self.path).path
                key = self.headers.get('Idempotency-Key')
                entry = {'method': self.command, 'path': path, 'body': body, 'key': key,
                         'account': self.headers.get('Stripe-Account'), 'version': self.headers.get('Stripe-Version')}
                with fixture.lock:
                    fixture.calls.append(copy.deepcopy(entry))
                    if self.headers.get('Authorization') != 'Basic ' + base64.b64encode((KEY + ':').encode()).decode():
                        return self.reply(401, {'error': {'type': 'authentication_error'}})
                    if entry['account'] is not None or entry['version'] != '2026-08-26.dahlia':
                        return self.reply(400, {'error': {'type': 'invalid_request_error', 'code': 'fixture_context'}})
                    if self.command == 'POST' and not content_type.startswith('application/json' if path.startswith('/v2/') else 'application/x-www-form-urlencoded'):
                        return self.reply(400, {'error': {'type': 'invalid_request_error', 'code': 'fixture_encoding'}})
                    if path in fixture.failures:
                        return self.reply(*fixture.failures[path])
                    if key and key in fixture.keys:
                        previous, response = fixture.keys[key]
                        if previous != (path, body):
                            return self.reply(400, {'error': {'type': 'idempotency_error'}})
                        return self.reply(200, response)
                    result = fixture.route(self.command, path, body)
                    if result is None:
                        return self.reply(404, {'error': {'type': 'invalid_request_error', 'code': 'resource_missing'}})
                    if key:
                        fixture.keys[key] = ((path, copy.deepcopy(body)), copy.deepcopy(result))
                    if path in fixture.fail_once:
                        fixture.fail_once.remove(path)
                        return self.reply(500, {'error': {'type': 'api_error', 'code': 'fixture_uncertain'}})
                    return self.reply(200, result)

            do_GET = do_POST = handle_request

        self.server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
        self.url = 'http://127.0.0.1:' + str(self.server.server_port)
        threading.Thread(target=self.server.serve_forever, daemon=True).start()

    def close(self):
        self.server.shutdown()
        self.server.server_close()

    def route(self, method, path, body):
        if path == '/v2/core/accounts' and method == 'POST':
            ident = 'acct_driver' + str(len(self.accounts) + 1)
            result = {'id': ident, 'object': 'v2.core.account', 'livemode': False, 'closed': False,
                      'dashboard': body['dashboard'], 'identity': body['identity'], 'metadata': body['metadata'],
                      'defaults': body['defaults'], 'configuration': {'recipient': {'applied': True,
                      'capabilities': {'stripe_balance': {'stripe_transfers': {'status': 'active'}, 'payouts': {'status': 'active'}}}}},
                      'requirements': {'entries': [], 'summary': {'minimum_deadline': None}}}
            self.accounts[ident] = result
            return result
        if path.startswith('/v2/core/accounts/') and method == 'GET':
            return self.accounts.get(path.rsplit('/', 1)[-1])
        if path == '/v2/core/account_links' and method == 'POST':
            return {'object': 'v2.core.account_link', 'livemode': False, 'account': body['account'], 'url': self.link_url}
        if path == '/v1/checkout/sessions' and method == 'POST':
            ident = 'cs_test_driver' + str(len(self.sessions) + 1)
            metadata = {k[9:-1]: v for k, v in body.items() if k.startswith('metadata[')}
            amount = sum(int(v) * int(body[k.replace('[price_data][unit_amount]', '[quantity]')])
                         for k, v in body.items() if k.endswith('[price_data][unit_amount]'))
            result = {'id': ident, 'object': 'checkout.session', 'livemode': False, 'mode': body['mode'],
                      'client_reference_id': body['client_reference_id'], 'metadata': metadata,
                      'amount_total': amount, 'currency': 'usd', 'status': 'open', 'payment_status': 'unpaid',
                      'payment_method_types': ['card'], 'expires_at': int(body['expires_at']),
                      'url': 'https://checkout.stripe.com/c/pay/' + ident,
                      'success_url': body['success_url'], 'cancel_url': body['cancel_url'],
                      '_destination': body['payment_intent_data[transfer_data][destination]'],
                      '_fee': int(body.get('payment_intent_data[application_fee_amount]', '0')),
                      '_intent_metadata': {k[30:-1]: v for k, v in body.items() if k.startswith('payment_intent_data[metadata][')}}
            self.sessions[ident] = result
            return result
        stores = {'/v1/checkout/sessions/': self.sessions, '/v1/payment_intents/': self.intents,
                  '/v1/transfers/': self.transfers, '/v1/application_fees/': self.fees}
        for prefix, store in stores.items():
            if path.startswith(prefix) and method == 'GET':
                return store.get(path.rsplit('/', 1)[-1])
        return None

    def pay(self, session_id):
        with self.lock:
            session = self.sessions[session_id]
            suffix = session_id.removeprefix('cs_test_')
            pi, ch, tr, py, fee_id = (prefix + suffix for prefix in ('pi_', 'ch_', 'tr_', 'py_', 'fee_'))
            amount, fee, account = session['amount_total'], session['_fee'], session['_destination']
            session.update(status='complete', payment_status='paid', payment_intent=pi, url=None)
            transfer_data = {'destination': account, 'amount': None}
            charge = {'id': ch, 'object': 'charge', 'livemode': False, 'status': 'succeeded', 'paid': True,
                      'captured': True, 'amount': amount, 'amount_captured': amount, 'amount_refunded': 0,
                      'refunded': False, 'disputed': False, 'currency': 'usd', 'payment_intent': pi,
                      'application_fee_amount': fee or None, 'application_fee': fee_id if fee else None,
                      'transfer_data': copy.deepcopy(transfer_data), 'transfer': tr,
                      'payment_method_details': {'type': 'card', 'card': {'brand': 'visa'}}}
            self.intents[pi] = {'id': pi, 'object': 'payment_intent', 'livemode': False, 'status': 'succeeded',
                                'amount': amount, 'amount_received': amount, 'currency': 'usd',
                                'metadata': session['_intent_metadata'], 'payment_method_types': ['card'],
                                'application_fee_amount': fee or None, 'transfer_data': copy.deepcopy(transfer_data),
                                'latest_charge': charge}
            self.transfers[tr] = {'id': tr, 'object': 'transfer', 'livemode': False, 'amount': amount, 'currency': 'usd',
                                  'destination': account, 'destination_payment': py, 'source_transaction': ch,
                                  'amount_reversed': 0, 'reversed': False}
            if fee:
                self.fees[fee_id] = {'id': fee_id, 'object': 'application_fee', 'livemode': False, 'amount': fee,
                                     'amount_refunded': 0, 'refunded': False, 'currency': 'usd', 'account': account,
                                     'charge': py, 'originating_transaction': ch}
            return self.intents[pi]
