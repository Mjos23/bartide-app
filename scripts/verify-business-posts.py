"""Business posts and consent-based Web Push through real API/SQLite/Blazor.

Only identity and the encrypted push-service boundary are loopback substitutes.
All identities, subscriptions, EC keys and posts are synthetic. Node's built-in
crypto independently decrypts RFC 8291 payloads. No remote notification is sent.
Build both projects first; optional TIDE_TEST_BUILD_ROOT selects frozen binaries.
"""
import base64
import concurrent.futures
from datetime import datetime, timedelta, timezone
import hashlib
import html
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import importlib.util
import json
import os
from pathlib import Path
import platform
import re
import shutil
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

import restaurant_test_support as s

s.RUN = s.ROOT / '.tools/business-posts-verification' / datetime.now(timezone.utc).strftime('%Y%m%d-%H%M%S-%f')
s.RUN.mkdir(parents=True); s.DB = s.RUN / 'synthetic.db'
RUN, DB, check = s.RUN, s.DB, s.check
WEB = 'http://127.0.0.1:' + str(s.port())
TOKENS = {}
spec = importlib.util.spec_from_file_location('business_post_forms', Path(__file__).with_name('verify-management-web.py'))
forms_module = importlib.util.module_from_spec(spec); spec.loader.exec_module(forms_module)
forms_module.WEB = WEB
web, browser, forms_for, find_form, post, login = (getattr(forms_module, name) for name in ('web', 'browser', 'forms_for', 'find_form', 'post', 'login'))

# NIST P-256's generator is the public key for synthetic private scalar one.
PUB_BYTES = bytes.fromhex('04' + '6b17d1f2e12c4247f8bce6e563a440f277037d812deb33a0f4a13945d898c296' +
                         '4fe342e2fe1a7f9b8ee7eb4a7c0f9e162bce33576b315ececbb6406837bf51f5')
b64 = lambda value: base64.urlsafe_b64encode(value).decode().rstrip('=')
PUB, PRIVATE, AUTH = b64(PUB_BYTES), b64(bytes(31) + b'\x01'), b64(b'synthetic-auth16')
WIRE_LOCK, WIRE, PLANS, GATES = threading.Lock(), [], {}, {}


class PushService(BaseHTTPRequestHandler):
    def log_message(self, *args): pass
    def do_POST(self):
        raw = self.rfile.read(int(self.headers.get('Content-Length', 0)))
        if self.headers.get('Transfer-Encoding', '').lower() == 'chunked':
            chunks = []
            while True:
                length = int(self.rfile.readline().split(b';', 1)[0], 16)
                if not length:
                    while self.rfile.readline().strip(): pass
                    break
                chunks.append(self.rfile.read(length)); self.rfile.read(2)
            raw = b''.join(chunks)
        with WIRE_LOCK:
            WIRE.append({'path': self.path, 'body': base64.b64encode(raw).decode(), 'headers': dict(self.headers)})
            planned = PLANS.get(self.path, [])
            status = planned.pop(0) if planned else 201
        if status == 'hold':
            gate = GATES[self.path]; gate['entered'].set(); gate['release'].wait(timeout=30); status = 201
        try:
            self.send_response(status)
            if status == 429: self.send_header('Retry-After', '1')
            self.end_headers()
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError, OSError): pass


PUSH = ThreadingHTTPServer(('127.0.0.1', 0), PushService)
threading.Thread(target=PUSH.serve_forever, daemon=True).start()
PUSH_ORIGIN = f'http://127.0.0.1:{PUSH.server_port}'


def runtime_ignores(directory, names):
    ignored = {name for name in names if name.endswith('.pdb')}
    if os.name == 'nt' and platform.machine().lower() in ('amd64', 'x86_64') and Path(directory).name == 'runtimes':
        ignored.update(name for name in names if name not in ('win', 'win-x64'))
    return ignored


def push_options(**extra):
    return {'WebPush__Enabled': 'true', 'WebPush__VapidSubject': 'mailto:synthetic@example.invalid',
        'WebPush__VapidPublicKey': PUB, 'WebPush__VapidPrivateKey': PRIVATE,
        'WebPush__AllowDevelopmentLoopback': 'true', 'WebPush__DevelopmentPushOrigin': PUSH_ORIGIN,
        'WebPush__DevelopmentDispatchMilliseconds': '250', **extra}


def launch(project='TideCasa.Api', address=None, extra=None, label=None, expect_failure=False):
    address = address or s.API
    copied = RUN / project
    if not copied.exists():
        source = Path(os.environ.get('TIDE_TEST_BUILD_ROOT', str(s.ROOT))) / project / 'bin/Debug/net10.0'
        shutil.copytree(source, copied, ignore=runtime_ignores)
    env = os.environ.copy()
    for name in list(env):
        if any(x in name.upper() for x in ('STRIPE', 'RESEND', 'SUPABASE', 'CLOUDFLARE', 'AUTH__', 'STORAGE__',
            'API__', 'DATAPROTECTION__', 'MERCHANTPAYMENTS__', 'SERVICEBILLING__', 'NOTIFICATIONS__', 'MEDIA__', 'WEBPUSH__')):
            env.pop(name)
    env.update({'ASPNETCORE_ENVIRONMENT': 'Development', 'ASPNETCORE_URLS': address,
        'Storage__DatabasePath': str(DB), 'Auth__Enabled': 'true', 'Auth__AllowLocalTestProvider': 'true',
        'Auth__SupabaseUrl': f'http://127.0.0.1:{s.PROVIDER.server_port}', 'Auth__PublishableKey': s.KEY,
        'Auth__PlatformOwnerUserId': 'supabase:' + s.PLATFORM, 'ReverseProxy__KnownClientProxy': '127.0.0.1',
        'Stripe__CheckoutEnabled': 'false', 'MerchantPayments__CheckoutEnabled': 'false',
        'MerchantPayments__OnboardingEnabled': 'false', 'ServiceBilling__CheckoutEnabled': 'false',
        'Notifications__Mode': 'disabled', 'WebPush__Enabled': 'false', **(extra or {})})
    env['DOTNET_ENVIRONMENT'] = env['ASPNETCORE_ENVIRONMENT']
    label = label or (project + '-' + str(len(s.PROCESSES)))
    logfile = RUN / (label + '.log'); log = logfile.open('w', encoding='utf-8'); s.LOGS.append(log)
    proc = subprocess.Popen([str(s.SDK), str(copied / (project + '.dll'))], cwd=s.ROOT / project,
        env=env, stdout=log, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
    s.PROCESSES.append(proc)
    if expect_failure:
        try: proc.wait(timeout=20)
        except subprocess.TimeoutExpired: raise AssertionError('Unsafe configuration stayed running: ' + label)
        check('Unsafe push configuration rejected: ' + label, proc.returncode != 0)
        return proc
    deadline = time.monotonic() + 80
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    while time.monotonic() < deadline:
        if proc.poll() is not None: raise RuntimeError('Fixture stopped: ' + str(logfile))
        try:
            with opener.open(address + '/health', timeout=2) as result:
                if result.status == 200: return proc
        except (OSError, urllib.error.URLError): pass
        time.sleep(.2)
    raise TimeoutError(label)


def stop(proc):
    if proc.poll() is None:
        proc.terminate()
        try: proc.wait(timeout=15)
        except subprocess.TimeoutExpired: proc.kill(); proc.wait(timeout=10)


def call(path, body=None, person='alice', method=None, headers=None):
    hdrs = {'X-Forwarded-For': '192.0.2.' + str(1 + (uuid.uuid4().int % 249)), **(headers or {})}
    if person: hdrs['Authorization'] = 'Bearer ' + TOKENS[person]
    raw = None
    if body is not None: raw = json.dumps(body).encode(); hdrs['Content-Type'] = 'application/json'
    request = urllib.request.Request(s.API + path, data=raw, headers=hdrs, method=method)
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), s.NoRedirect())
    try: response = opener.open(request, timeout=25)
    except urllib.error.HTTPError as error: response = error
    with response:
        text = response.read().decode()
        try: parsed = json.loads(text)
        except json.JSONDecodeError: parsed = None
        return response.status, parsed, text, response.headers


