"""Check pre-provisioned runtime grants and real local API/Web forwarding.

Synthetic, loopback only. Production API uses verified PostgreSQL TLS. Web uses
Development solely to allow an HTTP loopback API URL; ingress and PostgreSQL key
storage are enabled. This is not proof of real DigitalOcean networking or Linux.
"""
import hashlib
import json
import os
from pathlib import Path
import secrets
import socket
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

fixture = json.loads((ROOT / '.tools/postgresql-17-test/fixture.json').read_text())
assert fixture['host'] == '127.0.0.1'
certificate = ROOT / '.tools/postgresql-17-test/tls/server.crt'
assert certificate.is_file()
identity = uuid.uuid4().hex[:16]
schema, api_role, web_role = 'tide_runtime_' + identity, 'tide_api_' + identity, 'tide_web_' + identity
RUN = ROOT / '.tools/launch-private' / ('runtime-check-' + identity)
RUN.mkdir()
SDK = ROOT / '.tools/dotnet-10.0.401/dotnet.exe'
admin_args = {'host': fixture['host'], 'port': fixture['port'], 'dbname': fixture['database'],
              'user': fixture['user'], 'password': fixture['password'], 'sslmode': 'verify-full', 'sslrootcert': str(certificate)}
api_password, web_password, token = secrets.token_hex(32), secrets.token_hex(32), secrets.token_urlsafe(40)
checks, processes, logs = [], [], []
result = {'completed': False, 'checks': checks, 'boundary': __doc__, 'binarySha256': {
    p: hashlib.sha256((ROOT / p / 'bin/Debug/net10.0' / (p + '.dll')).read_bytes()).hexdigest()
    for p in ['TideCasa.Api', 'TideCasa.Blazor']}}

def check(label, condition):
    checks.append({'check': label, 'passed': bool(condition)})
    print(('PASS ' if condition else 'FAIL ') + label, flush=True)
    if not condition: raise AssertionError(label)

def port():
    with socket.socket() as s:
        s.bind(('127.0.0.1', 0)); return s.getsockname()[1]

def connection(user, password):
    # Every inserted value is from this constrained test fixture; generated passwords
    # are hex, so connection-string delimiters cannot be introduced by a test value.
    return f'Host=127.0.0.1;Port={fixture["port"]};Database={fixture["database"]};Username={user};Password={password};SSL Mode=VerifyFull;Root Certificate={certificate};Maximum Pool Size=3'

def request(base, path, data=None, forwarded=True):
    headers = {'Content-Type': 'application/json'}
    if forwarded: headers.update({'do-connecting-ip': '203.0.113.20', 'X-Forwarded-Proto': 'https', 'X-Forwarded-For': '198.51.100.99'})
    req = urllib.request.Request(base + path, data=None if data is None else json.dumps(data).encode(), headers=headers)
    try: response = urllib.request.urlopen(req, timeout=30)
    except urllib.error.HTTPError as error: response = error
    with response: return response.status, response.read().decode('utf-8'), response.headers

