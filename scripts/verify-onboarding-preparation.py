"""Verify private preparation without publication against a real isolated API.

Only the loopback identity provider is synthetic. No external Stripe calls or
production data are used. Run after building TideCasa.Api.
TIDE_PREPARATION_POSTGRES=1 uses the existing loopback PostgreSQL fixture.
"""
import json
import os
from pathlib import Path
import shutil
import uuid
import restaurant_test_support as s

source = Path(os.environ.get('TIDE_TEST_BUILD_ROOT', str(s.ROOT))).resolve()
s.RUN = s.ROOT / '.tools/onboarding-preparation-verification' / s.datetime.now(s.timezone.utc).strftime('%Y%m%d-%H%M%S-%f')
s.RUN.mkdir(parents=True)
s.DB = s.RUN / 'synthetic.db'
runtime = s.RUN / 'runtime'
for project in ('TideCasa.Api', 'TideCasa.Blazor'):
    shutil.copytree(source / project / 'bin/Debug/net10.0', runtime / project / 'bin/Debug/net10.0')
s.ROOT = runtime
os.environ['TIDE_TEST_BUILD_ROOT'] = str(runtime)
for key in list(os.environ):
    if any(value in key.upper() for value in ('CONNECTIONSTRINGS', 'REVERSEPROXY__', 'WEBPUSH__', 'BUSINESSPUSH__', 'DATAPROTECTION__')):
        os.environ.pop(key)
postgres = None
if os.environ.get('TIDE_PREPARATION_POSTGRES') == '1':
    from postgres_test_support import PostgresFixture
    postgres = PostgresFixture('onboarding-preparation')
    postgres.install_namespace(vars(s))
for n, name in enumerate(('manager', 'server', 'wrong'), 851):
    s.USERS[name + '@example.invalid'] = {'id': str(uuid.UUID(int=n)), 'email': name + '@example.invalid',
        'email_confirmed_at': '2026-01-01T00:00:00Z', 'is_anonymous': False,
        'user_metadata': {'role': 'owner', 'isPlatformOwner': True}}


def api(tenant, suffix, body=None, person='alice'):
    return s.call('/api/v1/tenants/' + tenant + '/' + suffix, body, tokens[person])


def ok(label, response, code=200):
    s.check(label, response[0] == code, response[:3])
    return response[1]


def settings_body(settings, **updates):
    return {**settings, 'expectedVersion': settings['version'], **updates}


