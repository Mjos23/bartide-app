"""Local Gulf Lantern fixture; never loads launch credentials or contacts live services.

Build API and Blazor first, then run this file. Stop by creating STOP in the preview
directory. A unique binary snapshot avoids locking normal build outputs. The database
and private media persist between runs; incomplete seed runs fail closed for inspection.
"""
import base64
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import io
import json
import os
from pathlib import Path
import shutil
import socket
import sqlite3
import subprocess
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]
RUN = ROOT / '.tools/gulf-lantern-preview'
FIXTURE = ROOT / 'fixtures/gulf-lantern.json'
BAR = json.loads(FIXTURE.read_text(encoding='utf-8'))
API = 'http://127.0.0.1:5520'
WEB = 'http://127.0.0.1:5521'
DB = RUN / 'gulf-lantern.db'
SDK = ROOT / '.tools/dotnet-10.0.401/dotnet.exe'
KEY = 'sb_publishable_localgulfLantern000000000'
TOKENS, PROCESSES, LOGS = {}, [], []
USERS = {p['email']: {'id': p['id'], 'email': p['email'],
    'email_confirmed_at': '2026-01-01T00:00:00Z', 'is_anonymous': False,
    'user_metadata': {'full_name': p['name']}} for p in BAR['people']}
BASE = '/api/v1/tenants/gulf-lantern'
GUEST = '/api/v1/restaurants/gulf-lantern'
REQUEST_COUNT = 0


class Provider(BaseHTTPRequestHandler):
    def log_message(self, *args): pass

    def reply(self, status, body):
        self.send_response(status)
        self.send_header('Content-Type', 'application/json')
        self.end_headers()
        self.wfile.write(json.dumps(body).encode())

    def handle_request(self):
        if self.headers.get('apikey') != KEY: return self.reply(403, {})
        if self.headers.get('Transfer-Encoding', '').lower() == 'chunked':
            chunks = []
            while True:
                length = int(self.rfile.readline().split(b';', 1)[0].strip(), 16)
                if not length:
                    while self.rfile.readline().strip(): pass
                    break
                chunks.append(self.rfile.read(length)); self.rfile.read(2)
            raw = b''.join(chunks)
        else: raw = self.rfile.read(int(self.headers.get('Content-Length', '0')))
        body = json.loads(raw or b'{}')
        path = urllib.parse.urlparse(self.path).path
        if path == '/auth/v1/token' and body.get('password') == BAR['password']:
            user = USERS.get(body.get('email'))
            if user:
                token = 'synthetic.' + base64.urlsafe_b64encode(uuid.uuid4().bytes).decode().rstrip('=') + '.signature'
                TOKENS[token] = user
                return self.reply(200, {'access_token': token, 'expires_in': 3600, 'user': user})
        if path == '/auth/v1/user':
            user = TOKENS.get(self.headers.get('Authorization', '').removeprefix('Bearer '))
            return self.reply(200 if user else 401, user or {})
        return self.reply(400, {})

    do_GET = do_POST = handle_request


def call(path, body=None, token=None, headers=None):
    global REQUEST_COUNT
    REQUEST_COUNT += 1
    head = {'X-Forwarded-For': '192.0.2.' + str(1 + REQUEST_COUNT % 250), **(headers or {})}
    if token: head['Authorization'] = 'Bearer ' + token
    if body is not None and not isinstance(body, bytes):
        body = json.dumps(body).encode(); head['Content-Type'] = 'application/json'
    request = urllib.request.Request(API + path, data=body, headers=head)
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    try: response = opener.open(request, timeout=45)
    except urllib.error.HTTPError as error: response = error
    with response:
        raw = response.read()
        try: parsed = json.loads(raw)
        except (ValueError, UnicodeDecodeError): parsed = None
        if not 200 <= response.status < 300:
            raise RuntimeError(f'{path}: {response.status} {str(parsed)[:1200]}')
        return parsed


