"""Verify driver dispatch through rendered native forms against an isolated API/web/SQLite fixture.

Only the local identity provider is fake. No production, real email or payment
providers are used. Build API and Blazor first. TIDE_DISPATCH_WEB_BUILD_DIR may
point to a separate Blazor output folder. --hold keeps the synthetic fixture for
a browser walkthrough until its reported stop file appears or the timeout ends.
"""
import argparse
import calendar
from datetime import datetime, timedelta, timezone
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import time
import urllib.parse

spec = importlib.util.spec_from_file_location('management_web', Path(__file__).with_name('verify-management-web.py'))
w = importlib.util.module_from_spec(spec)
spec.loader.exec_module(w)
s = w.support
PAGE = '/workspace/bistro/deliveries'


def check(name, condition, detail=None):
    s.check(name, condition, detail)


def notice(response, expected, name):
    check(name, response[0] in (302, 303) and ('notice=' + expected) in response[2].get('Location', ''), response[:2])


def page(client):
    return w.forms_for(client, PAGE)


def token(person):
    return next(key for key, user in s.TOKENS.items() if user['email'] == person + '@example.invalid')


def board(person='alice'):
    response = s.call('/api/v1/tenants/bistro/delivery-dispatch', token=token(person))
    assert response[0] == 200, response[:3]
    return response[1]


def entry(ident):
    response = s.call('/api/v1/tenants/bistro/ordering/operations', token=token('alice'))
    assert response[0] == 200, response[:3]
    return next(item for item in response[1]['orders'] if item['order']['receipt']['orderId'] == ident)


def profile(client, ident):
    return w.find_form(page(client)[0], '/dispatch/profile', driver=ident)


def plan(client, ident, mode):
    return w.find_form(page(client)[0], '/dispatch/orders/' + ident, mode=mode)


def create_order(name):
    response = s.submit(s.order_request({'items': [{'itemId': 'dish-1', 'quantity': 2}],
        'fulfillment': 'delivery', 'deliveryZip': '33101', 'paymentMethod': 'staff'}, customerName=name))
    check('Create synthetic delivery ' + name, response[0] == 201, response[:3])
    return response[1]['orderId']


def prepare_runtime():
    source_root = Path(os.environ.get('TIDE_TEST_BUILD_ROOT', str(s.ROOT)))
    if os.environ.get('TIDE_TEST_IN_PLACE_BUILD') == '1':
        assert not os.environ.get('TIDE_DISPATCH_WEB_BUILD_DIR'), 'In-place checks use a single immutable build root'
        return
    sources = {'TideCasa.Api': source_root / 'TideCasa.Api/bin/Debug/net10.0',
        'TideCasa.Blazor': Path(os.environ.get('TIDE_DISPATCH_WEB_BUILD_DIR', str(source_root / 'TideCasa.Blazor/bin/Debug/net10.0')))}
    runtime = s.RUN / 'runtime'
    for project, source in sources.items():
        assert (source / (project + '.dll')).is_file(), 'Build ' + project + ' first'
        shutil.copytree(source, runtime / project / 'bin/Debug/net10.0')
    os.environ['TIDE_TEST_BUILD_ROOT'] = str(runtime)


