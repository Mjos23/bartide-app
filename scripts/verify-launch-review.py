"""Launch review via isolated real API/SQLite and native Blazor SSR forms.

Only identity is a loopback fake; fixtures contain synthetic projects and users.
No provider operations, real account changes, billing writes or deployment.
"""
import concurrent.futures
from datetime import datetime, timedelta, timezone
import html
import http.cookiejar
import importlib.util
import json
import os
import platform
from pathlib import Path
import shutil
import subprocess
import time
import urllib.error
import urllib.parse
import urllib.request
import restaurant_test_support as s

s.RUN = s.ROOT / '.tools/launch-review-verification' / datetime.now(timezone.utc).strftime('%Y%m%d-%H%M%S-%f')
s.RUN.mkdir(parents=True); s.DB = s.RUN / 'synthetic.db'
WEB = 'http://127.0.0.1:' + str(s.port())
check = s.check
TOKENS = {}
spec = importlib.util.spec_from_file_location('launch_forms', Path(__file__).with_name('verify-management-web.py'))
forms_module = importlib.util.module_from_spec(spec); spec.loader.exec_module(forms_module)
Forms = forms_module.Forms


def runtime_copy_ignores(directory, names):
    ignored = {name for name in names if name.endswith('.pdb')}
    if os.name == 'nt' and platform.machine().lower() in ('amd64', 'x86_64') and Path(directory).name == 'runtimes':
        ignored.update(name for name in names if name not in ('win', 'win-x64'))
    return ignored