def owner(tenant='bistro'): return '/api/v1/tenants/' + tenant + '/posts'
def public(tenant='bistro'): return '/api/v1/restaurants/' + tenant + '/posts'
def create(tenant='bistro', person='alice', **changes):
    body = {'requestKey': str(uuid.uuid4()), 'title': 'Late-night music', 'body': 'Kitchen open until midnight.\nPay from your table.', **changes}
    return call(owner(tenant), body, person), body
def transition(ident, action='publish', version=0, tenant='bistro', person='alice', key=None):
    return call(owner(tenant) + '/' + ident + '/' + action, {'requestKey': key or str(uuid.uuid4()), 'expectedVersion': version}, person)
def new_post(tenant='bistro', **changes):
    response, _ = create(tenant, **changes); check('Synthetic draft saved', response[0] == 200, response[:3]); return response[1]
def subscription(path, tenant='bistro', person='bob', **changes):
    return call(public(tenant) + '/subscriptions', {'endpoint': PUSH_ORIGIN + path, 'p256dh': PUB, 'auth': AUTH, 'consent': True, **changes}, person)
def mine(tenant='bistro', person='bob'): return call(public(tenant) + '/subscriptions/mine', person=person)
def remove(ident, tenant='bistro', person='bob'): return call(public(tenant) + '/subscriptions/' + ident, person=person, method='DELETE')
def rows(statement, args=()): return s.sql(statement, args)
def stamp(seconds=0): return (datetime.now(timezone.utc) + timedelta(seconds=seconds)).isoformat()
def state(post_id, subscription_id):
    values = rows('SELECT state,attempts,lease_key,last_status FROM tide_push_outbox WHERE post_id=? AND subscription_id=?', (post_id, subscription_id))
    return values[0] if values else None
def requests(path):
    with WIRE_LOCK: return [dict(item) for item in WIRE if item['path'] == path]
def wait_for(label, predicate, timeout=12):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        found = predicate()
        if found: return found
        time.sleep(.1)
    raise AssertionError('Timed out: ' + label)
def due(post_id): rows('UPDATE tide_push_outbox SET next_attempt_at=? WHERE post_id=?', (stamp(-5), post_id))


DECRYPT = r'''
const c=require('node:crypto');let input='';process.stdin.on('data',d=>input+=d);process.stdin.on('end',()=>{
 const a=JSON.parse(input),b=Buffer.from(a.body,'base64'),salt=b.subarray(0,16),rs=b.readUInt32BE(16),n=b[20],server=b.subarray(21,21+n),enc=b.subarray(21+n);
 const ec=c.createECDH('prime256v1');ec.setPrivateKey(Buffer.from(a.private,'base64url'));
 const pub=ec.getPublicKey(),shared=ec.computeSecret(server),info=Buffer.concat([Buffer.from('WebPush: info\0'),pub,server]);
 const ikm=Buffer.from(c.hkdfSync('sha256',shared,Buffer.from(a.auth,'base64url'),info,32));
 const cek=c.hkdfSync('sha256',ikm,salt,Buffer.from('Content-Encoding: aes128gcm\0'),16),nonce=c.hkdfSync('sha256',ikm,salt,Buffer.from('Content-Encoding: nonce\0'),12);
 const d=c.createDecipheriv('aes-128-gcm',cek,nonce);d.setAuthTag(enc.subarray(enc.length-16));
 let plain=Buffer.concat([d.update(enc.subarray(0,enc.length-16)),d.final()]);let end=plain.length-1;while(plain[end]===0)end--;if(plain[end]!==2)throw Error('missing final record delimiter');
 const v=a.authorization.match(/^vapid t=([^, ]+), ?k=([^, ]+)$/i);if(!v)throw Error('missing VAPID');const bits=v[1].split('.'),vp=Buffer.from(v[2],'base64url');
 const key=c.createPublicKey({format:'jwk',key:{kty:'EC',crv:'P-256',x:vp.subarray(1,33).toString('base64url'),y:vp.subarray(33).toString('base64url')}});
 const valid=c.verify('sha256',Buffer.from(bits[0]+'.'+bits[1]),{key,dsaEncoding:'ieee-p1363'},Buffer.from(bits[2],'base64url'));
 console.log(JSON.stringify({payload:JSON.parse(plain.subarray(0,end).toString()),recordSize:rs,keyLength:n,vapidValid:valid,claims:JSON.parse(Buffer.from(bits[1],'base64url').toString())}));
});'''


def decrypt(request):
    hdr = {key.lower(): value for key, value in request['headers'].items()}
    outcome = subprocess.run(['node', '-e', DECRYPT], input=json.dumps({'body': request['body'], 'private': PRIVATE,
        'auth': AUTH, 'authorization': hdr.get('authorization', '')}), text=True, capture_output=True, timeout=15)
    if outcome.returncode: raise AssertionError('Independent RFC8291 decoder rejected synthetic wire: ' + outcome.stderr[:1000])
    return json.loads(outcome.stdout)


