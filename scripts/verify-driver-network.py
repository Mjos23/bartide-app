"""Shared driver signup/hiring against isolated real API/storage and fictional users.

No external identity, messaging, payment, or deployment calls. Build the API first.
--postgres uses only a freshly owned schema in the existing loopback fixture.
This checks hiring, assignment policy and the completion ledger; money movement is disabled.
"""
import json
import os
import re
import shutil
import sys
import restaurant_test_support as s

env = {'DeliveryDispatch__WorkerEnabled': 'false', 'DriverPayments__SetupEnabled': 'false',
       'DriverPayments__PaymentsEnabled': 'false', 'DriverPayments__WorkerEnabled': 'false'}
pg = None
schema = None
schema_owned = False
completed = False
tokens = {}
source = s.Path(os.environ.get('TIDE_TEST_BUILD_ROOT', str(s.ROOT))).resolve()
runtime = source if os.environ.get('TIDE_TEST_IN_PLACE_BUILD') == '1' else s.RUN / 'runtime'
if runtime != source:
    shutil.copytree(source / 'TideCasa.Api/bin/Debug/net10.0', runtime / 'TideCasa.Api/bin/Debug/net10.0')
os.environ['TIDE_TEST_BUILD_ROOT'] = str(runtime)
api_sha = s.hashlib.sha256((runtime / 'TideCasa.Api/bin/Debug/net10.0/TideCasa.Api.dll').read_bytes()).hexdigest()


def ok(label, response, status=200):
    s.check(label, response[0] == status, response[:3])
    return response[1]


def api(path, body=None, person='alice'):
    return s.call(path, body, tokens[person])


def account(person='bob'):
    result = api('/api/v1/drivers/me', person=person)
    if result[0] != 200:
        raise AssertionError('Driver account unavailable: ' + str(result[:3]))
    return result[1]


def save(person='bob', status=200, **changes):
    profile = account(person)['profile']
    request = {'expectedVersion': 0, 'name': person.title() + ' Synthetic Driver', 'bio': 'Fictional local delivery driver',
               'deliveryZips': ['33101'], 'listed': False, 'capacity': 1}
    if profile:
        request.update({key: profile[key] for key in ('name', 'bio', 'deliveryZips', 'listed', 'capacity')})
        request['expectedVersion'] = profile['version']
    request.update(changes)
    return ok(person + ' saves network profile (' + str(status) + ')', api('/api/v1/drivers/me', request, person), status)


def workspace(tenant='bistro', person='alice'):
    response = api('/api/v1/tenants/' + tenant + '/driver-network', person=person)
    if response[0] != 200:
        raise AssertionError('Client network unavailable: ' + str(response[:3]))
    return response[1]


def hire(tenant='bistro', person='bob'):
    return next(h for h in account(person)['hires'] if h['tenantId'] == tenant)


def offer(tenant='bistro', person='bob', actor='alice', status=200, **changes):
    driver = account(person)['profile']
    prior = next((h for h in account(person)['hires'] if h['tenantId'] == tenant), None)
    request = {'driverId': driver['id'], 'payPerDeliveryCents': 700, 'notes': 'Synthetic delivery terms',
               'expectedVersion': prior['version'] if prior else 0, **changes}
    return ok(tenant + ' offers to ' + person + ' (' + str(status) + ')', api('/api/v1/tenants/' + tenant + '/driver-network/offers', request, actor), status)


def respond(tenant='bistro', person='bob', accept=True, status=200, **changes):
    current = hire(tenant, person)
    return ok(person + ' responds to ' + tenant + ' (' + str(status) + ')', api('/api/v1/drivers/hires/' + current['id'] + '/respond',
              {'expectedVersion': current['version'], 'accept': accept, **changes}, person), status)


def end(tenant='bistro', person='bob', actor='alice', status=200, **changes):
    current = hire(tenant, person)
    return ok(tenant + ' ends ' + person + ' hire (' + str(status) + ')', api('/api/v1/tenants/' + tenant + '/driver-network/hires/' + current['id'] + '/end',
              {'expectedVersion': current['version'], **changes}, actor), status)


def operations(tenant='bistro'):
    return api('/api/v1/tenants/' + tenant + '/ordering/operations')[1]['orders']


def entry(tenant, order):
    return next(o for o in operations(tenant) if o['order']['receipt']['orderId'] == order)