def launch(project, address, extra):
    build_root = os.environ.get('TIDE_TEST_BUILD_ROOT')
    source = Path(build_root) / project / 'bin/Debug/net10.0' if build_root else Path(os.environ.get('LAUNCH_ARTIFACTS', s.ROOT / '.tools/launch-review-isolated-build')) / 'bin' / project / 'debug'
    copied = s.RUN / project
    if not copied.exists(): shutil.copytree(source, copied, ignore=runtime_copy_ignores)
    env = os.environ.copy()
    for key in list(env):
        if any(x in key.upper() for x in ('STRIPE', 'SUPABASE', 'RESEND', 'CLOUDFLARE', 'AUTH__', 'STORAGE__', 'API__', 'DATAPROTECTION__', 'MERCHANTPAYMENTS__', 'SERVICEBILLING__', 'NOTIFICATIONS__', 'MEDIA__')): env.pop(key)
    env.update({'ASPNETCORE_ENVIRONMENT': 'Development', 'ASPNETCORE_URLS': address,
        'Stripe__CheckoutEnabled': 'false', 'MerchantPayments__OnboardingEnabled': 'false', 'MerchantPayments__CheckoutEnabled': 'false', 'Notifications__Mode': 'disabled', **extra})
    log = (s.RUN / (project + '-' + str(len(s.PROCESSES)) + '.log')).open('w', encoding='utf-8'); s.LOGS.append(log)
    proc = subprocess.Popen([str(s.SDK), str(copied / (project + '.dll'))], cwd=s.ROOT / project, env=env,
        stdout=log, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
    s.PROCESSES.append(proc)
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    for _ in range(400):
        if proc.poll() is not None: raise RuntimeError('Local fixture process stopped: ' + project)
        try:
            with opener.open(address + '/health', timeout=2) as response:
                if response.status == 200: return proc
        except OSError: pass
        time.sleep(.2)
    raise TimeoutError(project)


def api(path='', body=None, person='platform'):
    return s.call('/api/v1/owner/launch-review' + path, body, TOKENS.get(person))


def transition(ident, status='active', version=0, reviewed=True, person='platform'):
    return api('/' + ident + '/transition', {'expectedVersion': version, 'status': status, 'reviewedWithCustomer': reviewed}, person)


def seed(ident, status='building', enrolled=None, due=None, menu=None, vertical='bartide'):
    s.seed(ident, s.ALICE, status, True, vertical)
    now = datetime.now(timezone.utc)
    s.sql('UPDATE bartide_customers SET enrolled_at=?,build_ready_at=?,menu_json=COALESCE(?,menu_json) WHERE id=?',
        (enrolled or (now-timedelta(days=8)).isoformat(), due or (now-timedelta(days=1)).isoformat(), menu, ident))


def web(client, path, fields=None, headers=None):
    raw = None if fields is None else urllib.parse.urlencode(fields).encode()
    hdrs = {} if fields is None else {'Content-Type': 'application/x-www-form-urlencoded', 'Origin': WEB}
    try: response = client.open(urllib.request.Request(WEB + path, data=raw, headers={**hdrs, **(headers or {})}), timeout=40)
    except urllib.error.HTTPError as error: response = error
    with response: return response.status, response.read().decode(), response.headers


def form(client, path, action):
    response = web(client, path); check('SSR page loads ' + path, response[0] == 200, response[:2])
    selected = [x for x in Forms(response[1]).forms if x['action'] == action]
    check('Expected native form exists', len(selected) == 1, action)
    found = selected[0]; found['fields'] = {k: v or '' for k, v in found['fields'].items()}
    return found, response


def browser(person=None):
    client = urllib.request.build_opener(urllib.request.ProxyHandler({}), s.NoRedirect(), urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
    if person:
        login, _ = form(client, '/signin', '/auth/session/signin')
        response = web(client, login['action'], {**login['fields'], 'email': person + '@example.invalid', 'password': s.PASSWORD})
        check('Native sign in ' + person, response[0] in (302, 303))
    return client


def run():
    env = {'Storage__DatabasePath': str(s.DB), 'Auth__Enabled': 'true', 'Auth__AllowLocalTestProvider': 'true',
        'Auth__SupabaseUrl': f'http://127.0.0.1:{s.PROVIDER.server_port}', 'Auth__PublishableKey': s.KEY,
        'Auth__PlatformOwnerUserId': 'supabase:' + s.PLATFORM, 'ReverseProxy__KnownClientProxy': '127.0.0.1'}
    process = launch('TideCasa.Api', s.API, env)
    for person in ('alice', 'bob', 'staff', 'platform'):
        result = s.call('/api/v1/auth/signin', {'email': person + '@example.invalid', 'password': s.PASSWORD})
        check('Verified synthetic sign in ' + person, result[0] == 200); TOKENS[person] = result[1]['accessToken']
    seed('a-ready'); seed('a-draft', 'draft'); seed('a-business', vertical='tide-casa'); seed('a-active', 'active'); seed('a-paused', 'paused')
    seed('a-native'); seed('a-race'); seed('a-menu-race')
    check('Anonymous project list denied', api(person=None)[0] == 401)
    for person in ('alice', 'bob', 'staff'):
        check('Non-platform list denied ' + person, api(person=person)[0] == 403)
        check('Non-platform launch denied ' + person, transition('a-ready', person=person)[0] == 403)
    listed = api(); check('Platform owner sees projects', listed[0] == 200 and len(listed[1]['projects']) == 8)
    check('Project list has no private contact or enrollment notes', s.PRIVATE not in listed[2] and '@example.invalid' not in listed[2] and all(set(x) == {'id','name','vertical','status','version','enrolledAt','buildReadyAt','finishedItemCount','canLaunch','launchBlockReason'} for x in listed[1]['projects']))
    check('Owner API response prevents caching', 'no-store' in listed[3].get('Cache-Control', ''))
    check('Draft cannot be manually enrolled or activated', transition('a-draft')[0] == 409 and transition('a-draft', 'building')[0] == 400)
    check('Launch requires explicit customer review', transition('a-ready', reviewed=False)[0] == 400)
    check('Building cannot skip into paused state', transition('a-ready', 'paused')[0] == 400)
    now = datetime.now(timezone.utc)
    seed('new-pricing-build')
    s.sql("INSERT INTO tide_service_orders(id,tenant_id,environment,status,request_json,initial_cents,monthly_cents,total_cents,created_at,updated_at) VALUES('new-pricing-order','new-pricing-build','sandbox','paid','{}',60000,14900,60000,?,?)", (s.NOW, s.NOW))
    check('New pricing cannot launch using a seven-day lead time', transition('new-pricing-build')[0] == 409)
    s.sql('UPDATE bartide_customers SET enrolled_at=?,build_ready_at=? WHERE id=?', ((now-timedelta(days=29)).isoformat(), (now+timedelta(days=1)).isoformat(), 'new-pricing-build'))
    check('New app remains private before its 30-day lead time ends', transition('new-pricing-build')[0] == 409)
    s.sql('UPDATE bartide_customers SET enrolled_at=?,build_ready_at=? WHERE id=?', ((now-timedelta(days=31)).isoformat(), (now-timedelta(days=1)).isoformat(), 'new-pricing-build'))
    check('New app may launch after 30 days and customer review', transition('new-pricing-build')[0] == 200)
    invalid_dates = [('missing', None, None), ('future', now.isoformat(), (now+timedelta(days=7)).isoformat()),
        ('short', (now-timedelta(days=8)).isoformat(), (now-timedelta(days=2)).isoformat()),
        ('inverted', now.isoformat(), (now-timedelta(days=8)).isoformat()), ('bad-start','not-a-date',(now-timedelta(days=1)).isoformat()),
        ('bad-due',(now-timedelta(days=8)).isoformat(),'2026-02-30T00:00:00Z'),
        ('unzoned',(now-timedelta(days=8)).strftime('%Y-%m-%dT%H:%M:%S'),(now-timedelta(days=1)).isoformat())]
    for label, start, due in invalid_dates:
        ident = 'date-' + label; seed(ident)
        s.sql('UPDATE bartide_customers SET enrolled_at=?,build_ready_at=? WHERE id=?', (start, due, ident))
        result = transition(ident)
        check('Launch fails closed for dates ' + label, result[0] == 409 and result[1]['code'] == 'launch_not_ready')
    for n, value in enumerate(['{}','{"items":[]}','{"items":"finished"}','{"items":{}}','{"items":[null]}','{"items":[{"name":"Finished"}]}','{"items":[{"id":"bad id","name":"Finished"}]}','{"items":[{"id":"dish","name":" "}]}','{"items":[{"id":"dish","name":"bad\\u0000name"}]}','broken-json']):
        ident = 'menu-' + str(n); seed(ident, menu=value)
        check('Malformed or unfinished content cannot launch ' + str(n), transition(ident)[0] == 409)
    before = s.sql('SELECT enrolled_at,build_ready_at,enrollment_note,requested_plan FROM bartide_customers WHERE id=?', ('a-ready',))[0]
    result = transition('a-ready'); check('Reviewed elapsed build becomes active', result[0] == 200 and result[1]['status'] == 'active' and result[1]['version'] == 1)
    check('Activation preserves enrollment and billing fields', s.sql('SELECT enrolled_at,build_ready_at,enrollment_note,requested_plan FROM bartide_customers WHERE id=?', ('a-ready',))[0] == before)
    check('Old launch form cannot apply twice', transition('a-ready')[0] == 409)
    check('Business vertical uses same human review lifecycle', transition('a-business')[0] == 200)
    check('Owner pauses active app', transition('a-ready', 'paused', 1, False)[0] == 200)
    check('Pause removes public ordering access', s.call('/api/v1/restaurants/a-ready/menu')[0] == 404)
    check('Stale resume cannot overwrite pause', transition('a-ready', 'active', 1, False)[0] == 409)
    check('Owner resumes previously active app without new enrollment', transition('a-ready', 'active', 2, False)[0] == 200)
    check('Resumed app returns to public ordering', s.call('/api/v1/restaurants/a-ready/menu')[0] == 200)
    check('Resume preserves original enrollment dates', s.sql('SELECT enrolled_at,build_ready_at,enrollment_note,requested_plan FROM bartide_customers WHERE id=?', ('a-ready',))[0] == before)
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool: results = list(pool.map(lambda _: transition('a-race'), range(2)))
    check('Concurrent approvals commit once', sorted(x[0] for x in results) == [200, 409] and s.sql('SELECT version FROM bartide_customers WHERE id=?', ('a-race',))[0][0] == 1)
    profile = {'expectedVersion': 0, 'profile': {'name': 'Edited menu', 'area':'Test','tagline':'','hours':'','website':'','serviceNote':''}}
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        launch_future = pool.submit(transition, 'a-menu-race')
        edit_future = pool.submit(s.call, '/api/v1/tenants/a-menu-race/menu/profile', profile, TOKENS['platform'])
        results = [launch_future.result(), edit_future.result()]
    check('Concurrent menu change invalidates approval version', sorted(x[0] for x in results) == [200, 409], results)
    seed('long-name', menu=json.dumps({'items':[{'id':'service','name':'x'*160}]}))
    long_name = transition('long-name')
    check('Full supported menu item name remains launchable', long_name[0] == 200, long_name[:3])
    seed('offset', enrolled=(now-timedelta(days=8)).astimezone(timezone(timedelta(hours=-4))).isoformat(), due=(now-timedelta(days=1)).astimezone(timezone(timedelta(hours=3))).isoformat())
    check('Explicit timezone offsets honor full seven-day duration', transition('offset')[0] == 200)
    process.terminate(); process.wait(); launch('TideCasa.Api', s.API, env)
    check('Publication and version survive restart', next(x for x in api()[1]['projects'] if x['id']=='a-ready')['version'] == 3)
    for n in range(105): seed('z-project-' + str(n).zfill(3), 'draft')
    page = api()[1]; all_ids = [x['id'] for x in page['projects']]
    check('Large owner list is bounded and paginated', len(all_ids) == 100 and page['nextCursor'] is not None)
    second = api('?after=' + page['nextCursor'])[1]; second_ids = [x['id'] for x in second['projects']]
    check('Pagination exposes remaining projects without overlap', second_ids and not(set(all_ids) & set(second_ids)) and len(all_ids+second_ids) == s.sql('SELECT COUNT(*) FROM bartide_customers')[0][0])
    check('Unknown project returns 404 without mutation', transition('missing')[0] == 404)
    check('Invalid expected version rejected', transition('a-native', version=-1)[0] == 400)
    check('Oversized authenticated transition body rejected', api('/a-native/transition', {'expectedVersion':0,'status':'active','reviewedWithCustomer':True,'extra':'x'*9000})[0] == 413)
    launch('TideCasa.Blazor', WEB, {'Api__BaseUrl':s.API+'/', 'DataProtection__KeysPath':str(s.RUN/'keys')})
    owner, customer, anon = browser('platform'), browser('alice'), browser()
    check('Anonymous review page requires sign in', web(anon, '/owner/launch-review')[0] in (302,303))
    denied = web(customer, '/owner/launch-review')
    check('Customer cannot see platform project list', denied[0] == 403 and 'Synthetic a-native' not in denied[1])
    native, page = form(owner, '/owner/launch-review', '/owner/launch-review/a-native/transition')
    check('Private SSR review is never cached', 'no-store' in page[2].get('Cache-Control',''))
    check('Review page exposes private editor and no email', '/workspace/a-native/menu' in page[1] and 'a-native@example.invalid' not in page[1] and s.PRIVATE not in page[1])
    check('Review page separates publication from external deployment', 'does not deploy a website or change billing' in page[1])
    check('Draft project has no launch form', not any(x['action']=='/owner/launch-review/a-draft/transition' for x in Forms(page[1]).forms))
    missing = {k:v for k,v in native['fields'].items() if k!='__RequestVerificationToken'}
    check('Native launch requires CSRF', web(owner, native['action'], missing)[0] == 400)
    check('Native launch rejects foreign origin', web(owner, native['action'], native['fields'], {'Origin':'https://foreign.example.invalid'})[0] == 400)
    check('Customer cannot post owner launch form', web(customer, native['action'], native['fields'])[0] == 403)
    unchecked = {k:v for k,v in native['fields'].items() if k!='reviewed'}
    check('Native launch requires customer review acknowledgement', 'notice=launch_review_required' in web(owner,native['action'],unchecked)[2].get('Location',''))
    check('Native form body is bounded', web(owner,native['action'],{**native['fields'],'extra':'x'*9000})[0] == 413)
    response = web(owner,native['action'],{**native['fields'],'reviewed':'true'})
    check('Native reviewed approval publishes app', 'notice=saved' in response[2].get('Location','') and s.sql('SELECT status FROM bartide_customers WHERE id=?',('a-native',))[0][0] == 'active')
    check('Native old form cannot repeat approval', 'notice=launch_stale' in web(owner,native['action'],{**native['fields'],'reviewed':'true'})[2].get('Location',''))
    pause, _ = form(owner, '/owner/launch-review', native['action'])
    check('Native pause works', 'notice=saved' in web(owner,pause['action'],pause['fields'])[2].get('Location',''))
    resume, _ = form(owner, '/owner/launch-review', native['action'])
    check('Native resume works', 'notice=saved' in web(owner,resume['action'],resume['fields'])[2].get('Location',''))
    check('Feature does not create payment or notification records', s.sql('SELECT COUNT(*) FROM tide_restaurant_payment_attempts')[0][0] == 0 and s.sql('SELECT COUNT(*) FROM tide_merchant_notification_inbox')[0][0] == 0)
    check('Database foreign keys remain valid', s.sql('PRAGMA foreign_key_check') == [])


if __name__ == '__main__':
    try: run()
    finally:
        for proc in s.PROCESSES:
            if proc.poll() is None:
                proc.terminate()
                try: proc.wait(timeout=20)
                except subprocess.TimeoutExpired: proc.kill(); proc.wait(timeout=10)
        s.PROVIDER.shutdown(); s.PROVIDER.server_close()
        for log in s.LOGS: log.close()
        (s.RUN/'results.json').write_text(json.dumps(s.RESULTS,indent=2),encoding='utf-8')
        print('Evidence: '+str(s.RUN),flush=True)