def run():
    api = launch(label='api-push-disabled')
    for person in ('alice', 'bob', 'staff', 'platform'):
        response = s.call('/api/v1/auth/signin', {'email': person + '@example.invalid', 'password': s.PASSWORD})
        check('Verified synthetic sign in ' + person, response[0] == 200); TOKENS[person] = response[1]['accessToken']
    TOKENS['unverified'] = 'synthetic.unverified.signature'
    s.TOKENS[TOKENS['unverified']] = {**s.USERS['bob@example.invalid'],'email_confirmed_at':None}
    for ident, own, status in [('bistro',s.ALICE,'active'),('foreign',s.BOB,'active'),('draft',s.ALICE,'draft'),('paused',s.ALICE,'paused'),('disabled',s.ALICE,'active')]:
        s.seed(ident, own, status, ident != 'disabled')
    baseline = rows('SELECT * FROM bartide_customers ORDER BY id')
    check('Additive posts and consent/outbox tables exist', all(rows("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=?", (name,))[0][0] == 1 for name in ('tide_business_posts','tide_business_post_requests','tide_push_subscriptions','tide_push_outbox')))
    check('Public active feed works without authentication', call(public(), person=None)[0] == 200)
    check('Push availability truthfully remains disabled', call(public(), person=None)[1]['pushAvailable'] is False and call(public(), person=None)[1]['vapidPublicKey'] is None)
    for tenant in ('draft','paused','disabled','missing'):
        check('Unavailable business has no public feed: ' + tenant, call(public(tenant), person=None)[0] == 404)
    check('Owner workspace requires authentication', call(owner(), person=None)[0] == 401)
    for person in ('bob','staff'):
        check('Other identities cannot read owner workspace: ' + person, call(owner(), person=person)[0] == 403)
        check('Other identities cannot create posts: ' + person, create(person=person)[0][0] == 403)
    for tenant in ('draft','paused','disabled'):
        check('Private owner can inspect inactive workspace: ' + tenant, call(owner(tenant))[0] == 200)
        check('Inactive workspace truthfully disables publishing controls: ' + tenant, call(owner(tenant))[1]['canPublish'] is False)
        check('Inactive workspace cannot create a post: ' + tenant, create(tenant)[0][0] in (404,409))
    check('Unverified subscription reads require authentication', mine(person=None)[0] == 401)
    check('Unverified email cannot subscribe', subscription('/push/unverified',person='unverified')[0] in (401,403))
    check('Disabled push cannot collect new subscriptions', subscription('/push/disabled',endpoint='https://fcm.googleapis.com/fcm/send/synthetic-disabled')[0] == 503)
    invalid = [{'title':''},{'body':''},{'title':'x'*121},{'body':'x'*2001},{'title':'control\u0000'},{'requestKey':'bad'}]
    for item in invalid:
        check('Post validation rejects ' + next(iter(item)) + ' length ' + str(len(str(next(iter(item.values()))))), create(**item)[0][0] == 400)
    check('API post payload bounded', create(extra='x'*20000)[0][0] == 413)
    response, request = create(title='<script>alert(1)</script>',body='Plain text <img src=x onerror=alert(1)>\nLate-night offers.')
    check('Post draft stores plain text', response[0] == 200 and response[1]['state'] == 'draft', response[:3]); draft = response[1]
    check('Draft is not in public feed', call(public(), person=None)[1]['posts'] == [])
    with concurrent.futures.ThreadPoolExecutor(max_workers=3) as pool:
        repeats = list(pool.map(lambda _:call(owner(),request),range(3)))
    check('Create retry persists one logical draft', all(x[0] == 200 and x[1]['id'] == draft['id'] for x in repeats) and rows('SELECT COUNT(*) FROM tide_business_posts')[0][0] == 1)
    check('Create request key with changed body conflicts', call(owner(), {**request,'body':'changed'})[0] == 409)
    check('Foreign owner cannot publish tenant post', transition(draft['id'],person='bob')[0] == 403)
    check('Wrong tenant cannot publish post', transition(draft['id'],tenant='foreign',person='bob')[0] == 404)
    key = str(uuid.uuid4())
    published = transition(draft['id'],key=key)
    check('Explicit publish changes draft once', published[0] == 200 and published[1]['state'] == 'published' and published[1]['version'] == 1, published[:3])
    check('Publish retry with same request key is idempotent', transition(draft['id'],key=key)[0] == 200)
    check('Stale publish with different key conflicts', transition(draft['id'])[0] == 409)
    feed = call(public(), person=None)
    check('Public feed only discloses designed plain-text fields', len(feed[1]['posts']) == 1 and set(feed[1]['posts'][0]) == {'id','title','body','state','version','createdAt','publishedAt'} and s.PRIVATE not in feed[2] and '@example.invalid' not in feed[2])
    check('Disabled push queues no historical notification work', rows('SELECT COUNT(*) FROM tide_push_outbox')[0][0] == 0 and WIRE == [])
    check('Owner responses prevent caching', 'no-store' in call(owner())[3].get('Cache-Control',''))

    launch('TideCasa.Blazor',WEB,{'Api__BaseUrl':s.API+'/','DataProtection__KeysPath':str(RUN/'keys')},'web')
    anon, alice, bob = browser(), login('alice'), login('bob')
    rendered = web('/updates/bistro', client=anon)
    check('Public update page safely HTML encodes customer content', rendered[0] == 200 and '<script>alert(1)</script>' not in rendered[1] and '&lt;script&gt;alert(1)&lt;/script&gt;' in rendered[1] and '<img src=x onerror=alert(1)>' not in rendered[1])
    check('Anonymous post-management page requires sign-in', web('/workspace/bistro/posts',client=anon)[0] in (302,303,401))
    check('Other owner cannot see management page', web('/workspace/bistro/posts',client=bob)[0] == 403)
    inactive = web('/workspace/paused/posts',client=alice)
    check('Paused owner page disables creation and hides publish actions', inactive[0] == 200 and re.search(r'<fieldset\s+disabled(?:="[^"]*")?>',inactive[1]) is not None and not any(f['action'].endswith('/publish') for f in forms_module.Forms(inactive[1]).forms))
    native, page = forms_for(alice,'/workspace/bistro/posts')
    check('Private post page prevents caching', 'no-store' in page[2].get('Cache-Control',''))
    create_form = find_form(native,'/create')
    check('Native post create rejects foreign origin', post(alice,create_form,{'title':'Foreign','body':'Rejected'},headers={'Origin':'https://foreign.example.invalid'})[0] == 400)
    no_csrf = post(alice,create_form,{'title':'No CSRF','body':'Rejected'},remove=('__RequestVerificationToken',))
    check('Native post create rejects missing CSRF', no_csrf[0] == 400)
    check('Native post form is bounded', post(alice,create_form,{'title':'Large','body':'x'*18000})[0] in (400,413))
    created = post(alice,create_form,{'title':'Native late-night update','body':'Pay at your table.'})
    check('Native owner form creates draft', created[0] in (302,303) and rows("SELECT COUNT(*) FROM tide_business_posts WHERE title='Native late-night update' AND state='draft'")[0][0] == 1, created[:2])
    native_id = rows("SELECT id FROM tide_business_posts WHERE title='Native late-night update'")[0][0]
    native, _ = forms_for(alice,'/workspace/bistro/posts'); publish_form = find_form(native,'/'+native_id+'/publish')
    before = rows('SELECT version FROM tide_business_posts WHERE id=?',(native_id,))[0][0]
    rejected = post(alice,publish_form,remove=('confirm_publish',))
    check('Native publication needs explicit acknowledgement', rejected[0] in (302,303) and rows('SELECT version FROM tide_business_posts WHERE id=?',(native_id,))[0][0] == before)
    check('Native publish succeeds with explicit acknowledgement', post(alice,publish_form,{'confirm_publish':'true'})[0] in (302,303) and rows('SELECT state FROM tide_business_posts WHERE id=?',(native_id,))[0][0] == 'published')
    native, _ = forms_for(alice,'/workspace/bistro/posts'); hide_form = find_form(native,'/'+native_id+'/hide')
    check('Native hide removes post from public feed', post(alice,hide_form)[0] in (302,303) and all(item['id'] != native_id for item in call(public(),person=None)[1]['posts']))
    discard = new_post(title='Unused owner draft')
    check('Owner can discard an unpublished draft without notification', transition(discard['id'],'hide')[0] == 200 and rows('SELECT state FROM tide_business_posts WHERE id=?',(discard['id'],))[0][0] == 'hidden' and rows('SELECT COUNT(*) FROM tide_push_outbox WHERE post_id=?',(discard['id'],))[0][0] == 0)
    check('Original tenant/menu/contact records remain unchanged', rows('SELECT * FROM bartide_customers ORDER BY id') == baseline)
    history = rows('SELECT * FROM tide_feature_migrations ORDER BY version')
    check('Eighth additive migration has recorded source checksum', history[-1][0] == 8 and history[-1][2] == hashlib.sha256((s.ROOT/'TideCasa.Api/FeatureMigrations/0008_business_posts.sql').read_bytes()).hexdigest())
    stop(api)
    api = launch(extra=push_options(),label='api-push-enabled')
    check('Restart preserves published and hidden state', call(public(),person=None)[1]['posts'][0]['id'] == draft['id'] and rows('SELECT state FROM tide_business_posts WHERE id=?',(native_id,))[0][0] == 'hidden')
    check('Restart does not reapply or rewrite migration history', rows('SELECT * FROM tide_feature_migrations ORDER BY version') == history)
    check('Configured feed exposes only public VAPID key', call(public(),person=None)[1]['pushAvailable'] is True and call(public(),person=None)[1]['vapidPublicKey'] == PUB and PRIVATE not in call(public(),person=None)[2])
    check('Enabling push does not backfill old posts', rows('SELECT COUNT(*) FROM tide_push_outbox')[0][0] == 0 and WIRE == [])
    run_push(api, alice, bob)
    check('Business post foreign keys remain valid', rows('PRAGMA foreign_key_check') == [])