def identity(key):
    person = next(p for p in BAR['people'] if p['key'] == key)
    return call('/api/v1/auth/signin', {'email': person['email'], 'password': BAR['password']})['accessToken']


def new_id(): return str(uuid.uuid4())


def environment(provider_port):
    env = os.environ.copy()
    prefixes = ('AUTH__', 'STORAGE__', 'API__', 'DATAPROTECTION__', 'ORDERING__',
        'STRIPE__', 'SERVICEBILLING__', 'MERCHANTPAYMENTS__', 'MEDIA__', 'NOTIFICATIONS__',
        'WEBPUSH__', 'REVERSEPROXY__', 'SAMPLEBAR__', 'ASPNETCORE_', 'DOTNET_')
    for key in list(env):
        if key.upper().startswith(prefixes) or any(x in key.upper() for x in ('SUPABASE', 'CLOUDFLARE', 'RESEND', 'STRIPE')):
            env.pop(key)
    env.update({'ASPNETCORE_ENVIRONMENT': 'Development', 'DOTNET_ROOT': str(SDK.parent),
        'DOTNET_PROCESSOR_COUNT': '1',
        'DOTNET_CLI_HOME': str(ROOT / '.tools/dotnet-home'), 'APPDATA': str(RUN / 'isolated-appdata'),
        'Storage__Provider': 'Sqlite', 'Storage__DatabasePath': str(DB),
        'Auth__Enabled': 'true', 'Auth__AllowLocalTestProvider': 'true',
        'Auth__SupabaseUrl': f'http://127.0.0.1:{provider_port}', 'Auth__PublishableKey': KEY,
        'Auth__PlatformOwnerUserId': '', 'Stripe__CheckoutEnabled': 'false',
        'Stripe__InvoicesEnabled': 'false', 'ServiceBilling__CheckoutEnabled': 'false',
        'MerchantPayments__Enabled': 'false', 'Notifications__Enabled': 'false', 'WebPush__Enabled': 'false',
        'Media__Provider': 'local', 'Media__AllowLocalStore': 'true',
        'Media__LocalRoot': str(RUN / 'private-objects'), 'Media__SpoolPath': str(RUN / 'private-spool'),
        'DataProtection__KeysPath': str(RUN / 'private-keys'),
        'ReverseProxy__KnownClientProxy': '127.0.0.1', 'Ordering__PublicBaseUrl': WEB,
        'Api__BaseUrl': API + '/', 'SampleBar__Enabled': 'true', 'SampleBar__FixturePath': str(FIXTURE)})
    return env