def run_checks():
    prepare_runtime()
    safe = {'ConnectionStrings__Application': '', 'DOTNET_PROCESSOR_COUNT': '1', 'PublicDemo__Enabled': 'false',
        'MerchantPayments__CheckoutEnabled': 'false', 'Notifications__Mode': 'disabled', 'DeliveryDispatch__WorkerEnabled': 'false'}
    w.launch('TideCasa.Api', s.API, {**safe, 'Storage__DatabasePath': str(s.DB), 'Storage__Provider': 'SQLite',
        'Auth__Enabled': 'true', 'Auth__AllowLocalTestProvider': 'true',
        'Auth__SupabaseUrl': f'http://127.0.0.1:{s.PROVIDER.server_port}', 'Auth__PublishableKey': s.KEY,
        'Auth__PlatformOwnerUserId': 'supabase:' + s.PLATFORM, 'Ordering__PublicBaseUrl': w.WEB,
        'ReverseProxy__KnownClientProxy': '127.0.0.1'})
    s.seed('bistro')
    s.alter_config(lambda config: config.update(delivery_workflow_enabled=True, delivery_capacity=30))
    w.launch('TideCasa.Blazor', w.WEB, {**safe, 'Api__BaseUrl': s.API + '/', 'DataProtection__KeysPath': str(s.RUN / 'keys')})
    check('Anonymous driver desk requires sign-in', w.web(PAGE)[0] in (302, 303, 401))
    owner = w.login('alice')
    initial, response = page(owner)
    check('Owner dispatch desk renders native protected forms', 'Drivers and dispatch' in response[1]
          and all('__RequestVerificationToken' in form['fields'] for form in initial))
    check('Delivery desk disallows caching and uses same-origin form policy', 'no-store' in response[2].get('Cache-Control', '')
          and response[2].get('Referrer-Policy') == 'same-origin')
    add = w.find_form(initial, '/dispatch/driver')
    notice(w.post(owner, add, {'name': 'Bob Dispatch Driver', 'email': 'bob@example.invalid'}), 'dispatch-saved', 'Owner adds driver through native form')
    notice(w.post(owner, w.find_form(page(owner)[0], '/dispatch/driver'), {'name': 'Other Dispatch Driver', 'email': 'staff@example.invalid'}), 'dispatch-saved', 'Owner adds second driver')
    check('New driver accounts await verified sign-in', all(not d['accountLinked'] for d in board()['drivers']))
    ids = {driver['name']: driver['id'] for driver in board()['drivers']}
    bob, other = ids['Bob Dispatch Driver'], ids['Other Dispatch Driver']
    driver = w.browser()
    sign_in, sign_page = w.forms_for(driver, '/driver/signin?return_to=' + urllib.parse.quote(PAGE, safe=''))
    sign_form = w.find_form(sign_in, '/auth/session/signin')
    check('Dedicated driver sign-in retains return path and verified email guidance', 'Driver sign-in.' in sign_page[1]
          and 'verify that same email' in sign_page[1] and sign_form['fields']['return_to'] == PAGE
          and 'no-store' in sign_page[2].get('Cache-Control', ''))
    login = w.post(driver, sign_form, {'email': 'bob@example.invalid', 'password': s.PASSWORD})
    check('Driver sign-in returns directly to delivery desk', login[0] in (302, 303) and login[2].get('Location') == PAGE, login[:2])
    second = w.login('staff')
    page(driver)  # Follow the redirect like a browser; the fresh account read links verified team emails.
    check('Second driver account loads after sign-in', w.web('/account', client=second)[0] == 200)
    check('Verified driver sign-in links the team account', all(d['accountLinked'] for d in board()['drivers']))
    driver_account = w.web('/account', client=driver)
    check('Driver account exposes My deliveries and availability link', driver_account[0] == 200 and 'My deliveries and availability' in driver_account[1] and PAGE in driver_account[1])
    check('Owner account exposes Delivery desk and driver accounts link', 'Delivery desk and driver accounts' in w.web('/account', client=owner)[1])

    current = profile(owner, bob)
    before = next(d for d in board()['drivers'] if d['id'] == bob)
    for label, kwargs in [('missing antiforgery', {'remove': ('__RequestVerificationToken',)}),
                          ('foreign origin', {'headers': {'Origin': 'https://other.example.invalid'}}),
                          ('opaque origin', {'headers': {'Origin': 'null'}}),
                          ('cross-site fetch', {'headers': {'Sec-Fetch-Site': 'cross-site'}})]:
        check('Driver settings reject ' + label, w.post(owner, current, {'availability': 'available'}, **kwargs)[0] == 400)
    check('Rejected forms leave driver settings unchanged', next(d for d in board()['drivers'] if d['id'] == bob) == before)
    notice(w.post(owner, current, {'availability': 'available', 'capacity': '2', 'zips': '33101'}), 'dispatch-saved', 'Owner sets driver availability capacity and ZIP')
    configured = next(d for d in board()['drivers'] if d['id'] == bob)
    check('Driver profile readback preserves settings', configured['availability'] == 'available' and configured['capacity'] == 2 and configured['deliveryZips'] == ['33101'] and configured['availableNow'])
    notice(w.post(owner, current, {'availability': 'offline'}), 'dispatch-changed', 'Stale driver settings cannot overwrite saved profile')
    notice(w.post(driver, profile(driver, bob), {'availability': 'offline'}), 'dispatch-saved', 'Driver saves own offline availability')
    check('Offline availability stops eligibility', not next(d for d in board()['drivers'] if d['id'] == bob)['availableNow'])
    own = profile(driver, bob)
    notice(w.post(driver, own, {'capacity': '9'}), 'denied', 'Driver cannot raise manager-set capacity')
    notice(w.post(driver, own, {'driver': other}), 'denied', 'Driver cannot modify another driver profile')
    notice(w.post(driver, own, {'availability': 'available'}), 'dispatch-saved', 'Driver returns to available')
    driver_forms, driver_page = page(driver)
    check('Driver sees only own profile and no manager controls', len([f for f in driver_forms if f['action'].endswith('/dispatch/profile')]) == 1
          and other not in driver_page[1] and not any(f['action'].endswith(('/dispatch/settings', '/dispatch/run', '/dispatch/driver', '/dispatch/shift', '/dispatch/remove-shift')) for f in driver_forms))
    check('Driver capacity and ZIP fields are hidden read-only values', all(c.get('type') == 'hidden' for c in own['controls'] if c.get('name') in ('capacity', 'zips')))

    tomorrow = (datetime.now(timezone.utc) + timedelta(days=2)).replace(hour=12, minute=15, second=0, microsecond=0)
    local_time = tomorrow.strftime('%Y-%m-%dT%H:%M')
    sunday = lambda month, occurrence: [week[calendar.SUNDAY] for week in calendar.monthcalendar(tomorrow.year, month) if week[calendar.SUNDAY]][occurrence - 1]
    march, november = tomorrow.replace(month=3, day=sunday(3, 2)), tomorrow.replace(month=11, day=sunday(11, 1))
    utc_time = tomorrow + timedelta(hours=4 if march.date() <= tomorrow.date() < november.date() else 5)
    scheduled_id = create_order('Scheduled Driver Guest')
    schedule_form = plan(owner, scheduled_id, 'scheduled')
    notice(w.post(owner, schedule_form, {'dispatch_at': local_time, 'timezone': 'America/New_York', 'driver': bob}), 'dispatch-saved', 'Manager schedules order in explicit Eastern time')
    scheduled = entry(scheduled_id)
    check('Scheduled form converts Eastern time to offset-bearing UTC and leaves new order unassigned',
          datetime.fromisoformat(scheduled['assignmentPlan']['dispatchAt'].replace('Z', '+00:00')) == utc_time
          and scheduled['assignmentPlan']['preferredDriverId'] == bob and scheduled['driverId'] is None)
    reopened = plan(owner, scheduled_id, 'scheduled')
    check('Reopened schedule defaults to UTC with matching local field value', reopened['fields']['timezone'] == 'UTC'
          and reopened['fields']['dispatch_at'] == utc_time.strftime('%Y-%m-%dT%H:%M'))
    notice(w.post(owner, schedule_form, {'dispatch_at': local_time, 'timezone': 'UTC'}), 'dispatch-changed', 'Stale scheduling form cannot overwrite plan')
    for value in ('2026-03-08T02:30', '2026-11-01T01:30'):
        notice(w.post(owner, reopened, {'dispatch_at': value, 'timezone': 'America/New_York'}), 'dispatch-time', 'Skipped or repeated DST hour rejected: ' + value)
    check('DST rejection preserves scheduled plan', entry(scheduled_id)['assignmentPlan'] == scheduled['assignmentPlan'])

    shift_form = w.find_form(page(owner)[0], '/dispatch/shift', driver=bob)
    notice(w.post(owner, shift_form, {'label': 'Synthetic delivery shift', 'starts': local_time,
        'ends': (tomorrow + timedelta(hours=4)).strftime('%Y-%m-%dT%H:%M'), 'timezone': 'UTC'}), 'dispatch-saved', 'Owner creates driver shift through native fields')
    shift = next(shift for shift in board()['shifts'] if shift['memberId'] == bob)
    check('Shift saves UTC boundary exactly', datetime.fromisoformat(shift['startsAt'].replace('Z', '+00:00')) == tomorrow)
    check('Driver can read own shift and second driver cannot see it', 'Synthetic delivery shift' in page(driver)[1][1] and 'Synthetic delivery shift' not in page(second)[1][1])
    notice(w.post(owner, profile(owner, bob), {'availability': 'scheduled'}), 'dispatch-saved', 'Manager switches to scheduled-shift availability')
    check('Future shift does not make driver available early', not next(d for d in board()['drivers'] if d['id'] == bob)['availableNow'])
    removal = w.find_form(page(owner)[0], '/dispatch/remove-shift', shift=shift['id'])
    notice(w.post(owner, removal), 'dispatch-saved', 'Manager removes scheduled driver shift')
    check('Removed shift disappears from readback', not board()['shifts'])
    notice(w.post(driver, profile(driver, bob), {'availability': 'available'}), 'dispatch-saved', 'Driver returns to active availability for assignment')

    automatic_id = create_order('Automatic Driver Guest')
    manual_id = create_order('Manual Driver Guest')
    notice(w.post(owner, plan(owner, manual_id, 'manual')), 'dispatch-saved', 'Manager marks order for manual assignment')
    toggle = w.find_form(page(owner)[0], '/dispatch/settings')
    notice(w.post(owner, toggle), 'dispatch-saved', 'Manager enables automatic dispatch')
    check('Automatic setting readback is enabled', board()['automaticAssignment'])
    notice(w.post(owner, toggle), 'dispatch-changed', 'Stale dispatch toggle is rejected')
    notice(w.post(owner, w.find_form(page(owner)[0], '/dispatch/run')), 'dispatch-none', 'Dispatch leaves new orders awaiting acceptance')
    for ident in (automatic_id, manual_id, scheduled_id):
        accept = w.find_form(page(owner)[0], '/orders/' + ident, action='accepted')
        w.saved(w.post(owner, accept), 'Manager accepts delivery ' + ident)
    notice(w.post(owner, w.find_form(page(owner)[0], '/dispatch/run')), 'dispatch-assigned', 'Manager runs dispatch on eligible accepted orders')
    check('Automatic dispatch assigns eligible order while honoring manual and future scheduled plans',
          entry(automatic_id)['driverId'] == bob and entry(manual_id)['driverId'] is None and entry(scheduled_id)['driverId'] is None)
    notice(w.post(owner, w.find_form(page(owner)[0], '/dispatch/settings')), 'dispatch-saved', 'Manager disables workspace automatic dispatch')
    notice(w.post(owner, plan(owner, manual_id, 'automatic')), 'dispatch-saved', 'Manager chooses explicit automatic assignment for one order')
    notice(w.post(owner, w.find_form(page(owner)[0], '/dispatch/run')), 'dispatch-assigned', 'Explicit automatic order dispatch works with workspace switch off')
    check('Explicit automatic order assigned to eligible driver', entry(manual_id)['driverId'] == bob)
    my_orders = page(driver)[1][1]
    second_orders = page(second)[1][1]
    check('Drivers only see orders assigned to them', 'Automatic Driver Guest' in my_orders and 'Manual Driver Guest' in my_orders
          and 'Scheduled Driver Guest' not in my_orders and all(name not in second_orders for name in ('Automatic Driver Guest', 'Manual Driver Guest', 'Scheduled Driver Guest')))
    check('Assigned order no longer exposes scheduling forms', not any(f['action'].endswith('/dispatch/orders/' + automatic_id) for f in page(owner)[0]))
    check('Driver cannot forge manager dispatch action', w.web('/restaurant-management/bistro/dispatch/run',
        {'__RequestVerificationToken': profile(driver, bob)['fields']['__RequestVerificationToken']}, driver, {'Origin': w.WEB})[2].get('Location', '').endswith('notice=denied'))
    check('Delivery UI does not expose provider tokens', all(key not in my_orders for key in s.TOKENS))
    check('SQLite fixture remains consistent', s.sql('PRAGMA foreign_key_check') == [])
    return {'owner': 'alice@example.invalid', 'driver': 'bob@example.invalid', 'secondDriver': 'staff@example.invalid',
        'password': s.PASSWORD, 'signinUrl': w.WEB + '/driver/signin?return_to=' + urllib.parse.quote(PAGE, safe=''), 'deliveriesUrl': w.WEB + PAGE}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--hold', action='store_true')
    parser.add_argument('--hold-seconds', type=int, default=1800)
    args = parser.parse_args()
    metadata = s.RUN / 'driver-dispatch-web-fixture.json'
    results = s.RUN / 'driver-dispatch-web-results.json'
    stop = s.RUN / 'stop-fixture'
    try:
        credentials = run_checks()
        credentials_file = s.RUN / 'synthetic-credentials.json'
        credentials_file.write_text(json.dumps(credentials, indent=2), encoding='utf-8')
        metadata.write_text(json.dumps({'syntheticOnly': True, 'running': args.hold, 'web': w.WEB, 'api': s.API,
            'database': str(s.DB), 'credentialsFile': str(credentials_file), 'stopFile': str(stop),
            'checksPassed': len(s.RESULTS), 'processIds': [p.pid for p in s.PROCESSES]}, indent=2), encoding='utf-8')
        results.write_text(json.dumps(s.RESULTS, indent=2), encoding='utf-8')
        print(str(len(s.RESULTS)) + ' driver dispatch web checks passed.', flush=True)
        print('Fixture metadata: ' + str(metadata), flush=True)
        if args.hold:
            deadline = time.monotonic() + max(1, min(args.hold_seconds, 7200))
            while not stop.exists() and time.monotonic() < deadline:
                if any(proc.poll() is not None for proc in s.PROCESSES):
                    raise RuntimeError('A held fixture child stopped unexpectedly')
                time.sleep(.5)
    finally:
        for proc in s.PROCESSES:
            if proc.poll() is None:
                proc.terminate()
                try:
                    proc.wait(timeout=20)
                except subprocess.TimeoutExpired:
                    proc.kill()
                    proc.wait(timeout=10)
        s.PROVIDER.shutdown()
        s.PROVIDER.server_close()
        for log in s.LOGS:
            log.close()
        results.write_text(json.dumps(s.RESULTS, indent=2), encoding='utf-8')
        if metadata.exists():
            data = json.loads(metadata.read_text(encoding='utf-8'))
            data['running'] = False
            metadata.write_text(json.dumps(data, indent=2), encoding='utf-8')
        print('Driver dispatch web evidence: ' + str(s.RUN), flush=True)


if __name__ == '__main__':
    main()
