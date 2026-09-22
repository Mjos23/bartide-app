"""Public demo authentication/boundary contract against an owned loopback PostgreSQL database.

Requires the existing .tools/postgresql-17-test TLS fixture and a built API. Reads no
live credentials, makes no external requests, and removes only its random test DB/role.
"""
from datetime import datetime, timezone
from pathlib import Path
import hashlib
import json
import os
import re
import secrets
import shutil
import socket
import sqlite3
import subprocess
import sys
import time
import urllib.error
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / '.tools/postgres-python'))
import psycopg
from psycopg import sql

RUN = ROOT / '.tools/public-demo-auth-verification' / datetime.now(timezone.utc).strftime('%Y%m%d-%H%M%S-%f')
RUN.mkdir(parents=True)
SOURCE = Path(os.environ.get('TIDE_TEST_BUILD_ROOT', ROOT)) / 'TideCasa.Api/bin/Debug/net10.0'
RUNTIME = RUN / 'api'
shutil.copytree(SOURCE, RUNTIME)
DOTNET = ROOT / '.tools/dotnet-10.0.401/dotnet.exe'
BAR = json.loads((ROOT / 'fixtures/gulf-lantern.json').read_text())
FIXTURE = json.loads((ROOT / '.tools/postgresql-17-test/fixture.json').read_text(encoding='utf-8-sig'))
assert FIXTURE['host'] == '127.0.0.1' and FIXTURE['database'] == 'tide_test'
assert 1024 <= FIXTURE['port'] <= 65535
TLS = ROOT / '.tools/postgresql-17-test/tls/server.crt'
assert TLS.is_file()
ADMIN = {k: FIXTURE[k] for k in ('host', 'port', 'user', 'password')}
ADMIN.update(dbname=FIXTURE['database'], sslmode='verify-full', sslrootcert=str(TLS), connect_timeout=10)
STAMP = uuid.uuid4().hex[:20]
DATABASE = 'tide_demo_check_' + STAMP
ROLE = 'tide_demo_api.' + STAMP
PASSWORD = secrets.token_hex(24)
SCHEMA = 'tide_demo_gulf_lantern'
CREATED_DB = CREATED_ROLE = False
COMPLETED = False
ERROR_TYPE = None
CHECKS, PROCESSES = [], []
FLAGS = subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0


def check(name, passed):
    CHECKS.append({'check': name, 'passed': bool(passed)})
    print(('PASS ' if passed else 'FAIL ') + name, flush=True)
    if not passed:
        raise AssertionError(name)


def connect_admin(database=DATABASE):
    return psycopg.connect(**{**ADMIN, 'dbname': database}, autocommit=True)


def db_execute(statement, args=()):
    with connect_admin() as connection:
        connection.execute(sql.SQL('SET search_path TO {}').format(sql.Identifier(SCHEMA)))
        result = connection.execute(statement, args)
        return result.fetchall() if result.description else []


def port():
    with socket.socket() as listener:
        listener.bind(('127.0.0.1', 0))
        return listener.getsockname()[1]


def environment():
    env = dict(os.environ)
    prefixes = ('AUTH__', 'PUBLICDEMO__', 'SAMPLEBAR__', 'STORAGE__', 'CONNECTIONSTRINGS__', 'REVERSEPROXY__',
                'SERVICEBILLING__', 'STRIPE__', 'MERCHANTPAYMENTS__', 'NOTIFICATIONS__', 'WEBPUSH__', 'MEDIA__', 'DATAPROTECTION__')
    for key in list(env):
        if key.upper().startswith(prefixes):
            env.pop(key)
    env.update(ASPNETCORE_ENVIRONMENT='Production', DOTNET_ENVIRONMENT='Production', AllowedHosts='localhost;127.0.0.1',
        PublicDemo__Enabled='true', Storage__Provider='PostgreSql', Storage__PostgresSchema=SCHEMA,
        SampleBar__FixturePath=str(ROOT / 'fixtures/gulf-lantern.json'), Auth__Enabled='false', Media__Provider='demo-bundled',
        Notifications__Mode='disabled', ServiceBilling__CheckoutEnabled='false', WebPush__Enabled='false',
        DOTNET_PROCESSOR_COUNT='1',
        Logging__LogLevel__Default='Warning',
        ConnectionStrings__Application=f'Host=127.0.0.1;Port={FIXTURE["port"]};Database={DATABASE};Username={ROLE};Password={PASSWORD};SSL Mode=VerifyFull;Root Certificate={TLS};Maximum Pool Size=4')
    return env


