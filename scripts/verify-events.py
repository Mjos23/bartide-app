"""Events: real isolated API, database and native SSR forms; fake identity only.

No external services or real users. DLLs are copied into the evidence directory
before launch, allowing the main build to continue without executable file locks.
"""
import concurrent.futures
import copy
from datetime import datetime, timedelta, timezone
import html
import http.cookiejar
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

import restaurant_test_support as s

s.RUN = s.ROOT / '.tools/events-verification' / datetime.now(timezone.utc).strftime('%Y%m%d-%H%M%S-%f')
s.RUN.mkdir(parents=True)
s.DB = s.RUN / 'synthetic.db'
WEB = 'http://127.0.0.1:' + str(s.port())
spec = importlib.util.spec_from_file_location('event_html_forms', Path(__file__).with_name('verify-management-web.py'))
forms_module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(forms_module)
Forms = forms_module.Forms
check = s.check
tokens = {}


def launch(project, address, extra):
    copied = s.RUN / project
    if not copied.exists():
        source = Path(os.environ.get('EVENTS_ARTIFACTS', '')) / 'bin' / project / 'debug' if os.environ.get('EVENTS_ARTIFACTS') else s.ROOT / project / 'bin/Debug/net10.0'
        shutil.copytree(source, copied, ignore=shutil.ignore_patterns("libSkiaSharp.pdb"))
    env = os.environ.copy()
    for name in list(env):
        if any(word in name.upper() for word in ('STRIPE', 'RESEND', 'SUPABASE', 'CLOUDFLARE', 'AUTH__', 'ORDERING__', 'STORAGE__', 'API__', 'DATAPROTECTION__', 'MEDIA__')):
            env.pop(name)
    env.update({'ASPNETCORE_ENVIRONMENT': 'Development', 'ASPNETCORE_URLS': address,
        'Stripe__CheckoutEnabled': 'false', 'Stripe__InvoicesEnabled': 'false', **extra})
    output = (s.RUN / (project + '.log')).open('w', encoding='utf-8')
    s.LOGS.append(output)
    proc = subprocess.Popen([str(s.SDK), str(copied / (project + '.dll'))], cwd=s.ROOT / project,
        env=env, stdout=output, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
    s.PROCESSES.append(proc)
    deadline = time.monotonic() + 150
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    while time.monotonic() < deadline:
        if proc.poll() is not None:
            raise RuntimeError(project + ' stopped; inspect its local fixture log')
        try:
            with opener.open(address + '/health', timeout=3) as response:
                if response.status == 200:
                    return proc
        except (OSError, urllib.error.URLError):
            pass
        time.sleep(.3)
    raise TimeoutError(project)


def owner_path(event='', suffix=''):
    return '/api/v1/tenants/bistro/events' + ('/' + event if event else '') + suffix


def public_path(event='', suffix=''):
    return '/api/v1/restaurants/bistro/events' + ('/' + event if event else '') + suffix


def api(path, body=None, person=None):
    return s.call(path, body, tokens.get(person))


def save_request(**changes):
    start = datetime.now(timezone.utc) + timedelta(hours=2)
    return {'requestKey': str(uuid.uuid4()), 'expectedVersion': 0, 'title': 'Synthetic live music',
        'details': 'A friendly night\nBring your friends.', 'location': 'Synthetic Bistro',
        'startsAt': start.isoformat(), 'endsAt': (start + timedelta(hours=2)).isoformat(),
        'timeZone': 'America/New_York', 'capacity': 2, 'published': False, **changes}


def rsvp(event, person, attend=True, version=-1, key=None):
    request = {'requestKey': key or str(uuid.uuid4()), 'expectedVersion': version, 'attending': attend}
    return api(public_path(event, '/rsvp'), request, person), request


def browser():
    return urllib.request.build_opener(urllib.request.ProxyHandler({}), s.NoRedirect(), urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))


def web(client, path, fields=None, headers=None):
    encoded = None if fields is None else urllib.parse.urlencode(fields).encode()
    request_headers = {} if fields is None else {'Content-Type': 'application/x-www-form-urlencoded', 'Origin': WEB}
    request_headers.update(headers or {})
    request = urllib.request.Request(WEB + path, data=encoded, headers=request_headers)
    try:
        response = client.open(request, timeout=40)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        return response.status, response.read().decode(), response.headers