def launch(project, user, password, environment='Production', api_base=None):
    listen = port(); base = 'http://127.0.0.1:' + str(listen)
    env = os.environ.copy()
    for key in list(env):
        if any(x in key.upper() for x in ['STRIPE', 'RESEND', 'SUPABASE', 'CLOUDFLARE', 'AUTH__', 'MEDIA__', 'STORAGE__', 'CONNECTIONSTRINGS__', 'NOTIFICATIONS__', 'WEBPUSH__', 'SERVICEBILLING__', 'MERCHANTPAYMENTS__', 'API__', 'DATAPROTECTION__', 'REVERSEPROXY__', 'ASPNETCORE_', 'DOTNET_ENVIRONMENT']):
            env.pop(key)
    env.update({'DOTNET_PROCESSOR_COUNT': '1', 'ASPNETCORE_ENVIRONMENT': environment, 'DOTNET_ENVIRONMENT': environment,
        'ASPNETCORE_URLS': base, 'AllowedHosts': 'localhost;127.0.0.1',
        'Storage__Provider': 'PostgreSql', 'Storage__PostgresSchema': schema, 'Storage__CreatePostgresSchema': 'false',
        'ConnectionStrings__Application': connection(user, password), 'Auth__Enabled': 'false', 'Auth__AllowLocalTestProvider': 'false',
        'Media__Provider': 'disabled', 'Media__AllowLocalStore': 'false', 'Notifications__Mode': 'disabled',
        'WebPush__Enabled': 'false', 'ServiceBilling__CheckoutEnabled': 'false', 'ServiceBilling__LiveEnabled': 'false',
        'MerchantPayments__CheckoutEnabled': 'false', 'MerchantPayments__OnboardingEnabled': 'false',
        'ReverseProxy__Provider': 'DigitalOceanAppPlatform', 'ReverseProxy__InternalToken': token})
    if api_base:
        import base64
        env.update({'Api__BaseUrl': api_base + '/', 'DataProtection__Provider': 'PostgreSql',
                    'DataProtection__EncryptionKey': base64.b64encode(bytes.fromhex(web_password)).decode()})
    logfile = (RUN / (project + '-' + user + '.log')).open('w', encoding='utf-8'); logs.append(logfile)
    process = subprocess.Popen([str(SDK), str(ROOT / project / 'bin/Debug/net10.0' / (project + '.dll'))],
        cwd=ROOT / project, env=env, stdin=subprocess.DEVNULL, stdout=logfile, stderr=subprocess.STDOUT,
        creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
    processes.append(process)
    until = time.monotonic() + 180
    while time.monotonic() < until:
        if process.poll() is not None: raise RuntimeError(project + ' stopped at startup; see protected log')
        try:
            if request(base, '/health', forwarded=False)[0] == 200: return process, base
        except (OSError, urllib.error.URLError): pass
        time.sleep(.5)
    raise RuntimeError(project + ' startup timeout')

def stop(process):
    if process.poll() is None:
        process.terminate()
        try: process.wait(timeout=20)
        except subprocess.TimeoutExpired: process.kill(); process.wait(timeout=10)

try:
    with psycopg.connect(**admin_args, autocommit=True) as admin:
        admin.execute(sql.SQL('CREATE SCHEMA {}').format(sql.Identifier(schema)))
    provisioner, _ = launch('TideCasa.Api', fixture['user'], fixture['password'])
    stop(provisioner)
    with psycopg.connect(**admin_args, autocommit=True) as admin:
        for name, password in [(api_role, api_password), (web_role, web_password)]:
            admin.execute(sql.SQL('CREATE ROLE {} LOGIN PASSWORD {} NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT').format(sql.Identifier(name), sql.Literal(password)))
            admin.execute(sql.SQL('GRANT USAGE ON SCHEMA {} TO {}').format(sql.Identifier(schema), sql.Identifier(name)))
        tables = json.loads((ROOT / 'TideCasa.Api/PostgresMigrations/manifest.json').read_text(encoding='utf-8-sig'))['tables']
        for table in tables:
            if table['name'] == 'tide_data_protection_keys': continue
            admin.execute(sql.SQL('GRANT SELECT, INSERT, UPDATE, DELETE ON {}.{} TO {}').format(sql.Identifier(schema), sql.Identifier(table['name']), sql.Identifier(api_role)))
        admin.execute(sql.SQL('GRANT SELECT ON {}.tide_postgres_migrations TO {}').format(sql.Identifier(schema), sql.Identifier(api_role)))
        admin.execute(sql.SQL('GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA {} TO {}').format(sql.Identifier(schema), sql.Identifier(api_role)))
        admin.execute(sql.SQL('GRANT SELECT, INSERT ON {}.tide_data_protection_keys TO {}').format(sql.Identifier(schema), sql.Identifier(web_role)))
        check('API runtime has no schema CREATE permission', not admin.execute('SELECT has_schema_privilege(%s,%s,%s)', (api_role, schema, 'CREATE')).fetchone()[0])
        check('API cannot modify the migration ledger', not admin.execute('SELECT has_table_privilege(%s,%s,%s)', (api_role, schema + '.tide_postgres_migrations', 'UPDATE')).fetchone()[0])
        check('API cannot read Web encryption envelopes', not admin.execute('SELECT has_table_privilege(%s,%s,%s)', (api_role, schema + '.tide_data_protection_keys', 'SELECT')).fetchone()[0])
        check('Web cannot read customer booking records', not admin.execute('SELECT has_table_privilege(%s,%s,%s)', (web_role, schema + '.demo_requests', 'SELECT')).fetchone()[0])
    api_process, api = launch('TideCasa.Api', api_role, api_password)
    check('Production API starts with verified TLS and pre-provisioned limited grants', request(api, '/health', forwarded=False)[0] == 200)
    check('Direct API request cannot omit authenticated ingress information', request(api, '/api/v1/pricing/quote', {'appStores': False}, forwarded=False)[0] == 400)
    booking = {'id': str(uuid.uuid4()), 'name': 'Synthetic runtime grant check', 'business': 'Synthetic only', 'email': 'runtime@example.invalid',
        'phone': '', 'city': '', 'businessType': 'bartide', 'preferredTimes': 'Tuesday afternoon', 'timeZone': 'Eastern Time', 'goals': 'QA only', 'contactWebsite': ''}
    check('Limited API role can persist a booking through the public ingress boundary', request(api, '/api/v1/demo-requests', booking)[0] == 201)
    web_process, web = launch('TideCasa.Blazor', web_role, web_password, 'Development', api)
    status, html, headers = request(web, '/purchase/restaurant')
    check('Web reaches the API through its authenticated internal forwarding handler', status == 200 and '$650' in html and 'Continue with your business' in html)
    status, html, headers = request(web, '/book-a-demo')
    check('Booking form uses durable PostgreSQL key storage and HTTPS cookie policy', status == 200 and 'Request my demo' in html
          and 'secure' in ' '.join(headers.get_all('Set-Cookie', [])).lower())
    check('Canceled restaurant simulation gallery is absent', request(web, '/demo-restaurants')[0] == 404)
    check('Canceled restaurant simulation detail is absent', request(web, '/demo-restaurants/unused')[0] == 404)
    with psycopg.connect(**admin_args, autocommit=True) as admin:
        envelopes = admin.execute(sql.SQL('SELECT ciphertext FROM {}.tide_data_protection_keys').format(sql.Identifier(schema))).fetchall()
        check('Actual Web key manager saved only authenticated encrypted envelopes', len(envelopes) > 0 and all(row[0].startswith('v1.') and '<key' not in row[0] for row in envelopes))
    stop(web_process)
    _, restarted_web = launch('TideCasa.Blazor', web_role, web_password, 'Development', api)
    check('Actual Web process reopens its durable key ring after restart', request(restarted_web, '/book-a-demo')[0] == 200)
    result['completed'] = True
except BaseException as error:
    result['errorType'] = type(error).__name__
    print('Runtime check failed; inspect its protected evidence.', flush=True)
finally:
    for process in reversed(processes): stop(process)
    for log in logs: log.close()
    with psycopg.connect(**admin_args, autocommit=True) as admin:
        admin.execute(sql.SQL('DROP SCHEMA IF EXISTS {} CASCADE').format(sql.Identifier(schema)))
        for name in [api_role, web_role]:
            if admin.execute('SELECT 1 FROM pg_roles WHERE rolname=%s', (name,)).fetchone():
                admin.execute(sql.SQL('DROP ROLE {}').format(sql.Identifier(name)))
    result['ownedResourcesRemoved'] = True
    (RUN / 'results.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(json.dumps({'evidence': str(RUN / 'results.json'), 'passed': sum(c['passed'] for c in checks), 'completed': result['completed']}))
sys.exit(0 if result['completed'] else 1)