def launch(project, url, env, snapshot):
    folder = snapshot / project
    shutil.copytree(ROOT / project / 'bin/Debug/net10.0', folder, ignore=shutil.ignore_patterns('libSkiaSharp.pdb'))
    log = (RUN / (project + '.log')).open('w', encoding='utf-8'); LOGS.append(log)
    proc = subprocess.Popen([str(SDK), str(folder / (project + '.dll'))], cwd=ROOT / project,
        env={**env, 'ASPNETCORE_URLS': url}, stdout=log, stderr=subprocess.STDOUT,
        creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
    PROCESSES.append(proc)
    for _ in range(180):
        if proc.poll() is not None: raise RuntimeError(project + ' stopped; inspect its preview log.')
        try:
            with urllib.request.build_opener(urllib.request.ProxyHandler({})).open(url + '/health', timeout=2) as response:
                if response.status == 200: return
        except (OSError, urllib.error.URLError): pass
        time.sleep(.5)
    raise TimeoutError(project + ' startup timeout')


def seed():
    marker = RUN / 'seed-complete.json'
    with sqlite3.connect(DB, timeout=20) as db:
        present = db.execute('SELECT COUNT(*) FROM bartide_customers').fetchone()[0]
    if marker.exists():
        if present != 1: raise RuntimeError('Unexpected tenant count; refusing to reseed.')
        return
    if present: raise RuntimeError('Incomplete fixture: inspect database before rebuilding; no automatic deletion.')
    now = datetime.now(timezone.utc).isoformat()
    table_token = uuid.uuid5(uuid.NAMESPACE_URL, 'https://gulf-lantern.example.invalid/table').hex * 2
    menu = {'schema': 'bartide-menu/1', 'venue': {'name': BAR['name'], 'vertical': 'bartide',
        'area': BAR['location'], 'tagline': BAR['tagline'], 'hours_text': BAR['hours'], 'website_url': '',
        'service_note': 'Fictional local venue. Ask staff about allergens. Sample orders only.', 'currency': 'USD'},
        'categories': BAR['categories'], 'items': [{'id': x['id'], 'category': x['category'], 'name': x['name'],
            'description': x['description'], 'price_cents': x['priceCents'], 'price_label': None, 'available': True, 'ingredients': x.get('ingredients', [])} for x in BAR['menu']]}
    config = {'enabled': True, 'accepting_orders': True, 'delivery_enabled': True,
        'tax_basis_points': 700, 'delivery_fee_cents': 500, 'delivery_minimum_cents': 1500,
        'delivery_capacity': 5, 'delivery_zips': ['33706'], 'prep_minutes': 25, 'blocked_item_ids': [],
        'pickup_instructions': 'Sample pickup: check in with the host by the lantern wall.',
        'payment_instructions': 'Sample orders: pay a member of staff. No real payment is collected.',
        'checkout': {'dine_in_enabled': True, 'pickup_enabled': True, 'pay_staff_enabled': True, 'tips_enabled': True,
            'tables': [{'id': new_id(), 'label': 'Patio 1', 'token': table_token, 'enabled': True}]
                + [{'id': new_id(), 'label': f'Table {number}', 'token': uuid.uuid4().hex * 2, 'enabled': True}
                   for number in range(1, 13)]}}
    owner = next(p for p in BAR['people'] if p['role'] == 'owner')
    with sqlite3.connect(DB, timeout=20) as db:
        db.execute('INSERT INTO bartide_customers(id,slug,email,user_id,name,menu_json,version,status,enrollment_note,created_at,updated_at,vertical) VALUES(?,?,?,?,?,?,0,?,?,?,?,?)',
            (BAR['id'], BAR['slug'], owner['email'], 'supabase:' + owner['id'], BAR['name'], json.dumps(menu),
             'active', 'Isolated fictional Gulf Lantern fixture', now, now, 'bartide'))
        db.execute('INSERT INTO bartide_enhanced_configs(tenant_id,settings_json,version,updated_at) VALUES(?,?,0,?)',
            (BAR['id'], json.dumps(config), now))
    token = identity('owner')
    print('Created isolated venue; filling real API workflows.', flush=True)
    members = {}
    staff = [p for p in BAR['people'] if p['role'] not in ('owner', 'customer')]
    for p in staff:
        workspace = call(BASE + '/team/members', {k: p[k] for k in ('name', 'email', 'role')}, token)
        members[p['key']] = next(m['id'] for m in workspace['members'] if m['email'] == p['email'])
    start = datetime.fromisoformat(BAR['events'][0]['startsAt']) - timedelta(hours=5)
    for p in staff:
        call(BASE + '/team/shifts', {'memberId': members[p['key']], 'startsAt': start.isoformat(),
            'endsAt': (start + timedelta(hours=8)).isoformat(), 'label': 'Sunset patio opening & music'}, token)
    call(BASE + '/team/messages', {'requestId': new_id(), 'text': 'Welcome to Gulf Lantern! This Friday: Sunset Strings on the patio. Check allergy notes, confirm table numbers and keep guest handoffs clear. All activity here is fictional practice.'}, token)
    # Codec conversion only: existing app photos enter the real JPEG upload pipeline.
    from PIL import Image
    photos, encoded_bytes, stored_bytes = {}, 0, 0
    for item in BAR['menu']:
        if item['image'] in photos: continue
        with Image.open(ROOT / 'TideCasa.Blazor/wwwroot' / item['image'].lstrip('/')) as im:
            im = im.convert('RGB'); im.thumbnail((960, 960))
            output = io.BytesIO(); im.save(output, format='JPEG', quality=82, optimize=True)
        data = output.getvalue(); encoded_bytes += len(data)
        media = call(BASE + '/media/photo', data, token, {'Content-Type': 'image/jpeg',
            'X-Request-Id': new_id(), 'X-File-Name': urllib.parse.quote(item['name'] + '.jpg')})['file']
        photos[item['image']] = media['id']; stored_bytes += media['byteSize']
    editor = call(BASE + '/menu', token=token)
    for item in BAR['menu']:
        editor = call(BASE + '/menu/items', {'expectedVersion': editor['version'], 'photoId': photos[item['image']],
            'item': {'id': item['id'], 'categoryId': item['category'], 'name': item['name'], 'description': item['description'],
                'priceCents': item['priceCents'], 'priceLabel': None, 'available': True, 'ingredients': item.get('ingredients', [])}}, token)
    print('Attached 18 unique photos to 20 menu items.', flush=True)
    video = ROOT / 'TideCasa.Blazor/wwwroot/assets/verticals/course-preview.mp4'
    media = call(BASE + '/media/video', video.read_bytes(), token, {'Content-Type': 'video/mp4',
        'X-Request-Id': new_id(), 'X-File-Name': 'Sample-player-check.mp4'})['file']
    workspace = call(BASE + '/team/courses', {'title': 'Gulf Lantern shift orientation',
        'description': 'Fictional practice: order handoff, allergy notes, team messages and sample video playback. The clip is a player test, not professional training.', 'published': False}, token)
    course = next(c for c in workspace['courses'] if c['title'] == 'Gulf Lantern shift orientation')
    workspace = call(BASE + '/team/lessons', {'courseId': course['id'], 'title': 'Practice a clear order handoff',
        'description': 'Read the order and allergy note. Confirm the table or pickup name. Use the order board to accept, prepare and mark ready. Record a staff payment only after the fictional collection step. Tell the next role what is ready. The attached clip only checks video playback.',
        'videoUrl': '', 'uploadedVideoId': media['id'], 'position': 1}, token)
    lesson = workspace['lessons'][0]
    for member in members.values():
        call(BASE + '/team/courses/' + course['id'] + '/assignments', {'memberId': member, 'active': True, 'expectedVersion': -1}, token)
    call(BASE + '/team/courses/' + course['id'], {'title': course['title'], 'description': course['description'], 'published': True, 'expectedVersion': course['version']}, token)
    for key in ('bartender', 'server-maya', 'kitchen'):
        call(BASE + '/team/lessons/' + lesson['id'] + '/progress', {'completed': True}, identity(key))
    events = []
    for event in BAR['events']:
        created = call(BASE + '/events', {**event, 'requestKey': new_id(), 'expectedVersion': 0, 'published': True}, token)
        events.append(created['id'])
    call(BASE + '/rewards/rules', {'requestId': new_id(), 'expectedVersion': -1, 'title': 'Lantern regulars',
        'reward': 'A sample zero-proof refresher', 'kind': 'points', 'threshold': 100, 'itemId': None,
        'timeZoneId': 'America/New_York', 'active': True}, token)
    orders = []
    customers = [p for p in BAR['people'] if p['role'] == 'customer']
    for index, p in enumerate(customers):
        customer = identity(p['key'])
        call(GUEST + '/rewards/join', {'requestId': new_id(), 'name': p['name']}, customer)
        wallet = call(GUEST + '/rewards', token=customer)
        call(BASE + '/rewards/points', {'requestId': new_id(), 'memberId': wallet['memberId'], 'points': [120, 60, 30, 80, 10][index],
            'sourceReference': 'fictional-opening-' + p['key'], 'reason': 'Fictional sample visits for local testing'}, token)
        call(GUEST + '/events/' + events[index % 3] + '/rsvp', {'requestKey': new_id(), 'expectedVersion': -1, 'attending': True}, customer)
        fulfillment = ['dine-in', 'dine-in', 'pickup', 'delivery', 'pickup'][index]
        selected = {'items': [{'itemId': ['gulf-tacos', 'sunset-burger', 'margherita-flatbread', 'coconut-shrimp', 'key-lime-fizz'][index], 'quantity': 2}],
            'fulfillment': fulfillment, 'paymentMethod': 'staff', 'tableToken': table_token if fulfillment == 'dine-in' else None,
            'deliveryZip': '33706' if fulfillment == 'delivery' else None, 'tipPercent': 0}
        quote = call(GUEST + '/quote', selected)
        tracking = uuid.uuid4().hex + uuid.uuid4().hex
        receipt = call(GUEST + '/orders', {'requestKey': new_id(), 'trackingKey': tracking, 'order': selected,
            'quoteFingerprint': quote['fingerprint'], 'customerName': p['name'], 'phone': '(727) 555-01' + str(20 + index),
            'address': 'Fictional delivery desk, St. Pete Beach, FL 33706' if fulfillment == 'delivery' else None,
            'note': 'Sample allergy note: ask the guest about ingredients before preparing.' if index == 2 else 'Fictional local test order.'})
        orders.append({'person': p['key'], 'orderId': receipt['orderId'], 'trackingKey': tracking})
    post = call(BASE + '/posts', {'requestKey': new_id(), 'title': 'Meet us under the lanterns',
        'body': 'Our fictional St. Pete Beach patio is ready: Gulf-style plates, zero-proof refreshers and three live-music nights. This venue is a local test of Tide Casa.'}, token)
    call(BASE + '/posts/' + post['id'] + '/publish', {'requestKey': new_id(), 'expectedVersion': post['version']}, token)
    marker.write_text(json.dumps({'schema': 1, 'createdAt': now, 'members': members, 'events': events,
        'orders': orders, 'tableToken': table_token, 'uniquePhotos': len(photos), 'uploadBytes': encoded_bytes,
        'storedPhotoBytes': stored_bytes, 'courseId': course['id'], 'lessonId': lesson['id']}, indent=2), encoding='utf-8')
    print('Seed complete: 20 items, 5 staff + GM, 5 customers, 3 events.', flush=True)


def main():
    RUN.mkdir(parents=True, exist_ok=True)
    # Holding a loopback socket prevents two runners from touching this fixture.
    lock = socket.socket(); lock.bind(('127.0.0.1', 5522)); lock.listen(1)
    stop = RUN / 'STOP'
    if stop.exists(): stop.unlink()
    provider = ThreadingHTTPServer(('127.0.0.1', 0), Provider)
    threading.Thread(target=provider.serve_forever, daemon=True).start()
    snapshot = RUN / ('runtime-' + datetime.now(timezone.utc).strftime('%Y%m%d-%H%M%S'))
    try:
        env = environment(provider.server_port)
        launch('TideCasa.Api', API, env, snapshot)
        seed()
        launch('TideCasa.Blazor', WEB, env, snapshot)
        (RUN / 'running.json').write_text(json.dumps({'pid': os.getpid(), 'api': API, 'web': WEB,
            'hub': WEB + '/sample-bar', 'children': [p.pid for p in PROCESSES], 'startedAt': datetime.now(timezone.utc).isoformat()}, indent=2), encoding='utf-8')
        print('READY ' + WEB + '/sample-bar', flush=True)
        while not stop.exists():
            if any(p.poll() is not None for p in PROCESSES): raise RuntimeError('A preview service exited.')
            time.sleep(1)
    finally:
        for proc in reversed(PROCESSES):
            if proc.poll() is None: proc.terminate(); proc.wait(timeout=20)
        for log in LOGS: log.close()
        provider.shutdown(); provider.server_close(); lock.close()
        active = RUN / 'running.json'
        if active.exists(): active.unlink()


if __name__ == '__main__': main()