def run_push(api, alice, bob):
    check('Anonymous browser cannot subscribe', subscription('/push/no-auth',person=None)[0] == 401)
    check('Subscription requires explicit consent', subscription('/push/no-consent',consent=False)[0] == 400)
    for label, value in [('private-ip','https://192.168.1.1/push/x'),('metadata','http://169.254.169.254/latest/meta-data/'),
        ('host-suffix','https://fcm.googleapis.com.evil.example/push/x'),('wrong-scheme','http://fcm.googleapis.com/push/x'),
        ('nonstandard-port','https://fcm.googleapis.com:8443/push/x'),('userinfo','https://user:secret@fcm.googleapis.com/push/x'),
        ('fragment','https://fcm.googleapis.com/push/x#fragment'),('trailing-dot','https://fcm.googleapis.com./push/x'),
        ('wrong-loopback',f'http://127.0.0.1:{s.port()}/push/x'),('localhost','http://localhost/push/x'),
        ('backslash','https://fcm.googleapis.com\\@evil.example/push/x'),('oversize','https://fcm.googleapis.com/push/'+('x'*2100))]:
        check('Subscription blocks SSRF endpoint: ' + label, subscription('/push/bad',endpoint=value)[0] == 400)
    for label, change in [('short point',{'p256dh':b64(b'x'*64)}),('invalid curve point',{'p256dh':b64(b'\x04'+bytes(64))}),
        ('short auth',{'auth':b64(b'x'*15)}),('oversize key',{'p256dh':'x'*101}),('invalid alphabet',{'auth':'+'*22})]:
        check('Subscription validates cryptographic material: ' + label, subscription('/push/bad',**change)[0] == 400)
    for tenant in ('draft','paused','disabled'):
        check('Cannot follow inactive business: ' + tenant, subscription('/push/inactive',tenant=tenant)[0] == 404)
    canonical_endpoint = 'https://fcm.googleapis.com/fcm/send/synthetic-canonical'
    canonical = subscription('/unused',endpoint='https://FCM.GOOGLEAPIS.COM:443/fcm/send/synthetic-canonical')
    check('Endpoint normalization yields stable browser fingerprint', canonical[0] == 200 and canonical[1]['endpointHash'] == hashlib.sha256(canonical_endpoint.encode()).hexdigest(),canonical[:3])
    check('Equivalent endpoint spelling cannot bypass account ownership', subscription('/unused',person='alice',endpoint=canonical_endpoint)[0] == 409)
    check('Canonical endpoint persisted once', rows('SELECT endpoint FROM tide_push_subscriptions WHERE id=?',(canonical[1]['id'],))[0][0] == canonical_endpoint)
    encoded_alias=subscription('/unused',person='alice',endpoint='https://fcm.googleapis.com/fcm/send/synthetic-%63anonical')
    check('Encoded unreserved endpoint alias cannot bypass account ownership',encoded_alias[0] == 409)
    remove(canonical[1]['id'])
    reserved=subscription('/push/reserved%2fcase')
    expected_reserved=hashlib.sha256((PUSH_ORIGIN+'/push/reserved%2Fcase').encode()).hexdigest()
    check('Reserved endpoint escape casing has one stable fingerprint',reserved[0] == 200 and reserved[1]['endpointHash'] == expected_reserved)
    check('Equivalent reserved escape casing cannot bypass endpoint ownership',subscription('/push/reserved%2Fcase',person='alice')[0] == 409)
    remove(reserved[1]['id'])
    check('Canonicalization test made no external notification request', WIRE == [])
    first = subscription('/push/main')
    check('Verified consenting customer can follow venue', first[0] == 200 and first[1]['active'] is True, first[:3]); sid = first[1]['id']
    expected_hash = hashlib.sha256((PUSH_ORIGIN+'/push/main').encode()).hexdigest()
    check('Subscription receipt excludes endpoint and encryption secrets', set(first[1]) == {'id','endpointHash','active','createdAt'} and first[1]['endpointHash'] == expected_hash and PUSH_ORIGIN not in first[2] and AUTH not in first[2] and PUB not in first[2])
    with concurrent.futures.ThreadPoolExecutor(max_workers=3) as pool:
        duplicates = list(pool.map(lambda _:subscription('/push/main'),range(3)))
    check('Concurrent opt-in creates one subscription without generation churn', all(x[0] == 200 and x[1]['id'] == sid for x in duplicates) and rows('SELECT generation FROM tide_push_subscriptions WHERE id=?',(sid,))[0][0] == 0)
    check('Different account cannot take over browser endpoint', subscription('/push/main',person='alice')[0] == 409)
    check('Only subscriber can list its endpoints', mine()[1]['subscriptions'][0]['id'] == sid and mine(person='alice')[1]['subscriptions'] == [] and mine(person='platform')[1]['subscriptions'] == [])
    check('Subscription list prevents caching', 'no-store' in mine()[3].get('Cache-Control',''))
    check('Owner cannot unsubscribe another person', remove(sid,person='alice')[0] == 404 and remove(sid,person='platform')[0] == 404)
    check('Foreign venue cannot unsubscribe original endpoint', remove(sid,tenant='foreign')[0] == 404)
    foreign = subscription('/push/main',tenant='foreign')
    check('Same device can independently follow two venues', foreign[0] == 200 and foreign[1]['id'] != sid)
    extras = [subscription('/push/limit-'+str(n)) for n in range(4)]
    check('Customer can follow on five devices per venue', all(x[0] == 200 for x in extras) and len(mine()[1]['subscriptions']) == 5)
    check('Sixth device rejected without persisting a row', subscription('/push/sixth')[0] == 429 and rows("SELECT COUNT(*) FROM tide_push_subscriptions WHERE endpoint LIKE '%/sixth'")[0][0] == 0)
    for result in extras: remove(result[1]['id'])
    check('Owner sees only count, not subscription secrets', call(owner())[1]['subscriberCount'] == 1 and PUSH_ORIGIN not in call(owner())[2] and AUTH not in call(owner())[2])

    # Clock shifts below separate independent functional cases from the real
    # four/hour, eight/day posting cap. The cap itself is tested later.
    def fresh(**changes):
        rows('UPDATE tide_business_posts SET published_at=? WHERE published_at IS NOT NULL',(stamp(-172800),))
        return new_post(**changes)
    announcement = fresh(title='After-hours jazz',body='Music starts at 10.\nOrder and pay at your table.')
    published = transition(announcement['id'])
    check('Publish queues only current venue subscribers', published[0] == 200 and rows('SELECT subscription_id FROM tide_push_outbox WHERE post_id=?',(announcement['id'],)) == [(sid,)])
    wait_for('encrypted notification accepted',lambda:state(announcement['id'],sid)[0] == 'sent')
    wire = requests('/push/main')
    check('One initial publish creates one accepted request', len(wire) == 1 and state(announcement['id'],sid)[1] == 1)
    decoded = decrypt(wire[0]); headers = {k.lower():v for k,v in wire[0]['headers'].items()}
    check('RFC8291 message independently decrypts and authenticates', decoded['keyLength'] == 65 and decoded['recordSize'] >= 1024 and decoded['vapidValid'])
    check('Push wire uses encrypted body and bounded browser TTL', headers.get('content-encoding') == 'aes128gcm' and 0 < int(headers.get('ttl','0')) <= 86400 and 'After-hours jazz' not in base64.b64decode(wire[0]['body']).decode(errors='replace'))
    # Lib.Net.Http.WebPush 3.3.1 omits nondefault ports from its audience. This
    # explicit Development fixture therefore verifies its scheme/host value;
    # production endpoints are separately restricted to HTTPS default port443.
    expected_audience=urllib.parse.urlsplit(PUSH_ORIGIN).scheme+'://'+urllib.parse.urlsplit(PUSH_ORIGIN).hostname
    check('VAPID signature claims bind push-service host and bounded expiry', decoded['claims']['aud'] == expected_audience and decoded['claims']['sub'] == 'mailto:synthetic@example.invalid' and time.time() < decoded['claims']['exp'] <= time.time()+86400)
    payload = decoded['payload']
    check('Notification carries venue post and safe local click destination', payload.get('title') == announcement['title'] and payload.get('body') == announcement['body'] and payload.get('url') == '/updates/bistro#post-'+announcement['id'] and payload.get('tag') == 'post-'+announcement['id'], payload)
    check('Push payload excludes private customer identity and keys', s.PRIVATE not in json.dumps(payload) and '@example.invalid' not in json.dumps(payload) and AUTH not in json.dumps(payload))
    late = subscription('/push/late')
    check('New follower gets no historical notifications', late[0] == 200 and rows('SELECT COUNT(*) FROM tide_push_outbox WHERE subscription_id=?',(late[1]['id'],))[0][0] == 0 and requests('/push/late') == [])
    remove(late[1]['id'])
    check('Hiding published post removes public content', transition(announcement['id'],'hide',1)[0] == 200 and all(x['id'] != announcement['id'] for x in call(public(),person=None)[1]['posts']))
    check('Hidden post cannot republish to resend notifications', transition(announcement['id'],'publish',2)[0] == 409)

    race = fresh(title='One concurrent announcement')
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        responses = list(pool.map(lambda _:transition(race['id']),range(2)))
    check('Concurrent publication commits once', sorted(x[0] for x in responses) == [200,409] and rows('SELECT COUNT(*) FROM tide_push_outbox WHERE post_id=?',(race['id'],))[0][0] == 1)
    wait_for('concurrent publication accepted once',lambda:state(race['id'],sid)[0] == 'sent')
    remove(sid)
    check('Unsubscribe is idempotent and removes private list entry', remove(sid)[0] == 204 and mine()[1]['subscriptions'] == [])
    generation = rows('SELECT generation FROM tide_push_subscriptions WHERE id=?',(sid,))[0][0]
    resub = subscription('/push/main')
    check('Fresh consent advances subscription generation without old backlog', resub[0] == 200 and resub[1]['id'] == sid and rows('SELECT generation FROM tide_push_subscriptions WHERE id=?',(sid,))[0][0] == generation+1 and rows("SELECT COUNT(*) FROM tide_push_outbox WHERE subscription_id=? AND state IN ('pending','sending')",(sid,))[0][0] == 0)
    remove(sid)

    retry_sub = subscription('/push/retry'); retry_id = retry_sub[1]['id']
    PLANS['/push/retry'] = [429,503,201]
    retry = fresh(title='Transient provider recovery'); transition(retry['id'])
    wait_for('429 waits for retry',lambda:state(retry['id'],retry_id)[0] == 'pending' and state(retry['id'],retry_id)[1] == 1)
    check('429 leaves consent active and defers notification', len(requests('/push/retry')) == 1 and rows('SELECT active FROM tide_push_subscriptions WHERE id=?',(retry_id,))[0][0] == 1)
    due(retry['id']); wait_for('503 waits for retry',lambda:state(retry['id'],retry_id)[0] == 'pending' and state(retry['id'],retry_id)[1] == 2)
    due(retry['id']); wait_for('retry succeeds',lambda:state(retry['id'],retry_id)[0] == 'sent')
    check('429 and 503 recover with bounded durable attempt count', len(requests('/push/retry')) == 3 and state(retry['id'],retry_id)[1] == 3)
    check('Retry re-encryption preserves logical post and collapse tag', len({json.dumps(decrypt(x)['payload'],sort_keys=True) for x in requests('/push/retry')}) == 1)
    remove(retry_id)

    gone = subscription('/push/gone'); gone_id = gone[1]['id']; PLANS['/push/gone'] = [410]
    gone_post = fresh(title='Expired browser subscription'); transition(gone_post['id'])
    wait_for('410 deactivates expired subscription',lambda:rows('SELECT active FROM tide_push_subscriptions WHERE id=?',(gone_id,))[0][0] == 0)
    check('410 stops retry and deactivates only failed browser', len(requests('/push/gone')) == 1 and state(gone_post['id'],gone_id)[0] in ('failed','cancelled') and rows('SELECT active FROM tide_push_subscriptions WHERE id=?',(foreign[1]['id'],))[0][0] == 1)

    held = subscription('/push/held'); held_id = held[1]['id']; PLANS['/push/held'] = ['hold']
    gate = {'entered':threading.Event(),'release':threading.Event()}; GATES['/push/held'] = gate
    held_post = fresh(title='Consent revoked during send'); transition(held_post['id'])
    check('Notification obtains durable lease before provider returns', gate['entered'].wait(timeout=12) and state(held_post['id'],held_id)[0] == 'sending')
    check('Unsubscribe cancels pending or in-flight bookkeeping', remove(held_id)[0] == 204 and state(held_post['id'],held_id)[0] == 'cancelled')
    gate['release'].set(); time.sleep(.6)
    check('Late provider response cannot resurrect withdrawn consent', state(held_post['id'],held_id)[0] == 'cancelled' and rows('SELECT active FROM tide_push_subscriptions WHERE id=?',(held_id,))[0][0] == 0)

    crash_sub = subscription('/push/crash'); crash_id = crash_sub[1]['id']; PLANS['/push/crash'] = ['hold',201]
    crash_gate = {'entered':threading.Event(),'release':threading.Event()}; GATES['/push/crash'] = crash_gate
    crash = fresh(title='Recover after interruption'); transition(crash['id'])
    check('Interrupted worker had leased durable work', crash_gate['entered'].wait(timeout=12) and state(crash['id'],crash_id)[0] == 'sending')
    stop(api); crash_gate['release'].set()
    rows('UPDATE tide_push_outbox SET lease_until=?,next_attempt_at=? WHERE post_id=?',(stamp(-5),stamp(-5),crash['id']))
    api = launch(extra=push_options(),label='api-restart-push-recovery')
    wait_for('expired lease recovered after restart',lambda:state(crash['id'],crash_id)[0] == 'sent')
    check('Expired lease retries same notification after restart', len(requests('/push/crash')) == 2 and state(crash['id'],crash_id)[1] == 2 and decrypt(requests('/push/crash')[0])['payload'] == decrypt(requests('/push/crash')[1])['payload'])
    remove(crash_id)

    cap_sub = subscription('/push/attempt-cap'); cap_id = cap_sub[1]['id']; PLANS['/push/attempt-cap'] = [503]
    capped_post = fresh(title='Bound retry attempts'); transition(capped_post['id'])
    wait_for('attempt cap fixture first retry',lambda:state(capped_post['id'],cap_id)[:2] == ('pending',1))
    rows('UPDATE tide_push_outbox SET attempts=5,next_attempt_at=? WHERE post_id=?',(stamp(-5),capped_post['id']))
    wait_for('attempt ceiling enforced',lambda:state(capped_post['id'],cap_id)[0] == 'failed')
    check('Five-attempt ceiling prevents additional provider request', len(requests('/push/attempt-cap')) == 1)
    remove(cap_id)

    old_sub = subscription('/push/expired-post'); old_id = old_sub[1]['id']; PLANS['/push/expired-post'] = [503]
    old_post = fresh(title='Expired notification'); transition(old_post['id'])
    wait_for('age fixture first retry',lambda:state(old_post['id'],old_id)[:2] == ('pending',1))
    rows('UPDATE tide_push_outbox SET created_at=?,next_attempt_at=? WHERE post_id=?',(stamp(-172800),stamp(-5),old_post['id']))
    wait_for('old alert cancelled',lambda:state(old_post['id'],old_id)[0] == 'cancelled')
    check('One-day expiry prevents stale phone alerts', len(requests('/push/expired-post')) == 1)
    remove(old_id)

    backlog = subscription('/push/disable-backlog'); backlog_id = backlog[1]['id']; PLANS['/push/disable-backlog'] = [503]
    backlog_post = fresh(title='Stop disabled sender backlog'); transition(backlog_post['id'])
    wait_for('disabled backlog fixture pending',lambda:state(backlog_post['id'],backlog_id)[:2] == ('pending',1))
    stop(api); api = launch(label='api-disable-existing-backlog')
    wait_for('disabled worker cancels old queue',lambda:state(backlog_post['id'],backlog_id)[0] == 'cancelled')
    check('Disabling notifications cancels outstanding work without sending', len(requests('/push/disable-backlog')) == 1)
    stop(api); api = launch(extra=push_options(),label='api-reenable-no-backfill')
    time.sleep(.6)
    check('Reenabling sender cannot deliver cancelled backlog', state(backlog_post['id'],backlog_id)[0] == 'cancelled' and len(requests('/push/disable-backlog')) == 1)
    remove(backlog_id)

    atomic_sub = subscription('/push/atomic')
    atomic_post = fresh(title='Atomic publication')
    rows("CREATE TRIGGER synthetic_push_failure BEFORE INSERT ON tide_push_outbox BEGIN SELECT RAISE(ABORT,'synthetic push failure'); END")
    rejected = transition(atomic_post['id'])
    check('Queue failure rolls back publication atomically', rejected[0] == 503 and rows('SELECT state,version FROM tide_business_posts WHERE id=?',(atomic_post['id'],))[0] == ('draft',0) and rows('SELECT COUNT(*) FROM tide_push_outbox WHERE post_id=?',(atomic_post['id'],))[0][0] == 0)
    rows('DROP TRIGGER synthetic_push_failure'); remove(atomic_sub[1]['id'])

    # Native browser opt-in is authenticated, explicit, CSRF-protected JSON.
    native_forms, page = forms_for(bob,'/updates/bistro')
    token_form=re.search(r'<form\b[^>]*\bdata-push-token\b[^>]*>.*?</form>',page[1],re.S)
    check('Notification control renders its CSRF token form',token_form is not None)
    sub_form = forms_module.Forms(token_form.group(0)).forms[0]
    # The visible gesture requests browser permission in JavaScript, then uses
    # this rendered CSRF token and data-subscribe route for its form POST.
    sub_form['action'] = html.unescape(re.search(r'data-subscribe="([^"]+)"', page[1]).group(1))
    fields = {'endpoint':PUSH_ORIGIN+'/push/native','p256dh':PUB,'auth':AUTH,'consent':'true'}
    check('Native opt-in rejects foreign origin', post(bob,sub_form,fields,headers={'Origin':'https://foreign.example.invalid'})[0] == 400)
    check('Native opt-in rejects missing CSRF', post(bob,sub_form,fields,remove=('__RequestVerificationToken',))[0] == 400)
    check('Native opt-in rejects oversized form', post(bob,sub_form,{**fields,'extra':'x'*17000})[0] in (400,413))
    result = post(bob,sub_form,fields)
    check('Native opt-in registers only after explicit consent', result[0] == 200 and json.loads(result[1])['active'] is True, result[:2])
    native_id = json.loads(result[1])['id']; native_forms, page = forms_for(bob,'/updates/bistro')
    check('Rendered subscriber state excludes raw endpoint and keys', PUSH_ORIGIN not in page[1] and AUTH not in page[1] and expected_hash not in page[1])
    rows("UPDATE bartide_customers SET status='paused' WHERE id='bistro'")
    paused = web('/updates/bistro',client=bob)
    check('Paused business still lets signed-in customer stop alerts', paused[0] == 200 and 'data-push-enable' not in paused[1] and native_id in paused[1], {'status':paused[0],'bodyLength':len(paused[1]),'hasDevice':native_id in paused[1]})
    remove_form = find_form(forms_module.Forms(paused[1]).forms,'/'+native_id+'/remove')
    removed = post(bob,remove_form)
    check('Native unsubscribe works while business is paused', removed[0] in (302,303) and rows('SELECT active FROM tide_push_subscriptions WHERE id=?',(native_id,))[0][0] == 0)
    rows("UPDATE bartide_customers SET status='active' WHERE id='bistro'")

    rows('UPDATE tide_business_posts SET published_at=? WHERE published_at IS NOT NULL',(stamp(-172800),))
    for _ in range(4):
        p = new_post(); check('Within four-hourly-post allowance', transition(p['id'])[0] == 200)
    capped = new_post()
    check('Fifth post in an hour is rejected without publishing', transition(capped['id'])[0] == 429 and rows('SELECT state FROM tide_business_posts WHERE id=?',(capped['id'],))[0][0] == 'draft')
    rows("UPDATE tide_business_posts SET published_at=? WHERE julianday(published_at)>julianday('now','-1 hour')",(stamp(-7200),))
    for _ in range(4):
        p = new_post(); check('Within eight-daily-post allowance', transition(p['id'])[0] == 200)
    rows("UPDATE tide_business_posts SET published_at=? WHERE julianday(published_at)>julianday('now','-1 hour')",(stamp(-7200),))
    check('Ninth post in a day is rejected', transition(capped['id'])[0] == 429)
    # Explicit historical fixtures isolate retained-row and rolling-day limits
    # from the separate active-device allowance tested earlier.
    def historical(tenant,user,count,prefix,created):
        values=[]
        for n in range(count):
            endpoint=PUSH_ORIGIN+'/push/'+prefix+'-'+str(n)
            values.append((str(uuid.uuid4()),tenant,'supabase:'+user,endpoint,hashlib.sha256(endpoint.encode()).hexdigest(),PUB,AUTH,created,created))
        with s.sqlite3.connect(DB) as connection:
            connection.executemany('INSERT INTO tide_push_subscriptions(id,tenant_id,user_id,endpoint,endpoint_hash,p256dh,auth,active,generation,created_at,updated_at) VALUES(?,?,?,?,?,?,?,0,1,?,?)',values)
        return values
    retained=historical('bistro',s.STAFF,100,'retained-user',stamp(-172800))
    check('One hundred retained device rows stop further churn', subscription('/push/overflow',person='staff')[0] == 429)
    reactivated=subscription('/push/retained-user-0',person='staff')
    check('Retained-row ceiling still allows fresh consent for known device', reactivated[0] == 200 and reactivated[1]['id'] == retained[0][0])
    remove(reactivated[1]['id'],person='staff')
    historical('bistro',s.ALICE,20,'new-devices-today',stamp())
    check('Twenty new devices in one day stop endpoint churn', subscription('/push/day-overflow',person='alice')[0] == 429)
    s.seed('retention-cap')
    historical('retention-cap',str(uuid.uuid4()),4000,'retained-venue',stamp(-172800))
    check('Business retained-history limit is bounded independently', subscription('/push/tenant-overflow',tenant='retention-cap')[0] == 429)
    draft_ids=[x[0] for x in rows("SELECT id FROM tide_business_posts WHERE tenant_id='bistro' AND state='draft'")]
    with s.sqlite3.connect(DB) as connection:
        connection.executemany("INSERT INTO tide_business_posts(id,tenant_id,title,body,state,version,created_at,published_at,updated_at) VALUES(?,'bistro','Historical hidden','Synthetic history','hidden',2,?,?,?)",
            [(str(uuid.uuid4()),stamp(),stamp(),stamp()) for _ in range(105)])
    workspace=call(owner())[1]['feed']['posts']
    check('Bounded owner feed keeps editable drafts ahead of hidden history', len(workspace) == 100 and set(draft_ids).issubset({x['id'] for x in workspace}))
    limited = [call(public(),person=None,headers={'X-Forwarded-For':'203.0.113.244'})[0] for _ in range(121)]
    check('Public feed requests enforce per-client rate limit', limited[:120] == [200]*120 and limited[120] == 429)
    stop(api)
    before = len(WIRE)
    # Invalid push settings fail closed without making the rest of the app fail.
    for label, settings in [('production-loopback',{'ASPNETCORE_ENVIRONMENT':'Production','Auth__Enabled':'false'}),
        ('unapproved-loopback',{'WebPush__AllowDevelopmentLoopback':'false'}),
        ('external-test-origin',{'WebPush__DevelopmentPushOrigin':'http://example.invalid'}),
        ('invalid-vapid',{'WebPush__VapidPrivateKey':'invalid'})]:
        guarded = launch(extra=push_options(**settings),label=label)
        check('Unsafe push setting disables notification availability: '+label, call(public(),person=None)[1]['pushAvailable'] is False)
        stop(guarded)
    check('Invalid push configurations make no outbound requests', len(WIRE) == before)


