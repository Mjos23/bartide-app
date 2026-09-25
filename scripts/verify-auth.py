"""Synthetic authentication/access verification. No real identity provider or customer data."""
import base64
import concurrent.futures
from datetime import datetime, timezone
import hashlib
import http.cookiejar
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import re
import socket
import sqlite3
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]
BUILD_ROOT = Path(os.environ.get('TIDE_TEST_BUILD_ROOT', str(ROOT)))
RUN = ROOT / '.tools/auth-verification' / time.strftime('%Y%m%d-%H%M%S')
RUN.mkdir(parents=True, exist_ok=False)
SDK = ROOT / '.tools/dotnet-10.0.401/dotnet.exe'
DB = RUN / 'synthetic.db'
PASSWORD = 'local-test-password-only'
KEY = 'sb_publishable_localverification000000000'
ALICE, BOB, OWNER = [str(uuid.UUID(int=n)) for n in (101, 102, 103)]
users = {email: {'id': ident, 'email': email, 'email_confirmed_at': '2026-01-01T00:00:00Z',
    'password': PASSWORD, 'is_anonymous': False, 'user_metadata': {'full_name': label, 'role': 'admin'}}
    for email, ident, label in [('alice@example.invalid', ALICE, 'Alice Test'), ('bob@example.invalid', BOB, 'Bob Test'), ('owner@example.invalid', OWNER, 'Owner Test')]}
tokens, requests, results = {}, [], []
state = {'outage': False, 'redirect': False, 'trap': 0, 'expiry': 3600}
processes, logs = [], []

class Provider(BaseHTTPRequestHandler):
    def log_message(self, *args): pass
    def respond(self, status, data=None, headers=None):
        self.send_response(status)
        for name, value in (headers or {}).items(): self.send_header(name, value)
        self.send_header('Content-Type', 'application/json')
        self.end_headers()
        if data is not None: self.wfile.write(json.dumps(data).encode())
    def handle_request(self):
        path = urllib.parse.urlparse(self.path).path
        if path == '/trap':
            state['trap'] += 1; return self.respond(200, {})
        requests.append((self.command, path))
        if self.headers.get('apikey') != KEY: return self.respond(403, {})
        if state['outage']: return self.respond(503, {'error': 'PROVIDER-PRIVATE-DETAIL'})
        if state['redirect']:
            state['redirect'] = False
            return self.respond(302, {}, {'Location': f'http://127.0.0.1:{self.server.server_port}/trap'})
        # JsonContent legitimately uses HTTP/1.1 chunked bodies. Decode them here;
        # BaseHTTPRequestHandler does not do that for a fake provider automatically.
        if self.headers.get('Transfer-Encoding', '').lower() == 'chunked':
            chunks=[]
            while True:
                length=int(self.rfile.readline().split(b';',1)[0].strip(),16)
                if length==0:
                    while self.rfile.readline().strip(): pass
                    break
                chunks.append(self.rfile.read(length)); self.rfile.read(2)
            body=b''.join(chunks)
        else:
            body=self.rfile.read(int(self.headers.get('Content-Length', '0')))
        data = json.loads(body or b'{}')
        email = data.get('email', '')
        user = users.get(email)
        token = self.headers.get('Authorization', '').removeprefix('Bearer ')
        if path == '/auth/v1/user':
            identity = tokens.get(token)
            if identity is None: return self.respond(401, {})
            record = next((u for u in users.values() if u['id'] == identity), None)
            if record is None: return self.respond(401, {})
            if self.command == 'PUT': record['password'] = data.get('password', record['password'])
            return self.respond(200, {k: v for k, v in record.items() if k != 'password'})
        if path == '/auth/v1/logout':
            # Deliberately leave access JWTs valid: the API must enforce its own revocation registry.
            return self.respond(204)
        if path in ('/auth/v1/recover', '/auth/v1/resend'):
            return self.respond(200 if user else 400, {})
        if path == '/auth/v1/signup':
            # Misconfigured confirmation can return a token; signup must still never create an API session.
            return self.respond(200, session(user or users['alice@example.invalid']))
        if path == '/auth/v1/token':
            if not user or user['password'] != data.get('password'): return self.respond(400, {'error': 'invalid_credentials'})
            return self.respond(200, session(user))
        if path == '/auth/v1/verify':
            if not user or data.get('token') != '123456': return self.respond(400, {})
            return self.respond(200, session(user))
        return self.respond(404, {})
    do_GET = do_POST = do_PUT = handle_request

