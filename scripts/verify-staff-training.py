"""Exercise staff, scheduling and training through isolated API, Blazor and SQLite.

Only the identity-provider boundary is fake. All employees, venues, and courses are
synthetic; this does not contact Stripe, Supabase, the owner preview, or production.
Build the solution first. Evidence and owned child-process logs stay under .tools.
"""
import base64
from datetime import datetime, timezone
import html
import http.cookiejar
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
import re
from pathlib import Path
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
BUILD_ROOT = Path(os.environ.get('TIDE_TEST_BUILD_ROOT', str(ROOT)))
RUN = ROOT / '.tools/staff-training-verification' / (
    datetime.now(timezone.utc).strftime('%Y%m%d-%H%M%S-%f'))
RUN.mkdir(parents=True, exist_ok=False)
SDK = ROOT / '.tools/dotnet-10.0.401/dotnet.exe'
DB = RUN / 'synthetic.db'
KEY = 'sb_publishable_localverification000000000'
PASSWORD = 'synthetic-team-password'
ALICE, BOB, STAFF, PLATFORM = [str(uuid.UUID(int=n)) for n in (301, 302, 303, 304)]
USERS = {name + '@example.invalid': {'id': ident, 'email': name + '@example.invalid',
    'email_confirmed_at': '2026-01-01T00:00:00Z', 'is_anonymous': False,
    'user_metadata': {'full_name': name.title() + ' Synthetic'}}
    for name, ident in [('alice', ALICE), ('bob', BOB), ('staff', STAFF), ('platform', PLATFORM)]}
TOKENS, RESULTS, PROCESSES, LOGS = {}, [], [], []
IP_LOCK, IP_COUNTER = threading.Lock(), 0
PRIVATE = 'SYNTHETIC-PRIVATE-MARKER'
NOW = '2026-09-20T12:00:00Z'