def form(client, path, suffix=None, **fields):
    response = web(client, path)
    check('SSR page loads ' + path, response[0] == 200, response[:2])
    parsed_forms = Forms(response[1]).forms
    # Razor may serialize an empty value attribute as `value`; browsers submit
    # that successful input as an empty string, while HTMLParser reports None.
    for parsed in parsed_forms:
        parsed['fields'] = {key: '' if value is None else value for key, value in parsed['fields'].items()}
    matches = [f for f in parsed_forms if (suffix is None or f['action'].endswith(suffix))
        and all(f['fields'].get(k) == str(v) for k, v in fields.items())]
    if len(matches) != 1:
        (s.RUN / 'failed-form.html').write_text(response[1], encoding='utf-8')
    check('Expected native form exists', len(matches) == 1, (suffix, fields, len(matches), [(f['action'], {k: v for k, v in f['fields'].items() if k in ('event_id', 'version', 'action')}) for f in parsed_forms]))
    return matches[0], response


def post(client, selected, changes=None, headers=None, remove=()):
    fields = {**selected['fields'], **(changes or {})}
    for key in remove:
        fields.pop(key, None)
    return web(client, selected['action'], fields, headers)


def notice(response, expected, name):
    check(name, response[0] in (302, 303) and ('notice=' + expected) in response[2].get('Location', ''), response[:2])


def login(person):
    client = browser()
    selected, _ = form(client, '/signin', '/auth/session/signin')
    response = post(client, selected, {'email': person + '@example.invalid', 'password': s.PASSWORD})
    check('Native sign in ' + person, response[0] in (302, 303) and '/account' in response[2].get('Location', ''), response[:2])
    return client