def action(tenant, order, name, status=200, person='alice', **values):
    request = {'expectedVersion': entry(tenant, order)['order']['version'], 'action': name, **values}
    return ok(tenant + ' ' + name + ' (' + str(status) + ')', api('/api/v1/tenants/' + tenant + '/ordering/orders/' + order, request, person), status)


def place(tenant='bistro'):
    request = s.order_request({'items': [{'itemId': 'dish-1', 'quantity': 2}], 'fulfillment': 'delivery', 'deliveryZip': '33101', 'paymentMethod': 'staff'}, tenant)
    receipt = ok(tenant + ' places synthetic order', s.submit(request, tenant), 201)
    action(tenant, receipt['orderId'], 'accepted')
    return receipt['orderId']


def available(tenant):
    current = hire(tenant)
    board = api('/api/v1/tenants/' + tenant + '/delivery-dispatch')[1]
    profile = next(d for d in board['drivers'] if d['id'] == current['memberId'])
    return ok(tenant + ' makes accepted driver available', api('/api/v1/tenants/' + tenant + '/delivery-dispatch/drivers/' + current['memberId'],
        {'expectedVersion': profile['version'], 'availability': 'available', 'capacity': 2, 'deliveryZips': ['33101']}))


def setup(tenant, owner=s.ALICE):
    s.seed(tenant, owner)
    s.alter_config(lambda c: c.update(delivery_workflow_enabled=True, delivery_capacity=30), tenant)


