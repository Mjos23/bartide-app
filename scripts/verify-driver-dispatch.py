"""Driver accounts and dispatch through a real isolated API and storage.

Only the loopback identity provider and people/orders are synthetic. No external
providers, real payments, live data, or deployments are used. Build the API first.
--postgres uses a freshly owned schema in the existing loopback fixture.
--worker also proves the real background dispatcher after restarting the API.
"""
from datetime import timedelta
import json
import os
import re
import shutil
import sys
import restaurant_test_support as s

pg = None
schema = None
schema_owned = False
completed = False
env = {'DeliveryDispatch__WorkerEnabled': 'false'}
tokens = {}
members = {}
source = s.Path(os.environ.get('TIDE_TEST_BUILD_ROOT', str(s.ROOT))).resolve()
runtime = source if os.environ.get('TIDE_TEST_IN_PLACE_BUILD') == '1' else s.RUN / 'runtime'
if runtime != source:
    shutil.copytree(source / 'TideCasa.Api/bin/Debug/net10.0', runtime / 'TideCasa.Api/bin/Debug/net10.0')
os.environ['TIDE_TEST_BUILD_ROOT'] = str(runtime)
binary_sha256 = s.hashlib.sha256((runtime / 'TideCasa.Api/bin/Debug/net10.0/TideCasa.Api.dll').read_bytes()).hexdigest()


def ok(label, response, status=200):
    s.check(label, response[0] == status, response[:3])
    return response[1]


def api(tenant, suffix='', body=None, person='alice'):
    return s.call('/api/v1/tenants/' + tenant + '/delivery-dispatch' + suffix, body, tokens[person])


def board(tenant, person='alice'):
    response = api(tenant, person=person)
    if response[0] != 200:
        raise AssertionError('Dispatch board unavailable: ' + str(response[:3]))
    return response[1]


def profile(tenant, person='bob'):
    return next(d for d in board(tenant)['drivers'] if d['id'] == members[(tenant, person)])


def save_profile(tenant, person='bob', actor='alice', status=200, **values):
    current = profile(tenant, person)
    request = {k: current[k] for k in ('availability', 'capacity', 'deliveryZips')}
    request.update(expectedVersion=current['version'])
    request.update(values)
    return ok(tenant + ' saves ' + person + ' profile (' + str(status) + ')',
              api(tenant, '/drivers/' + current['id'], request, actor), status)


def automatic(tenant, enabled):
    current = board(tenant)
    return ok(tenant + ' automatic assignment ' + str(enabled), api(tenant, '/settings',
              {'expectedVersion': current['version'], 'automaticAssignment': enabled}))


def setup(tenant, people=('bob', 'staff'), owner=s.ALICE):
    s.seed(tenant, owner)
    s.alter_config(lambda c: c.update(delivery_workflow_enabled=True, delivery_capacity=30), tenant)
    actor = 'platform' if owner == s.PLATFORM else 'alice'
    for person in people:
        team = ok(tenant + ' invites ' + person + ' as driver', s.call('/api/v1/tenants/' + tenant + '/team/members',
                  {'name': person.title() + ' Synthetic Driver', 'email': person + '@example.invalid', 'role': 'driver'}, tokens[actor]))
        member = next(m for m in team['members'] if m['email'] == person + '@example.invalid')
        members[(tenant, person)] = member['id']
        s.check('New invitation is not yet account linked', not member['accountLinked'])
    return tenant


def bind(tenant, person):
    own = ok(tenant + ' direct driver visit binds verified account ' + person, api(tenant, person=person))
    s.check('Driver sees only own profile and identity', own['role'] == 'driver' and own['memberId'] == members[(tenant, person)]
            and [d['id'] for d in own['drivers']] == [members[(tenant, person)]])
    s.check('Driver invitation has durable verified identity', s.sql('SELECT user_id FROM bartide_enhanced_members WHERE id=?',
            (members[(tenant, person)],))[0][0] == 'supabase:' + s.USERS[person + '@example.invalid']['id'])
    return own


def operations(tenant, person='alice'):
    response = s.call('/api/v1/tenants/' + tenant + '/ordering/operations', token=tokens[person])
    if response[0] != 200:
        raise AssertionError('Operations unavailable: ' + str(response[:3]))
    return response[1]['orders']