def start(name, overrides=None, negative=False):
    env = environment()
    env.update(overrides or {})
    url = 'http://127.0.0.1:' + str(port())
    env['ASPNETCORE_URLS'] = url
    log_path = RUN / (name + '.log')
    with log_path.open('wb') as output:
        process = subprocess.Popen([str(DOTNET), str(RUNTIME / 'TideCasa.Api.dll')], cwd=RUNTIME,
            env=env, stdout=output, stderr=subprocess.STDOUT, creationflags=FLAGS)
    PROCESSES.append(process)
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        if process.poll() is not None:
            if negative:
                check('Startup rejects ' + name, process.returncode != 0 and 'Public demo isolation requires' in log_path.read_text())
                return None, None
            raise RuntimeError('API failed to start; inspect ' + str(log_path))
        if not negative:
            try:
                if call(url, '/health')[0] == 200:
                    return process, url
            except (OSError, urllib.error.URLError):
                pass
        time.sleep(.08)
    raise TimeoutError('API startup did not finish: ' + name)


def call(url, path, body=None, token=None, method=None):
    data = json.dumps(body).encode() if body is not None else None
    headers = {'Content-Type': 'application/json'}
    if token:
        headers['Authorization'] = 'Bearer ' + token
    request = urllib.request.Request(url + path, data=data, headers=headers, method=method or ('POST' if body is not None else 'GET'))
    try:
        response = urllib.request.urlopen(request, timeout=15)
    except urllib.error.HTTPError as response_error:
        response = response_error
    with response:
        raw = response.read()
        try:
            value = json.loads(raw)
        except (ValueError, UnicodeDecodeError):
            value = None
        return response.status, value, dict(response.headers)