try:
    if '--postgres' in sys.argv:
        sys.path.insert(0, str(s.ROOT / '.tools/postgres-python'))
        import psycopg
        from psycopg import sql as pgsql
        supplied = json.loads((s.ROOT / '.tools/postgresql-17-test/fixture.json').read_text(encoding='utf-8-sig'))
        assert supplied['host'] in ('127.0.0.1', 'localhost', '::1') and 1024 <= int(supplied['port']) <= 65535
        creds = {key: supplied[key] for key in ('host', 'port', 'user', 'password')}
        creds.update(dbname=supplied['database'], sslmode='disable', connect_timeout=5)
        schema = 'tide_network_' + s.uuid.uuid4().hex[:16]
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

    for person, ident in [('manager', 901), ('outsider', 902), ('unverified', 903)]:
        s.USERS[person + '@example.invalid'] = {'id': str(s.uuid.UUID(int=ident)), 'email': person + '@example.invalid',
            'email_confirmed_at': None if person == 'unverified' else '2026-01-01T00:00:00Z',
            'is_anonymous': False, 'user_metadata': {'full_name': person.title() + ' Synthetic'}}
    s.launch(env)
    for person in ('alice', 'bob', 'staff', 'platform', 'manager', 'outsider'):
        tokens[person] = ok('Synthetic sign-in ' + person, s.call('/api/v1/auth/signin',
            {'email': person + '@example.invalid', 'password': s.PASSWORD}))['accessToken']
    empty = ok('Independent driver account endpoint exists', api('/api/v1/drivers/me', person='bob'))
    s.check('Any verified user may begin without restaurant membership', empty == {'profile': None, 'hires': []})
    s.check('Anonymous signup is rejected', s.call('/api/v1/drivers/me', {})[0] == 401)
    rejected = s.call('/api/v1/auth/signin', {'email': 'unverified@example.invalid', 'password': s.PASSWORD})
    s.check('Unverified identity cannot register', rejected[0] in (400, 401, 403))
    profile = save()['profile']
    s.check('Signup belongs to authenticated account and starts version one', profile['version'] == 1 and not profile['listed']
            and account('staff')['profile'] is None and not account()['hires'])
    save(expectedVersion=0, status=409)
    for invalid in ({'name': ''}, {'name': 'A\nB'}, {'name': 'n' * 81}, {'bio': 'b' * 1001}, {'capacity': 0}, {'capacity': 11},
                    {'deliveryZips': []}, {'deliveryZips': ['bad']}, {'deliveryZips': ['33101', '33101']}):
        save(status=400, **invalid)
    setup('bistro'); setup('second'); setup('foreign', s.PLATFORM)
    s.check('Unlisted driver is absent from discovery', workspace()['drivers'] == [])
    offer(status=404)
    save(listed=True)
    listed = workspace()['drivers']
    s.check('Listed directory exposes profile but no identity secrets', len(listed) == 1 and listed[0]['id'] == profile['id']
            and 'email' not in json.dumps(listed).lower() and 'userId' not in json.dumps(listed) and s.BOB not in json.dumps(listed))
    s.check('Driver cannot use client hiring workspace', api('/api/v1/tenants/bistro/driver-network', person='bob')[0] == 403)
    s.check('Other owner cannot read foreign hires', api('/api/v1/tenants/foreign/driver-network')[0] == 403)
    for invalid in ({'payPerDeliveryCents': 49}, {'payPerDeliveryCents': 100001}, {'notes': 'n' * 501}):
        offer(status=400, **invalid)
    offer()
    pending = hire()
    s.check('Client offer grants no membership or driver access', pending['status'] == 'offered' and pending['memberId'] is None
            and api('/api/v1/tenants/bistro/ordering/operations', person='bob')[0] == 403)
    order = place()
    action('bistro', order, 'assign-driver', driverId=profile['id'], status=400)
    save('outsider')
    for person in ('alice', 'outsider'):
        s.check(person + ' cannot accept another person hire', api('/api/v1/drivers/hires/' + pending['id'] + '/respond',
            {'expectedVersion': pending['version'], 'accept': True}, person)[0] == 404)
    s.check('A different client cannot end this hire by identifier', api('/api/v1/tenants/second/driver-network/hires/' + pending['id'] + '/end',
        {'expectedVersion': pending['version']})[0] == 404)
    respond(expectedVersion=0, status=409)
    with s.concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        responses = list(pool.map(lambda _: api('/api/v1/drivers/hires/' + pending['id'] + '/respond',
            {'expectedVersion': pending['version'], 'accept': True}, 'bob'), range(2)))
    s.check('Concurrent acceptance commits once', sorted(r[0] for r in responses) == [200, 409], [r[:3] for r in responses])
    accepted = hire()
    member = accepted['memberId']
    dispatch_board = ok('Accepted driver can enter own dispatch workspace', api('/api/v1/tenants/bistro/delivery-dispatch', person='bob'))
    d = dispatch_board['drivers'][0]
    s.check('Acceptance creates linked active driver and safe offline profile', accepted['status'] == 'active' and member
            and d['id'] == member and d['accountLinked'] and d['availability'] == 'offline' and d['capacity'] == 1
            and s.sql('SELECT COUNT(*) FROM bartide_enhanced_members WHERE tenant_id=? AND user_id=? AND active=1 AND role=?',
                ('bistro', 'supabase:' + s.BOB, 'driver'))[0][0] == 1)
    end(actor='bob', status=403)
    offer(payPerDeliveryCents=900, status=409)
    action('bistro', order, 'assign-driver', driverId=member, status=409)
    available('bistro')
    action('bistro', order, 'assign-driver', driverId=member)
    end(status=409)
    s.check('Active deliveries preserve hire and membership', hire()['status'] == 'active')

    offer('second'); respond('second'); available('second')
    second_order = place('second')
    action('second', second_order, 'assign-driver', driverId=hire('second')['memberId'], status=409)
    settings = api('/api/v1/tenants/second/delivery-dispatch')[1]
    ok('Second client enables automatic assignment', api('/api/v1/tenants/second/delivery-dispatch/settings',
        {'expectedVersion': settings['version'], 'automaticAssignment': True}))
    waiting = ok('Automatic assignment checks network-wide capacity', api('/api/v1/tenants/second/delivery-dispatch/run', {}))
    s.check('Network-wide capacity prevents simultaneous client assignments', waiting['assigned'] == 0 and entry('second', second_order)['driverId'] is None)
    action('bistro', order, 'cancelled')
    moved = ok('Cross-client capacity frees after cancellation', api('/api/v1/tenants/second/delivery-dispatch/run', {}))
    s.check('Waiting second client can assign after capacity frees', moved['assigned'] == 1 and entry('second', second_order)['driverId'] == hire('second')['memberId'])
    action('second', second_order, 'cancelled')

    # Global capacity follows the person in both directions across mixed employment.
    setup('own-capacity')
    own_team = ok('Another client invites the network driver as in-house staff', api('/api/v1/tenants/own-capacity/team/members',
        {'name': 'Bob In-house Synthetic', 'email': 'bob@example.invalid', 'role': 'driver'}))
    mixed_member = next(m['id'] for m in own_team['members'] if m['email'] == 'bob@example.invalid')
    ok('Mixed-employment driver binds own-client membership', api('/api/v1/tenants/own-capacity/ordering/operations', person='bob'))
    mixed_profile = api('/api/v1/tenants/own-capacity/delivery-dispatch')[1]['drivers'][0]
    ok('Own client configures capacity above the shared personal limit', api('/api/v1/tenants/own-capacity/delivery-dispatch/drivers/' + mixed_member,
        {'expectedVersion': mixed_profile['version'], 'availability': 'available', 'capacity': 2, 'deliveryZips': ['33101']}))
    mixed_own_order = place('own-capacity')
    action('own-capacity', mixed_own_order, 'assign-driver', driverId=mixed_member)
    mixed_network_order = place()
    action('bistro', mixed_network_order, 'assign-driver', driverId=member, status=409)
    for tenant in ('bistro', 'own-capacity'):
        setting = api('/api/v1/tenants/' + tenant + '/delivery-dispatch')[1]
        ok(tenant + ' enables mixed-employment automatic dispatch', api('/api/v1/tenants/' + tenant + '/delivery-dispatch/settings',
            {'expectedVersion': setting['version'], 'automaticAssignment': True}))
    mixed_run = ok('Network auto dispatch observes an active in-house delivery', api('/api/v1/tenants/bistro/delivery-dispatch/run', {}))
    s.check('In-house workload blocks manual and automatic network assignment', mixed_run['assigned'] == 0
            and entry('bistro', mixed_network_order)['driverId'] is None)
    action('own-capacity', mixed_own_order, 'cancelled')
    mixed_run = ok('Network capacity frees when in-house work ends', api('/api/v1/tenants/bistro/delivery-dispatch/run', {}))
    s.check('Network assignment resumes after in-house work ends', mixed_run['assigned'] == 1)
    mixed_own_order = place('own-capacity')
    action('own-capacity', mixed_own_order, 'assign-driver', driverId=mixed_member, status=409)
    mixed_run = ok('In-house auto dispatch observes active network delivery', api('/api/v1/tenants/own-capacity/delivery-dispatch/run', {}))
    s.check('Network workload blocks manual and automatic in-house assignment', mixed_run['assigned'] == 0
            and entry('own-capacity', mixed_own_order)['driverId'] is None)
    action('bistro', mixed_network_order, 'cancelled')
    mixed_run = ok('In-house capacity frees when network work ends', api('/api/v1/tenants/own-capacity/delivery-dispatch/run', {}))
    own_payload = json.loads(s.sql('SELECT payload_json FROM bartide_enhanced_orders WHERE id=?', (mixed_own_order,))[0][0])
    s.check('Mixed-employment in-house assignment preserves own payment origin', mixed_run['assigned'] == 1
            and entry('own-capacity', mixed_own_order)['driverId'] == mixed_member and 'network_assignment' not in own_payload)
    action('own-capacity', mixed_own_order, 'cancelled')

    # A changed verified email must not change the same person's established client origin.
    for state in ('offered', 'active', 'declined', 'ended'):
        origin_tenant = 'origin-' + state
        setup(origin_tenant)
        offer(origin_tenant)
        if state in ('active', 'ended'):
            respond(origin_tenant)
        elif state == 'declined':
            respond(origin_tenant, accept=False)
        if state == 'ended':
            end(origin_tenant)
        if state == 'active':
            ok('Active-hire client pauses original member before reinviting', api('/api/v1/tenants/' + origin_tenant + '/team/members/' + hire(origin_tenant)['memberId'],
                {'expectedActive': True, 'active': False}))
        changed_email = 'bob-' + state + '@example.invalid'
        s.USERS['bob@example.invalid']['email'] = changed_email
        alternate_team = ok(state + ' client creates alternate-email invitation', api('/api/v1/tenants/' + origin_tenant + '/team/members',
            {'name': 'Same Bob New Email', 'email': changed_email, 'role': 'driver'}))
        alternate = next(m['id'] for m in alternate_team['members'] if m['email'] == changed_email)
        ok(state + ' verified new email binds to the same durable user', api('/api/v1/account', person='bob'))
        s.check(state + ' hire cannot be bypassed by another membership for the same person',
            api('/api/v1/tenants/' + origin_tenant + '/ordering/operations', person='bob')[0] == 403
            and s.sql('SELECT user_id FROM bartide_enhanced_members WHERE id=?', (alternate,))[0][0] == 'supabase:' + s.BOB)
        s.check(state + ' client cannot repeat the same-email own invitation', api('/api/v1/tenants/' + origin_tenant + '/team/members',
            {'name': 'Duplicate Same Bob', 'email': changed_email, 'role': 'driver'})[0] == 409)
        alternate_profile = next(d for d in api('/api/v1/tenants/' + origin_tenant + '/delivery-dispatch')[1]['drivers'] if d['id'] == alternate)
        ok(state + ' client configures alternate membership availability', api('/api/v1/tenants/' + origin_tenant + '/delivery-dispatch/drivers/' + alternate,
            {'expectedVersion': alternate_profile['version'], 'availability': 'available', 'capacity': 2, 'deliveryZips': ['33101']}))
        alternate_order = place(origin_tenant)
        action(origin_tenant, alternate_order, 'assign-driver', driverId=alternate, status=409)
        setting = api('/api/v1/tenants/' + origin_tenant + '/delivery-dispatch')[1]
        ok(state + ' client enables automatic dispatch', api('/api/v1/tenants/' + origin_tenant + '/delivery-dispatch/settings',
            {'expectedVersion': setting['version'], 'automaticAssignment': True}))
        alternate_run = ok(state + ' automatic dispatch checks durable hiring origin', api('/api/v1/tenants/' + origin_tenant + '/delivery-dispatch/run', {}))
        s.check(state + ' alternate membership cannot acquire an own-source order', alternate_run['assigned'] == 0
                and entry(origin_tenant, alternate_order)['driverId'] is None)
        ok(state + ' client pauses the alternate invitation', api('/api/v1/tenants/' + origin_tenant + '/team/members/' + alternate,
            {'expectedActive': True, 'active': False}))
        s.USERS['bob@example.invalid']['email'] = 'bob@example.invalid'
    old_version = hire()['version']
    end()
    s.check('Ending hire removes operational access and active membership', hire()['status'] == 'ended'
            and api('/api/v1/tenants/bistro/ordering/operations', person='bob')[0] == 403
            and s.sql('SELECT active FROM bartide_enhanced_members WHERE id=?', (member,))[0][0] == 0)
    end(expectedVersion=old_version, status=409)
    # Existing staff management cannot bypass the accepted-hire requirement.
    ok('Client can update ordinary member record', api('/api/v1/tenants/bistro/team/members/' + member, {'expectedActive': False, 'active': True}))
    s.check('Reactivating member alone cannot restore ended network hire access', api('/api/v1/tenants/bistro/ordering/operations', person='bob')[0] == 403)
    blocked = place()
    action('bistro', blocked, 'assign-driver', driverId=member, status=409)
    offer(payPerDeliveryCents=800)
    s.check('Reoffer remains offered until explicit new acceptance', hire()['status'] == 'offered'
            and api('/api/v1/tenants/bistro/ordering/operations', person='bob')[0] == 403)
    respond(accept=False)
    s.check('Declined hire remains inactive', hire()['status'] == 'declined')
    offer(payPerDeliveryCents=900); respond()
    s.check('Reaccepted hire reuses membership and new agreed rate', hire()['memberId'] == member and hire()['payPerDeliveryCents'] == 900
            and s.sql('SELECT COUNT(*) FROM bartide_enhanced_members WHERE tenant_id=? AND user_id=?', ('bistro', 'supabase:' + s.BOB))[0][0] == 1)

    save('staff', listed=True)
    team = ok('Owner adds in-house driver invitation', api('/api/v1/tenants/bistro/team/members',
        {'name': 'Inhouse Synthetic', 'email': 'staff@example.invalid', 'role': 'driver'}))
    inhouse = next(m for m in team['members'] if m['email'] == 'staff@example.invalid')
    offer(person='staff', status=409)
    ok('Owner pauses in-house invitation', api('/api/v1/tenants/bistro/team/members/' + inhouse['id'], {'expectedActive': True, 'active': False}))
    offer(person='staff', status=409)
    setup('race-offer')
    offer('race-offer', 'staff')
    ok('Client adds in-house invitation after network offer', api('/api/v1/tenants/race-offer/team/members',
        {'name': 'Inhouse after offer', 'email': 'staff@example.invalid', 'role': 'driver'}))
    respond('race-offer', 'staff', status=409)
    s.check('Acceptance rechecks intervening in-house membership', hire('race-offer', 'staff')['status'] == 'offered')
    manager_team = ok('Owner invites manager', api('/api/v1/tenants/bistro/team/members',
        {'name': 'Manager Synthetic', 'email': 'manager@example.invalid', 'role': 'manager'}))
    manager_workspace = ok('Manager can discover hired network drivers', api('/api/v1/tenants/bistro/driver-network', person='manager'))
    s.check('Manager retains manager role', manager_workspace['role'] == 'manager')
    save('outsider', listed=True)
    offer(person='outsider', actor='manager')
    end(person='outsider', actor='manager')
    s.check('Client can withdraw a pending offer', hire(person='outsider')['status'] == 'ended')
    save(listed=False)
    s.check('Unlisting hides discovery without removing accepted hires', profile['id'] not in [d['id'] for d in workspace()['drivers']]
            and hire()['status'] == 'active' and account()['profile']['listed'] is False)
    s.check('Only own hires appear in driver account', all(h['driverId'] == profile['id'] for h in account()['hires']))
    s.check('Only tenant hires appear in client workspace', all(h['tenantId'] == 'bistro' for h in workspace()['hires']))

    # Completion records the debt; this suite never enables or invokes a payment provider.
    initial_payments = ok('Client can review empty driver payment ledger', api('/api/v1/tenants/bistro/driver-payments'))
    s.check('Cancelled, declined and incomplete work creates no payable', initial_payments['payments'] == []
            and s.sql('SELECT COUNT(*) FROM tide_driver_payables')[0][0] == 0)
    available('bistro')
    completed_order = place()
    action('bistro', completed_order, 'assign-driver', driverId=member)
    assignment = json.loads(s.sql('SELECT payload_json FROM bartide_enhanced_orders WHERE id=?', (completed_order,))[0][0])['network_assignment']
    s.check('Assignment snapshots the accepted driver and agreed delivery pay', assignment['hire_id'] == hire()['id']
            and assignment['driver_id'] == profile['id'] and assignment['driver_user_id'] == 'supabase:' + s.BOB
            and assignment['pay_per_delivery_cents'] == 900)
    action('bistro', completed_order, 'acknowledge-delivery', person='bob')
    action('bistro', completed_order, 'preparing')
    action('bistro', completed_order, 'ready')
    action('bistro', completed_order, 'out_for_delivery', person='bob')
    action('bistro', completed_order, 'confirm-delivery', person='staff', status=403)
    s.check('An attempted or incomplete handoff cannot create driver debt', s.sql('SELECT COUNT(*) FROM tide_driver_payables')[0][0] == 0)
    version = entry('bistro', completed_order)['order']['version']
    with s.concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        handoffs = list(pool.map(lambda _: api('/api/v1/tenants/bistro/ordering/orders/' + completed_order,
            {'expectedVersion': version, 'action': 'confirm-delivery'}, 'bob'), range(2)))
    s.check('Concurrent handoff creates exactly one delivery payable', sorted(r[0] for r in handoffs) == [200, 409]
            and s.sql('SELECT COUNT(*) FROM tide_driver_payables WHERE order_id=?', (completed_order,))[0][0] == 1,
            [r[:3] for r in handoffs])
    owner_ledger = ok('Owner reviews completed delivery before payment', api('/api/v1/tenants/bistro/driver-payments'))
    network_payment = next(p for p in owner_ledger['payments'] if p['orderId'] == completed_order)
    s.check('Network completion owes agreed pay plus five percent pending approval', network_payment['source'] == 'network'
            and network_payment['driverPayCents'] == 900 and network_payment['platformFeeCents'] == 45
            and network_payment['totalCents'] == 945 and network_payment['status'] == 'pending_approval'
            and network_payment['version'] == 0 and network_payment['completedAt'] and network_payment['checkoutUrl'] is None
            and owner_ledger['canApprove'] and not owner_ledger['paymentsEnabled'])
    manager_ledger = ok('Manager can review completed delivery payments', api('/api/v1/tenants/bistro/driver-payments', person='manager'))
    s.check('Manager sees amount but cannot approve payments', not manager_ledger['canApprove']
            and manager_ledger['payments'][0]['totalCents'] == 945 and manager_ledger['payments'][0]['checkoutUrl'] is None)
    earnings = ok('Driver privately sees own earned delivery pay', api('/api/v1/drivers/payments', person='bob'))
    s.check('Driver sees earned payable without client checkout link', [p['orderId'] for p in earnings['payments']] == [completed_order]
            and earnings['payments'][0]['driverPayCents'] == 900 and earnings['payments'][0]['checkoutUrl'] is None)
    s.check('Other driver cannot see network driver earnings', api('/api/v1/drivers/payments', person='staff')[1]['payments'] == [])
    s.check('Unrelated user cannot see network driver earnings', api('/api/v1/drivers/payments', person='outsider')[1]['payments'] == [])
    s.check('Driver cannot read client payment ledger', api('/api/v1/tenants/bistro/driver-payments', person='bob')[0] == 403)
    s.check('Foreign client payment ledger remains isolated', api('/api/v1/tenants/foreign/driver-payments')[0] == 403)
    disabled_approval = api('/api/v1/tenants/bistro/driver-payments/' + network_payment['id'] + '/approve',
        {'expectedVersion': 0, 'driverPayCents': 900, 'confirmPayment': True})
    s.check('Disabled payment provider cannot initiate or mark payment', disabled_approval[0] == 503
            and s.sql('SELECT COUNT(*) FROM tide_driver_payment_states')[0][0] == 0)
    action('bistro', completed_order, 'mark-paid', paymentCollected=True)
    s.check('Customer payment reconciliation never duplicates driver payable', s.sql('SELECT COUNT(*) FROM tide_driver_payables WHERE order_id=?', (completed_order,))[0][0] == 1)
    end()
    save(listed=True)
    offer(payPerDeliveryCents=1000); respond()
    preserved = next(p for p in api('/api/v1/tenants/bistro/driver-payments')[1]['payments'] if p['orderId'] == completed_order)
    s.check('A later hire rate cannot change the completed payable', preserved['driverPayCents'] == 900
            and preserved['platformFeeCents'] == 45 and preserved['totalCents'] == 945)

    ok('Owner restores in-house driver', api('/api/v1/tenants/bistro/team/members/' + inhouse['id'], {'expectedActive': False, 'active': True}))
    ok('In-house driver binds verified membership', api('/api/v1/tenants/bistro/ordering/operations', person='staff'))
    own_order = place()
    action('bistro', own_order, 'assign-driver', driverId=inhouse['id'])
    action('bistro', own_order, 'acknowledge-delivery', person='staff')
    action('bistro', own_order, 'preparing')
    action('bistro', own_order, 'ready')
    action('bistro', own_order, 'out_for_delivery', person='staff')
    action('bistro', own_order, 'confirm-delivery', person='staff')
    own_payment = next(p for p in api('/api/v1/tenants/bistro/driver-payments')[1]['payments'] if p['orderId'] == own_order)
    s.check('In-house delivery stays own-source with no platform fee', own_payment['source'] == 'own'
            and own_payment['platformFeeCents'] == 0 and own_payment['driverPayCents'] == 0
            and own_payment['status'] == 'pending_approval')
    s.check('In-house driver earnings contain only their own completion', [p['orderId'] for p in api('/api/v1/drivers/payments', person='staff')[1]['payments']] == [own_order])
    s.check('Network driver earnings remain isolated from in-house completion', [p['orderId'] for p in api('/api/v1/drivers/payments', person='bob')[1]['payments']] == [completed_order])
    completed = True
finally:
    for proc in s.PROCESSES:
        if proc.poll() is None:
            proc.terminate()
            try:
                proc.wait(timeout=20)
            except s.subprocess.TimeoutExpired:
                proc.kill(); proc.wait(timeout=10)
    s.PROVIDER.shutdown(); s.PROVIDER.server_close()
    for log in s.LOGS:
        log.close()
    if pg and schema_owned:
        assert re.fullmatch(r'tide_network_[a-f0-9]{16}', schema)
        with pg.connect(**creds, autocommit=True) as db:
            db.execute(pgsql.SQL('DROP SCHEMA {} CASCADE').format(pgsql.Identifier(schema)))
    (s.RUN / 'driver-network-results.json').write_text(json.dumps({'completed': completed, 'apiSha256': api_sha,
        'provider': 'postgres' if pg else 'sqlite', 'checks': s.RESULTS}, indent=2), encoding='utf-8')
    print('Driver network evidence: ' + str(s.RUN), flush=True)
print(str(len(s.RESULTS)) + ' driver network checks passed.', flush=True)