def entry(tenant, ident):
    return next(o for o in operations(tenant) if o['order']['receipt']['orderId'] == ident)


def action(tenant, ident, name, person='alice', status=200, **extra):
    request = {'expectedVersion': entry(tenant, ident)['order']['version'], 'action': name, **extra}
    return ok(tenant + ' ' + name + ' (' + str(status) + ')', s.call('/api/v1/tenants/' + tenant + '/ordering/orders/' + ident,
              request, tokens[person]), status)


def place(tenant, accepted=True, fulfillment='delivery'):
    selected = {'items': [{'itemId': 'dish-1', 'quantity': 2}], 'fulfillment': fulfillment, 'paymentMethod': 'staff'}
    if fulfillment == 'delivery':
        selected['deliveryZip'] = '33101'
    request = s.order_request(selected, tenant)
    receipt = ok(tenant + ' places ' + fulfillment, s.submit(request, tenant), 201)
    if accepted:
        action(tenant, receipt['orderId'], 'accepted')
    return receipt['orderId']


def plan(tenant, ident, mode, status=200, person='alice', **extra):
    request = {'expectedVersion': entry(tenant, ident)['order']['version'], 'mode': mode, **extra}
    return ok(tenant + ' ' + mode + ' plan (' + str(status) + ')', api(tenant, '/orders/' + ident + '/plan', request, person), status)


def dispatch(tenant, expected=None):
    result = ok(tenant + ' dispatch run', api(tenant, '/run', {}))
    if expected is not None:
        s.check(tenant + ' assigns exactly ' + str(expected), result['assigned'] == expected, result)
    return result['assigned']


def stop_api(stage):
    for proc in s.PROCESSES:
        if proc.poll() is None:
            proc.terminate()
            try:
                proc.wait(timeout=20)
            except s.subprocess.TimeoutExpired:
                proc.kill()
                proc.wait(timeout=10)
    for log in s.LOGS:
        log.close()
    current = s.RUN / 'TideCasa.Api.log'
    if current.exists():
        current.rename(s.RUN / ('TideCasa.Api.' + stage + '.log'))