try:
    with connect_admin(FIXTURE['database']) as admin:
        admin.execute(sql.SQL('CREATE ROLE {} LOGIN PASSWORD {} NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS').format(sql.Identifier(ROLE), sql.Literal(PASSWORD)))
        CREATED_ROLE = True
        admin.execute(sql.SQL('CREATE DATABASE {}').format(sql.Identifier(DATABASE)))
        CREATED_DB = True
    with connect_admin() as admin:
        admin.execute(sql.SQL('REVOKE ALL ON DATABASE {} FROM PUBLIC').format(sql.Identifier(DATABASE)))
        admin.execute(sql.SQL('GRANT CONNECT ON DATABASE {} TO {}').format(sql.Identifier(DATABASE), sql.Identifier(ROLE)))
        admin.execute('REVOKE ALL ON SCHEMA public FROM PUBLIC')
        admin.execute(sql.SQL('CREATE SCHEMA {} AUTHORIZATION {}').format(sql.Identifier(SCHEMA), sql.Identifier(ROLE)))
        admin.execute('CREATE SCHEMA separate_business; CREATE TABLE separate_business.private_data(secret text)')
    with psycopg.connect(**{**ADMIN, 'dbname': DATABASE, 'user': ROLE, 'password': PASSWORD}, autocommit=True) as restricted:
        try:
            restricted.execute('SELECT * FROM separate_business.private_data')
            denied = False
        except psycopg.errors.InsufficientPrivilege:
            denied = True
        check('Demo database login cannot read another schema', denied)
        check('Demo login has no privileged role attributes', restricted.execute('SELECT NOT (rolsuper OR rolcreatedb OR rolcreaterole OR rolbypassrls) FROM pg_roles WHERE rolname=current_user').fetchone()[0])

    api_process, api = start('demo')
    # Use only public fictional fixture rows, not the user's production application database.
    source = ROOT / '.tools/gulf-lantern-preview/gulf-lantern.db'
    with sqlite3.connect(source.as_uri() + '?mode=ro', uri=True) as seed:
        seed.row_factory = sqlite3.Row
        for table, key in (('bartide_customers', 'id'), ('bartide_enhanced_configs', 'tenant_id')):
            row = dict(seed.execute(f'SELECT * FROM {table} WHERE {key}=?', ('gulf-lantern',)).fetchone())
            if table == 'bartide_customers':
                assert row['email'].endswith('@gulf-lantern.example.invalid')
            columns = list(row)
            db_execute(sql.SQL('INSERT INTO {} ({}) VALUES ({})').format(sql.Identifier(table),
                sql.SQL(',').join(map(sql.Identifier, columns)), sql.SQL(',').join(sql.Placeholder() for _ in columns)), tuple(row.values()))

    tokens = {}
    for person in BAR['people']:
        status, session, _ = call(api, '/api/v1/auth/demo-switch', {'personKey': person['key']})
        check('Switch to fictional ' + person['key'], status == 200 and session['user']['userId'] == 'supabase:' + person['id']
            and session['user']['email'] == person['email'] and session['user']['isPlatformOwner'] is False
            and session['expiresIn'] == 1800 and re.fullmatch(r'demo\.[0-9a-f]{64}\.session', session['accessToken']))
        tokens[person['key']] = session['accessToken']
        status, account, _ = call(api, '/api/v1/account', token=tokens[person['key']])
        check('Registered session authenticates ' + person['key'], status == 200 and account['user']['userId'] == session['user']['userId'])
    check('Every person receives an independent opaque token', len(set(tokens.values())) == 12)
    check('Tokens are stored only as hashes', all(len(row[0]) == 64 and row[0] not in tokens.values() for row in db_execute('SELECT token_hash FROM bartide_auth_sessions')))

    for person in BAR['people']:
        if person['role'] not in ('owner', 'customer'):
            status, team, _ = call(api, '/api/v1/tenants/gulf-lantern/team/members', {k: person[k] for k in ('name', 'email', 'role')}, tokens['owner'])
            check('Demo owner can add fictional staff ' + person['key'], status == 200)
            status, access, _ = call(api, '/api/v1/tenants/gulf-lantern/access', token=tokens[person['key']])
            check('Demo staff keeps actual role ' + person['key'], status == 200 and access['staffRole'] == person['role'] and not access['isOwner'])
    for feature in ('menu', 'ordering', 'ordering/settings', 'ordering/operations', 'team', 'events', 'rewards', 'media', 'posts'):
        result = call(api, '/api/v1/tenants/gulf-lantern/' + feature, token=tokens['manager'])
        if result[0] != 200: print(json.dumps({'feature':feature,'status':result[0],'response':result[1]}),flush=True)
        check('Manager can use allowed demo ' + feature, result[0] == 200)
    check('Bartender cannot manage restaurant settings', call(api, '/api/v1/tenants/gulf-lantern/ordering/settings', token=tokens['bartender'])[0] == 403)
    check('Customer cannot access staff team', call(api, '/api/v1/tenants/gulf-lantern/team', token=tokens['avery'])[0] == 403)
    for value in ('admin', 'owner@example.com', BAR['people'][0]['id'], 'OWNER', '', None):
        check('Unlisted demo person rejected ' + str(value), call(api, '/api/v1/auth/demo-switch', {'personKey': value})[0] == 400)
    for field, value in (('role', 'owner'), ('tenant', 'real-business'), ('userId', 'real-user'), ('isPlatformOwner', True)):
        check('Client cannot supply identity attribute ' + field, call(api, '/api/v1/auth/demo-switch', {'personKey': 'avery', field: value})[0] == 400)
    for path in ('/api/v1/tenants/other/access', '/api/v1/tenants/other/team', '/api/v1/restaurants/other/menu',
                 '/api/v1/owner/sales', '/api/v1/owner/launch-review', '/api/v1/tenants/gulf-lantern/billing',
                 '/api/v1/tenants/gulf-lantern/payments/connect', '/api/v1/restaurants/gulf-lantern/posts/subscriptions/mine'):
        status, value, _ = call(api, path, token=tokens['owner'])
        check('Demo boundary rejects ' + path, status == 403 and value.get('code') == 'demo_boundary')
    for path in ('/api/v1/account/workspace', '/api/v1/auth/signin', '/api/v1/auth/signup', '/api/v1/auth/verify',
                 '/api/v1/auth/forgot', '/api/v1/auth/resend', '/api/v1/auth/reset', '/api/v1/demo-requests',
                 '/api/v1/service-purchases', '/api/v1/restaurants/gulf-lantern/posts/subscriptions',
                 '/api/v1/tenants/gulf-lantern/media/photo', '/api/v1/restaurants/gulf-lantern/checkout'):
        status, value, _ = call(api, path, {}, tokens['owner'])
        check('Demo boundary blocks mutation ' + path, status == 403 and value.get('code') == 'demo_boundary')
    check('Demo media deletes blocked', call(api, '/api/v1/tenants/gulf-lantern/media/photo/' + str(uuid.uuid4()), token=tokens['owner'], method='DELETE')[0] == 403)
    check('Scoped media reads reach media authorization with a distinct file id', call(api, '/api/v1/tenants/gulf-lantern/media/photo/' + str(uuid.uuid4()), token=tokens['owner'])[0] == 404)
    check('Unknown future endpoints fail closed', call(api, '/api/v1/tenants/gulf-lantern/new-sensitive-operation', {}, tokens['owner'])[0] == 403)
    check('Public demo forbids indexing', call(api, '/health')[2].get('X-Robots-Tag') == 'noindex, nofollow, noarchive')
    check('Unregistered plausible demo token rejected', call(api, '/api/v1/account', token='demo.' + secrets.token_hex(32) + '.session')[0] == 401)
    forged = 'normal.fixture.token'
    db_execute('INSERT INTO bartide_auth_sessions(token_hash,provider_user_id,expires_at) VALUES(%s,%s,%s)', (hashlib.sha256(forged.encode()).hexdigest(), BAR['people'][0]['id'], int(time.time()) + 600))
    check('Even registered ordinary tokens are rejected in demo mode', call(api, '/api/v1/account', token=forged)[0] == 401)
    unknown_provider = str(uuid.uuid4())
    unknown_token = 'demo.' + secrets.token_hex(32) + '.session'
    db_execute('INSERT INTO bartide_auth_identities(provider_user_id,app_user_id,verified_email,created_at) VALUES(%s,%s,%s,%s)',
        (unknown_provider, 'unlisted-user', 'unlisted@example.invalid', datetime.now(timezone.utc).isoformat()))
    db_execute('INSERT INTO bartide_auth_sessions(token_hash,provider_user_id,expires_at) VALUES(%s,%s,%s)',
        (hashlib.sha256(unknown_token.encode()).hexdigest(), unknown_provider, int(time.time()) + 600))
    check('Registered demo token for an unlisted identity rejected', call(api, '/api/v1/account', token=unknown_token)[0] == 401)
    owner_id = BAR['people'][0]['id']
    db_execute('UPDATE bartide_auth_identities SET app_user_id=%s WHERE provider_user_id=%s', ('unrelated-user', owner_id))
    check('A remapped demo identity cannot authenticate as another user', call(api, '/api/v1/account', token=tokens['owner'])[0] == 503)
    db_execute('UPDATE bartide_auth_identities SET app_user_id=%s WHERE provider_user_id=%s', ('supabase:' + owner_id, owner_id))
    check('Demo logout succeeds without an identity provider', call(api, '/api/v1/auth/signout', {}, tokens['reese'])[0] == 204)
    check('Logged out demo token cannot be reused', call(api, '/api/v1/account', token=tokens['reese'])[0] == 401)
    db_execute('UPDATE bartide_auth_sessions SET expires_at=%s WHERE token_hash=%s', (int(time.time()) - 1, hashlib.sha256(tokens['quinn'].encode()).hexdigest()))
    check('Expired registered demo token rejected', call(api, '/api/v1/account', token=tokens['quinn'])[0] == 401)

    api_process.terminate(); api_process.wait(timeout=15)
    normal_process, normal = start('normal-mode-same-registry', {'PublicDemo__Enabled': 'false', 'Media__Provider': 'disabled'})
    check('Demo switch absent in normal app', call(normal, '/api/v1/auth/demo-switch', {'personKey': 'owner'})[0] == 404)
    check('Normal app rejects demo token even with its registry row', call(normal, '/api/v1/account', token=tokens['owner'])[0] == 401)
    normal_process.terminate(); normal_process.wait(timeout=15)
    api_process, api = start('demo-restart')
    check('Registered sessions remain usable after API restart', call(api, '/api/v1/account', token=tokens['owner'])[0] == 200)
    responses = [call(api, '/api/v1/auth/demo-switch', {'personKey': 'owner'})[0] for _ in range(31)]
    check('Role switching permits 30 presentations per IP/minute then throttles', responses == [200] * 30 + [429])
    api_process.terminate(); api_process.wait(timeout=15)

    bad_configs = {
        'shared-schema': {'Storage__PostgresSchema': 'tide_casa'},
        'sqlite-storage': {'Storage__Provider': 'Sqlite'},
        'privileged-login': {'ConnectionStrings__Application': environment()['ConnectionStrings__Application'].replace('Username=' + ROLE, 'Username=postgres')},
        'live-auth-enabled': {'Auth__Enabled': 'true'},
        'real-auth-origin': {'Auth__SupabaseUrl': 'https://example.supabase.co'},
        'auth-provider-key': {'Auth__PublishableKey': 'forbidden-fixture-value'},
        'platform-owner': {'Auth__PlatformOwnerUserId': 'supabase:' + BAR['people'][0]['id']},
        'service-payments': {'ServiceBilling__CheckoutEnabled': 'true'},
        'billing-key': {'ServiceBilling__RestrictedKey': 'forbidden-fixture-value'},
        'live-stripe': {'Stripe__Mode': 'live'},
        'merchant-onboarding': {'MerchantPayments__OnboardingEnabled': 'true'},
        'merchant-key': {'MerchantPayments__RestrictedKey': 'forbidden-fixture-value'},
        'external-notifications': {'Notifications__Mode': 'resend'},
        'notification-key': {'Notifications__ResendApiKey': 'forbidden-fixture-value'},
        'web-push': {'WebPush__Enabled': 'true'},
        'web-push-key': {'WebPush__VapidPrivateKey': 'forbidden-fixture-value'},
        'live-media': {'Media__Provider': 'r2'},
        'media-credentials': {'Media__R2__AccessKeyId': 'forbidden-fixture-value'},
    }
    for name, overrides in bad_configs.items():
        start(name, overrides, negative=True)
    for name, mutate in (
        ('foreign-venue', lambda bar: bar.update(id='live-business')),
        ('foreign-person-id', lambda bar: bar['people'][0].update(id=str(uuid.uuid4()))),
        ('real-email', lambda bar: bar['people'][0].update(email='owner@example.com')),
        ('escalated-role', lambda bar: bar['people'][1].update(role='owner')),
        ('duplicate-person', lambda bar: bar['people'].__setitem__(1, bar['people'][0])),
    ):
        changed = json.loads(json.dumps(BAR)); mutate(changed)
        path = RUN / (name + '.json'); path.write_text(json.dumps(changed))
        start(name, {'SampleBar__FixturePath': str(path)}, negative=True)
    COMPLETED = True
