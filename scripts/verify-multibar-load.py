"""Twenty fictional venues on one isolated local API/database; no live services.

Uses the Friday runner's measured request/resource helpers, 128-connection fake
identity listener, and current compiled API/Web snapshots. All tenants, users,
prices and table secrets are disposable. No preview database is opened or changed.
"""
from collections import Counter
import concurrent.futures
from datetime import datetime, timezone
from decimal import Decimal, ROUND_HALF_UP
import importlib.util
import json
import os
from pathlib import Path
import platform
import sqlite3
import threading
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location('friday_helpers', ROOT / 'scripts/verify-friday-load.py')
FRIDAY = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(FRIDAY)
FIXTURE = FRIDAY.FIXTURE


def ident(value):
    return str(uuid.uuid5(uuid.NAMESPACE_URL, 'https://multibar.example.invalid/' + value))


class MultiBar(FRIDAY.Runner):
    def __init__(self):
        self.run = ROOT / '.tools/multibar-load' / datetime.now(timezone.utc).strftime('%Y%m%d-%H%M%S-%f')
        self.run.mkdir(parents=True, exist_ok=False)
        self.db = self.run / 'twenty-fictional-bars.db'
        self.api, self.web = ['http://127.0.0.1:' + str(FRIDAY.free_port()) for _ in range(2)]
        self.lock, self.stop, self.monitor_done = threading.Lock(), threading.Event(), threading.Event()
        self.requests, self.orders, self.resources, self.stages, self.checks = [], [], [], [], []
        self.tokens, self.provider_requests = {}, []
        self.deadline = float('inf')
        self.shared_keys = [ident(self.run.name + '/round/' + str(n)) for n in range(5)]
        self.bars = []
        locations = ['St. Pete Beach', 'Tampa', 'Orlando', 'Miami', 'Jacksonville',
            'Savannah', 'Charleston', 'Asheville', 'Nashville', 'New Orleans',
            'Austin', 'San Antonio', 'Denver', 'Phoenix', 'San Diego',
            'Portland', 'Seattle', 'Chicago', 'Boston', 'New York']
        for n, location in enumerate(locations, 1):
            slug = f'friday-bar-{n:02d}'
            self.bars.append({'id': slug, 'slug': slug, 'name': f'Fictional Friday Bar {n:02d}',
                'location': location + ' - fictional test location', 'price_cents': 1000 + 37 * n,
                'table_id': ident(slug + '/table/1'), 'table_token': uuid.uuid5(uuid.NAMESPACE_URL, slug + '/table/1').hex * 2,
                'ip': '192.0.2.' + str(n), 'orders': [],
                'people': {role: {'key': slug + ':' + role, 'id': ident(slug + '/' + role),
                    'name': f'Bar {n:02d} {role.title()}', 'email': role + '@' + slug + '.example.invalid', 'role': role}
                    for role in ('owner', 'kitchen', 'server', 'customer')}})
        self.report = {'schema': 1, 'started_at': datetime.now(timezone.utc).isoformat(),
            'scope': '20 fictional venue tenants share one local API process and one disposable SQLite database.',
            'limitations': ['20 tenants are not 20 physical servers.',
                '80 fictional identities: distinct owner, kitchen, server and customer for each venue.',
                'Public guest checkout uses the venue-specific sample customer names; customer accounts are signed in but ordering does not require them.',
                '20 parallel automated venue workflows have no human think time; this is not a production concurrent-user claim.',
                'Fake local identity provider; no real payment, email, deployment, live identity service or external network.',
                'Local Windows client and services share a machine; SQLite differs from production PostgreSQL.',
                'Web is started but browser circuits, media/CDN delivery and rendering are not load-tested.'],
            'workload': {'concurrent_venue_workflows': 20, 'orders_per_venue': 5, 'total_new_orders': 100,
                'idempotent_replays': 20, 'cross_tenant_negative_probes': 560,
                'negative_probe_breakdown': {'paired_resource_and_staff_probes': 180,
                    'owner_to_foreign_private_board_all_pairs': 380},
                'max_load_seconds': 240, 'request_timeout_seconds': 20,
                'resource_stop': 'Immediate stop below 512 MiB host free RAM or above 2 GiB private RAM per owned process.',
                'shared_request_keys': self.shared_keys,
                'same_public_values': {'menu_item_id': 'house-special', 'table_label': 'Table 1'},
                'distinct_private_values': ['tenant', 'owner/staff/customer identities', 'table ID/token', 'item price', 'tracking secret'],
                'guards_unchanged': {'new_orders_per_ip_per_minute': 10, 'guest_requests_per_ip_per_minute': 120, 'active_orders_per_venue': 100}},
            'hardware': {'os': platform.platform(), 'logical_cpu_count': os.cpu_count(),
                'processor': os.environ.get('PROCESSOR_IDENTIFIER', platform.processor()), 'memory_at_start': FRIDAY.memory()},
            'requests': self.requests, 'orders': self.orders, 'checks': self.checks,
            'resources': self.resources, 'stages': self.stages}

    def owner_path(self, bar):
        return '/api/v1/tenants/' + bar['id']

    def guest_path(self, bar):
        return '/api/v1/restaurants/' + bar['slug']

    def setup(self):
        FIXTURE.RUN, FIXTURE.DB, FIXTURE.API, FIXTURE.WEB = self.run, self.db, self.api, self.web
        FIXTURE.USERS = {person['email']: {'id': person['id'], 'email': person['email'],
            'email_confirmed_at': '2026-01-01T00:00:00Z', 'is_anonymous': False,
            'user_metadata': {'full_name': person['name']}}
            for bar in self.bars for person in bar['people'].values()}
        runner = self
        class MeasuredProvider(FIXTURE.Provider):
            def reply(handler, status, body):
                handler.response_status = status
                return super(MeasuredProvider, handler).reply(status, body)

            def measured_request(handler):
                start, failure = time.perf_counter(), None
                handler.response_status = 0
                try:
                    FIXTURE.Provider.handle_request(handler)
                except Exception as error:
                    failure = type(error).__name__
                    raise
                finally:
                    with runner.lock:
                        runner.provider_requests.append({'status': handler.response_status,
                            'ms': round((time.perf_counter() - start) * 1000, 3), 'error': failure})
            do_GET = do_POST = measured_request

        class ProviderServer(FIXTURE.ThreadingHTTPServer):
            request_queue_size = 128

        self.provider = ProviderServer(('127.0.0.1', 0), MeasuredProvider)
        threading.Thread(target=self.provider.serve_forever, daemon=True).start()
        env = FIXTURE.environment(self.provider.server_port)
        env['ConnectionStrings__Application'] = ''
        # The Gulf Lantern showcase is irrelevant to this disposable venue set.
        env['SampleBar__Enabled'] = 'false'
        for project, url in [('TideCasa.Api', self.api), ('TideCasa.Blazor', self.web)]:
            FIXTURE.launch(project, url, env, self.run / 'runtime')
        self.report['runtime'] = {'api': self.api, 'web': self.web, 'private_database': str(self.db),
            'api_processes': 1, 'build_hashes': {p: FRIDAY.digest(self.run / 'runtime' / p / (p + '.dll'))
            for p in ('TideCasa.Api', 'TideCasa.Blazor')}}
        now = datetime.now(timezone.utc).isoformat()
        with sqlite3.connect(self.db, timeout=20) as db:
            self.check('empty-disposable-database', db.execute('SELECT COUNT(*) FROM bartide_customers').fetchone()[0] == 0)
            for bar in self.bars:
                menu = {'schema': 'bartide-menu/1', 'venue': {'name': bar['name'], 'vertical': 'bartide',
                    'area': bar['location'], 'tagline': 'Isolated fictional multi-venue load test', 'hours_text': 'Fictional Friday',
                    'website_url': '', 'service_note': 'Synthetic orders only.', 'currency': 'USD'},
                    'categories': [{'id': 'food', 'label': 'Food'}], 'items': [{'id': 'house-special', 'category': 'food',
                    'name': bar['name'] + ' house special', 'description': 'Fictional local test item.',
                    'price_cents': bar['price_cents'], 'price_label': None, 'available': True}]}
                config = {'enabled': True, 'accepting_orders': True, 'delivery_enabled': False,
                    'tax_basis_points': 700, 'delivery_fee_cents': 0, 'delivery_minimum_cents': 0,
                    'delivery_capacity': 5, 'delivery_zips': [], 'blocked_item_ids': [],
                    'checkout': {'dine_in_enabled': True, 'pickup_enabled': True, 'pay_staff_enabled': True, 'tips_enabled': True,
                        'tables': [{'id': bar['table_id'], 'label': 'Table 1', 'token': bar['table_token'], 'enabled': True}]}}
                owner = bar['people']['owner']
                db.execute('INSERT INTO bartide_customers(id,slug,email,user_id,name,menu_json,version,status,enrollment_note,created_at,updated_at,vertical) VALUES(?,?,?,?,?,?,0,?,?,?,?,?)',
                    (bar['id'], bar['slug'], owner['email'], 'supabase:' + owner['id'], bar['name'], json.dumps(menu),
                    'active', 'Disposable 20-venue isolation fixture', now, now, 'bartide'))
                db.execute('INSERT INTO bartide_enhanced_configs(tenant_id,settings_json,version,updated_at) VALUES(?,?,0,?)',
                    (bar['id'], json.dumps(config), now))
        for bar in self.bars:
            for person in bar['people'].values():
                self.tokens[person['key']] = self.call('signin', '/api/v1/auth/signin',
                    {'email': person['email'], 'password': FIXTURE.BAR['password']}, ip=bar['ip'])['accessToken']
            for role in ('kitchen', 'server'):
                person = bar['people'][role]
                team = self.call('add-staff', self.owner_path(bar) + '/team/members',
                    {k: person[k] for k in ('name', 'email', 'role')}, person=bar['people']['owner']['key'], ip=bar['ip'])
                self.check('staff-scoped-' + person['key'], all(m['email'].endswith('@' + bar['slug'] + '.example.invalid') for m in team['members']))
        (self.run / 'fixture.json').write_text(json.dumps({'tenants': self.bars,
            'shared_order_request_keys': self.shared_keys}, indent=2), encoding='utf-8')
        self.monitor = threading.Thread(target=self.watch_resources, daemon=True)
        self.monitor.start()
        print('Ready: 20 fictional tenants, 80 distinct identities, one API/database, unique table secrets and prices.', flush=True)

    def inspect_board(self, bar, board, label):
        own = {order['id'] for order in bar['orders']}
        rows = board['orders']
        self.check(label, board['tenantId'] == bar['id'] and board['name'] == bar['name'] and
            {row['order']['receipt']['orderId'] for row in rows} == own and
            all(row['order']['customerName'] == bar['people']['customer']['name'] and
                all(line['unitCents'] == bar['price_cents'] for line in row['order']['receipt']['quote']['lines']) for row in rows))

    def order_round(self, bar, round_number):
        phase, common = 'load-20-bars', {'ip': bar['ip'], 'phase': 'load-20-bars'}
        guest = self.guest_path(bar)
        menu = self.call('menu', guest + '/menu', **common)
        self.check('own-menu-' + bar['id'] + '-' + str(round_number), menu['slug'] == bar['slug'] and menu['name'] == bar['name'] and
            len(menu['items']) == 1 and menu['items'][0]['id'] == 'house-special' and menu['items'][0]['priceCents'] == bar['price_cents'])
        quantity, tip_percent = 1 + round_number % 2, 15 if round_number % 2 else 0
        selected = {'items': [{'itemId': 'house-special', 'quantity': quantity}], 'fulfillment': 'dine-in',
                    'paymentMethod': 'staff', 'tipPercent': tip_percent}
        selected['tableLabel' if round_number % 2 == 0 else 'tableToken'] = '1' if round_number % 2 == 0 else bar['table_token']
        quote = self.call('quote', guest + '/quote', selected, **common)
        subtotal = bar['price_cents'] * quantity
        tax = int((Decimal(subtotal) * Decimal('.07')).quantize(Decimal(1), rounding=ROUND_HALF_UP))
        tip = int((Decimal(subtotal) * tip_percent / 100).quantize(Decimal(1), rounding=ROUND_HALF_UP))
        expected = {'subtotalCents': subtotal, 'taxCents': tax, 'tipCents': tip,
                    'deliveryFeeCents': 0, 'totalCents': subtotal + tax + tip, 'tableLabel': 'Table 1'}
        self.check('quote-' + bar['id'] + '-' + str(round_number), all(quote[key] == value for key, value in expected.items()))
        body = {'requestKey': self.shared_keys[round_number], 'trackingKey': uuid.uuid4().hex + uuid.uuid4().hex,
            'order': selected, 'quoteFingerprint': quote['fingerprint'], 'customerName': bar['people']['customer']['name'],
            'phone': '', 'note': 'Fictional order for ' + bar['id']}
        receipt, status = self.request('submit', guest + '/orders', body, **common)
        order = {'tenant_id': bar['id'], 'round': round_number, 'id': receipt['orderId'], 'request_key': body['requestKey'],
            'tracking': body['trackingKey'], 'table_id': bar['table_id'], 'table_entry': 'manual' if round_number % 2 == 0 else 'qr',
            'expected': expected, 'unit_cents': bar['price_cents'], 'quantity': quantity, 'completed': False}
        bar['orders'].append(order)
        with self.lock:
            self.orders.append(order)
        self.check('receipt-' + bar['id'] + '-' + str(round_number), status == 201 and all(receipt['quote'][key] == value for key, value in expected.items()))
        if round_number == 0:
            replay, replay_status = self.request('idempotent-replay', guest + '/orders', body, **common)
            self.check('tenant-idempotency-' + bar['id'], replay_status == 200 and replay['orderId'] == order['id'])
        for version, (action, role) in enumerate([('accepted', 'owner'), ('preparing', 'kitchen'),
                ('ready', 'kitchen'), ('mark-paid', 'server'), ('completed', 'owner')]):
            board = self.call('staff-' + action, self.owner_path(bar) + '/ordering/orders/' + order['id'],
                {'expectedVersion': version, 'action': action, 'paymentCollected': action == 'mark-paid'},
                person=bar['people'][role]['key'], **common)
            self.inspect_board(bar, board, 'board-' + bar['id'] + '-' + str(round_number) + '-' + action)
            current = next(row['order'] for row in board['orders'] if row['order']['receipt']['orderId'] == order['id'])
            self.check('version-' + bar['id'] + '-' + str(round_number) + '-' + action, current['version'] == version + 1)
        tracking = self.call('track', guest + '/track', {'orderId': order['id'], 'trackingKey': order['tracking']}, **common)
        self.check('completed-' + bar['id'] + '-' + str(round_number), tracking['status'] == 'completed' and
            tracking['paymentStatus'] == 'paid_in_person' and all(tracking['quote'][key] == value for key, value in expected.items()))
        order['completed'] = True

    def venue_worker(self, bar):
        times, completed = [], 0
        for round_number in range(5):
            started = time.perf_counter()
            try:
                self.order_round(bar, round_number)
                completed += 1
            except Exception as error:
                self.stop.set()
                return {'tenant': bar['id'], 'completed': completed, 'failure': type(error).__name__ + ': ' + str(error)[:350], 'ms': times}
            times.append((time.perf_counter() - started) * 1000)
        return {'tenant': bar['id'], 'completed': completed, 'ms': times}

    def load(self):
        self.deadline = time.monotonic() + 240
        started = time.perf_counter()
        with concurrent.futures.ThreadPoolExecutor(max_workers=20) as pool:
            results = list(pool.map(self.venue_worker, self.bars))
        elapsed = time.perf_counter() - started
        requests = [row for row in self.requests if row['phase'] == 'load-20-bars']
        completed = sum(row['completed'] for row in results)
        stage = {'concurrent_venue_workflows': 20, 'completed_orders': completed, 'elapsed_seconds': round(elapsed, 3),
            'completed_orders_per_second': round(completed / elapsed, 3),
            'request_latency_ms': FRIDAY.distribution([row['ms'] for row in requests]),
            'order_workflow_latency_ms': FRIDAY.distribution([value for row in results for value in row['ms']]),
            'http_statuses': dict(Counter(str(row['status']) for row in requests)),
            'response_bytes': sum(row['response_bytes'] for row in requests),
            'per_tenant': [{k: value for k, value in row.items() if k != 'ms'} for row in results]}
        self.stages.append(stage)
        self.save()
        print(f'20 venue workflows: {completed}/100 complete, {stage["completed_orders_per_second"]}/s, '
              f'p95 {stage["request_latency_ms"].get("p95")}ms, HTTP {stage["http_statuses"]}', flush=True)

    def isolation_probes(self):
        for index, bar in enumerate(self.bars):
            foreign = self.bars[(index + 1) % len(self.bars)]
            own_order, foreign_order = bar['orders'][0], foreign['orders'][0]
            common = {'ip': bar['ip'], 'phase': 'load-isolation'}
            own_person = bar['people']['owner']['key']
            cases = [
                ('foreign-owner-board', self.owner_path(foreign) + '/ordering/operations', None, own_person, 403, 'forbidden'),
                ('own-path-foreign-order', self.owner_path(bar) + '/ordering/orders/' + foreign_order['id'],
                 {'expectedVersion': 5, 'action': 'completed'}, own_person, 404, 'not_found'),
                ('foreign-slug-valid-tracking-secret', self.guest_path(foreign) + '/track',
                 {'orderId': own_order['id'], 'trackingKey': own_order['tracking']}, None, 404, 'not_found'),
                ('foreign-table-token', self.guest_path(bar) + '/menu?table=' + foreign['table_token'], None, None, 404, 'not_found')]
            for name, path, body, person, status, code in cases:
                result = self.call(name, path, body, person=person, allow=(status,), **common)
                self.check(name + '-' + bar['id'], result.get('code') == code)
            for role in ('kitchen', 'server'):
                for action, path, body in [
                    ('read', self.owner_path(foreign) + '/ordering/operations', None),
                    ('change', self.owner_path(foreign) + '/ordering/orders/' + foreign_order['id'],
                     {'expectedVersion': 5, 'action': 'completed'})]:
                    result = self.call('foreign-' + role + '-' + action, path, body,
                        person=bar['people'][role]['key'], allow=(403,), **common)
                    self.check('foreign-' + role + '-' + action + '-' + bar['id'], result.get('code') == 'forbidden')
            selected = {'items': [{'itemId': 'house-special', 'quantity': 1}], 'fulfillment': 'dine-in',
                        'paymentMethod': 'staff', 'tableLabel': '1', 'tipPercent': 0}
            foreign_quote = self.call('foreign-public-quote', self.guest_path(foreign) + '/quote', selected, **common)
            result = self.call('foreign-quote-fingerprint', self.guest_path(bar) + '/orders',
                {'requestKey': ident(self.run.name + '/negative-quote'), 'trackingKey': uuid.uuid4().hex + uuid.uuid4().hex,
                 'order': selected, 'quoteFingerprint': foreign_quote['fingerprint'],
                 'customerName': bar['people']['customer']['name'], 'phone': '', 'note': 'Rejected isolation probe'},
                allow=(409,), **common)
            self.check('foreign-fingerprint-' + bar['id'], result.get('code') == 'stale_quote')
        for bar in self.bars:
            for foreign in self.bars:
                if bar['id'] == foreign['id']:
                    continue
                result = self.call('owner-foreign-board-matrix', self.owner_path(foreign) + '/ordering/operations',
                    person=bar['people']['owner']['key'], ip=bar['ip'], phase='load-isolation', allow=(403,))
                self.check('owner-matrix-' + bar['id'] + '-to-' + foreign['id'], result.get('code') == 'forbidden')
        self.report['isolation_probe_results'] = {'expected_denials': 560,
            'denial_statuses': dict(Counter(str(row['status']) for row in self.requests
                if row['phase'] == 'load-isolation' and row['status'] >= 400)),
            'owner_foreign_board_pairs_tested': 380, 'paired_resource_staff_tests': 180}
        print('560 cross-tenant probes rejected: 180 paired resource/staff checks plus 380 owner-to-foreign board pairs.', flush=True)

    def verify(self):
        with sqlite3.connect(self.db, timeout=20) as db:
            rows = db.execute('SELECT id,tenant_id,request_key,payload_json,status,version FROM bartide_enhanced_orders').fetchall()
            stored = {row[0]: row for row in rows}
            self.check('no-lost-duplicate-or-unexpected-orders', set(stored) == {order['id'] for order in self.orders} and len(stored) == len(self.orders))
            for bar in self.bars:
                own_rows = [row for row in rows if row[1] == bar['id']]
                self.check('stored-tenant-count-' + bar['id'], len(own_rows) == len(bar['orders']))
                if not self.stop.is_set():
                    self.check('five-complete-orders-' + bar['id'], len(own_rows) == 5)
                for order in bar['orders']:
                    row, expected = stored[order['id']], order['expected']
                    payload = json.loads(row[3])
                    self.check('stored-ownership-' + order['id'], row[1] == bar['id'] and row[2] == order['request_key'] and
                        payload['table_id'] == bar['table_id'] and payload['table_label'] == 'Table 1' and
                        payload['customer_name'] == bar['people']['customer']['name'] and
                        len(payload['lines']) == 1 and payload['lines'][0]['item_id'] == 'house-special' and
                        payload['lines'][0]['unit_cents'] == bar['price_cents'] and payload['lines'][0]['quantity'] == order['quantity'] and
                        payload['subtotal_cents'] == expected['subtotalCents'] and payload['tax_cents'] == expected['taxCents'] and
                        payload['tip_cents'] == expected['tipCents'] and payload['total_cents'] == expected['totalCents'] and
                        (not order['completed'] or row[4] == 'completed' and row[5] == 5 and
                         payload['payment_status'] == 'paid_in_person' and len(payload['history']) == 6))
                board = self.call('final-owner-board', self.owner_path(bar) + '/ordering/operations', person=bar['people']['owner']['key'], ip=bar['ip'], phase='verification')
                self.inspect_board(bar, board, 'final-owner-isolation-' + bar['id'])
                menu = self.call('final-menu', self.guest_path(bar) + '/menu', ip=bar['ip'], phase='verification')
                self.check('final-menu-isolation-' + bar['id'], menu['slug'] == bar['slug'] and menu['name'] == bar['name'] and
                    len(menu['items']) == 1 and menu['items'][0]['priceCents'] == bar['price_cents'] and
                    menu['items'][0]['name'] == bar['name'] + ' house special')
                stored_menu = json.loads(db.execute('SELECT menu_json FROM bartide_customers WHERE id=?', (bar['id'],)).fetchone()[0])
                self.check('distinct-location-' + bar['id'], stored_menu['venue']['area'] == bar['location'])
            if not self.stop.is_set():
                self.check('shared-request-key-namespace', sorted(db.execute('SELECT request_key,COUNT(DISTINCT tenant_id),COUNT(*) FROM bartide_enhanced_orders GROUP BY request_key').fetchall()) ==
                           sorted((key, 20, 20) for key in self.shared_keys))
            self.check('sqlite-integrity', db.execute('PRAGMA integrity_check').fetchone()[0] == 'ok')
            self.check('sqlite-foreign-keys', not db.execute('PRAGMA foreign_key_check').fetchall())
        self.report['integrity'] = {'accepted_orders': len(self.orders), 'completed_orders': sum(order['completed'] for order in self.orders),
            'cross_tenant_data_leaks_observed': 0, 'duplicate_or_lost_orders': 0,
            'checks_passed': sum(c['passed'] for c in self.checks), 'checks_failed': sum(not c['passed'] for c in self.checks)}

    def main(self):
        try:
            self.setup()
            self.load()
            if not self.stop.is_set():
                self.isolation_probes()
            self.report['load_and_probe_elapsed_seconds'] = round(240 - (self.deadline - time.monotonic()), 3)
            self.verify()
            self.report['result'] = 'bounded-stop' if self.stop.is_set() else 'passed'
        except Exception as error:
            self.report['result'], self.report['failure'] = 'failed', type(error).__name__ + ': ' + str(error)
            raise
        finally:
            self.monitor_done.set()
            for proc in reversed(FIXTURE.PROCESSES):
                if proc.poll() is None:
                    proc.terminate()
                    try:
                        proc.wait(timeout=15)
                    except FIXTURE.subprocess.TimeoutExpired:
                        proc.kill()
                        proc.wait(timeout=5)
            for log in FIXTURE.LOGS:
                log.close()
            if hasattr(self, 'provider'):
                self.provider.shutdown()
                self.provider.server_close()
            self.report['finished_at'] = datetime.now(timezone.utc).isoformat()
            self.save()
            print('REPORT ' + str(self.run / 'report.json'), flush=True)


if __name__ == '__main__':
    MultiBar().main()