try:
    if '--postgres' in sys.argv:
        sys.path.insert(0, str(s.ROOT / '.tools/postgres-python'))
        import psycopg
        from psycopg import sql as pgsql
        supplied = json.loads((s.ROOT / '.tools/postgresql-17-test/fixture.json').read_text(encoding='utf-8-sig'))
        assert supplied['host'] in ('127.0.0.1', 'localhost', '::1') and 1024 <= int(supplied['port']) <= 65535
        creds = {k: supplied[k] for k in ('host', 'port', 'user', 'password')}
        creds.update(dbname=supplied['database'], sslmode='disable', connect_timeout=5)
        schema = 'tide_dispatch_' + s.uuid.uuid4().hex[:16]
        pg = psycopg
        with pg.connect(**creds, autocommit=True) as db:
            db.execute(pgsql.SQL('CREATE SCHEMA {}').format(pgsql.Identifier(schema)))
        schema_owned = True

        def pg_query(statement, values=()):
            with pg.connect(**creds, options='-c search_path=' + schema) as db:
                db.execute('SELECT pg_advisory_xact_lock(hashtext(current_database()),hashtext(current_schema()))')
                cur = db.execute(statement.replace('?', '%s'), values)
                return cur.fetchall() if cur.description else []

        s.sql = pg_query
        def quoted(value):
            return '"' + str(value).replace('"', '""') + '"'
        env.update({'Storage__Provider': 'PostgreSql', 'Storage__PostgresSchema': schema, 'Storage__CreatePostgresSchema': 'false',
            'ConnectionStrings__Application': ';'.join(n + '=' + quoted(creds[k]) for n, k in
                [('Host', 'host'), ('Port', 'port'), ('Database', 'dbname'), ('Username', 'user'), ('Password', 'password')])
                + ';SSL Mode=Disable;Pooling=true;Maximum Pool Size=8'})

    s.launch(env)
    for person in ('alice', 'bob', 'staff', 'platform'):
        tokens[person] = ok('Synthetic sign-in ' + person, s.call('/api/v1/auth/signin',
            {'email': person + '@example.invalid', 'password': s.PASSWORD}))['accessToken']

    setup('bistro')
    setup('foreign', ('staff',), s.PLATFORM)
    initial = ok('Delivery dispatch API is available', api('bistro'))
    s.check('Dispatch defaults are safe and opt-in', not initial['automaticAssignment'] and initial['version'] == 0
            and initial['workflowEnabled'] and all(d['availability'] == 'offline' and d['capacity'] == 1
            and d['activeOrders'] == 0 and d['version'] == 0 for d in initial['drivers']))
    s.check('Anonymous dispatch request denied', s.call('/api/v1/tenants/bistro/delivery-dispatch')[0] == 401)
    s.check('Other restaurant owner cannot read board', api('foreign')[0] == 403)
    s.check('Uninvited driver cannot cross restaurants', api('foreign', person='bob')[0] == 403)
    s.check('Expired authentication denied', s.call('/api/v1/tenants/bistro/delivery-dispatch', token='expired.invalid.token')[0] == 401)
    bind('bistro', 'bob')
    unlinked = members[('bistro', 'staff')]
    legacy = place('bistro')
    action('bistro', legacy, 'assign-driver', driverId=unlinked, status=400)
    action('bistro', legacy, 'assign-driver', driverId=members[('foreign', 'staff')], status=400)
    action('bistro', legacy, 'assign-driver', driverId=members[('bistro', 'bob')])
    s.check('Legacy manual assignment needs no saved dispatch profile', entry('bistro', legacy)['driverId'] == members[('bistro', 'bob')])
    action('bistro', legacy, 'cancelled')
    bind('bistro', 'staff')

    s.USERS['unverified@example.invalid'] = {'id': str(s.uuid.uuid4()), 'email': 'unverified@example.invalid',
        'email_confirmed_at': None, 'is_anonymous': False, 'user_metadata': {}}
    unverified = ok('Owner invites unverified driver', s.call('/api/v1/tenants/bistro/team/members',
        {'name': 'Unverified Synthetic', 'email': 'unverified@example.invalid', 'role': 'driver'}, tokens['alice']))
    unverified_id = next(m['id'] for m in unverified['members'] if m['email'] == 'unverified@example.invalid')
    members[('bistro', 'unverified')] = unverified_id
    rejected = s.call('/api/v1/auth/signin', {'email': 'unverified@example.invalid', 'password': s.PASSWORD})
    s.check('Unverified driver cannot sign in or bind invitation', rejected[0] in (400, 401, 403)
            and s.sql('SELECT user_id FROM bartide_enhanced_members WHERE id=?', (unverified_id,))[0][0] is None)
    save_profile('bistro', 'unverified', availability='available', capacity=10)
    s.check('Unlinked account stays unavailable even with an available profile', not profile('bistro', 'unverified')['availableNow'])

    save_profile('bistro', availability='available', deliveryZips=['33101'])
    save_profile('bistro', expectedVersion=0, status=409)
    for change in ({'capacity': 0}, {'capacity': 11}, {'availability': 'on-call'}, {'deliveryZips': ['bad-zip']}):
        save_profile('bistro', status=400, **change)
    save_profile('bistro', actor='bob', availability='offline')
    save_profile('bistro', actor='bob', capacity=2, status=403)
    save_profile('bistro', actor='bob', deliveryZips=['99999'], status=403)
    save_profile('bistro', person='staff', actor='bob', availability='available', status=403)
    s.check('Driver cannot enable automatic dispatch', api('bistro', '/settings', {'expectedVersion': 0, 'automaticAssignment': True}, 'bob')[0] == 403)
    s.check('Driver cannot run dispatch', api('bistro', '/run', {}, 'bob')[0] == 403)
    automatic('bistro', True)
    s.check('Dispatch settings reject stale saves', api('bistro', '/settings', {'expectedVersion': 0, 'automaticAssignment': False})[0] == 409)
    save_profile('bistro', availability='available', deliveryZips=['99999'])
    waiting = place('bistro')
    dispatch('bistro', 0)
    s.check('No eligible driver leaves order waiting safely', entry('bistro', waiting)['driverId'] is None)
    action('bistro', waiting, 'assign-driver', driverId=members[('bistro', 'bob')], status=409)
    save_profile('bistro', deliveryZips=['33101'])
    dispatch('bistro', 1)
    assigned = entry('bistro', waiting)
    s.check('Unpaid pay-staff order may dispatch without charging', assigned['driverId'] == members[('bistro', 'bob')]
            and assigned['order']['receipt']['paymentStatus'] == 'unpaid' and profile('bistro')['lastAssignedAt'])
    s.check('Other driver sees no customer order', operations('bistro', 'staff') == [])
    plan('bistro', waiting, 'manual', status=409)
    new = place('bistro', accepted=False)
    action('bistro', waiting, 'cancelled')
    dispatch('bistro', 0)
    s.check('Unaccepted order does not auto-assign', entry('bistro', new)['driverId'] is None)
    action('bistro', new, 'accepted')
    dispatch('bistro', 1)
    action('bistro', new, 'cancelled')

    # Competing real requests must share one capacity decision and durable writes.
    setup('capacity')
    for person in ('bob', 'staff'):
        bind('capacity', person)
        save_profile('capacity', person, availability='available', capacity=1)
    orders = [place('capacity') for _ in range(5)]
    automatic('capacity', True)
    with s.concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        runs = list(pool.map(lambda _: api('capacity', '/run', {}), range(4)))
    s.check('Concurrent dispatch runs return successfully', all(r[0] == 200 for r in runs), [r[:3] for r in runs])
    assigned_orders = [o for o in operations('capacity') if o['driverId']]
    s.check('Concurrent runs assign exactly two and respect capacity', sum(r[1]['assigned'] for r in runs) == 2
            and len(assigned_orders) == 2 and len({o['driverId'] for o in assigned_orders}) == 2
            and all(d['activeOrders'] == 1 for d in board('capacity')['drivers']))
    pending = next(i for i in orders if entry('capacity', i)['driverId'] is None)
    action('capacity', pending, 'assign-driver', driverId=members[('capacity', 'bob')], status=409)
    for order in assigned_orders:
        action('capacity', order['order']['receipt']['orderId'], 'cancelled')
    dispatch('capacity', 2)
    s.check('Cancelled deliveries release driver capacity', sum(d['activeOrders'] for d in board('capacity')['drivers']) == 2)

    setup('race', ('bob',))
    bind('race', 'bob')
    save_profile('race', availability='available')
    manual_race, automatic_race = place('race'), place('race')
    plan('race', manual_race, 'manual')
    automatic('race', True)
    version = entry('race', manual_race)['order']['version']
    barrier = s.threading.Barrier(2)
    def race_dispatch(manual):
        barrier.wait(timeout=10)
        if manual:
            return s.call('/api/v1/tenants/race/ordering/orders/' + manual_race,
                {'expectedVersion': version, 'action': 'assign-driver', 'driverId': members[('race', 'bob')]}, tokens['alice'])
        return api('race', '/run', {})
    with s.concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        raced = list(pool.map(race_dispatch, (True, False)))
    s.check('Manual and automatic assignments share capacity atomically', raced[0][0] in (200, 409) and raced[1][0] == 200
            and sum(bool(o['driverId']) for o in operations('race')) == 1 and profile('race')['activeOrders'] == 1,
            [r[:3] for r in raced])

    setup('plans', ('bob',))
    bind('plans', 'bob')
    save_profile('plans', availability='available', capacity=3)
    manual = place('plans')
    plan('plans', manual, 'manual')
    automatic('plans', True)
    dispatch('plans', 0)
    s.check('Explicit manual plan excludes automatic dispatch', entry('plans', manual)['driverId'] is None)
    automatic('plans', False)
    explicit = place('plans')
    dispatch('plans', 0)
    s.check('Tenant opt-out leaves ordinary accepted order unassigned', entry('plans', explicit)['driverId'] is None)
    previous = entry('plans', explicit)['order']['version']
    plan('plans', explicit, 'automatic')
    plan('plans', explicit, 'manual', expectedVersion=previous, status=409)
    dispatch('plans', 1)
    s.check('Explicit automatic plan works with tenant automation off', entry('plans', explicit)['driverId'] == members[('plans', 'bob')])
    plan('plans', manual, 'automatic', person='bob', status=403)
    s.check('Foreign plan cannot target another tenant order', api('plans', '/orders/' + waiting + '/plan', {'mode': 'manual', 'expectedVersion': 0})[0] == 404)
    pickup = place('plans', fulfillment='pickup')
    plan('plans', pickup, 'automatic', status=409)
    future = (s.datetime.now(s.timezone.utc) + timedelta(days=1)).isoformat()
    plan('plans', manual, 'scheduled', status=400, dispatchAt=future, preferredDriverId=members[('foreign', 'staff')])
    for invalid in ({'mode': 'invalid'}, {'mode': 'manual', 'dispatchAt': '2099-01-01T00:00:00Z'},
                    {'mode': 'automatic', 'preferredDriverId': members[('plans', 'bob')]},
                    {'mode': 'scheduled'}, {'mode': 'scheduled', 'dispatchAt': '2000-01-01T00:00:00Z'},
                    {'mode': 'scheduled', 'dispatchAt': '2099-01-01T00:00:00Z'},
                    {'mode': 'scheduled', 'dispatchAt': (s.datetime.now(s.timezone.utc) + timedelta(days=1)).replace(tzinfo=None).isoformat()}):
        mode = invalid['mode']
        plan('plans', manual, mode, status=400, **{k: v for k, v in invalid.items() if k != 'mode'})

    # A real shift with non-UTC offsets proves availability uses instants.
    setup('scheduled', ('bob', 'staff'))
    for person in ('bob', 'staff'):
        bind('scheduled', person)
    save_profile('scheduled', availability='scheduled')
    s.check('Scheduled driver without current shift stays unavailable', not profile('scheduled')['availableNow'])
    offset = s.timezone(timedelta(hours=-4))
    now = s.datetime.now(s.timezone.utc)
    shift = {'memberId': members[('scheduled', 'bob')], 'startsAt': (now - timedelta(minutes=10)).astimezone(offset).isoformat(),
             'endsAt': (now + timedelta(hours=1)).astimezone(offset).isoformat(), 'label': 'Synthetic evening delivery'}
    ok('Owner schedules current driver shift with timezone offsets', s.call('/api/v1/tenants/scheduled/team/shifts', shift, tokens['alice']))
    s.check('Current offset shift activates scheduled driver', profile('scheduled')['availableNow'])
    future_shift = {**shift, 'memberId': members[('scheduled', 'staff')], 'startsAt': (now + timedelta(hours=2)).astimezone(offset).isoformat(),
                    'endsAt': (now + timedelta(hours=3)).astimezone(offset).isoformat()}
    ok('Owner schedules future driver shift', s.call('/api/v1/tenants/scheduled/team/shifts', future_shift, tokens['alice']))
    save_profile('scheduled', 'staff', availability='scheduled')
    s.check('A future shift does not make a driver available early', not profile('scheduled', 'staff')['availableNow'])
    own = board('scheduled', 'staff')
    s.check('Driver board hides another driver shift', all(x['memberId'] == members[('scheduled', 'staff')] for x in own['shifts']))
    scheduled = place('scheduled')
    due = s.datetime.now(s.timezone.utc) + timedelta(seconds=14)
    plan('scheduled', scheduled, 'scheduled', dispatchAt=due.astimezone(offset).isoformat(), preferredDriverId=members[('scheduled', 'bob')])
    dispatch('scheduled', 0)
    persisted = entry('scheduled', scheduled)
    s.check('Scheduled order exposes saved assignment plan', persisted['assignmentPlan']['mode'] == 'scheduled'
            and s.datetime.fromisoformat(persisted['assignmentPlan']['dispatchAt'].replace('Z', '+00:00')) == due)
    stop_api('before-restart')
    s.launch(env)
    recovered = entry('scheduled', scheduled)
    s.check('Scheduled assignment and driver profile survive restart', recovered['assignmentPlan'] == persisted['assignmentPlan']
            and profile('scheduled')['availability'] == 'scheduled' and profile('scheduled')['availableNow'])
    save_profile('scheduled', availability='offline')
    while s.datetime.now(s.timezone.utc) < due:
        s.time.sleep(min(.2, max(0, (due - s.datetime.now(s.timezone.utc)).total_seconds())))
    dispatch('scheduled', 0)
    s.check('Due schedule rechecks current driver eligibility', entry('scheduled', scheduled)['driverId'] is None)
    save_profile('scheduled', availability='scheduled')
    dispatch('scheduled', 1)
    s.check('Due preferred driver receives scheduled order', entry('scheduled', scheduled)['driverId'] == members[('scheduled', 'bob')])

    # Seed only the payment-provider outcome, never call a payment service.
    setup('payment', ('bob',))
    bind('payment', 'bob')
    save_profile('payment', availability='available')
    phone = place('payment')
    plan('payment', phone, 'automatic')
    payload = json.loads(s.sql('SELECT payload_json FROM bartide_enhanced_orders WHERE id=?', (phone,))[0][0])
    payload['payment_method'] = 'phone'
    payload['dotnet_receipt']['quote']['paymentMethod'] = 'phone'
    for payment_state in ('pending', 'failed', 'refunded'):
        payload['payment_status'] = payment_state
        s.sql('UPDATE bartide_enhanced_orders SET payload_json=? WHERE id=?', (json.dumps(payload), phone))
        dispatch('payment', 0)
        s.check(payment_state + ' phone payment remains unassigned', entry('payment', phone)['driverId'] is None)
    payload['payment_status'] = 'paid'
    s.sql('UPDATE bartide_enhanced_orders SET payload_json=? WHERE id=?', (json.dumps(payload), phone))
    dispatch('payment', 1)
    s.check('Confirmed phone payment permits assignment', entry('payment', phone)['driverId'] == members[('payment', 'bob')])

    setup('disabled', ('bob',))
    bind('disabled', 'bob')
    save_profile('disabled', availability='available')
    disabled = place('disabled')
    plan('disabled', disabled, 'automatic')
    for flag in ('delivery_enabled', 'delivery_workflow_enabled'):
        s.alter_config(lambda c: c.update({flag: False}), 'disabled')
        dispatch('disabled', 0)
        s.check(flag + ' off suppresses dispatch', entry('disabled', disabled)['driverId'] is None)
        s.alter_config(lambda c: c.update({flag: True}), 'disabled')
    ok('Owner pauses driver membership', s.call('/api/v1/tenants/disabled/team/members/' + members[('disabled', 'bob')],
        {'expectedActive': True, 'active': False}, tokens['alice']))
    s.check('Paused driver cannot read dispatch workspace', api('disabled', person='bob')[0] == 403)
    dispatch('disabled', 0)

    if '--worker' in sys.argv:
        setup('worker', ('bob',))
        bind('worker', 'bob')
        save_profile('worker', availability='offline')
        worker_order = place('worker')
        worker_due = s.datetime.now(s.timezone.utc) + timedelta(seconds=3)
        plan('worker', worker_order, 'scheduled', dispatchAt=worker_due.isoformat(), preferredDriverId=members[('worker', 'bob')])
        stop_api('before-worker')
        s.launch({**env, 'DeliveryDispatch__WorkerEnabled': 'true'})
        s.check('Live worker leaves due order waiting while driver offline', entry('worker', worker_order)['driverId'] is None)
        save_profile('worker', availability='available')
        deadline = s.time.monotonic() + 40
        while s.time.monotonic() < deadline and entry('worker', worker_order)['driverId'] is None:
            s.time.sleep(.5)
        s.check('Background worker retries and assigns without a manual run', entry('worker', worker_order)['driverId'] == members[('worker', 'bob')])
        s.check('Background dispatch keeps staff payment unpaid', entry('worker', worker_order)['order']['receipt']['paymentStatus'] == 'unpaid')
    completed = True
finally:
    stop_api('final')
    s.PROVIDER.shutdown()
    s.PROVIDER.server_close()
    if pg and schema_owned:
        assert re.fullmatch(r'tide_dispatch_[a-f0-9]{16}', schema)
        with pg.connect(**creds, autocommit=True) as db:
            db.execute(pgsql.SQL('DROP SCHEMA {} CASCADE').format(pgsql.Identifier(schema)))
    (s.RUN / 'driver-dispatch-results.json').write_text(json.dumps({'completed': completed, 'apiSha256': binary_sha256, 'provider': 'postgres' if pg else 'sqlite',
        'liveWorker': '--worker' in sys.argv, 'checks': s.RESULTS}, indent=2), encoding='utf-8')
    print('Driver dispatch evidence: ' + str(s.RUN), flush=True)
print(str(len(s.RESULTS)) + ' driver dispatch checks passed.', flush=True)