try:
    s.launch()
    tokens = {name: ok('Sign in ' + name, s.call('/api/v1/auth/signin',
              {'email': name + '@example.invalid', 'password': s.PASSWORD}))['accessToken']
              for name in ('alice', 'bob', 'manager', 'server', 'wrong')}
    s.seed('bistro')
    s.seed('foreign', owner=s.BOB, status='draft', enabled=False)
    for state in ('draft', 'building'):
        s.seed(state, status=state, enabled=False)
        s.alter_config(lambda cfg: cfg.update(accepting_orders=False), state)
        settings = ok(state + ' owner opens service preparation', api(state, 'ordering/settings'))
        s.check(state + ' service starts closed', not settings['acceptingOrders'])
        settings = ok(state + ' owner saves closed service', api(state, 'ordering/settings', settings_body(settings, contactPhone='305-555-0123')))
        response = api(state, 'ordering/settings', settings_body(settings, acceptingOrders=True))
        s.check(state + ' cannot open orders through settings', response[0] == 409 and response[1]['code'] == 'launch_required', response[:3])
        team = ok(state + ' owner prepares manager', api(state, 'team/members',
            {'name': 'Manager', 'email': 'manager@example.invalid', 'role': 'manager'}))
        manager = next(member['id'] for member in team['members'] if member['role'] == 'manager')
        team = ok(state + ' owner prepares server', api(state, 'team/members',
            {'name': 'Server', 'email': 'server@example.invalid', 'role': 'server'}))
        server = next(member['id'] for member in team['members'] if member['role'] == 'server')
        s.check(state + ' mismatched verified email cannot claim manager invitation', api(state, 'menu', person='wrong')[0] == 403)
        ok(state + ' direct manager editor binds invitation', api(state, 'menu', person='manager'))
        bound = 'supabase:' + s.USERS['manager@example.invalid']['id']
        s.check(state + ' manager binding durable', s.sql('SELECT user_id FROM bartide_enhanced_members WHERE id=?', (manager,))[0][0] == bound)
        account = ok(state + ' manager discovers preparation workspace', s.call('/api/v1/account', token=tokens['manager']))
        access = next(workspace for workspace in account['workspaces'] if workspace['tenantId'] == state)
        s.check(state + ' manager can prepare but is not active or owner', access['canPrepare'] and not access['canEdit'] and not access['isOwner'] and access['staffRole'] == 'manager')
        for feature in ('ordering/settings', 'ordering', 'team'):
            ok(state + ' manager prepares ' + feature, api(state, feature, person='manager'))
            s.check(state + ' foreign manager denied ' + feature, api('foreign', feature, person='manager')[0] == 403)
        menu = ok(state + ' manager reads menu', api(state, 'menu', person='manager'))
        ok(state + ' manager saves menu', api(state, 'menu/profile',
            {'expectedVersion': menu['version'], 'profile': {**menu['profile'], 'tagline': 'Prepared privately'}}, 'manager'))
        s.alter_config(lambda cfg: cfg.update(accepting_orders=True), state)
        private_settings = ok(state + ' private service reports orders closed despite stale flag', api(state, 'ordering/settings'))
        s.check(state + ' private acceptance is never advertised', not private_settings['acceptingOrders'])
        table_workspace = ok(state + ' read tables', api(state, 'ordering', person='manager'))
        table_workspace = ok(state + ' manager creates table', api(state, 'ordering/tables',
            {'expectedVersion': table_workspace['configVersion'], 'label': 'Private patio'}, 'manager'))
        s.check(state + ' table preparation available', any(table['label'] == 'Private patio' for table in table_workspace['tables']))
        table_token = next(table['token'] for table in table_workspace['tables'] if table['label'] == 'Private patio')
        s.check(state + ' public QR remains unavailable', s.call('/api/v1/restaurants/' + state + '/tables/' + table_token + '/qr')[0] == 404)
        s.check(state + ' manager cannot grant managers', api(state, 'team/members',
            {'name': 'No grant', 'email': 'denied@example.invalid', 'role': 'manager'}, 'manager')[0] == 403)
        s.check(state + ' manager cannot change manager grants', api(state, 'team/members/' + manager,
            {'expectedActive': True, 'active': False}, 'manager')[0] == 403)
        ok(state + ' owner reaches merchant setup status', api(state, 'payments/connect'))
        merchant_request = {'requestKey': str(uuid.uuid4()), 'confirmUsBusiness': True}
        owner_session = api(state, 'payments/connect/session', merchant_request)
        s.check(state + ' owner reaches disabled embedded setup boundary', owner_session[0] == 503 and owner_session[1]['code'] == 'embedded_unavailable', owner_session[:3])
        for person in ('manager', 'server', 'bob'):
            s.check(state + ' banking remains owner-only ' + person, api(state, 'payments/connect', person=person)[0] == 403)
            s.check(state + ' banking session owner-only ' + person, api(state, 'payments/connect/session', merchant_request, person)[0] == 403)
        account = ok(state + ' ordinary staff account', s.call('/api/v1/account', token=tokens['server']))
        s.check(state + ' ordinary staff sees no private workspace', all(workspace['tenantId'] != state for workspace in account['workspaces']))
        s.check(state + ' ordinary staff invitation stays unbound', s.sql('SELECT user_id FROM bartide_enhanced_members WHERE id=?', (server,))[0][0] is None)
        for person in ('alice', 'manager', 'server'):
            s.check(state + ' operations remain inactive ' + person, api(state, 'ordering/operations', person=person)[0] == 404)
        for feature in ('menu', 'ordering/settings', 'ordering', 'team'):
            s.check(state + ' ordinary staff cannot prepare ' + feature, api(state, feature, person='server')[0] == 403)
        for route in ('/api/v1/restaurants/' + state + '/menu', '/api/v1/restaurants/' + state + '/quote'):
            body = s.quote(tenant=state)[0] if route.endswith('/quote') else None
            s.check(state + ' public path stays private ' + route, s.call(route, body)[0] == 404)
        request = s.order_request()
        s.check(state + ' guest order cannot be created', s.call('/api/v1/restaurants/' + state + '/orders', request)[0] == 404)
        s.check(state + ' public order table empty', s.sql('SELECT COUNT(*) FROM bartide_enhanced_orders WHERE tenant_id=?', (state,))[0][0] == 0)
        s.sql('UPDATE bartide_enhanced_members SET email=? WHERE id=?', ('wrong@example.invalid', manager))
        ok(state + ' original durable manager survives email edit', api(state, 'menu', person='manager'))
        s.check(state + ' changed email cannot replace binding', api(state, 'menu', person='wrong')[0] == 403)
        s.sql('UPDATE bartide_enhanced_members SET user_id=NULL,email=? WHERE id=?', ('manager@example.invalid', manager))
        account = ok(state + ' account discovers unbound manager invitation', s.call('/api/v1/account', token=tokens['manager']))
        s.check(state + ' account binds manager directly', any(workspace['tenantId'] == state and workspace['canPrepare'] for workspace in account['workspaces']))
        s.sql('UPDATE bartide_enhanced_members SET active=0 WHERE id=?', (manager,))
        for feature in ('menu', 'ordering/settings', 'ordering', 'team'):
            s.check(state + ' revoked manager loses preparation ' + feature, api(state, feature, person='manager')[0] == 403)
        cfg = json.loads(s.sql('SELECT settings_json FROM bartide_enhanced_configs WHERE tenant_id=?', (state,))[0][0])
        s.check(state + ' no preparation write activates business', not cfg['enabled'] and not cfg['accepting_orders']
                and s.sql('SELECT status FROM bartide_customers WHERE id=?', (state,))[0][0] == state)
    for state in ('paused', 'active'):
        s.seed('disabled-' + state, status=state, enabled=False)
        for feature in ('ordering/settings', 'ordering', 'team', 'payments/connect'):
            s.check(state + ' unavailable service remains denied ' + feature, api('disabled-' + state, feature)[0] in (403, 404))
    s.seed('no-config', status='draft', enabled=False)
    s.sql("DELETE FROM bartide_enhanced_configs WHERE tenant_id='no-config'")
    for feature in ('ordering/settings', 'ordering'):
        response = api('no-config', feature)
        s.check('Missing setup clear for ' + feature, response[0] == 409 and response[1]['code'] == 'setup_required', response[:3])
    ok('Team preparation before config', api('no-config', 'team'))
    s.check('Relationships intact', s.sql('PRAGMA foreign_key_check') == [])
finally:
    for process in s.PROCESSES:
        if process.poll() is None:
            process.terminate()
            process.wait(timeout=20)
    s.PROVIDER.shutdown()
    s.PROVIDER.server_close()
    for log in s.LOGS:
        log.close()
    if postgres:
        postgres.cleanup()
    (s.RUN / 'provider.json').write_text(json.dumps({'provider': 'PostgreSQL' if postgres else 'SQLite',
        'loopbackOnly': True, 'postgresVersion': postgres.server_version if postgres else None}, indent=2), encoding='utf-8')
    (s.RUN / 'results.json').write_text(json.dumps(s.RESULTS, indent=2), encoding='utf-8')
    print('Evidence: ' + str(s.RUN), flush=True)