def reproduce_pre_fix_gaps():
    """Explicit baseline evidence. Register synthetic endpoints; never publish."""
    launch(extra=push_options(),label='api-before-key-endpoint-retention-fixes')
    for person in ('alice','bob'):
        result = s.call('/api/v1/auth/signin',{'email':person+'@example.invalid','password':s.PASSWORD})
        TOKENS[person] = result[1]['accessToken']
    s.seed('bistro')
    zero = subscription('/push/zero',p256dh=b64(b'\x04'+bytes(64)))
    check('Baseline gap: malformed curve point returns server error',zero[0] == 500,zero[:3])
    first = subscription('/unused',endpoint='https://fcm.googleapis.com/fcm/send/synthetic-alias')
    alias = subscription('/unused',person='alice',endpoint='https://FCM.GOOGLEAPIS.COM:443/fcm/send/synthetic-alias')
    check('Baseline gap: equivalent endpoint can be claimed by another account',first[0] == 200 and alias[0] == 200 and first[1]['endpointHash'] != alias[1]['endpointHash'],(first[:3],alias[:3]))
    remove(first[1]['id']); remove(alias[1]['id'],person='alice')
    for n in range(100):
        endpoint=PUSH_ORIGIN+'/push/historical-'+str(n)
        rows('INSERT INTO tide_push_subscriptions(id,tenant_id,user_id,endpoint,endpoint_hash,p256dh,auth,active,generation,created_at,updated_at) VALUES(?,?,?,?,?,?,?,0,1,?,?)',
            (str(uuid.uuid4()),'bistro','supabase:'+s.BOB,endpoint,hashlib.sha256(endpoint.encode()).hexdigest(),PUB,AUTH,stamp(-172800),stamp(-172800)))
    churn = subscription('/push/retention-overflow')
    check('Baseline gap: inactive device history permits unbounded new rows',churn[0] == 200 and rows('SELECT COUNT(*) FROM tide_push_subscriptions WHERE user_id=?',('supabase:'+s.BOB,))[0][0] > 100,churn[:3])
    check('Baseline reproduction never enqueues or sends external notifications',rows('SELECT COUNT(*) FROM tide_push_outbox')[0][0] == 0 and WIRE == [])


