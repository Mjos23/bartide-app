"""Bounded, local-only Gulf Lantern Friday workload on a SQLite backup.

Build API and Blazor first. This never changes, stresses, or stops the preview.
The existing 12 fictional accounts are reused; simulated sessions are NOT unique
people. Real app routes, persistence, roles and throttles run with a fake local
identity provider. Results are local measurements, not production capacity.
"""
import argparse
from collections import Counter
import concurrent.futures
import ctypes
from datetime import datetime, timezone
from decimal import Decimal, ROUND_HALF_UP
import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path
import platform
import re
import shutil
import socket
import sqlite3
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location('sample_fixture', ROOT / 'scripts/run-sample-bar.py')
FIXTURE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(FIXTURE)
BASE, GUEST = FIXTURE.BASE, FIXTURE.GUEST


class StopWork(RuntimeError):
    pass


class HttpFailure(RuntimeError):
    def __init__(self, operation, status, body):
        super().__init__(f'{operation}: HTTP {status}: {str(body)[:250]}')
        self.status = status


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def free_port():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        return sock.getsockname()[1]


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def distribution(values):
    values = sorted(values)
    if not values:
        return {'count': 0}
    return {'count': len(values), 'p50': round(values[math.ceil(len(values) * .50) - 1], 2),
            'p95': round(values[math.ceil(len(values) * .95) - 1], 2),
            'max': round(values[-1], 2)}


def memory():
    if os.name != 'nt':
        return {}
    class Memory(ctypes.Structure):
        _fields_ = [('length', ctypes.c_ulong), ('load', ctypes.c_ulong)] + [
            (name, ctypes.c_ulonglong) for name in ('total', 'available', 'page_total',
                                                  'page_available', 'virtual_total', 'virtual_available', 'extended')]
    info = Memory()
    info.length = ctypes.sizeof(info)
    ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(info))
    return {'total_bytes': info.total, 'available_bytes': info.available, 'load_percent': info.load}


def process_resource(proc):
    if os.name != 'nt' or proc.poll() is not None:
        return {'pid': proc.pid}
    class Counters(ctypes.Structure):
        _fields_ = [('cb', ctypes.c_ulong), ('faults', ctypes.c_ulong)] + [
            (name, ctypes.c_size_t) for name in ('peak_working', 'working', 'peak_paged',
                'paged', 'peak_nonpaged', 'nonpaged', 'pagefile', 'peak_pagefile', 'private')]
    info = Counters()
    info.cb = ctypes.sizeof(info)
    handle = ctypes.c_void_p(int(proc._handle))
    ctypes.windll.psapi.GetProcessMemoryInfo(handle, ctypes.byref(info), info.cb)
    times = [ctypes.c_ulonglong() for _ in range(4)]
    ctypes.windll.kernel32.GetProcessTimes(handle, *[ctypes.byref(value) for value in times])
    return {'pid': proc.pid, 'working_set_bytes': info.working, 'private_bytes': info.private,
            'cpu_seconds': (times[2].value + times[3].value) / 10000000}