except BaseException as error:
    ERROR_TYPE = type(error).__name__
    raise
finally:
    for process in PROCESSES:
        if process.poll() is None:
            process.terminate(); process.wait(timeout=15)
    assert re.fullmatch(r'tide_demo_check_[a-f0-9]{20}', DATABASE) and re.fullmatch(r'tide_demo_api\.[a-f0-9]{20}', ROLE)
    if CREATED_DB or CREATED_ROLE:
        with connect_admin(FIXTURE['database']) as admin:
            if CREATED_DB:
                admin.execute(sql.SQL('DROP DATABASE {} WITH (FORCE)').format(sql.Identifier(DATABASE)))
            if CREATED_ROLE:
                admin.execute(sql.SQL('DROP ROLE {}').format(sql.Identifier(ROLE)))
    (RUN / 'summary.json').write_text(json.dumps({'completed': COMPLETED, 'errorType': ERROR_TYPE, 'checks': CHECKS, 'passed': sum(c['passed'] for c in CHECKS),
        'failed': sum(not c['passed'] for c in CHECKS), 'apiSha256': hashlib.sha256((RUNTIME / 'TideCasa.Api.dll').read_bytes()).hexdigest(),
        'postgresOnlyLoopback': True, 'environment': 'Production', 'tls': 'VerifyFull', 'ownedDatabaseRemoved': CREATED_DB,
        'ownedRoleRemoved': CREATED_ROLE, 'noExternalProviderConfigured': True}, indent=2))
print(json.dumps({'passed': len(CHECKS), 'evidence': str(RUN / 'summary.json')}))