def hold_for_browser():
    requested=os.environ.get('POSTS_HOLD_FILE')
    if not requested: return
    stop_file=Path(requested).resolve()
    if not stop_file.is_relative_to(s.ROOT): raise ValueError('Browser hold stop file must stay inside the workspace')
    launch(label='api-browser-preview-push-disabled')
    fixture={'webUrl':WEB,'apiUrl':s.API,'ownerPage':WEB+'/workspace/bistro/posts',
        'publicPage':WEB+'/updates/bistro','signInEmail':'alice@example.invalid',
        'passwordReference':'scripts/restaurant_test_support.py PASSWORD (synthetic fixture only)',
        'stopFile':str(stop_file),'notificationsEnabled':False}
    (RUN/'browser-fixture.json').write_text(json.dumps(fixture,indent=2),encoding='utf-8')
    print('SYNTHETIC BROWSER FIXTURE '+json.dumps(fixture),flush=True)
    while not stop_file.exists(): time.sleep(.5)


if __name__ == '__main__':
    completed = False
    try:
        if '--baseline-gaps' in sys.argv: reproduce_pre_fix_gaps()
        else:
            run()
            hold_for_browser()
        completed = True
    finally:
        for gate in GATES.values(): gate['release'].set()
        for proc in s.PROCESSES: stop(proc)
        s.PROVIDER.shutdown(); s.PROVIDER.server_close(); PUSH.shutdown(); PUSH.server_close()
        for log in s.LOGS: log.close()
        (RUN/'results.json').write_text(json.dumps(s.RESULTS,indent=2),encoding='utf-8')
        (RUN/'completion.json').write_text(json.dumps({'completed':completed,'checks':len(s.RESULTS)},indent=2),encoding='utf-8')
        binaries = {str(path.relative_to(RUN)):hashlib.sha256(path.read_bytes()).hexdigest()
            for project in ('TideCasa.Api','TideCasa.Blazor')
            for path in (RUN/project).glob('TideCasa*.dll')}
        (RUN/'binary-sha256.json').write_text(json.dumps(binaries,indent=2),encoding='utf-8')
        # Ciphertexts and metadata concern synthetic endpoints and generated test keys only.
        (RUN/'synthetic-push-evidence.json').write_text(json.dumps(WIRE,indent=2),encoding='utf-8')
        print('Evidence: ' + str(RUN),flush=True)
    print(str(len(s.RESULTS)) + ' business post checks passed.',flush=True)
