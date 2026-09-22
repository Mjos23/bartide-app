"""Real isolated-API checks for a 23-new-order/minute cap per venue location.

Build API first. Uses disposable fictional SQLite/PostgreSQL tenants, loopback-only service
snapshots, unchanged guest-request throttles, and no live identity/payment/email.
PostgreSQL mode uses --provider postgres with the installed Python 3.14 runtime
matching .tools/postgres-python. Old-minute state is seeded only in private storage to check rollover without
changing the system clock or waiting a full minute.
"""
import argparse
from collections import Counter
import concurrent.futures
from contextlib import contextmanager
from datetime import datetime, timezone
import importlib.util
import json
from pathlib import Path
import re
import sqlite3
import sys
import threading
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location('friday_helpers', ROOT / 'scripts/verify-friday-load.py')
FRIDAY = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(FRIDAY)
FIXTURE = FRIDAY.FIXTURE


class LocationLimit(FRIDAY.Runner):
    def __init__(self, storage_provider='sqlite', minimum_free_mib=512):
        self.run = ROOT / '.tools/location-order-limit' / datetime.now(timezone.utc).strftime('%Y%m%d-%H%M%S-%f')
        self.run.mkdir(parents=True, exist_ok=False)
        self.db = self.run / 'fictional-locations.db'
        self.api, self.web = ['http://127.0.0.1:' + str(FRIDAY.free_port()) for _ in range(2)]
        self.lock, self.stop, self.monitor_done = threading.Lock(), threading.Event(), threading.Event()
        self.requests, self.orders, self.resources, self.stages, self.checks = [], [], [], [], []
        self.tokens, self.provider_requests, self.bodies = {}, [], {}
        self.storage_provider = storage_provider
        self.minimum_free_mib = minimum_free_mib
        self.pg, self.pg_sql, self.pg_credentials = None, None, None
        self.pg_schema, self.pg_owned = None, False
        self.deadline = float('inf')
        self.locations = ['location-a', 'location-b', 'location-c', 'location-d', 'location-e']
        self.report = {'schema': 1, 'started_at': datetime.now(timezone.utc).isoformat(),
            'requirement': 'Each tenant/location accepts at most 23 new orders in a UTC wall-clock minute, across all client IPs.',
            'scope': 'One isolated loopback API, fresh disposable storage, fictional venues; no preview/live service access.',
            'storage_provider': storage_provider, 'public_demo_enabled': False,
            'resource_budget': {'minimum_host_free_mib': minimum_free_mib, 'api_logical_processor_count': 1,
                'maximum_api_private_mib': 2048, 'concurrent_order_attempts': 40,
                'purpose': 'Short bounded correctness checks; not a performance or capacity measurement.'},
            'limitations': ['Only the selected real storage provider is exercised in this run.',
                'Prior-minute bucket state is seeded locally; system-clock movement is not simulated.',
                'Public guest order routes are real; authentication is not needed for these endpoints.'],
            'requests': self.requests, 'orders': self.orders, 'checks': self.checks,
            'resources': self.resources, 'stages': self.stages}

    def watch_resources(self):
        # This focused runner may use the explicitly selected 300 MiB reserve on
        # a constrained machine. Friday/multibar stress-runner guards stay intact.
        while not self.monitor_done.wait(.5):
            snapshot = {'at': time.monotonic(), 'host': FRIDAY.memory(),
                        'processes': [FRIDAY.process_resource(proc) for proc in FIXTURE.PROCESSES]}
            self.resources.append(snapshot)
            if snapshot['host'].get('available_bytes', 10**10) < self.minimum_free_mib * 1024**2 or any(
                    proc.get('private_bytes', 0) > 2 * 1024**3 for proc in snapshot['processes']):
                self.report['resource_stop'] = f'Host free memory below {self.minimum_free_mib} MiB or API private memory above 2 GiB.'
                self.stop.set()

    @contextmanager
    def connection(self):
        raw = self.pg.connect(**self.pg_credentials,
            options='-c search_path=' + self.pg_schema + ' -c timezone=UTC -c lock_timeout=10000') if self.pg else sqlite3.connect(self.db, timeout=20)
        postgres = self.pg is not None
        class FixtureConnection:
            def execute(_, statement, parameters=()):
                # All SQL belongs to this fixture; question marks appear only as
                # parameter markers, never inside SQL literals or identifiers.
                return raw.execute(statement.replace('?', '%s') if postgres else statement, parameters)
        try:
            with raw:
                yield FixtureConnection()
        finally:
            raw.close()

    def configure_postgres(self, env):
        sys.path.insert(0, str(ROOT / '.tools/postgres-python'))
        import psycopg
        from psycopg import sql
        supplied = json.loads((ROOT / '.tools/postgresql-17-test/fixture.json').read_text(encoding='utf-8-sig'))
        if supplied['host'] not in ('127.0.0.1', 'localhost', '::1') or not 1024 <= int(supplied['port']) <= 65535:
            raise RuntimeError('Only the existing loopback PostgreSQL fixture is allowed')
        self.pg, self.pg_sql = psycopg, sql
        self.pg_credentials = {key: supplied[key] for key in ('host', 'port', 'user', 'password')}
        self.pg_credentials.update(dbname=supplied['database'], sslmode='disable', connect_timeout=5)
        self.pg_schema = 'tide_limit_' + uuid.uuid4().hex[:16]
        with self.pg.connect(**self.pg_credentials, autocommit=True) as db:
            self.report['postgres'] = {'schema': self.pg_schema, 'server_version': db.info.server_version,
                'transport': 'Existing disposable loopback fixture; Development API; schema unique to this run.'}
            db.execute(sql.SQL('CREATE SCHEMA {}').format(sql.Identifier(self.pg_schema)))
        self.pg_owned = True
        def quoted(value):
            return '"' + str(value).replace('"', '""') + '"'
        env.update({'Storage__Provider': 'PostgreSql', 'Storage__PostgresSchema': self.pg_schema,
            'Storage__CreatePostgresSchema': 'false',
            'ConnectionStrings__Application': ';'.join(name + '=' + quoted(self.pg_credentials[key])
                for name, key in [('Host', 'host'), ('Port', 'port'), ('Database', 'dbname'), ('Username', 'user'), ('Password', 'password')])
                + ';SSL Mode=Disable;Pooling=true;Maximum Pool Size=8'})

    def cleanup_postgres(self):
        if not self.pg_owned:
            return
        if not re.fullmatch(r'tide_limit_[a-f0-9]{16}', self.pg_schema):
            raise RuntimeError('Refusing cleanup outside this runner owned PostgreSQL schema')
        with self.pg.connect(**self.pg_credentials, autocommit=True) as db:
            db.execute(self.pg_sql.SQL('DROP SCHEMA {} CASCADE').format(self.pg_sql.Identifier(self.pg_schema)))
            self.report['postgres']['schema_removed'] = db.execute(
                'SELECT COUNT(*) FROM pg_namespace WHERE nspname=%s', (self.pg_schema,)).fetchone()[0] == 0
        self.pg_owned = False

    def route(self, location):
        return '/api/v1/restaurants/' + location

    def setup(self):
        FIXTURE.RUN, FIXTURE.DB, FIXTURE.API, FIXTURE.WEB = self.run, self.db, self.api, self.web
        class ProviderServer(FIXTURE.ThreadingHTTPServer):
            request_queue_size = 128
        self.provider = ProviderServer(('127.0.0.1', 0), FIXTURE.Provider)
        threading.Thread(target=self.provider.serve_forever, daemon=True).start()
        env = FIXTURE.environment(self.provider.server_port)
        env['ConnectionStrings__Application'] = ''
        env['SampleBar__Enabled'] = 'false'
        env.update({'PublicDemo__Enabled': 'false', 'DOTNET_PROCESSOR_COUNT': '1', 'Notifications__Mode': 'disabled',
            'MerchantPayments__CheckoutEnabled': 'false', 'MerchantPayments__OnboardingEnabled': 'false',
            'MerchantPayments__RestrictedKey': '', 'ServiceBilling__RestrictedKey': ''})
        if self.storage_provider == 'postgres':
            self.configure_postgres(env)
        FIXTURE.launch('TideCasa.Api', self.api, env, self.run / 'runtime')
        self.report['runtime'] = {'api': self.api, 'private_database': str(self.db) if not self.pg else None,
            'owned_api_pids': [proc.pid for proc in FIXTURE.PROCESSES],
            'api_sha256': FRIDAY.digest(self.run / 'runtime/TideCasa.Api/TideCasa.Api.dll'),
            'ordering_source_sha256': FRIDAY.digest(ROOT / 'TideCasa.Api/Features/RestaurantOrdering/RestaurantOrderingStore.cs')}
        now = datetime.now(timezone.utc).isoformat()
        with self.connection() as db:
            for location in self.locations:
                menu = {'schema': 'bartide-menu/1', 'venue': {'name': 'Fictional ' + location, 'vertical': 'bartide',
                    'area': 'Isolated test location', 'tagline': '', 'hours_text': '', 'website_url': '',
                    'service_note': 'Fictional test orders only.', 'currency': 'USD'},
                    'categories': [{'id': 'food', 'label': 'Food'}], 'items': [{'id': 'test-meal', 'category': 'food',
                    'name': 'Fictional meal', 'description': '', 'price_cents': 1000, 'price_label': None, 'available': True}]}
                config = {'enabled': True, 'accepting_orders': True, 'delivery_enabled': True, 'tax_basis_points': 700,
                    'delivery_fee_cents': 0, 'delivery_minimum_cents': 0, 'delivery_capacity': 1, 'delivery_zips': ['33706'],
                    'blocked_item_ids': [], 'checkout': {'dine_in_enabled': True, 'pickup_enabled': True,
                    'pay_staff_enabled': True, 'tips_enabled': True, 'tables': [{'id': str(uuid.uuid4()),
                    'label': 'Table 1', 'token': uuid.uuid4().hex + uuid.uuid4().hex, 'enabled': True}]}}
                db.execute('INSERT INTO bartide_customers(id,slug,email,user_id,name,menu_json,version,status,enrollment_note,created_at,updated_at,vertical) VALUES(?,?,?,?,?,?,0,?,?,?,?,?)',
                    (location, location, location + '@example.invalid', 'supabase:' + str(uuid.uuid4()),
                    'Fictional ' + location, json.dumps(menu), 'active', 'Disposable location cap regression', now, now, 'bartide'))
                db.execute('INSERT INTO bartide_enhanced_configs(tenant_id,settings_json,version,updated_at) VALUES(?,?,0,?)',
                    (location, json.dumps(config), now))
        self.monitor = threading.Thread(target=self.watch_resources, daemon=True)
        self.monitor.start()

    def quote(self, location, delivery=False):
        selected = {'items': [{'itemId': 'test-meal', 'quantity': 1}], 'fulfillment': 'delivery' if delivery else 'dine-in',
            'paymentMethod': 'staff', 'tipPercent': 0}
        selected['deliveryZip' if delivery else 'tableLabel'] = '33706' if delivery else '1'
        quote = self.call('quote', self.route(location) + '/quote', selected, ip='192.0.2.240', phase='load-tests')
        return selected, quote['fingerprint']

    def body(self, selection, fingerprint, delivery=False):
        return {'requestKey': str(uuid.uuid4()), 'trackingKey': uuid.uuid4().hex + uuid.uuid4().hex,
            'order': selection, 'quoteFingerprint': fingerprint, 'customerName': 'Fictional test guest',
            'phone': '(727) 555-0147' if delivery else '',
            'address': 'Fictional delivery desk, 33706' if delivery else None,
            'note': 'Isolated per-location limit regression.'}

    def submit(self, location, body, ip, operation='submit'):
        result, status = self.request(operation, self.route(location) + '/orders', body,
            ip=ip, phase='load-tests', allow=(200, 201, 400, 409, 429))
        if status == 201:
            with self.lock:
                self.orders.append({'id': result['orderId'], 'tenant_id': location, 'request_key': body['requestKey']})
        return result, status

    def count(self, location):
        with self.connection() as db:
            return db.execute('SELECT COUNT(*) FROM bartide_enhanced_orders WHERE tenant_id=?', (location,)).fetchone()[0]

    def tests(self):
        # Keep all acceptance/concurrency checks within one real minute. This
        # wait is always below 30 seconds, never a full-minute rollover wait.
        remaining = 60 - time.time() % 60
        if remaining < 30:
            print(f'Aligning isolated checks with a fresh minute ({remaining:.1f}s maximum wait).', flush=True)
            time.sleep(remaining + .1)
        started, minute = time.monotonic(), int(time.time() // 60)
        self.deadline = started + 120
        selected, fingerprint = self.quote('location-a')
        first_body = self.body(selected, fingerprint)
        first, status = self.submit('location-a', first_body, '192.0.2.1')
        self.check('first-location-order-accepted', status == 201)
        for n in range(5):
            replay, status = self.submit('location-a', first_body, '198.51.100.' + str(n + 1), 'retry')
            self.check('retry-does-not-create-' + str(n), status == 200 and replay['orderId'] == first['orderId'])
        statuses = [201]
        for n in range(2, 25):
            _, status = self.submit('location-a', self.body(selected, fingerprint), '192.0.2.' + str(n))
            statuses.append(status)
        self.report['different_ip_location_statuses'] = statuses
        self.check('location-cap-is-23-across-different-ips', statuses == [201] * 23 + [429], statuses)
        replay, status = self.submit('location-a', first_body, '198.51.100.6', 'retry-while-full')
        self.check('full-location-still-allows-idempotent-retry', status == 200 and replay['orderId'] == first['orderId'])
        conflict, status = self.submit('location-a', {**first_body, 'customerName': 'Different guest'}, '198.51.100.7', 'changed-retry')
        self.check('changed-retry-remains-conflict', status == 409 and conflict.get('code') == 'request_conflict')
        self.check('retries-never-add-orders', self.count('location-a') == 23)

        selected, fingerprint = self.quote('location-b')
        statuses = [self.submit('location-b', self.body(selected, fingerprint), '198.51.100.100')[1] for _ in range(24)]
        self.report['second_location_same_ip_statuses'] = statuses
        self.check('second-location-independent-and-old-ten-ip-cap-removed', statuses == [201] * 23 + [429], statuses)

        selected, fingerprint = self.quote('location-c')
        jobs = [self.body(selected, fingerprint) for _ in range(40)]
        with concurrent.futures.ThreadPoolExecutor(max_workers=40) as pool:
            results = list(pool.map(lambda pair: self.submit('location-c', pair[1], '203.0.113.' + str(pair[0] + 1), 'concurrent-submit'), enumerate(jobs)))
        statuses = Counter(status for _, status in results)
        self.report['concurrent_location_statuses'] = dict(statuses)
        self.check('concurrent-new-orders-atomically-capped-at-23', statuses == {201: 23, 429: 17}, dict(statuses))
        self.check('concurrent-only-23-persisted', self.count('location-c') == 23)

        delivery, delivery_fingerprint = self.quote('location-d', delivery=True)
        result, status = self.submit('location-d', self.body(delivery, '0' * 64, delivery=True), '198.51.100.150', 'stale-quote')
        self.check('bad-quote-rejected-before-counting', status == 409 and result.get('code') == 'stale_quote')
        _, status = self.submit('location-d', self.body(delivery, delivery_fingerprint, delivery=True), '198.51.100.150')
        self.check('first-delivery-accepted', status == 201)
        result, status = self.submit('location-d', self.body(delivery, delivery_fingerprint, delivery=True), '198.51.100.151', 'delivery-full')
        self.check('post-limit-check-rejection-is-delivery-capacity', status == 409 and result.get('code') == 'delivery_full')
        selected, fingerprint = self.quote('location-d')
        statuses = [self.submit('location-d', self.body(selected, fingerprint), '198.51.100.' + str(n + 152))[1] for n in range(23)]
        self.check('rejected-order-rolls-back-location-allowance', statuses == [201] * 22 + [429], statuses)
        self.check('rollback-case-persists-exactly-23', self.count('location-d') == 23)

        current = int(time.time() // 60)
        prior_key = 'dotnet-order-location:location-e:' + str(current - 1)
        expired_key = 'dotnet-order-location:expired-fixture:' + str(current - 2)
        with self.connection() as db:
            db.execute('INSERT INTO bartide_enhanced_limits(id,count,expires_at) VALUES(?,23,?)', (prior_key, int(time.time() * 1000) + 120000))
            db.execute('INSERT INTO bartide_enhanced_limits(id,count,expires_at) VALUES(?,23,?)', (expired_key, int(time.time() * 1000) - 1))
        selected, fingerprint = self.quote('location-e')
        _, status = self.submit('location-e', self.body(selected, fingerprint), '203.0.113.100')
        self.check('prior-minute-full-bucket-does-not-block-current-minute', status == 201)
        with self.connection() as db:
            self.check('prior-minute-count-remains-separate', db.execute('SELECT count FROM bartide_enhanced_limits WHERE id=?', (prior_key,)).fetchone() == (23,))
            self.check('current-minute-gets-independent-count', db.execute('SELECT count FROM bartide_enhanced_limits WHERE id=?', ('dotnet-order-location:location-e:' + str(current),)).fetchone() == (1,))
            self.check('expired-buckets-still-cleaned', db.execute('SELECT COUNT(*) FROM bartide_enhanced_limits WHERE id=?', (expired_key,)).fetchone()[0] == 0)
        self.check('acceptance-and-concurrency-checks-stayed-in-one-minute', minute == int(time.time() // 60))
        gateway_statuses = [self.request('guest-request-guard', self.route('location-e') + '/menu',
            ip='203.0.113.240', phase='load-tests', allow=(200, 429))[1] for _ in range(121)]
        self.check('guest-120-request-ip-guard-preserved', gateway_statuses == [200] * 120 + [429])
        with self.connection() as db:
            rows = db.execute('SELECT id,tenant_id,request_key FROM bartide_enhanced_orders').fetchall()
            self.check('no-lost-duplicate-or-unexpected-orders', {row[0] for row in rows} == {o['id'] for o in self.orders} and len(rows) == len(self.orders))
            self.report['persisted_orders'] = [{'id': row[0], 'tenant_id': row[1], 'request_key': row[2]} for row in rows]
            self.report['persisted_limit_buckets'] = [{'id': row[0], 'count': row[1]} for row in
                db.execute('SELECT id,count FROM bartide_enhanced_limits ORDER BY id').fetchall()]
            if self.pg:
                self.check('postgres-isolated-schema-and-valid-constraints', db.execute('SELECT current_schema()').fetchone()[0] == self.pg_schema and
                    db.execute('SELECT COUNT(*) FROM pg_constraint c JOIN pg_namespace n ON n.oid=c.connamespace WHERE n.nspname=current_schema() AND NOT c.convalidated').fetchone()[0] == 0)
            else:
                self.check('sqlite-integrity', db.execute('PRAGMA integrity_check').fetchone()[0] == 'ok')
        self.report['elapsed_test_seconds'] = round(time.monotonic() - started, 3)
        self.report['accepted_by_location'] = {location: self.count(location) for location in self.locations}
        print('PASS: 23/location across IPs; independent location; same-IP23; concurrent23/40; retries, rollback, old-minute isolation and guest request guard.', flush=True)

    def main(self):
        try:
            self.setup()
            self.tests()
            self.report['result'] = 'passed'
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
            self.report['owned_api_processes_stopped'] = all(proc.poll() is not None for proc in FIXTURE.PROCESSES)
            for log in FIXTURE.LOGS:
                log.close()
            if hasattr(self, 'provider'):
                self.provider.shutdown()
                self.provider.server_close()
            try:
                self.cleanup_postgres()
            except Exception as error:
                self.report['result'] = 'failed'
                self.report['cleanup_failure'] = type(error).__name__
                raise
            finally:
                self.report['finished_at'] = datetime.now(timezone.utc).isoformat()
                self.save()
                print('REPORT ' + str(self.run / 'report.json'), flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--provider', choices=['sqlite', 'postgres'], default='sqlite')
    parser.add_argument('--minimum-free-mib', type=int, choices=[300, 512], default=512,
                        help='Explicitly bounded host-memory reserve for this correctness test only.')
    arguments = parser.parse_args()
    LocationLimit(arguments.provider, arguments.minimum_free_mib).main()