def run():
    api_env = {'Storage__DatabasePath': str(s.DB), 'Auth__Enabled': 'true', 'Auth__AllowLocalTestProvider': 'true',
        'Auth__SupabaseUrl': f'http://127.0.0.1:{s.PROVIDER.server_port}', 'Auth__PublishableKey': s.KEY,
        'Auth__PlatformOwnerUserId': 'supabase:' + s.PLATFORM, 'ReverseProxy__KnownClientProxy': '127.0.0.1'}
    api_process = launch('TideCasa.Api', s.API, api_env)
    for args in [('bistro',), ('foreign', s.BOB), ('draft', s.ALICE, 'draft'), ('basic', s.ALICE, 'active', False)]:
        s.seed(*args)
    for n in range(8):
        s.USERS[f'guest{n}@example.invalid'] = {'id': str(uuid.UUID(int=800+n)), 'email': f'guest{n}@example.invalid',
            'email_confirmed_at': '2026-01-01T00:00:00Z', 'is_anonymous': False, 'user_metadata': {'full_name': f'Guest {n}'}}
    for person in ['alice', 'bob', 'staff', 'platform'] + ['guest' + str(n) for n in range(8)]:
        response = s.call('/api/v1/auth/signin', {'email': person + '@example.invalid', 'password': s.PASSWORD})
        check('Verified synthetic sign in ' + person, response[0] == 200, response[:2])
        tokens[person] = response[1]['accessToken']
    check('Owner API rejects anonymous', api(owner_path())[0] == 401)
    check('Foreign owner rejected', api(owner_path(), person='bob')[0] == 403)
    check('Staff cannot manage events', api(owner_path(), person='staff')[0] == 403)
    check('Inactive venue hidden', api('/api/v1/restaurants/draft/events')[0] == 404)
    check('Not-enrolled venue hidden', api('/api/v1/restaurants/basic/events')[0] == 404)
    for changes in [{'capacity': 0}, {'capacity': 501}, {'timeZone': 'Made/Up'}, {'startsAt': '2026-10-01T12:00:00'}, {'startsAt': '2000-01-01T00:00:00Z'}, {'title': '\0bad'}]:
        check('Invalid event rejected ' + str(changes), api(owner_path(), save_request(**changes), 'alice')[0] == 400)
    draft = save_request()
    created = api(owner_path(), draft, 'alice')
    check('Create owner draft', created[0] == 200 and created[1]['state'] == 'draft', created[:2])
    event = created[1]['id']
    check('Duplicate create returns same event', api(owner_path(), draft, 'alice')[1]['id'] == event)
    check('Conflicting create replay rejected', api(owner_path(), {**draft, 'title': 'different'}, 'alice')[0] == 409)
    check('Draft absent publicly', api(public_path())[1]['events'] == [])
    check('Draft RSVP closed', rsvp(event, 'bob')[0][0] == 409)
    published = {**draft, 'requestKey': str(uuid.uuid4()), 'published': True}
    result = api(owner_path(event), published, 'alice')
    check('Owner publishes event', result[0] == 200 and result[1]['version'] == 1, result[:2])
    check('Publish replay does not bump version', api(owner_path(event), published, 'alice')[1]['version'] == 1)
    check('Old-version edit rejected', api(owner_path(event), {**published, 'requestKey': str(uuid.uuid4())}, 'alice')[0] == 409)
    public = api(public_path())
    check('Published event appears without attendee PII', public[0] == 200 and len(public[1]['events']) == 1 and 'email' not in public[2] and 'userId' not in public[2])
    check('Anonymous cannot reserve', rsvp(event, None)[0][0] == 401)
    requests = {person: {'requestKey': str(uuid.uuid4()), 'expectedVersion': -1, 'attending': True} for person in ['guest' + str(n) for n in range(8)]}
    with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
        results = dict(zip(requests, pool.map(lambda person: api(public_path(event, '/rsvp'), requests[person], person), requests)))
    winners = [name for name, response in results.items() if response[0] == 200]
    losers = [name for name, response in results.items() if response[0] == 409 and response[1]['code'] == 'event_full']
    check('Concurrent reservations admit exactly capacity', len(winners) == 2 and len(losers) == 6, [(name, r[:2]) for name, r in results.items()])
    check('Capacity never oversubscribed in database', s.sql("SELECT COUNT(*) FROM tide_event_rsvps WHERE event_id=? AND state='confirmed'", (event,))[0][0] == 2)
    check('RSVP retry is idempotent', api(public_path(event, '/rsvp'), requests[winners[0]], winners[0])[0] == 200)
    check('RSVP altered replay rejected', api(public_path(event, '/rsvp'), {**requests[winners[0]], 'attending': False}, winners[0])[0] == 409)
    mine = api(public_path(suffix='/mine'), person=winners[0])
    check('Guest sees only own reservation without email', len(mine[1]['reservations']) == 1 and 'email' not in mine[2] and 'userId' not in mine[2])
    check('Other customer sees no reservations', api(public_path(suffix='/mine'), person='bob')[1]['reservations'] == [])
    check('Private guests forbidden to customer', api(owner_path(event, '/guests'), person=winners[0])[0] == 403)
    check('Foreign tenant cannot expose guest list', api('/api/v1/tenants/foreign/events/' + event + '/guests', person='bob')[0] == 404)
    check('Owner sees complete private list', len(api(owner_path(event, '/guests'), person='alice')[1]['guests']) == 2)
    for changes, code in [({'capacity': 1}, 'capacity_below_reserved'), ({'published': False}, 'event_has_guests')]:
        response = api(owner_path(event), {**published, 'requestKey': str(uuid.uuid4()), 'expectedVersion': 1, **changes}, 'alice')
        check('Reservation-safe owner edit ' + code, response[0] == 409 and response[1]['code'] == code)
    cancelled, cancel_request = rsvp(event, winners[0], False, 0)
    check('Customer cancels own reservation', cancelled[0] == 200 and cancelled[1]['state'] == 'cancelled' and cancelled[1]['version'] == 1)
    check('Cancellation replay stable', api(public_path(event, '/rsvp'), cancel_request, winners[0])[1]['version'] == 1)
    check('Stale request cannot restore cancelled RSVP', rsvp(event, winners[0], True, 0)[0][0] == 409)
    replacement, _ = rsvp(event, losers[0])
    check('Freed place can be reserved', replacement[0] == 200)
    checkin_request = {'requestKey': str(uuid.uuid4()), 'userId': 'supabase:' + s.USERS[winners[1] + '@example.invalid']['id'], 'expectedVersion': 0}
    check('Customer cannot check in guests', api(owner_path(event, '/check-in'), checkin_request, winners[1])[0] == 403)
    checked = api(owner_path(event, '/check-in'), checkin_request, 'alice')
    check('Owner checks in confirmed guest', checked[0] == 200 and any(g['state'] == 'checked_in' for g in checked[1]['guests']), checked[:2])
    check('Check-in replay stable', api(owner_path(event, '/check-in'), checkin_request, 'alice')[0] == 200)
    check('Checked-in guest still consumes capacity', api(public_path())[1]['events'][0]['reserved'] == 2)
    check('Checked-in guest cannot cancel attendance', rsvp(event, winners[1], False, 1)[0][0] == 409)
    cancel_event = {'requestKey': str(uuid.uuid4()), 'expectedVersion': 1}
    check('Owner cancels event', api(owner_path(event, '/cancel'), cancel_event, 'alice')[1]['state'] == 'cancelled')
    check('Cancellation retry stable', api(owner_path(event, '/cancel'), cancel_event, 'alice')[1]['version'] == 2)
    check('Cancelled event hidden publicly', api(public_path())[1]['events'] == [])
    check('Guest sees event cancellation', api(public_path(suffix='/mine'), person=losers[0])[1]['reservations'][0]['rsvp']['eventState'] == 'cancelled')
    check('Cannot reserve cancelled event', rsvp(event, 'bob')[0][0] == 409)
    check('Can leave cancelled event safely', rsvp(event, losers[0], False, 0)[0][0] == 200)
    check('Cancellation preserves private historical records', len(api(owner_path(event, '/guests'), person='alice')[1]['guests']) == 3)
    check('Cancellation preserves audit', s.sql("SELECT COUNT(*) FROM tide_event_audit WHERE event_id=? AND action='cancelled'", (event,))[0][0] == 1)
    # Owner capacity changes share the same serialized write boundary as RSVPs.
    race_request = save_request(published=True)
    race_event = api(owner_path(), race_request, 'alice')[1]['id']
    check('Capacity-race initial reservation', rsvp(race_event, 'bob')[0][0] == 200)
    resize = {**race_request, 'requestKey': str(uuid.uuid4()), 'capacity': 1}
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        resize_future = pool.submit(api, owner_path(race_event), resize, 'alice')
        reserve_future = pool.submit(rsvp, race_event, 'staff')
        resize_result, reserve_result = resize_future.result(), reserve_future.result()[0]
    check('Capacity reduction race has one safe winner', sorted([resize_result[0], reserve_result[0]]) == [200, 409], (resize_result[:2], reserve_result[:2]))
    race_state = api(owner_path(), person='alice')[1]['events']
    current_race = next(x for x in race_state if x['id'] == race_event)
    check('Capacity still holds after owner/customer race', current_race['reserved'] <= current_race['capacity'])
    retry_request = save_request(published=True, capacity=1)
    retry_event = api(owner_path(), retry_request, 'alice')[1]['id']
    same_request = {'requestKey': str(uuid.uuid4()), 'expectedVersion': -1, 'attending': True}
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        retries = list(pool.map(lambda _: api(public_path(retry_event, '/rsvp'), same_request, 'bob'), range(4)))
    check('Concurrent identical retries all return confirmed', all(r[0] == 200 and r[1]['state'] == 'confirmed' and r[1]['version'] == 0 for r in retries))
    check('Concurrent retries consume one place', s.sql('SELECT COUNT(*) FROM tide_event_rsvps WHERE event_id=?', (retry_event,))[0][0] == 1)
    check('Concurrent retries create one audit action', s.sql("SELECT COUNT(*) FROM tide_event_audit WHERE event_id=? AND action='rsvp_confirmed'", (retry_event,))[0][0] == 1)
    # Expiration, inactive businesses, and unverified identities must fail closed.
    s.sql("UPDATE tide_events SET starts_at='2000-01-01T00:00:00Z',ends_at='2000-01-01T01:00:00Z' WHERE id=?", (retry_event,))
    check('Past event hidden from public list', all(x['id'] != retry_event for x in api(public_path())[1]['events']))
    check('Past event refuses reservation', rsvp(retry_event, 'staff')[0][0] == 409)
    s.sql("UPDATE bartide_customers SET status='paused' WHERE id='bistro'")
    check('Paused business blocks event listing', api(public_path())[0] == 404)
    check('Paused business blocks RSVP writes', rsvp(race_event, 'alice')[0][0] == 404)
    s.sql("UPDATE bartide_customers SET status='active' WHERE id='bistro'")
    s.USERS['unverified@example.invalid'] = {'id': str(uuid.uuid4()), 'email': 'unverified@example.invalid', 'email_confirmed_at': None, 'is_anonymous': False, 'user_metadata': {}}
    not_verified = s.call('/api/v1/auth/signin', {'email': 'unverified@example.invalid', 'password': s.PASSWORD})
    check('Unverified account cannot obtain registered session', not_verified[0] != 200)
    s.USERS['longname@example.invalid'] = {'id': str(uuid.uuid4()), 'email': 'longname@example.invalid', 'email_confirmed_at': '2026-01-01T00:00:00Z', 'is_anonymous': False, 'user_metadata': {'full_name': 'G' * 200}}
    long_signin = s.call('/api/v1/auth/signin', {'email': 'longname@example.invalid', 'password': s.PASSWORD})
    check('Long valid profile signs in', long_signin[0] == 200)
    tokens['longname'] = long_signin[1]['accessToken']
    name_event = api(owner_path(), save_request(published=True), 'alice')[1]['id']
    check('Full allowed profile name can RSVP', rsvp(name_event, 'longname')[0][0] == 200)
    check('Foreign-key integrity', s.sql('PRAGMA foreign_key_check') == [])
    before_restart = s.sql('SELECT COUNT(*) FROM tide_event_rsvps')[0][0]
    api_process.terminate(); api_process.wait(timeout=15)
    launch('TideCasa.Api', s.API, api_env)
    check('Reservations survive application restart', s.sql('SELECT COUNT(*) FROM tide_event_rsvps')[0][0] == before_restart)
    check('Owner reads durable cancellation after restart', next(x for x in api(owner_path(), person='alice')[1]['events'] if x['id'] == event)['state'] == 'cancelled')
    if '--api-only' in sys.argv:
        return
    launch('TideCasa.Blazor', WEB, {'Api__BaseUrl': s.API + '/', 'DataProtection__KeysPath': str(s.RUN / 'keys')})
    owner = login('alice'); customer = login('bob'); visitor = browser()
    check('Anonymous owner page requires sign-in', web(visitor, '/workspace/bistro/events')[0] in (302, 303))
    check('Customer cannot open owner page', web(customer, '/workspace/bistro/events')[0] == 403)
    new, owner_page = form(owner, '/workspace/bistro/events', '/save', event_id='')
    check('Owner SSR protects cache', 'no-store' in owner_page[2].get('Cache-Control', ''))
    check('Missing CSRF rejected', post(owner, new, remove=('__RequestVerificationToken',))[0] == 400)
    check('Cross-origin form rejected', post(owner, new, headers={'Origin': 'https://evil.example.invalid'})[0] == 400)
    starts = (datetime.now(timezone.utc) + timedelta(hours=2)).strftime('%Y-%m-%dT%H:%M')
    ends = (datetime.now(timezone.utc) + timedelta(hours=4)).strftime('%Y-%m-%dT%H:%M')
    fields = {'title': 'SSR <script>alert(1)</script> music', 'details': '界' * 3990 + '\nWelcome', 'location': 'Synthetic SSR venue', 'starts_at': starts, 'ends_at': ends, 'time_zone': 'UTC', 'capacity': '3', 'published': 'true'}
    notice(post(owner, new, fields), 'saved', 'Native event form saves large multilingual details')
    notice(post(owner, new, fields), 'saved', 'Native create form retry does not duplicate')
    native_id = new['fields']['request_key']
    check('One event for repeated form', s.sql('SELECT COUNT(*) FROM tide_events WHERE id=?', (native_id,))[0][0] == 1)
    visit = web(visitor, '/events/bistro')
    check('Visitor sees safe public event HTML', visit[0] == 200 and '&lt;script&gt;' in visit[1] and '<script>alert(1)</script>' not in visit[1])
    check('Visitor has sign-in RSVP link', 'Sign in to RSVP' in visit[1])
    check('Public personalized page has no-store', 'no-store' in visit[2].get('Cache-Control', ''))
    reserve, page = form(customer, '/events/bistro', '/' + native_id, action='attend')
    check('Customer sees guest-list privacy explanation', 'shared privately with the business' in page[1])
    check('Anonymous form cannot reserve', post(visitor, reserve)[0] == 401)
    check('Guest missing CSRF blocked', post(customer, reserve, remove=('__RequestVerificationToken',))[0] == 400)
    check('Guest cross-origin blocked', post(customer, reserve, headers={'Origin': 'https://evil.example.invalid'})[0] == 400)
    notice(post(customer, reserve), 'reserved', 'Customer reserves through real native form')
    cancel_rsvp, page = form(customer, '/events/bistro', '/' + native_id, action='cancel')
    check('Own RSVP status visible', 'one place reserved' in page[1])
    check('No attendee email in public page', 'bob@example.invalid' not in page[1])
    checkin, guestpage = form(owner, '/workspace/bistro/events?event=' + native_id, '/' + native_id + '/check-in')
    check('Owner sees private guest email', 'bob@example.invalid' in guestpage[1])
    notice(post(owner, checkin), 'confirm-present', 'Check-in requires presence confirmation')
    notice(post(customer, cancel_rsvp), 'left', 'Customer cancels using native form')
    reserve2, _ = form(customer, '/events/bistro', '/' + native_id, action='attend')
    notice(post(customer, reserve2), 'reserved', 'Customer can reserve again with fresh version')
    checkin2, _ = form(owner, '/workspace/bistro/events?event=' + native_id, '/' + native_id + '/check-in')
    notice(post(owner, checkin2, {'confirm_present': 'true'}), 'saved', 'Owner native guest check-in works')
    check('Check-in reflected on guest page', 'checked in' in web(customer, '/events/bistro')[1])
    edit, _ = form(owner, '/workspace/bistro/events', '/save', event_id=native_id)
    notice(post(owner, edit, {'starts_at': '2027-03-14T02:30', 'ends_at': '2027-03-14T04:00', 'time_zone': 'America/New_York'}), 'time', 'Skipped DST time is rejected')
    notice(post(owner, edit, {'starts_at': '2026-11-01T01:30', 'ends_at': '2026-11-01T04:00', 'time_zone': 'America/New_York'}), 'time', 'Ambiguous DST time is rejected')
    notice(post(owner, edit, {'time_zone': 'Europe/London'}), 'saved', 'Native form accepts supported non-default IANA zone')
    edit, _ = form(owner, '/workspace/bistro/events', '/save', event_id=native_id)
    check('API-created time zone remains editable', edit['fields']['time_zone'] == 'Europe/London')
    cancel, _ = form(owner, '/workspace/bistro/events', '/' + native_id + '/cancel')
    notice(post(owner, cancel), 'confirm-cancel', 'Native event cancellation requires confirmation')
    notice(post(owner, cancel, {'confirm_cancel': 'true'}), 'saved', 'Owner cancels through native form')
    check('Guest sees cancellation update', 'cancelled by the business' in web(customer, '/events/bistro')[1])
    check('Cancelled event not leaked to anonymous HTML', fields['title'] not in html.unescape(web(visitor, '/events/bistro')[1]))
    check('Feature migration is recorded', s.sql("SELECT COUNT(*) FROM tide_feature_migrations WHERE resource_name LIKE '%0003_events.sql'")[0][0] == 1)


try:
    run()
finally:
    for process in reversed(s.PROCESSES):
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=15)
            except subprocess.TimeoutExpired:
                process.kill(); process.wait(timeout=10)
    s.PROVIDER.shutdown()
    for log in s.LOGS:
        log.close()
    evidence = {'suite': 'events', 'checks': s.RESULTS, 'passed': sum(x['passed'] for x in s.RESULTS), 'total': len(s.RESULTS), 'externalWrites': False}
    (s.RUN / 'results.json').write_text(json.dumps(evidence, indent=2), encoding='utf-8')
    print('Evidence: ' + str(s.RUN / 'results.json'), flush=True)