class Provider(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def respond(self, status, body):
        self.send_response(status)
        self.send_header('Content-Type', 'application/json')
        self.end_headers()
        self.wfile.write(json.dumps(body).encode())

    def handle_request(self):
        if self.headers.get('apikey') != KEY:
            return self.respond(403, {})
        if self.headers.get('Transfer-Encoding', '').lower() == 'chunked':
            chunks = []
            while True:
                length = int(self.rfile.readline().split(b';', 1)[0].strip(), 16)
                if length == 0:
                    while self.rfile.readline().strip():
                        pass
                    break
                chunks.append(self.rfile.read(length))
                self.rfile.read(2)
            raw = b''.join(chunks)
        else:
            raw = self.rfile.read(int(self.headers.get('Content-Length', '0')))
        body = json.loads(raw or b'{}')
        path = urllib.parse.urlparse(self.path).path
        if path == '/auth/v1/token' and body.get('password') == PASSWORD:
            user = USERS.get(body.get('email'))
            if user:
                token = 'synthetic.' + base64.urlsafe_b64encode(uuid.uuid4().bytes).decode().rstrip('=') + '.signature'
                TOKENS[token] = user
                return self.respond(200, {'access_token': token, 'expires_in': 3600, 'user': user})
        if path == '/auth/v1/user':
            user = TOKENS.get(self.headers.get('Authorization', '').removeprefix('Bearer '))
            return self.respond(200 if user else 401, user or {})
        return self.respond(400, {})

    do_GET = do_POST = handle_request


def port():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        return sock.getsockname()[1]


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


PROVIDER = ThreadingHTTPServer(('127.0.0.1', 0), Provider)
threading.Thread(target=PROVIDER.serve_forever, daemon=True).start()
API_PORT = port()
API = 'http://127.0.0.1:' + str(API_PORT)
WEB = 'http://127.0.0.1:' + str(port())


def call(path, body=None, token=None, headers=None, method=None):
    global IP_COUNTER
    with IP_LOCK:
        IP_COUNTER += 1
        # The isolated API trusts its loopback reverse proxy. Use documentation
        # addresses for separate synthetic clients, keeping functional tests from
        # exhausting another test's per-client rate-limit bucket.
        client_ip = '192.0.2.' + str(1 + (IP_COUNTER % 250))
    request_headers = {'X-Forwarded-For': client_ip, **(headers or {})}
    if token:
        request_headers['Authorization'] = 'Bearer ' + token
    encoded = None
    if body is not None:
        encoded = json.dumps(body).encode()
        request_headers['Content-Type'] = 'application/json'
    request = urllib.request.Request(API + path, data=encoded, headers=request_headers, method=method)
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    try:
        response = opener.open(request, timeout=40)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        text = response.read().decode('utf-8', errors='replace')
        try:
            parsed = json.loads(text)
        except json.JSONDecodeError:
            parsed = None
        return response.status, parsed, text, response.headers


def check(name, condition, detail=None):
    RESULTS.append({'check': name, 'passed': bool(condition)})
    print(('PASS ' if condition else 'FAIL ') + name, flush=True)
    if not condition:
        if detail is not None:
            # Results contain synthetic data only, never a provider credential.
            print('Synthetic failure detail: ' + str(detail)[:1800], flush=True)
        raise AssertionError(name)


def sql(statement, values=()):
    with sqlite3.connect(DB, timeout=20) as connection:
        return connection.execute(statement, values).fetchall()


def launch():
    env = os.environ.copy()
    for key in list(env):
        if any(word in key.upper() for word in ('STRIPE', 'RESEND', 'SUPABASE', 'CLOUDFLARE',
                                                  'AUTH__', 'ORDERING__', 'STORAGE__', 'SERVICEBILLING__', 'MERCHANTPAYMENTS__', 'NOTIFICATIONS__', 'MEDIA__')):
            env.pop(key)
    env.update({'ASPNETCORE_ENVIRONMENT': 'Development', 'ASPNETCORE_URLS': API,
        'Storage__DatabasePath': str(DB), 'Auth__Enabled': 'true', 'Auth__AllowLocalTestProvider': 'true',
        'Auth__SupabaseUrl': f'http://127.0.0.1:{PROVIDER.server_port}', 'Auth__PublishableKey': KEY,
        'Auth__PlatformOwnerUserId': 'supabase:' + PLATFORM,
        'Stripe__CheckoutEnabled': 'false', 'Stripe__InvoicesEnabled': 'false',
        'Ordering__PublicBaseUrl': 'https://ordering.example.invalid',
        'ReverseProxy__KnownClientProxy': '127.0.0.1'})
    log = (RUN / 'TideCasa.Api.log').open('w', encoding='utf-8')
    LOGS.append(log)
    proc = subprocess.Popen([str(SDK), str(BUILD_ROOT / 'TideCasa.Api/bin/Debug/net10.0/TideCasa.Api.dll')],
        cwd=ROOT / 'TideCasa.Api', env=env, stdout=log, stderr=subprocess.STDOUT,
        creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
    PROCESSES.append(proc)
    deadline = time.monotonic() + 180
    while time.monotonic() < deadline:
        if proc.poll() is not None:
            raise RuntimeError('Isolated API stopped; inspect ' + str(RUN / 'TideCasa.Api.log'))
        try:
            if call('/health')[0] == 200:
                return
        except (OSError, urllib.error.URLError):
            pass
        time.sleep(.5)
    raise TimeoutError('Isolated API startup timeout')


def seed(ident, owner=ALICE, status='active', enabled=True, vertical='bartide'):
    sql('INSERT INTO bartide_customers(id,slug,email,user_id,name,menu_json,version,status,enrollment_note,created_at,updated_at,vertical) '
        'VALUES (?,?,?,?,?,?,0,?,?,?,?,?)',
        (ident, ident, ident + '@example.invalid', 'supabase:' + owner, 'Synthetic ' + ident,
         '{}', status, PRIVATE, NOW, NOW, vertical))
    sql('INSERT INTO bartide_enhanced_configs(tenant_id,settings_json,version,updated_at) VALUES (?,?,0,?)',
        (ident, json.dumps({'enabled': enabled, 'preserve_unknown': {'nested': [1, True, 'keep']}}), NOW))


PATH = '/api/v1/tenants/team-one/team'


def sign_in(email):
    result = call('/api/v1/auth/signin', {'email': email, 'password': PASSWORD})
    check('Verified sign-in for ' + email, result[0] == 200, result[:3])
    return result[1]['accessToken']


def success(label, response):
    check(label, response[0] == 200, response[:3])
    return response[1]


def web_call(client, path, body=None, origin=None):
    headers = {}
    if origin is not None:
        headers['Origin'] = origin
    encoded = None
    if body is not None:
        headers['Content-Type'] = 'application/x-www-form-urlencoded'
        encoded = urllib.parse.urlencode(body).encode()
    request = urllib.request.Request(WEB + path, data=encoded, headers=headers)
    try:
        response = client.open(request, timeout=40)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        return response.status, response.read().decode('utf-8', errors='replace'), response.headers


def antiforgery(page):
    match = re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"', page)
    if not match:
        raise AssertionError('Rendered form did not supply antiforgery token')
    return html.unescape(match.group(1))


def web_login(email):
    jar = http.cookiejar.CookieJar()
    client = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect(), urllib.request.HTTPCookieProcessor(jar))
    status, page, _ = web_call(client, '/signin')
    check('SSR sign-in page loads for ' + email, status == 200)
    response = web_call(client, '/auth/session/signin', {'email': email, 'password': PASSWORD,
        'return_to': '/workspace/team-one/team', '__RequestVerificationToken': antiforgery(page)}, WEB)
    check('SSR sign-in succeeds for ' + email, response[0] == 302, (response[0], response[1][:500]))
    return client


def web_form(client, action, values):
    status, page, _ = web_call(client, '/workspace/team-one/team')
    if status != 200:
        raise AssertionError('Expected SSR team page: ' + str(status) + ' ' + page[:500])
    return web_call(client, '/team/manage/team-one/' + action,
        {**values, '__RequestVerificationToken': antiforgery(page)}, WEB)


def launch_web():
    env = os.environ.copy()
    for key in list(env):
        if any(word in key.upper() for word in ('STRIPE', 'RESEND', 'SUPABASE', 'CLOUDFLARE', 'AUTH__', 'ORDERING__', 'STORAGE__', 'SERVICEBILLING__', 'MERCHANTPAYMENTS__', 'NOTIFICATIONS__', 'MEDIA__')):
            env.pop(key)
    env.update({'ASPNETCORE_ENVIRONMENT': 'Development', 'ASPNETCORE_URLS': WEB,
        'Api__BaseUrl': API + '/', 'DataProtection__KeysPath': str(RUN / 'keys'),
        'Stripe__CheckoutEnabled': 'false', 'Stripe__InvoicesEnabled': 'false'})
    log = (RUN / 'TideCasa.Blazor.log').open('w', encoding='utf-8')
    LOGS.append(log)
    proc = subprocess.Popen([str(SDK), str(BUILD_ROOT / 'TideCasa.Blazor/bin/Debug/net10.0/TideCasa.Blazor.dll')],
        cwd=ROOT / 'TideCasa.Blazor', env=env, stdout=log, stderr=subprocess.STDOUT,
        creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
    PROCESSES.append(proc)
    client = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    deadline = time.monotonic() + 120
    while time.monotonic() < deadline:
        if proc.poll() is not None:
            raise RuntimeError('Isolated Blazor stopped; inspect ' + str(RUN / 'TideCasa.Blazor.log'))
        try:
            if web_call(client, '/health')[0] == 200:
                return client
        except (OSError, urllib.error.URLError):
            pass
        time.sleep(.5)
    raise TimeoutError('Isolated Blazor startup timeout')


try:
    launch()
    for args in [('team-one',), ('foreign', BOB), ('paused', ALICE, 'paused'),
                 ('disabled', ALICE, 'active', False), ('other-vertical', ALICE, 'active', True, 'fit-tide')]:
        seed(*args)
    owner = sign_in('alice@example.invalid')
    staff = sign_in('staff@example.invalid')
    outsider = sign_in('bob@example.invalid')
    platform = sign_in('platform@example.invalid')
    check('Anonymous team access denied', call(PATH)[0] == 401)
    check('Foreign owner cannot read team', call(PATH, token=outsider)[0] == 403)
    for tenant in ('paused', 'disabled', 'other-vertical'):
        check('Closed or unrelated tenant rejected: ' + tenant,
              call('/api/v1/tenants/' + tenant + '/team', token=owner)[0] == 403)
    ws = success('Owner workspace loads', call(PATH, token=owner))
    check('Owner has management role', ws['canManage'] and ws['memberId'] is None)
    before_json = sql('SELECT settings_json FROM bartide_enhanced_configs WHERE tenant_id=?', ('team-one',))[0][0]
    ws = success('Owner adds verified-email invitation', call(PATH + '/members',
        {'name': 'Kitchen Employee', 'email': 'STAFF@example.invalid', 'role': 'kitchen'}, owner))
    member = ws['members'][0]['id']
    check('Invitation begins unbound', not ws['members'][0]['accountLinked'] and
        sql('SELECT user_id FROM bartide_enhanced_members WHERE id=?', (member,))[0][0] is None)
    check('Case-insensitive duplicate invitation rejected', call(PATH + '/members',
        {'name': 'Another employee', 'email': 'Staff@example.invalid', 'role': 'driver'}, owner)[0] == 409)
    check('Unrecognized role rejected', call(PATH + '/members',
        {'name': 'Admin', 'email': 'admin@example.invalid', 'role': 'manager'}, owner)[0] == 400)
    ws = success('Staff opens team and binds durable user ID', call(PATH, token=staff))
    check('Staff cannot manage and has member ID', not ws['canManage'] and ws['memberId'] == member)
    check('Staff receives no team email or durable user IDs', all(m['email'] is None for m in ws['members']) and
          'supabase:' not in json.dumps(ws))
    check('Invitation binding saved', sql('SELECT user_id FROM bartide_enhanced_members WHERE id=?', (member,))[0][0] == 'supabase:' + STAFF)
    check('Staff cannot add another employee', call(PATH + '/members',
        {'name': 'No', 'email': 'no@example.invalid', 'role': 'driver'}, staff)[0] == 403)
    ws = success('Owner adds second team member', call(PATH + '/members',
        {'name': 'Driver Employee', 'email': 'driver@example.invalid', 'role': 'driver'}, owner))
    driver = next(m['id'] for m in ws['members'] if m['role'] == 'driver')
    # A valid offset is mandatory; overlapping instants are compared regardless of string timezone.
    day = datetime.now(timezone.utc).date().isoformat()
    shift = {'memberId': member, 'startsAt': day + 'T10:00:00-04:00', 'endsAt': day + 'T14:00:00-04:00', 'label': 'Lunch service'}
    ws = success('Owner creates a shift', call(PATH + '/shifts', shift, owner))
    shift_id = ws['shifts'][0]['id']
    check('Shift persisted as UTC', ws['shifts'][0]['startsAt'].startswith(day + 'T14:00:00'))
    check('Staff cannot schedule shifts', call(PATH + '/shifts', shift, staff)[0] == 403)
    check('Overlapping shift is rejected across timezones', call(PATH + '/shifts', {**shift,
          'startsAt': day + 'T15:00:00Z', 'endsAt': day + 'T19:00:00Z'}, owner)[0] == 409)
    for label, override in [('Timezone-less', {'startsAt': day + 'T10:00:00'}),
        ('Too short', {'endsAt': day + 'T10:10:00-04:00'}),
        ('Too long', {'endsAt': day + 'T23:00:00-10:00'}), ('Foreign member', {'memberId': 'not-in-this-tenant'})]:
        check(label + ' shift rejected', call(PATH + '/shifts', {**shift, **override}, owner)[0] == 400)
    check('Staff can read team schedule', len(success('Staff schedule loads', call(PATH, token=staff))['shifts']) == 1)
    message = {'requestId': str(uuid.uuid4()), 'text': 'Opening checklist\nBring clean aprons.'}
    ws = success('Staff posts team message', call(PATH + '/messages', message, staff))
    check('Message author comes from bound membership', ws['messages'][0]['author'] == 'Kitchen Employee')
    success('Message retry succeeds', call(PATH + '/messages', message, staff))
    check('Message retry does not duplicate', sql('SELECT COUNT(*) FROM bartide_enhanced_messages WHERE tenant_id=?', ('team-one',))[0][0] == 1)
    check('Changed message cannot reuse request ID', call(PATH + '/messages', {**message, 'text': 'Changed'}, staff)[0] == 409)
    check('Foreign member cannot read team messages', call(PATH, token=outsider)[0] == 403)
    check('Oversized message rejected', call(PATH + '/messages', {'requestId': str(uuid.uuid4()), 'text': 'x'*2001}, staff)[0] == 400)
    course_request = {'title': 'Opening safely', 'description': 'Owner-defined employee training', 'published': False}
    ws = success('Owner creates draft training course', call(PATH + '/courses', course_request, owner))
    course = ws['courses'][0]['id']
    check('Draft unavailable to unassigned employee', not success('Staff draft view loads', call(PATH, token=staff))['courses'])
    check('Staff cannot author course', call(PATH + '/courses', course_request, staff)[0] == 403)
    check('Empty course cannot publish', call(PATH + '/courses/' + course,
          {**course_request, 'published': True, 'expectedVersion': 0}, owner)[0] == 409)
    lesson_request = {'courseId': course, 'title': 'Welcome', 'description': 'Watch then mark complete.',
        'videoUrl': 'https://youtu.be/dQw4w9WgXcQ', 'position': 1}
    ws = success('Owner adds secure video lesson', call(PATH + '/lessons', lesson_request, owner))
    lesson = ws['lessons'][0]['id']
    check('YouTube video normalized to privacy embed', ws['lessons'][0]['videoSource'] == 'https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ')
    for url in ('javascript:alert(1)', 'https://youtube.com.evil.invalid/watch?v=dQw4w9WgXcQ',
                'http://youtu.be/dQw4w9WgXcQ', 'https://name:password@youtube.com/watch?v=dQw4w9WgXcQ',
                'https://localhost/video.mp4', 'https://youtube.com:444/watch?v=dQw4w9WgXcQ'):
        check('Untrusted video URL rejected: ' + url.split('/')[0],
              call(PATH + '/lessons', {**lesson_request, 'videoUrl': url}, owner)[0] == 400)
    ws = success('Owner assigns draft course to staff', call(PATH + '/courses/' + course + '/assignments',
        {'memberId': member, 'active': True, 'expectedVersion': -1}, owner))
    check('Assigned draft remains private', not success('Assigned staff draft view loads', call(PATH, token=staff))['courses'])
    check('Draft progress is rejected', call(PATH + '/lessons/' + lesson + '/progress', {'completed': True}, staff)[0] == 404)
    check('Owner cannot invent employee progress', call(PATH + '/lessons/' + lesson + '/progress', {'completed': True}, owner)[0] == 403)
    ws = success('Owner publishes course with lesson', call(PATH + '/courses/' + course,
        {**course_request, 'published': True, 'expectedVersion': 0}, owner))
    check('Course version increments', ws['courses'][0]['version'] == 1)
    check('Stale course update rejected', call(PATH + '/courses/' + course,
        {**course_request, 'published': False, 'expectedVersion': 0}, owner)[0] == 409)
    ws = success('Assigned staff reads published course', call(PATH, token=staff))
    check('Published course and lesson are visible', len(ws['courses']) == 1 and len(ws['lessons']) == 1)
    ws = success('Assigned staff records own completion', call(PATH + '/lessons/' + lesson + '/progress', {'completed': True}, staff))
    check('Progress bound to actual staff member', len(ws['progress']) == 1 and ws['progress'][0]['memberId'] == member and ws['progress'][0]['completed'])
    check('Progress durable in database', sql('SELECT completed FROM tide_staff_lesson_progress WHERE member_id=? AND lesson_id=?', (member, lesson)) == [(1,)])
    success('Employee may undo completion', call(PATH + '/lessons/' + lesson + '/progress', {'completed': False}, staff))
    check('Undo persisted', sql('SELECT completed FROM tide_staff_lesson_progress WHERE member_id=? AND lesson_id=?', (member, lesson)) == [(0,)])
    success('Employee may complete again', call(PATH + '/lessons/' + lesson + '/progress', {'completed': True}, staff))
    check('Published lesson cannot be deleted', call(PATH + '/lessons/' + lesson + '?expectedVersion=0', token=owner, method='DELETE')[0] == 409)
    success('Owner revokes a course assignment', call(PATH + '/courses/' + course + '/assignments',
        {'memberId': member, 'active': False, 'expectedVersion': 0}, owner))
    ws = success('Revoked assignment staff view loads', call(PATH, token=staff))
    check('Assignment revocation immediately hides content and progress', not ws['courses'] and not ws['lessons'] and not ws['progress'])
    check('Revoked assignment blocks progress write', call(PATH + '/lessons/' + lesson + '/progress', {'completed': False}, staff)[0] == 404)
    check('Stale assignment cannot override revocation', call(PATH + '/courses/' + course + '/assignments',
        {'memberId': member, 'active': True, 'expectedVersion': 0}, owner)[0] == 409)
    success('Owner restores assignment', call(PATH + '/courses/' + course + '/assignments',
        {'memberId': member, 'active': True, 'expectedVersion': 1}, owner))
    ws = success('Restored employee retains saved progress', call(PATH, token=staff))
    check('Historical progress was preserved', ws['progress'][0]['completed'])
    # A second course with no assignment remains invisible even when published.
    ws = success('Owner creates second course', call(PATH + '/courses', {**course_request, 'title': 'Managers only'}, owner))
    second = next(c['id'] for c in ws['courses'] if c['id'] != course)
    ws = success('Second course receives lesson', call(PATH + '/lessons', {**lesson_request, 'courseId': second,
          'videoUrl': 'https://vimeo.com/123456/abcdef123456'}, owner))
    second_lesson = next(l['id'] for l in ws['lessons'] if l['courseId'] == second)
    check('Vimeo privacy hash retained in canonical player URL', next(l['videoSource'] for l in ws['lessons'] if l['id'] == second_lesson) == 'https://player.vimeo.com/video/123456?h=abcdef123456')
    success('Second course publishes', call(PATH + '/courses/' + second, {**course_request, 'title': 'Managers only', 'published': True, 'expectedVersion': 0}, owner))
    ws = success('Unassigned course filtering loads', call(PATH, token=staff))
    check('Unassigned published content excluded', [c['id'] for c in ws['courses']] == [course] and all(l['courseId'] == course for l in ws['lessons']))
    check('Unassigned lesson progress forbidden', call(PATH + '/lessons/' + second_lesson + '/progress', {'completed': True}, staff)[0] == 404)
    check('Cross-tenant course update blocked', call('/api/v1/tenants/foreign/team/courses/' + course, {**course_request, 'expectedVersion': 1}, outsider)[0] == 409)
    check('Cross-tenant lesson reference blocked', call('/api/v1/tenants/foreign/team/lessons', lesson_request, outsider)[0] == 404)
    check('Cross-tenant staff assignment blocked', call('/api/v1/tenants/foreign/team/courses/' + course + '/assignments', {'memberId': member, 'active': True}, outsider)[0] == 400)
    # Corrupt/malicious legacy video locations never become iframe or object URLs.
    sql('UPDATE fit_lessons SET video_kind=?,video_source=? WHERE id=?', ('upload', 'PRIVATE-OBJECT-PATH', second_lesson))
    ws = success('Owner can inspect legacy upload lesson', call(PATH, token=owner))
    check('Unmigrated private video source is withheld', next(l['videoSource'] for l in ws['lessons'] if l['id'] == second_lesson) == '')
    # Deactivation takes effect for the existing bearer session, including writes.
    success('Owner pauses staff membership', call(PATH + '/members/' + member, {'expectedActive': True, 'active': False}, owner))
    check('Existing staff session loses workspace immediately', call(PATH, token=staff)[0] == 403)
    check('Inactive staff cannot send messages', call(PATH + '/messages', {'requestId': str(uuid.uuid4()), 'text': 'Denied'}, staff)[0] == 403)
    check('Inactive staff cannot alter progress', call(PATH + '/lessons/' + lesson + '/progress', {'completed': False}, staff)[0] == 403)
    check('Inactive staff cannot receive new shifts', call(PATH + '/shifts', {**shift, 'startsAt': day + 'T19:00:00Z', 'endsAt': day + 'T20:00:00Z'}, owner)[0] == 400)
    check('Stale membership form rejected', call(PATH + '/members/' + member, {'expectedActive': True, 'active': True}, owner)[0] == 409)
    success('Owner restores membership', call(PATH + '/members/' + member, {'expectedActive': False, 'active': True}, owner))
    check('Restored member session works', call(PATH, token=staff)[0] == 200)
    # Recycled email cannot claim the durable membership. Original ID remains authoritative.
    old_id = USERS['staff@example.invalid']['id']
    USERS['staff@example.invalid']['id'] = str(uuid.UUID(int=9901))
    recycled = sign_in('staff@example.invalid')
    check('Email reuse cannot take over bound membership', call(PATH, token=recycled)[0] == 403)
    check('Binding is unchanged after email reuse', sql('SELECT user_id FROM bartide_enhanced_members WHERE id=?', (member,))[0][0] == 'supabase:' + STAFF)
    USERS['staff@example.invalid']['id'] = old_id
    # Tenant ownership is rechecked after sign-in, too.
    sql('UPDATE bartide_customers SET user_id=? WHERE id=?', ('supabase:' + BOB, 'team-one'))
    check('Former owner cannot mutate team', call(PATH + '/members', {'name': 'Denied', 'email': 'denied@example.invalid', 'role': 'driver'}, owner)[0] == 403)
    sql('UPDATE bartide_customers SET user_id=? WHERE id=?', ('supabase:' + ALICE, 'team-one'))
    check('Configured platform owner can manage active team', success('Platform workspace loads', call(PATH, token=platform))['canManage'])
    # Removing lesson from a draft cleans staff progress but retains assignment.
    success('Owner returns course to draft', call(PATH + '/courses/' + course, {**course_request, 'published': False, 'expectedVersion': 1}, owner))
    check('Drafting a course immediately hides it', not success('Staff after unpublish loads', call(PATH, token=staff))['courses'])
    success('Owner removes draft lesson', call(PATH + '/lessons/' + lesson + '?expectedVersion=0', token=owner, method='DELETE'))
    check('Deleted lesson cascades only its progress', sql('SELECT COUNT(*) FROM tide_staff_lesson_progress WHERE lesson_id=?', (lesson,))[0][0] == 0)
    success('Owner deletes shift', call(PATH + '/shifts/' + shift_id, token=owner, method='DELETE'))
    check('Foreign shift delete is unavailable', call('/api/v1/tenants/foreign/team/shifts/' + shift_id, token=outsider, method='DELETE')[0] == 404)
    check('Unknown configuration JSON is unchanged', sql('SELECT settings_json FROM bartide_enhanced_configs WHERE tenant_id=?', ('team-one',))[0][0] == before_json)
    check('Customer learner and progress tables untouched', sql('SELECT COUNT(*) FROM fit_learners')[0][0] == 0 and sql('SELECT COUNT(*) FROM fit_progress')[0][0] == 0)
    check('Database foreign keys remain valid', not sql('PRAGMA foreign_key_check'))
    # Fresh SSR forms exercise the actual cookie and API boundary together.
    anonymous = launch_web()
    check('Anonymous SSR team page redirects to sign-in', web_call(anonymous, '/workspace/team-one/team')[0] == 302)
    owner_browser = web_login('alice@example.invalid')
    staff_browser = web_login('staff@example.invalid')
    status, page, headers = web_call(owner_browser, '/workspace/team-one/team')
    check('Owner SSR page exposes schedule, messages and training', status == 200 and all(text in page for text in ('Employee onboarding courses', 'Add employee', 'Add shift', 'Post message', 'Create draft course')))
    check('Owner SSR response is not cached', 'no-store' in headers.get('Cache-Control', ''))
    check('Dedicated team stylesheet is served', web_call(anonymous, '/staff-training.css')[0] == 200)
    token = antiforgery(page)
    check('Cross-origin team form is rejected', web_call(owner_browser, '/team/manage/team-one/member',
        {'name': 'Malicious', 'email': 'bad@example.invalid', 'role': 'driver', '__RequestVerificationToken': token}, 'https://foreign.example.invalid')[0] == 400)
    missing = web_call(owner_browser, '/team/manage/team-one/member', {'name': 'No token', 'email': 'bad@example.invalid', 'role': 'driver'}, WEB)
    check('Missing antiforgery does not write team membership', missing[0] == 302 and 'notice=expired' in missing[2].get('Location', '') and
        not sql('SELECT id FROM bartide_enhanced_members WHERE email=?', ('bad@example.invalid',)))
    response = web_form(owner_browser, 'member', {'name': 'SSR Employee', 'email': 'ssr@example.invalid', 'role': 'kitchen'})
    check('Owner SSR employee form reaches reusable API', response[0] == 302 and 'notice=saved' in response[2].get('Location', '') and
        bool(sql('SELECT id FROM bartide_enhanced_members WHERE email=?', ('ssr@example.invalid',))))
    response = web_form(owner_browser, 'shift', {'member': member, 'label': 'SSR closing', 'starts': day + 'T20:00', 'ends': day + 'T22:00', 'timezone': 'America/New_York'})
    check('SSR shift form resolves named timezone and saves', response[0] == 302 and 'notice=saved' in response[2].get('Location', '') and
        bool(sql('SELECT id FROM bartide_enhanced_shifts WHERE label=?', ('SSR closing',))))
    response = web_form(owner_browser, 'course', {'title': 'SSR Onboarding', 'description': 'From the real form', 'published': 'false'})
    check('SSR create course form saves', response[0] == 302 and 'notice=saved' in response[2].get('Location', ''))
    ssr_course = sql('SELECT id FROM fit_courses WHERE title=?', ('SSR Onboarding',))[0][0]
    response = web_form(owner_browser, 'lesson', {'course': ssr_course, 'title': 'SSR lesson', 'description': 'Safe\nnotes', 'video': 'https://youtu.be/dQw4w9WgXcQ', 'position': '1'})
    check('SSR video lesson form saves with multiline notes', response[0] == 302 and 'notice=saved' in response[2].get('Location', ''))
    ssr_lesson = sql('SELECT id FROM fit_lessons WHERE course_id=?', (ssr_course,))[0][0]
    response = web_form(owner_browser, 'lesson', {'id': ssr_lesson, 'course': ssr_course, 'version': '0',
        'title': 'SSR lesson', 'description': '\u5b66' * 4000, 'video': 'https://youtu.be/dQw4w9WgXcQ', 'position': '1'})
    check('SSR form accepts maximum multilingual lesson notes', response[0] == 302 and 'notice=saved' in response[2].get('Location', '') and
        sql('SELECT length(description) FROM fit_lessons WHERE id=?', (ssr_lesson,)) == [(4000,)])
    response = web_form(owner_browser, 'assignment', {'course': ssr_course, 'member': member, 'version': '-1', 'active': 'true'})
    check('SSR assignment form creates explicit staff access', response[0] == 302 and 'notice=saved' in response[2].get('Location', ''))
    response = web_form(owner_browser, 'course', {'id': ssr_course, 'version': '0', 'title': 'SSR Onboarding', 'description': 'From the real form', 'published': 'true'})
    check('SSR course form publishes saved lesson', response[0] == 302 and 'notice=saved' in response[2].get('Location', ''))
    status, staff_page, _ = web_call(staff_browser, '/workspace/team-one/team')
    check('Staff SSR page shows assigned video and completion form', status == 200 and 'SSR Onboarding' in staff_page and 'I completed this lesson' in staff_page and 'youtube-nocookie.com/embed/dQw4w9WgXcQ' in staff_page)
    check('Staff SSR page omits owner controls and emails', all(text not in staff_page for text in ('Add employee', 'Create draft course', 'Assign course', 'staff@example.invalid', 'driver@example.invalid')))
    response = web_form(staff_browser, 'progress', {'id': ssr_lesson, 'completed': 'true'})
    check('Staff SSR progress saves actual own completion', response[0] == 302 and 'notice=saved' in response[2].get('Location', '') and
        sql('SELECT completed FROM tide_staff_lesson_progress WHERE member_id=? AND lesson_id=?', (member, ssr_lesson)) == [(1,)])
    response = web_form(staff_browser, 'member', {'name': 'No', 'email': 'not-owner@example.invalid', 'role': 'driver'})
    check('Crafted staff management form denied by API', response[0] == 302 and 'notice=denied' in response[2].get('Location', ''))
    injection = '<script>alert("synthetic")</script>'
    response = web_form(staff_browser, 'message', {'request_id': str(uuid.uuid4()), 'text': injection})
    check('Staff SSR message form saves', response[0] == 302 and 'notice=saved' in response[2].get('Location', ''))
    _, rendered, _ = web_call(staff_browser, '/workspace/team-one/team')
    check('Message content is HTML encoded', injection not in rendered and '&lt;script&gt;' in rendered)
    response = web_form(owner_browser, 'member-state', {'id': member, 'expected_active': 'true', 'active': 'false'})
    check('Owner SSR form pauses membership', response[0] == 302 and 'notice=saved' in response[2].get('Location', ''))
    check('Existing staff browser loses team access on next page', web_call(staff_browser, '/workspace/team-one/team')[0] == 403)
finally:
    for proc in PROCESSES:
        if proc.poll() is None:
            proc.terminate()
            try:
                proc.wait(timeout=30)
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.wait(timeout=10)
    PROVIDER.shutdown()
    PROVIDER.server_close()
    for log in LOGS:
        log.close()
    (RUN / 'results.json').write_text(json.dumps(RESULTS, indent=2), encoding='utf-8')
    print('Staff and training evidence: ' + str(RUN), flush=True)

print(str(len(RESULTS)) + ' staff/training checks passed; synthetic local data only.', flush=True)