class Runner:
    def __init__(self, args):
        self.args = args
        self.run = ROOT / '.tools/friday-night-load' / datetime.now(timezone.utc).strftime('%Y%m%d-%H%M%S-%f')
        self.run.mkdir(parents=True, exist_ok=False)
        self.db = self.run / 'synthetic.db'
        self.api, self.web = ['http://127.0.0.1:' + str(free_port()) for _ in range(2)]
        self.lock, self.stop, self.monitor_done = threading.Lock(), threading.Event(), threading.Event()
        self.requests, self.orders, self.resources, self.stages, self.checks = [], [], [], [], []
        self.provider_requests = []
        self.tokens, self.wallets, self.events = {}, {}, []
        self.reservations = {}
        self.deadline = float('inf')
        self.report = {'schema': 1, 'started_at': datetime.now(timezone.utc).isoformat(),
            'scope': 'Isolated loopback development API + Web; cloned SQLite fixture; fake identity boundary.',
            'limitations': ['No production-capacity claim; no network latency, live identity provider, real payment, email or deployment.',
                'Sessions reuse five customer identities and seven owner/staff identities, not new unique people.',
                'Orders use the public guest checkout with the five sample names; rewards/events use their authenticated accounts.',
                'Closed-loop saturation workload has no human think time. Staff transitions are automated.',
                'Web page reads are smoke checks; browser rendering, SignalR circuits and video streaming are not load-tested.',
                'API and client share the same Windows machine. Existing preview remains running. SQLite differs from production PostgreSQL.'],
            'workload': {'concurrency_stages': args.concurrency, 'rounds_per_stage': args.rounds,
                'unique_customer_accounts': 5, 'unique_owner_staff_accounts': 7,
                'table_mode': args.table_mode, 'max_submitted_orders': 250, 'max_load_seconds': 240,
                'request_timeout_seconds': 20, 'shared_ip_probe_orders': 11,
                'resource_stop': 'Host free RAM below 512 MiB, or owned process private RAM above 2 GiB; sampled every 0.5s.',
                'client_ip_model': 'One stable documentation IP per synthetic session in each stage; reused for all calls in that session.',
                'scenario': 'Menu + events + rewards reads; quote; submit; duplicate replay for first order/stage; accept, prepare, ready, collect fictional staff payment, complete; track receipt.',
                'guards_unchanged': {'order_submissions_per_ip_per_wall_clock_minute': 10,
                    'guest_requests_per_ip_per_fixed_window_minute': 120, 'active_order_queue': 100}},
            'hardware': {'os': platform.platform(), 'logical_cpu_count': os.cpu_count(),
                'processor': os.environ.get('PROCESSOR_IDENTIFIER', platform.processor()), 'memory_at_start': memory()},
            'requests': self.requests, 'stages': self.stages, 'orders': self.orders,
            'resources': self.resources, 'checks': self.checks}

    def check(self, name, valid, detail=None):
        with self.lock:
            self.checks.append({'name': name, 'passed': bool(valid), 'detail': detail})
        if not valid:
            raise AssertionError(name + ': ' + str(detail)[:300])

    def request(self, operation, path, body=None, person=None, ip='192.0.2.240',
                phase='setup', allow=(200, 201), web=False):
        if (phase.startswith('load-') or phase == 'shared-ip-probe') and (
                self.stop.is_set() or time.monotonic() >= self.deadline):
            raise StopWork('Load time/resource/error safety stop')
        # The sample Web hub deliberately only admits loopback callers. Simulated
        # client IPs belong on API requests, behind its configured local proxy.
        headers = {} if web else {'X-Forwarded-For': ip}
        if person:
            headers['Authorization'] = 'Bearer ' + self.tokens[person]
        raw = None
        if body is not None:
            raw = json.dumps(body).encode()
            headers['Content-Type'] = 'application/json'
        request = urllib.request.Request((self.web if web else self.api) + path, data=raw, headers=headers)
        start = time.perf_counter()
        result, status, size, error = None, 0, 0, None
        try:
            opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
            try:
                response = opener.open(request, timeout=20)
            except urllib.error.HTTPError as failure:
                response = failure
            with response:
                data, status = response.read(), response.status
                size = len(data)
                try:
                    result = json.loads(data)
                except (ValueError, UnicodeDecodeError):
                    result = None
        except (OSError, urllib.error.URLError) as failure:
            error = type(failure).__name__ + ': ' + str(failure)[:160]
        metric = {'phase': phase, 'operation': operation, 'status': status,
                  'ms': round((time.perf_counter() - start) * 1000, 3), 'response_bytes': size,
                  'code': result.get('code') if isinstance(result, dict) else None}
        if status >= 400 and isinstance(result, dict):
            metric['problem_title'] = result.get('title')
        if error:
            metric['transport_error'] = error
        with self.lock:
            self.requests.append(metric)
            failures = [x for x in self.requests if x['phase'] == phase and (x['status'] == 0 or x['status'] >= 500)]
            if phase.startswith('load-') and len(failures) >= 5:
                self.stop.set()
        if status not in allow:
            raise HttpFailure(operation, status, result or error)
        return result, status

    def call(self, *args, **kwargs):
        return self.request(*args, **kwargs)[0]

    def setup(self):
        source = ROOT / '.tools/gulf-lantern-preview/gulf-lantern.db'
        with sqlite3.connect(source.as_uri() + '?mode=ro', uri=True, timeout=20) as original:
            with sqlite3.connect(self.db) as clone:
                original.backup(clone)
                self.initial_order_ids = {r[0] for r in clone.execute('SELECT id FROM bartide_enhanced_orders')}
                config = json.loads(clone.execute('SELECT settings_json FROM bartide_enhanced_configs WHERE tenant_id=?', ('gulf-lantern',)).fetchone()[0])
                self.tables = [t for t in config['checkout']['tables'] if t['enabled']]
                self.tax = config['tax_basis_points']
                self.report['sqlite'] = {'journal_mode': clone.execute('PRAGMA journal_mode').fetchone()[0],
                                        'source_initial_orders': len(self.initial_order_ids), 'enabled_tables': len(self.tables)}
        self.check('source_is_fictional_gulf_lantern', FIXTURE.BAR['fictional'] is True and len(FIXTURE.BAR['people']) == 12)
        self.check('at_least_one_active_table', len(self.tables) > 0)
        if self.args.table_mode == 'mixed':
            self.check('manual_numbered_tables_present', all(any(t['label'] == 'Table ' + str(n) for t in self.tables) for n in range(1, 13)))
        shutil.copytree(source.parent / 'private-objects', self.run / 'private-objects')
        FIXTURE.RUN, FIXTURE.DB, FIXTURE.API, FIXTURE.WEB = self.run, self.db, self.api, self.web
        runner = self
        class MeasuredProvider(FIXTURE.Provider):
            def reply(handler, status, body):
                handler.response_status = status
                return super(MeasuredProvider, handler).reply(status, body)

            def measured_request(handler):
                start = time.perf_counter()
                handler.response_status = 0
                failure = None
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
            # Configure the listener before activation, including on Windows.
            request_queue_size = 128

        self.provider = ProviderServer(('127.0.0.1', 0), MeasuredProvider)
        threading.Thread(target=self.provider.serve_forever, daemon=True).start()
        env = FIXTURE.environment(self.provider.server_port)
        env['ConnectionStrings__Application'] = ''
        snapshot = self.run / 'runtime'
        for project, url in [('TideCasa.Api', self.api), ('TideCasa.Blazor', self.web)]:
            FIXTURE.launch(project, url, env, snapshot)
        self.report['runtime'] = {'api': self.api, 'web': self.web,
            'build_hashes': {p: digest(snapshot / p / (p + '.dll')) for p in ('TideCasa.Api', 'TideCasa.Blazor')},
            'private_database': str(self.db), 'source_database_read_only': str(source)}
        for person in FIXTURE.BAR['people']:
            self.tokens[person['key']] = self.call('signin', '/api/v1/auth/signin',
                {'email': person['email'], 'password': FIXTURE.BAR['password']})['accessToken']
        self.customers = [p for p in FIXTURE.BAR['people'] if p['role'] == 'customer']
        self.staff = [p for p in FIXTURE.BAR['people'] if p['role'] != 'customer']
        self.events = self.call('events', GUEST + '/events')['events']
        for index, customer in enumerate(self.customers):
            key = customer['key']
            wallet = self.call('wallet-before', GUEST + '/rewards', person=key)
            self.wallets[key] = wallet['points']
            self.call('award-five-fictional-points', BASE + '/rewards/points',
                {'requestId': str(uuid.uuid4()), 'memberId': wallet['memberId'], 'points': 5,
                 'sourceReference': 'friday-' + self.run.name + '-' + key,
                 'reason': 'Fictional isolated Friday load rehearsal'}, person='owner')
            mine = self.call('rsvps', GUEST + '/events/mine', person=key)['reservations']
            event = self.events[(index + 1) % len(self.events)]
            old = next((r['rsvp'] for r in mine if r['event']['id'] == event['id']), None)
            self.call('rsvp', GUEST + '/events/' + event['id'] + '/rsvp',
                {'requestKey': str(uuid.uuid4()), 'expectedVersion': old['version'] if old else -1,
                 'attending': True}, person=key)
            self.reservations[key] = event['id']
        self.call('web-sample-page', '/sample-bar', web=True)
        self.call('web-order-page', '/order/gulf-lantern', web=True)
        self.monitor = threading.Thread(target=self.watch_resources, daemon=True)
        self.monitor.start()
        print(f'Isolated API/Web ready; {len(self.tables)} active tables; 12 reused sample identities.', flush=True)

    def watch_resources(self):
        while not self.monitor_done.wait(.5):
            snapshot = {'at': time.monotonic(), 'host': memory(),
                        'processes': [process_resource(proc) for proc in FIXTURE.PROCESSES]}
            self.resources.append(snapshot)
            if snapshot['host'].get('available_bytes', 10**10) < 512 * 1024**2 or any(
                    p.get('private_bytes', 0) > 2 * 1024**3 for p in snapshot['processes']):
                self.report['resource_stop'] = 'Host free memory below 512 MiB or owned process private memory above 2 GiB.'
                self.stop.set()

    def new_order(self, number, ip, phase, replay=False):
        customer = self.customers[number % len(self.customers)]
        table = self.tables[number % len(self.tables)]
        item = FIXTURE.BAR['menu'][number % len(FIXTURE.BAR['menu'])]
        quantity, tip_percent = 1 + number % 3, [0, 15, 20, 25][number % 4]
        selected = {'items': [{'itemId': item['id'], 'quantity': quantity}],
                    'fulfillment': 'dine-in', 'paymentMethod': 'staff', 'tipPercent': tip_percent}
        manual = self.args.table_mode == 'mixed' and number % 2 == 0
        selected['tableLabel' if manual else 'tableToken'] = (
            re.sub(r'^Table ', '', table['label']) if manual else table['token'])
        common = {'ip': ip, 'phase': phase}
        quote = self.call('quote', GUEST + '/quote', selected, **common)
        subtotal = item['priceCents'] * quantity
        tax = int((Decimal(subtotal) * Decimal(self.tax) / 10000).quantize(Decimal('1'), rounding=ROUND_HALF_UP))
        tip = int((Decimal(subtotal) * tip_percent / 100).quantize(Decimal('1'), rounding=ROUND_HALF_UP))
        expected = {'subtotalCents': subtotal, 'taxCents': tax, 'tipCents': tip,
                    'deliveryFeeCents': 0, 'totalCents': subtotal + tax + tip, 'tableLabel': table['label']}
        self.check('quote-correct-' + str(number), all(quote[k] == v for k, v in expected.items()), expected)
        body = {'requestKey': str(uuid.uuid4()), 'trackingKey': uuid.uuid4().hex + uuid.uuid4().hex,
                'order': selected, 'quoteFingerprint': quote['fingerprint'],
                'customerName': customer['name'], 'phone': '', 'note': f'Fictional Friday synthetic session {number}.'}
        receipt, status = self.request('submit', GUEST + '/orders', body, **common)
        record = {'number': number, 'phase': phase, 'request_key': body['requestKey'], 'id': receipt['orderId'],
                  'customer_key': customer['key'], 'table_id': table['id'], 'table_entry': 'manual' if manual else 'qr',
                  'expected_lines': [{'item_id': item['id'], 'name': item['name'], 'quantity': quantity, 'unit_cents': item['priceCents']}],
                  'expected': expected, 'completed': False, 'tracking': body['trackingKey']}
        with self.lock:
            self.orders.append(record)
        self.check('receipt-correct-' + str(number), status == 201 and all(receipt['quote'][k] == v for k, v in expected.items()))
        if replay:
            again, status = self.request('submit-idempotent-replay', GUEST + '/orders', body, **common)
            self.check('one-order-on-replay-' + str(number), status == 200 and again['orderId'] == record['id'])
        return record

    def complete(self, order, ip, phase):
        actors = [('accepted', 'manager'), ('preparing', 'kitchen'), ('ready', 'bartender'),
                  ('mark-paid', 'server-maya' if order['number'] % 2 else 'server-eli'), ('completed', 'manager')]
        for version, (action, person) in enumerate(actors):
            result = self.call('staff-' + action, BASE + '/ordering/orders/' + order['id'],
                {'expectedVersion': version, 'action': action, 'paymentCollected': action == 'mark-paid'},
                person=person, ip=ip, phase=phase)
            row = next((entry['order'] for entry in result['orders'] if entry['order']['receipt']['orderId'] == order['id']), None)
            self.check('staff-version-' + str(order['number']) + '-' + action,
                       row is not None and row['version'] == version + 1)
        receipt = self.call('track-completed', GUEST + '/track',
            {'orderId': order['id'], 'trackingKey': order['tracking']}, ip=ip, phase=phase)
        self.check('completed-paid-' + str(order['number']), receipt['status'] == 'completed' and
                   receipt['paymentStatus'] == 'paid_in_person' and
                   all(receipt['quote'][k] == v for k, v in order['expected'].items()))
        order['completed'] = True

    def session(self, stage, session):
        address = stage * 50 + session
        ip = ['192.0.2.', '198.51.100.', '203.0.113.'][address // 200] + str(address % 200 + 1)
        phase = 'load-' + str(stage)
        completed, outcomes, times = 0, [], []
        for round_number in range(self.args.rounds):
            start = time.perf_counter()
            number = stage * 1000 + session * self.args.rounds + round_number
            person = self.customers[number % 5]['key']
            try:
                self.call('menu', GUEST + '/menu', ip=ip, phase=phase)
                self.call('events', GUEST + '/events', ip=ip, phase=phase)
                self.call('wallet', GUEST + '/rewards', person=person, ip=ip, phase=phase)
                order = self.new_order(number, ip, phase, replay=session == 0 and round_number == 0)
                self.complete(order, ip, phase)
                completed += 1
                outcomes.append('completed')
            except HttpFailure as failure:
                outcomes.append('throttled' if failure.status == 429 else 'conflict' if failure.status == 409 else 'failed')
            except StopWork:
                outcomes.append('safety-stopped')
                break
            except Exception as failure:
                self.stop.set()
                outcomes.append('integrity-failure:' + str(failure)[:300])
                break
            times.append((time.perf_counter() - start) * 1000)
        return completed, outcomes, times

    def load(self):
        self.deadline = time.monotonic() + 240
        for stage, concurrency in enumerate(self.args.concurrency):
            if self.stop.is_set() or time.monotonic() >= self.deadline:
                break
            phase = 'load-' + str(stage)
            start, before = time.perf_counter(), [process_resource(p) for p in FIXTURE.PROCESSES]
            for staff in self.staff:
                self.call('staff-board', BASE + '/ordering/operations', person=staff['key'], phase=phase)
                self.call('team-read', BASE + '/team', person=staff['key'], phase=phase)
            with concurrent.futures.ThreadPoolExecutor(max_workers=concurrency) as pool:
                results = list(pool.map(lambda n: self.session(stage, n), range(concurrency)))
            elapsed = time.perf_counter() - start
            metrics = [r for r in self.requests if r['phase'] == phase]
            completed = sum(r[0] for r in results)
            outcomes = Counter(outcome for r in results for outcome in r[1])
            summary = {'phase': phase, 'synthetic_concurrent_sessions': concurrency,
                'attempted_scenarios': sum(outcomes.values()), 'completed_orders': completed,
                'elapsed_seconds': round(elapsed, 3), 'completed_orders_per_second': round(completed / elapsed, 3),
                'requests_per_second': round(len(metrics) / elapsed, 3), 'outcomes': dict(outcomes),
                'http_statuses': dict(Counter(str(r['status']) for r in metrics)),
                'request_latency_ms': distribution([r['ms'] for r in metrics]),
                'scenario_latency_ms': distribution([ms for r in results for ms in r[2]]),
                'response_bytes': sum(r['response_bytes'] for r in metrics),
                'operations': {op: {'latency_ms': distribution([r['ms'] for r in metrics if r['operation'] == op]),
                    'response_bytes': sum(r['response_bytes'] for r in metrics if r['operation'] == op)}
                    for op in sorted({r['operation'] for r in metrics})},
                'process_before': before, 'process_after': [process_resource(p) for p in FIXTURE.PROCESSES]}
            self.stages.append(summary)
            self.save()
            print(f'{concurrency} sessions: {completed}/{concurrency * self.args.rounds} complete, '
                  f'{summary["completed_orders_per_second"]}/s, p95 {summary["request_latency_ms"]["p95"]}ms, '
                  f'HTTP {summary["http_statuses"]}', flush=True)
            if completed < concurrency * self.args.rounds * .9:
                self.report['load_stop'] = 'Fewer than 90% of scheduled scenarios completed; stopped increasing concurrency.'
                break
        self.report['load_elapsed_seconds'] = round(240 - (self.deadline - time.monotonic()), 3)

    def shared_ip_probe(self):
        # 11 orders + 11 quotes, then 50 transitions + 10 tracking reads: bounded 82 requests.
        # Start only with >=30s before wall-clock minute end to make the 10/IP bucket observable.
        seconds_left = 60 - time.time() % 60
        if seconds_left < 30:
            time.sleep(seconds_left + .15)
        bucket = int(time.time() // 60)
        statuses, created = [], []
        for index in range(11):
            try:
                created.append(self.new_order(90000 + index, '203.0.113.77', 'shared-ip-probe'))
                statuses.append(201)
            except HttpFailure as failure:
                statuses.append(failure.status)
        self.report['shared_ip_probe'] = {'submitted_attempts': 11, 'statuses': statuses,
            'same_wall_clock_minute': bucket == int(time.time() // 60),
            'interpretation': 'Per-venue order protection is keyed by client IP; guests on one shared public IP share 10 submissions/minute.'}
        self.check('shared-ip-order-limit-observed', statuses == [201] * 10 + [429] and bucket == int(time.time() // 60))
        for order in created:
            self.complete(order, '203.0.113.77', 'shared-ip-probe')
        print('Shared-IP probe: first 10 orders accepted, 11th returned configured HTTP 429.', flush=True)

    def verify(self):
        with sqlite3.connect(self.db, timeout=20) as db:
            rows = db.execute('SELECT id,request_key,payload_json,status,version FROM bartide_enhanced_orders').fetchall()
            loaded = {row[0]: row for row in rows if row[0] not in self.initial_order_ids}
            self.check('no-lost-or-unexpected-orders', set(loaded) == {order['id'] for order in self.orders})
            self.check('unique-order-requests', len(loaded) == len({row[1] for row in loaded.values()}))
            for order in self.orders:
                row, expected = loaded[order['id']], order['expected']
                payload = json.loads(row[2])
                self.check('stored-order-' + str(order['number']),
                    row[1] == order['request_key'] and payload['table_id'] == order['table_id'] and
                    payload['table_label'] == expected['tableLabel'] and
                    payload['lines'] == order['expected_lines'] and
                    all(payload[key] == expected[value] for key, value in [
                        ('subtotal_cents', 'subtotalCents'), ('tax_cents', 'taxCents'),
                        ('tip_cents', 'tipCents'), ('total_cents', 'totalCents')]) and
                    (not order['completed'] or (row[3] == 'completed' and row[4] == 5 and
                     payload['payment_status'] == 'paid_in_person' and len(payload['history']) == 6)))
            self.check('sqlite-integrity', db.execute('PRAGMA integrity_check').fetchone()[0] == 'ok')
            self.check('sqlite-foreign-keys', not db.execute('PRAGMA foreign_key_check').fetchall())
        for person, before in self.wallets.items():
            wallet = self.call('wallet-after', GUEST + '/rewards', person=person, phase='verification')
            self.check('reward-points-' + person, wallet['points'] == before + 5)
            mine = self.call('rsvp-after', GUEST + '/events/mine', person=person, phase='verification')
            self.check('event-reservation-' + person, any(
                item['event']['id'] == self.reservations[person] and item['rsvp']['state'] in ('confirmed', 'checked_in')
                for item in mine['reservations']))
        self.report['integrity'] = {'accepted_orders': len(self.orders),
            'completed_orders': sum(order['completed'] for order in self.orders),
            'duplicate_or_lost_orders': 0, 'checks_passed': sum(c['passed'] for c in self.checks),
            'checks_failed': sum(not c['passed'] for c in self.checks)}

    def save(self):
        self.report['fake_provider'] = {'listen_backlog': 128, 'backlog_configured_before_activation': True,
            'responses': dict(Counter(str(r['status']) for r in self.provider_requests)),
            'handler_latency_ms': distribution([r['ms'] for r in self.provider_requests]),
            'handler_errors': dict(Counter(r['error'] for r in self.provider_requests if r['error']))}
        if self.resources:
            self.report['resource_summary'] = {
                'minimum_host_available_bytes': min(r['host'].get('available_bytes', 0) for r in self.resources),
                'peak_owned_private_bytes': max(sum(p.get('private_bytes', 0) for p in r['processes']) for r in self.resources),
                'peak_owned_working_set_bytes': max(sum(p.get('working_set_bytes', 0) for p in r['processes']) for r in self.resources)}
        # Tracking credentials belong only in the isolated database, not the report.
        report = {**self.report, 'orders': [{k: v for k, v in order.items() if k != 'tracking'} for order in self.orders]}
        (self.run / 'report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')

    def main(self):
        try:
            self.setup()
            self.load()
            if not self.stop.is_set():
                self.shared_ip_probe()
            self.verify()
            self.report['result'] = 'passed' if not self.stop.is_set() and not self.report.get('load_stop') else 'bounded-stop'
        except StopWork as failure:
            self.report['result'] = 'bounded-stop'
            self.report['failure'] = str(failure)
            self.verify()
        except Exception as failure:
            self.report['result'] = 'failed'
            self.report['failure'] = type(failure).__name__ + ': ' + str(failure)
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
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--concurrency', type=int, nargs='+', default=[5, 10, 25, 50])
    parser.add_argument('--rounds', type=int, default=2)
    parser.add_argument('--table-mode', choices=['qr', 'mixed'], default='qr')
    options = parser.parse_args()
    if not 1 <= options.rounds <= 4 or not options.concurrency or any(n < 1 or n > 50 for n in options.concurrency):
        parser.error('Use 1-4 rounds and concurrency between 1 and 50.')
    if sum(options.concurrency) * options.rounds + len(options.concurrency) + 11 > 250 or len(options.concurrency) > 8:
        parser.error('This local runner is capped at 250 order submissions and eight stages.')
    Runner(options).main()