def session(user):
    token = 'synthetic.' + base64.urlsafe_b64encode(uuid.uuid4().bytes).decode().rstrip('=') + '.signature'
    tokens[token] = user['id']
    return {'access_token': token, 'expires_in': state['expiry'], 'refresh_token': 'not-used-or-stored', 'user': user}

def port():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0)); return sock.getsockname()[1]

provider = ThreadingHTTPServer(('127.0.0.1', 0), Provider)
threading.Thread(target=provider.serve_forever, daemon=True).start()
API_PORT, WEB_PORT = port(), port()
API, WEB = f'http://127.0.0.1:{API_PORT}', f'http://127.0.0.1:{WEB_PORT}'

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl): return None
opener = urllib.request.build_opener(NoRedirect())

def call(path, data=None, token=None, base=API, headers=None, client=opener, form=False):
    extra = dict(headers or {})
    if token: extra['Authorization'] = 'Bearer ' + token
    if data is not None:
        body = urllib.parse.urlencode(data).encode() if form else json.dumps(data).encode()
        extra['Content-Type'] = 'application/x-www-form-urlencoded' if form else 'application/json'
    else: body = None
    req = urllib.request.Request(base + path, data=body, headers=extra)
    try: response = client.open(req, timeout=40)
    except urllib.error.HTTPError as error: response = error
    with response:
        text = response.read().decode('utf-8', errors='replace')
        return response.status, text, response.headers

def check(name, condition):
    results.append({'check': name, 'passed': bool(condition)})
    print(('PASS ' if condition else 'FAIL ') + name, flush=True)
    if not condition: raise AssertionError(name)

def sql(query, values=()):
    with sqlite3.connect(DB, timeout=20) as db:
        cursor = db.execute(query, values)
        return cursor.fetchall()

def clear_limits(): sql('DELETE FROM bartide_auth_limits')

def signin(email='alice@example.invalid', password=PASSWORD):
    status, text, _ = call('/api/v1/auth/signin', {'email': email, 'password': password})
    if status != 200:
        print('Synthetic sign-in failure status/body: ' + str(status) + ' ' + text[:500], flush=True)
        print('Synthetic provider request paths: ' + repr(requests[-5:]), flush=True)
    check('Sign-in verified for ' + email.split('@')[0], status == 200)
    return json.loads(text)

def launch(project, listen, extra):
    env = os.environ.copy()
    for key in list(env):
        if any(word in key.upper() for word in ('STRIPE', 'RESEND', 'SUPABASE', 'CLOUDFLARE', 'AUTH__')): env.pop(key)
    env.update({'ASPNETCORE_ENVIRONMENT': 'Development', 'ASPNETCORE_URLS': f'http://127.0.0.1:{listen}',
        'Storage__DatabasePath': str(DB), 'Auth__Enabled': 'true', 'Auth__AllowLocalTestProvider': 'true',
        'Auth__SupabaseUrl': f'http://127.0.0.1:{provider.server_port}', 'Auth__PublishableKey': KEY,
        'Auth__PlatformOwnerUserId': 'supabase:' + OWNER, 'Stripe__CheckoutEnabled': 'false', **extra})
    log = (RUN / (project + '-' + str(len(processes)) + '.log')).open('w', encoding='utf-8'); logs.append(log)
    proc = subprocess.Popen([str(SDK), str(BUILD_ROOT / project / 'bin/Debug/net10.0' / (project + '.dll'))],
        cwd=ROOT / project, env=env, stdout=log, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW)
    processes.append(proc)
    deadline=time.monotonic()+180
    while time.monotonic()<deadline:
        if proc.poll() is not None: raise RuntimeError(project + ' stopped; inspect isolated verification logs')
        try:
            if call('/health', base=f'http://127.0.0.1:{listen}')[0] == 200: return proc
        except (OSError, urllib.error.URLError): pass
        time.sleep(.5)
    raise TimeoutError(project + ' startup timeout')

def tenant(ident, email, user_id, status='active'):
    sql('INSERT INTO bartide_customers(id,slug,email,user_id,name,menu_json,version,status,enrollment_note,created_at,updated_at) VALUES (?,?,?,?,?,\'{}\',0,?,\'\',?,?)',
        (ident, ident, email, user_id, ident, status, '2026-09-20T00:00:00Z', '2026-09-20T00:00:00Z'))

def access(ident, token):
    status, text, _ = call('/api/v1/tenants/' + ident + '/access', token=token)
    return status, json.loads(text) if text else None

try:
    api=launch('TideCasa.Api', API_PORT, {})
    check('Anonymous account access requires authentication', call('/api/v1/account')[0] == 401)
    check('Spoofed identity headers do not authenticate', call('/api/v1/account', headers={'X-User-Id': 'supabase:' + OWNER, 'X-User-Email': 'owner@example.invalid'})[0] == 401)
    check('Legacy browser cookie is not an API credential', call('/api/v1/account', headers={'Cookie': '__Host-bartide_session=synthetic.unregistered.signature'})[0] == 401)
    before=len(requests)
    check('Unregistered JWT-shaped token is rejected', call('/api/v1/account', token='synthetic.unregistered.signature')[0] == 401)
    check('Unregistered token never reaches provider', len(requests) == before)
    check('Invalid sign-in input is rejected', call('/api/v1/auth/signin', {'email': 'invalid', 'password': 'x'})[0] == 400)
    check('Sign-up requires a strong enough password', call('/api/v1/auth/signup', {'email': 'alice@example.invalid', 'password': 'short'})[0] == 400)
    check('Invalid email code is rejected', call('/api/v1/auth/verify', {'email': 'alice@example.invalid', 'code': 'x'})[0] == 400)
    check('Wrong password is rejected', call('/api/v1/auth/signin', {'email': 'alice@example.invalid', 'password': 'incorrect'})[0] == 401)
    alice=signin(); a=alice['accessToken']
    check('User metadata cannot grant owner rights', not alice['user']['isPlatformOwner'])
    check('Stable application identity is derived from verified subject', alice['user']['userId'] == 'supabase:' + ALICE)
    check('Refresh tokens are not returned', 'refreshToken' not in alice)
    check('Only token hash is stored', sql('SELECT token_hash FROM bartide_auth_sessions') == [(hashlib.sha256(a.encode()).hexdigest(),)])
    check('Authenticated account is available', call('/api/v1/account', token=a)[0] == 200)
    clear_limits()
    before_sessions=sql('SELECT COUNT(*) FROM bartide_auth_sessions')[0][0]
    check('Signup is generic and requires verification', call('/api/v1/auth/signup', {'email': 'alice@example.invalid', 'password': PASSWORD})[0] == 202)
    check('Signup response never registers returned provider session', sql('SELECT COUNT(*) FROM bartide_auth_sessions')[0][0] == before_sessions)
    status1, body1, _=call('/api/v1/auth/forgot', {'email': 'alice@example.invalid'})
    status2, body2, _=call('/api/v1/auth/forgot', {'email': 'unknown@example.invalid'})
    check('Password recovery does not enumerate accounts', status1 == status2 == 202 and body1 == body2)
    status, body, _=call('/api/v1/auth/verify', {'email': 'alice@example.invalid', 'code': '123456'})
    check('Verified email can establish a session', status == 200 and json.loads(body)['user']['userId'] == 'supabase:' + ALICE)
    verified_token=json.loads(body)['accessToken']
    tenant('owned-active', 'alice-company@example.invalid', 'supabase:' + ALICE)
    tenant('owned-draft', 'alice-draft@example.invalid', 'supabase:' + ALICE, 'draft')
    tenant('owned-paused', 'alice-paused@example.invalid', 'supabase:' + ALICE, 'paused')
    tenant('foreign', 'bob-company@example.invalid', 'supabase:' + BOB)
    tenant('unbound', 'alice@example.invalid', None)
    tenant('staff-tenant', 'staff-owner@example.invalid', 'supabase:' + BOB)
    sql('INSERT INTO bartide_enhanced_configs(tenant_id,settings_json,version,updated_at) VALUES (?,?,?,?)', ('staff-tenant', '{"enabled":true}', 0, '2026-09-20'))
    tenant('bound-other', 'alice-bound@example.invalid', 'supabase:' + BOB)
    status, data=access('owned-active', a)
    check('Active tenant owner can prepare and edit', status==200 and data['canPrepare'] and data['canEdit'])
    status, data=access('owned-draft', a)
    check('Draft owner can prepare without active operations', status==200 and data['canPrepare'] and not data['canEdit'])
    status, data=access('owned-paused', a)
    check('Paused owner keeps association without edit rights', status==200 and not data['canPrepare'] and not data['canEdit'])
    check('Other businesses remain private', access('foreign', a)[0]==404)
    status, _=access('unbound', a)
    check('Verified matching email can bind an unclaimed workspace', status==200 and sql('SELECT user_id FROM bartide_customers WHERE id=?', ('unbound',))[0][0]=='supabase:'+ALICE)
    sql('INSERT INTO bartide_enhanced_members(id,tenant_id,name,email,user_id,role,active,created_at) VALUES (\'kitchen-a\',\'staff-tenant\',\'Staff Test\',\'alice@example.invalid\',NULL,\'kitchen\',1,\'2026-09-20\')')
    sql('INSERT INTO fit_learners(id,tenant_id,name,email,user_id,active,created_at) VALUES (\'learner-a\',\'staff-tenant\',\'Learner Test\',\'alice@example.invalid\',NULL,1,\'2026-09-20\')')
    status, data=access('staff-tenant', a)
    check('Active staff and learner invitations bind securely', status==200 and data['staffRole']=='kitchen' and data['staffMemberId']=='kitchen-a' and data['learnerId']=='learner-a' and not data['canEdit'])
    for settings in ('{broken', '{"enabled":1}', '{"enabled":"true"}', '{}'):
        sql('UPDATE bartide_enhanced_configs SET settings_json=? WHERE tenant_id=?', (settings, 'staff-tenant'))
        check('Invalid or non-boolean configuration cannot enable staff access: ' + settings, access('staff-tenant', a)[0]==404 and call('/api/v1/account', token=a)[0]==200)
    sql('UPDATE bartide_enhanced_configs SET settings_json=? WHERE tenant_id=?', ('{"enabled":true}', 'staff-tenant'))
    sql('UPDATE bartide_enhanced_members SET active=0 WHERE id=\'kitchen-a\'')
    sql('UPDATE fit_learners SET active=0 WHERE id=\'learner-a\'')
    check('Revoked staff and learner access takes effect immediately', access('staff-tenant', a)[0]==404)
    sql('UPDATE bartide_enhanced_members SET active=1 WHERE id=\'kitchen-a\'')
    sql('UPDATE bartide_customers SET status=\'draft\' WHERE id=\'staff-tenant\'')
    check('Draft tenants do not expose staff operations', access('staff-tenant', a)[0]==404)
    # Changed verified email must never override a previously bound owner.
    users['alice@example.invalid']['email']='alice-bound@example.invalid'
    check('Matching email cannot take over a bound business', access('bound-other', a)[0]==404)
    users['alice@example.invalid']['email']='alice@example.invalid'
    owner=signin('owner@example.invalid')
    check('Explicit pinned app-user ID grants platform owner', owner['user']['isPlatformOwner'] and access('foreign', owner['accessToken'])[1]['canEdit'])
    bob=signin('bob@example.invalid')
    tokens[a]=BOB
    check('Provider subject must match locally registered subject', call('/api/v1/account', token=a)[0]==401)
    tokens[a]=ALICE
    users['alice@example.invalid']['is_anonymous']=True
    check('Anonymous provider identities cannot access accounts', call('/api/v1/account', token=a)[0]==401)
    users['alice@example.invalid']['is_anonymous']=False
    users['alice@example.invalid']['email_confirmed_at']=None
    check('Unconfirmed provider email is denied', call('/api/v1/account', token=a)[0]==401)
    users['alice@example.invalid']['email_confirmed_at']='2026-01-01T00:00:00Z'
    state['outage']=True
    status, body, _=call('/api/v1/account', token=a)
    check('Provider failure closes access without leaking detail', status==503 and 'PROVIDER-PRIVATE-DETAIL' not in body)
    check('Logout works during provider outage', call('/api/v1/auth/signout', {}, token=a)[0]==204)
    state['outage']=False
    check('Logout revokes even a provider-valid token', call('/api/v1/account', token=a)[0]==401)
    sql('UPDATE bartide_auth_sessions SET expires_at=0 WHERE token_hash=?', (hashlib.sha256(verified_token.encode()).hexdigest(),))
    check('Expired local sessions are denied', call('/api/v1/account', token=verified_token)[0]==401)
    clear_limits()
    state['redirect']=True
    check('Provider redirects fail without forwarding credentials', call('/api/v1/auth/signin', {'email':'alice@example.invalid','password':PASSWORD})[0]==503 and state['trap']==0)
    state['expiry']='invalid'
    check('Invalid provider session lifetime is rejected safely', call('/api/v1/auth/signin', {'email':'alice@example.invalid','password':PASSWORD})[0]==502)
    state['expiry']=3600
    a1=signin()['accessToken']; a2=signin()['accessToken']
    status, _, _=call('/api/v1/auth/reset', {'email':'alice@example.invalid','code':'123456','password':'replacement-test-password'})
    check('Verified recovery changes password', status==200)
    check('Recovery revokes every local session', call('/api/v1/account', token=a1)[0]==401 and call('/api/v1/account', token=a2)[0]==401)
    check('Old password no longer signs in', call('/api/v1/auth/signin', {'email':'alice@example.invalid','password':PASSWORD})[0]==401)
    a=signin(password='replacement-test-password')['accessToken']
    clear_limits()
    # Exact approved mappings are reused; ambiguous/unapproved mappings never silently link.
    for name, ident in [('legacy', 104), ('ambiguous', 105), ('unapproved', 106)]:
        email=name+'@example.invalid'
        users[email]={**users['bob@example.invalid'], 'id':str(uuid.UUID(int=ident)), 'email':email}
    sql('INSERT INTO bartide_auth_legacy_claims(email,legacy_user_id,approved) VALUES (?,?,1)', ('legacy@example.invalid','legacy-approved'))
    legacy=signin('legacy@example.invalid')
    check('Approved legacy application ID is preserved', legacy['user']['userId']=='legacy-approved')
    sql('INSERT INTO bartide_auth_legacy_claims(email,legacy_user_id,approved) VALUES (?,?,1)', ('ambiguous@example.invalid','legacy-ambiguous-a'))
    sql('INSERT INTO bartide_auth_legacy_claims(email,legacy_user_id,approved) VALUES (?,?,1)', ('ambiguous@example.invalid','legacy-ambiguous-b'))
    check('Ambiguous legacy claims fail closed', call('/api/v1/auth/signin', {'email':'ambiguous@example.invalid','password':PASSWORD})[0]==409)
    sql('INSERT INTO bartide_auth_legacy_claims(email,legacy_user_id,approved) VALUES (?,?,0)', ('unapproved@example.invalid','legacy-unapproved'))
    check('Unapproved legacy claims fail closed', call('/api/v1/auth/signin', {'email':'unapproved@example.invalid','password':PASSWORD})[0]==409)
    clear_limits()
    codes=[call('/api/v1/auth/signin', {'email':'unknown@example.invalid','password':'incorrect'})[0] for _ in range(21)]
    check('Authentication attempts are bounded persistently', codes[:20]==[401]*20 and codes[20]==429)
    clear_limits()
    api.terminate(); api.wait(timeout=30)
    api=launch('TideCasa.Api', API_PORT, {})
    check('Schema rerun preserves identities and live sessions', call('/api/v1/account', token=a)[0]==200 and sql('SELECT app_user_id FROM bartide_auth_identities WHERE provider_user_id=?',(ALICE,))[0][0]=='supabase:'+ALICE)
    check('Migrated schema passes foreign-key integrity', sql('PRAGMA foreign_key_check')==[])
    check('All original 46 application tables are present', sql("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND (name LIKE 'bartide_%' OR name LIKE 'fit_%' OR name LIKE 'tide_%')")[0][0]>=46)
    # Frontend full HTTP forms exercise the BFF rather than bypassing it with API calls.
    web=launch('TideCasa.Blazor', WEB_PORT, {'Api__BaseUrl':API+'/', 'DataProtection__KeysPath':str(RUN/'keys')})
    jar=http.cookiejar.CookieJar(); browser=urllib.request.build_opener(NoRedirect(), urllib.request.HTTPCookieProcessor(jar))
    status, html, page_headers=call('/signin', base=WEB, client=browser)
    check('Server-rendered login form is available', status==200 and 'password' in html and '__RequestVerificationToken' in html)
    # Native browser form POSTs inherit this policy. no-referrer makes Origin
    # null, unlike urllib requests that explicitly supply an Origin header.
    check('Auth page policy preserves same-origin form submissions without external referrers', page_headers.get('Referrer-Policy')=='same-origin')
    (RUN/'signin.html').write_text(html,encoding='utf-8')
    # Contract-independent form extraction keeps the test anchored to the rendered controls.
    form_match=re.search(r'<form\b([^>]*)>(.*?)</form>',html,re.S)
    action=re.search(r'action="([^"]+)"',form_match.group(1)).group(1)
    hidden=dict(re.findall(r'<input(?=[^>]*type="hidden")(?=[^>]*name="([^"]+)")(?=[^>]*value="([^"]*)")[^>]*>',form_match.group(2)))
    import html as html_module
    hidden={html_module.unescape(k):html_module.unescape(v) for k,v in hidden.items()}
    fields=re.findall(r'<input[^>]+name="([^"]+)"',form_match.group(2))
    email_field=next(n for n in fields if n.lower().endswith('email'))
    password_field=next(n for n in fields if n.lower().endswith('password'))
    payload={**hidden,email_field:'bob@example.invalid',password_field:PASSWORD}
    check('Browser login rejects missing antiforgery', call(action, {email_field:'bob@example.invalid',password_field:PASSWORD}, base=WEB, client=browser, form=True, headers={'Origin':WEB})[0]==400)
    check('Browser login rejects foreign origin', call(action,payload,base=WEB,client=browser,form=True,headers={'Origin':'https://other.invalid'})[0] in (400,403))
    check('Browser login rejects opaque origin', call(action,payload,base=WEB,client=browser,form=True,headers={'Origin':'null'})[0]==400)
    check('Browser login rejects missing origin and referrer', call(action,payload,base=WEB,client=browser,form=True)[0]==400)
    check('Browser login rejects cross-site fetch metadata', call(action,payload,base=WEB,client=browser,form=True,headers={'Origin':WEB,'Sec-Fetch-Site':'cross-site'})[0]==400)
    status, _, headers=call(action,payload,base=WEB,client=browser,form=True,headers={'Origin':WEB})
    check('Valid browser login uses a local redirect', status in (302,303) and headers.get('Location','').startswith('/') and not headers.get('Location','').startswith('//'))
    check('Session cookie is HttpOnly and SameSite', 'httponly' in headers.get('Set-Cookie','').lower() and 'samesite=lax' in headers.get('Set-Cookie','').lower())
    status, account_html, _=call('/account',base=WEB,client=browser)
    check('Signed-in browser sees its account', status==200 and 'Bob Test' in account_html)
    check('Account markup contains no access token', all(token not in account_html for token in tokens))
    check('Cookies contain no raw provider token', all(all(token not in cookie.value for token in tokens) for cookie in jar))
    (RUN/'account.html').write_text(account_html,encoding='utf-8')
    check('Account responses cannot be cached', 'no-store' in call('/account',base=WEB,client=browser)[2].get('Cache-Control',''))
    logout_form=re.search(r'<form\b([^>]*)>(.*?)</form>',account_html,re.S)
    logout_action=html_module.unescape(re.search(r'action="([^"]+)"',logout_form.group(1)).group(1))
    logout_fields={html_module.unescape(k):html_module.unescape(v) for k,v in re.findall(r'<input(?=[^>]*type="hidden")(?=[^>]*name="([^"]+)")(?=[^>]*value="([^"]*)")[^>]*>',logout_form.group(2))}
    state['outage']=True
    status, _, headers=call(logout_action,logout_fields,base=WEB,client=browser,form=True,headers={'Origin':WEB})
    check('Browser logout clears the session during a provider outage', status in (302,303) and not any(c.name=='TideCasa.Auth' for c in jar))
    state['outage']=False
    check('Signed-out browser cannot reopen its account', call('/account',base=WEB,client=browser)[0] in (302,303,401))
    # A return path supplied by the caller must never take the browser off-site.
    status, html, _=call('/signin?return_to='+urllib.parse.quote('https://other.invalid/steal'),base=WEB,client=browser)
    form_match=re.search(r'<form\b([^>]*)>(.*?)</form>',html,re.S)
    action=html_module.unescape(re.search(r'action="([^"]+)"',form_match.group(1)).group(1))
    hidden={html_module.unescape(k):html_module.unescape(v) for k,v in re.findall(r'<input(?=[^>]*type="hidden")(?=[^>]*name="([^"]+)")(?=[^>]*value="([^"]*)")[^>]*>',form_match.group(2))}
    payload={**hidden,'email':'bob@example.invalid','password':PASSWORD,'return_to':'//other.invalid/steal'}
    status, _, headers=call(action,payload,base=WEB,client=browser,form=True,headers={'Origin':WEB})
    check('Untrusted browser return path stays on the account page', status in (302,303) and headers.get('Location')=='/account')
    state['outage']=True
    status, body, _=call('/account',base=WEB,client=browser)
    check('Browser account fails closed when provider verification is unavailable', status==503 and 'Bob Test' not in body and not any(c.name=='TideCasa.Auth' for c in jar))
    state['outage']=False
    status, recovery_html, recovery_headers=call('/forgot-password',base=WEB,client=browser)
    check('Recovery page preserves same-origin form submissions', status==200 and recovery_headers.get('Referrer-Policy')=='same-origin')
    recovery_form=re.search(r'<form\b([^>]*)>(.*?)</form>',recovery_html,re.S)
    recovery_action=html_module.unescape(re.search(r'action="([^"]+)"',recovery_form.group(1)).group(1))
    recovery_fields={html_module.unescape(k):html_module.unescape(v) for k,v in re.findall(r'<input(?=[^>]*type="hidden")(?=[^>]*name="([^"]+)")(?=[^>]*value="([^"]*)")[^>]*>',recovery_form.group(2))}
    recovery_fields['email']='bob@example.invalid'
    before=len(requests)
    status, _, recovery_redirect=call(recovery_action,recovery_fields,base=WEB,client=browser,form=True,headers={'Origin':WEB,'Sec-Fetch-Site':'same-origin'})
    check('Recovery form reaches provider and opens code entry', status in (302,303) and recovery_redirect.get('Location','').startswith('/reset-password?notice=reset-email') and ('POST','/auth/v1/recover') in requests[before:])
    check('Recovery form does not expose email in redirect', 'bob' not in recovery_redirect.get('Location',''))
    status, reset_html, _=call('/reset-password',base=WEB,client=browser)
    reset_form=re.search(r'<form\b([^>]*)>(.*?)</form>',reset_html,re.S)
    reset_action=html_module.unescape(re.search(r'action="([^"]+)"',reset_form.group(1)).group(1))
    reset_fields={html_module.unescape(k):html_module.unescape(v) for k,v in re.findall(r'<input(?=[^>]*type="hidden")(?=[^>]*name="([^"]+)")(?=[^>]*value="([^"]*)")[^>]*>',reset_form.group(2))}
    reset_fields.update(email='alice@example.invalid',code='123456',password='confirmed-reset-password')
    prior_password=users['alice@example.invalid']['password']
    for label, extra in [('missing',{}),('empty',{'confirm_password':''}),('different',{'confirm_password':'different-test-password'}),('case mismatch',{'confirm_password':'Confirmed-reset-password'})]:
        before=len(requests)
        status, _, rejected=call(reset_action,{**reset_fields,**extra},base=WEB,client=browser,form=True,headers={'Origin':WEB})
        check('Password reset rejects '+label+' confirmation without contacting provider', status in (302,303) and rejected.get('Location','').startswith('/reset-password?notice=password-mismatch') and len(requests)==before and users['alice@example.invalid']['password']==prior_password)
    status, mismatch_html, _=call(rejected['Location'],base=WEB,client=browser)
    check('Confirmation error explains the mismatch without exposing password or code', status==200 and 'Passwords don’t match' in html_module.unescape(mismatch_html) and reset_fields['password'] not in mismatch_html and reset_fields['code'] not in mismatch_html)
    confirmation=re.search(r'<input\b[^>]*name="confirm_password"[^>]*>',reset_html)
    check('Reset form requires a masked confirmation with password autofill support', confirmation is not None and all(part in confirmation.group(0) for part in ['type="password"','autocomplete="new-password"','minlength="12"','maxlength="128"','required']))
    status, _, completed=call(reset_action,{**reset_fields,'confirm_password':reset_fields['password']},base=WEB,client=browser,form=True,headers={'Origin':WEB})
    check('Matching reset confirmation updates password and returns to sign-in', status in (302,303) and completed.get('Location','').startswith('/signin?notice=password-reset') and users['alice@example.invalid']['password']==reset_fields['password'])
    from customer_entry_checks import run as run_customer_entry_checks
    run_customer_entry_checks(globals())
    if '--browser-preview' in sys.argv:
        # Optional real-browser checks use only this isolated fake-provider setup.
        # Create browser-finished.flag in the evidence directory to cleanly stop.
        # Cookies are host-scoped, not port-scoped: use localhost for this
        # synthetic browser session while the owner's preview uses 127.0.0.1.
        browser_web=f'http://localhost:{WEB_PORT}'
        (RUN/'browser-preview.json').write_text(json.dumps({'web':browser_web,'api':API}),encoding='utf-8')
        print('Synthetic browser preview: '+browser_web+'; evidence: '+str(RUN),flush=True)
        deadline=time.monotonic()+900
        while time.monotonic()<deadline and not (RUN/'browser-finished.flag').exists(): time.sleep(.5)
finally:
    for proc in processes:
        if proc.poll() is None:
            proc.terminate()
            try: proc.wait(timeout=30)
            except subprocess.TimeoutExpired: proc.kill(); proc.wait(timeout=10)
    provider.shutdown(); provider.server_close()
    for log in logs: log.close()
    (RUN/'results.json').write_text(json.dumps(results,indent=2),encoding='utf-8')
    print('Authentication evidence: '+str(RUN),flush=True)
print(f'{len(results)} authentication/access checks passed; no real provider calls or deployment.',flush=True)
