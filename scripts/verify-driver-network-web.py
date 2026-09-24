"""Native driver signup, hiring and payment approval UI against synthetic local services.

Uses real API, Blazor and SQLite with fake loopback identity/Stripe boundaries.
Never follows hosted redirects or contacts real accounts, payments or production.
Build API/web first. TIDE_DISPATCH_WEB_BUILD_DIR can select isolated web artifacts.
--hold leaves the synthetic fixture available for a browser walkthrough.
"""
import argparse
import importlib.util
import json
import os
import re
from pathlib import Path
import subprocess
import time
import urllib.parse
import uuid

from driver_payment_test_provider import DriverStripeFake, KEY, SECRET

spec = importlib.util.spec_from_file_location('dispatch_web', Path(__file__).with_name('verify-driver-dispatch-web.py'))
dw = importlib.util.module_from_spec(spec)
spec.loader.exec_module(dw)
w, s = dw.w, dw.s
NETWORK = '/workspace/bistro/driver-network'
PAYMENTS = '/workspace/bistro/driver-payments'
DELIVERIES = '/workspace/bistro/deliveries'
fake = DriverStripeFake()


def check(name, condition, detail=None):
    s.check(name, condition, detail)


def notice(response, expected, name):
    check(name, response[0] in (302, 303) and ('notice=' + expected) in response[2].get('Location', ''),
          (response[0], response[2].get('Location', ''), response[1][:400]))


def forms(client, page):
    return w.forms_for(client, page)


def text(document):
    return ' '.join(w.html.unescape(re.sub(r'<[^>]*>', ' ', document)).split())


def api(path, person='alice'):
    response = s.call(path, token=dw.token(person))
    assert response[0] == 200, response[:3]
    return response[1]


def driver_account(person='bob'):
    return api('/api/v1/drivers/me', person)


def payment_lines():
    return api('/api/v1/tenants/bistro/driver-payments')['payments']


def complete_delivery(owner, driver, member, name):
    ident = dw.create_order(name)
    for action, client, changes in [('assign-driver', owner, {'driver_id': member}), ('accepted', owner, {}),
            ('preparing', owner, {}), ('ready', owner, {}), ('acknowledge-delivery', driver, {}),
            ('out_for_delivery', driver, {}), ('confirm-delivery', driver, {})]:
        form = w.find_form(forms(client, DELIVERIES)[0], '/orders/' + ident, action=action)
        w.saved(w.post(client, form, changes), 'Native delivery workflow: ' + name + ' / ' + action)
    return ident


def setup_payout(client):
    setup = w.find_form(forms(client, '/driver/earnings')[0], '/driver/manage/payout')
    calls_before = len(fake.calls)
    notice(w.post(client, setup), 'confirm-payout', 'Payout setup requires US individual confirmation')
    check('Unconfirmed payout setup makes no provider call', len(fake.calls) == calls_before)
    check('Payout setup rejects missing antiforgery', w.post(client, setup, {'confirm_us': 'true'}, remove=('__RequestVerificationToken',))[0] == 400)
    response = w.post(client, setup, {'confirm_us': 'true'})
    check('Confirmed payout setup redirects only to hosted Stripe Connect', response[0] in (302, 303)
          and response[2].get('Location', '').startswith('https://connect.stripe.com/'), response[:2])
    return setup


def check_payment_pagination(owner, direct, direct_id):
    # Seed only this synthetic fixture at the completed-work storage seam. The
    # native completion workflow above already proves creation of the ledger.
    stamp = s.datetime.now(s.timezone.utc).isoformat()
    older_id = str(uuid.uuid4())
    for index in range(501):
        ident = str(uuid.uuid4()) if index < 500 else older_id
        s.sql('INSERT INTO tide_driver_payables(id,tenant_id,order_id,member_id,user_id,driver_name,source,agreed_pay_cents,completed_at,order_number) VALUES(?,?,?,?,?,?,?,?,?,?)',
              (ident, 'bistro', ident, direct_id, 'supabase:' + s.USERS['direct@example.invalid']['id'],
               'Synthetic Direct Driver', 'own', 0, stamp if index < 500 else '2020-01-01T00:00:00Z',
               'History-' + str(index) if index < 500 else 'Historical-own-review'))
    newest = api('/api/v1/tenants/bistro/driver-payments?page=0')
    older = api('/api/v1/tenants/bistro/driver-payments?page=1')
    check('Payment API pages beyond 500 completed deliveries without losing older work', newest['page'] == 0 and newest['hasMore']
          and len(newest['payments']) == 500 and older['page'] == 1 and not older['hasMore']
          and all(p['id'] != older_id for p in newest['payments']) and any(p['id'] == older_id for p in older['payments']))
    newest_html = forms(owner, PAYMENTS)[1][1]
    check('Client payment history links to older delivery page', PAYMENTS + '?page=1' in newest_html and 'Older deliveries' in newest_html)
    older_forms, older_html = forms(owner, PAYMENTS + '?page=1')
    check('Older client payment page renders old payable and links back to newer history', 'Historical-own-review' in older_html[1]
          and PAYMENTS + '?page=0' in older_html[1] and 'Newer deliveries' in older_html[1])
    earnings = api('/api/v1/drivers/payments?page=0', 'direct')
    older_earnings = api('/api/v1/drivers/payments?page=1', 'direct')
    check('Driver earnings preserve older work across API pages', earnings['hasMore'] and len(earnings['payments']) == 500
          and older_earnings['page'] == 1 and any(p['id'] == older_id for p in older_earnings['payments']))
    check('Driver earnings show older and newer navigation with correct entries', '/driver/earnings?page=1' in forms(direct, '/driver/earnings')[1][1]
          and 'Historical-own-review' in forms(direct, '/driver/earnings?page=1')[1][1]
          and '/driver/earnings?page=0' in forms(direct, '/driver/earnings?page=1')[1][1])
    check('Payment and earnings pages reject negative page values', w.web(PAYMENTS + '?page=-1', client=owner)[0] == 400
          and w.web('/driver/earnings?page=-1', client=direct)[0] == 400)
    review = w.find_form(older_forms, '/driver-payments/bistro/' + older_id + '/review')
    check('Older-page native amount form carries its page', review['fields']['page'] == '1')
    reviewed = w.post(owner, review, {'pay': '5.67'})
    review_path = reviewed[2].get('Location', '')
    check('Own-driver amount review preserves older page', review_path.startswith(PAYMENTS + '?page=1&payment=' + older_id)
          and 'pay=567' in review_path)
    approval = w.find_form(forms(owner, review_path)[0], '/driver-payments/bistro/' + older_id + '/approve')
    missing = w.post(owner, approval)
    check('Unconfirmed approval returns to its older page', missing[2].get('Location', '') == PAYMENTS + '?page=1&notice=confirm-payment')
    notice(w.post(owner, approval, {'confirm_payment': 'true', 'page': '100001'}), 'invalid', 'Native approval rejects excessive return-page input')
    before = set(fake.sessions)
    checkout = w.post(owner, approval, {'confirm_payment': 'true'})
    check('Owner can approve an older own-driver payable', checkout[2].get('Location', '').startswith('https://checkout.stripe.com/'))
    session = fake.sessions[next(iter(set(fake.sessions) - before))]
    check('Stripe checkout success and cancel return to the older history page', all(
        urllib.parse.parse_qs(urllib.parse.urlsplit(session[field]).query).get('page') == ['1'] for field in ('success_url', 'cancel_url')))
    stale = w.post(owner, approval, {'confirm_payment': 'true'})
    check('Stale older-page approval stays on the same history page', stale[2].get('Location', '') == PAYMENTS + '?page=1&notice=changed')
    refresh = w.find_form(forms(owner, PAYMENTS + '?page=1')[0], '/driver-payments/bistro/' + older_id + '/refresh')
    refreshed = w.post(owner, refresh)
    check('Payment refresh preserves older page', refresh['fields']['page'] == '1'
          and refreshed[2].get('Location', '') == PAYMENTS + '?page=1&notice=refreshed')


def run_checks():
    dw.prepare_runtime()
    for person, ident in [('direct', 911), ('outsider', 912)]:
        s.USERS[person + '@example.invalid'] = {'id': str(uuid.UUID(int=ident)), 'email': person + '@example.invalid',
            'email_confirmed_at': '2026-01-01T00:00:00Z', 'is_anonymous': False,
            'user_metadata': {'full_name': person.title() + ' Synthetic'}}
    safe = {'ConnectionStrings__Application': '', 'DOTNET_PROCESSOR_COUNT': '1', 'PublicDemo__Enabled': 'false',
        'MerchantPayments__CheckoutEnabled': 'false', 'Notifications__Mode': 'disabled',
        'DeliveryDispatch__WorkerEnabled': 'false', 'DriverPayments__WorkerEnabled': 'false',
        'DriverPayments__SetupEnabled': 'false', 'DriverPayments__PaymentsEnabled': 'false'}
    api_env = {**safe, 'Storage__DatabasePath': str(s.DB), 'Storage__Provider': 'SQLite',
        'Auth__Enabled': 'true', 'Auth__AllowLocalTestProvider': 'true',
        'Auth__SupabaseUrl': f'http://127.0.0.1:{s.PROVIDER.server_port}', 'Auth__PublishableKey': s.KEY,
        'Auth__PlatformOwnerUserId': 'supabase:' + s.PLATFORM, 'Ordering__PublicBaseUrl': w.WEB,
        'ReverseProxy__KnownClientProxy': '127.0.0.1'}
    w.launch('TideCasa.Api', s.API, api_env)
    old_api = s.PROCESSES[-1]
    for tenant, owner_id in [('bistro', s.ALICE), ('foreign', s.PLATFORM)]:
        s.seed(tenant, owner_id)
        s.alter_config(lambda cfg: cfg.update(delivery_workflow_enabled=True, delivery_capacity=30), tenant)
    s.sql('INSERT INTO bartide_enhanced_members(id,tenant_id,name,email,user_id,role,active,created_at) VALUES(?,?,?,?,?,?,1,?)',
          ('manager', 'bistro', 'Synthetic Manager', 'staff@example.invalid', 'supabase:' + s.STAFF, 'manager', s.NOW))
    w.launch('TideCasa.Blazor', w.WEB, {**safe, 'Api__BaseUrl': s.API + '/', 'DataProtection__KeysPath': str(s.RUN / 'keys')})
    check('Independent driver workspace requires verified sign-in', w.web('/driver')[0] in (302, 303, 401))
    sign_client = w.browser()
    signforms, signpage = forms(sign_client, '/driver/signin')
    check('Driver sign-in and signup preserve independent driver destination',
          w.find_form(signforms, '/auth/session/signin')['fields']['return_to'] == '/driver'
          and '/signup?return_to=%2Fdriver' in signpage[1] and 'Create your driver account' in signpage[1])
    owner, driver, manager, outsider = (w.login(person) for person in ('alice', 'bob', 'staff', 'outsider'))
    initial, response = forms(driver, '/driver')
    check('Verified independent driver can create a profile without client membership', 'Create your driver profile' in response[1]
          and driver_account() == {'profile': None, 'hires': []})
    check('Network pages are private and no-cache', 'no-store' in response[2].get('Cache-Control', '')
          and response[2].get('Referrer-Policy') == 'same-origin')
    profile = w.find_form(initial, '/driver/manage/profile')
    changes = {'name': 'Synthetic Network Driver', 'bio': 'Local test driver', 'zips': '33101', 'capacity': '2'}
    check('Network profile rejects foreign origin', w.post(driver, profile, changes, headers={'Origin': 'https://other.example.invalid'})[0] == 400)
    check('Network profile rejects missing antiforgery', w.post(driver, profile, changes, remove=('__RequestVerificationToken',))[0] == 400)
    notice(w.post(driver, profile, changes), 'saved', 'Driver saves unlisted independent profile')
    check('Unlisted profile is excluded from client directory', 'Synthetic Network Driver' not in forms(owner, NETWORK)[1][1])
    notice(w.post(driver, profile, changes), 'changed', 'Stale driver profile cannot overwrite changes')
    notice(w.post(driver, w.find_form(forms(driver, '/driver')[0], '/driver/manage/profile'), {'listed': 'true'}), 'saved', 'Driver opts into client directory')
    directory_forms, directory = forms(owner, NETWORK)
    directory_text = text(directory[1])
    check('Directory contains opted-in profile with exact five-percent example and live preview', 'Synthetic Network Driver' in directory[1]
          and '$10.00 driver pay + $0.50 network fee = $10.50 client total per delivery.' in directory_text
          and '/driver-network.js' in directory[1], directory_text[-2500:])
    network_id = driver_account()['profile']['id']
    offer = w.find_form(directory_forms, '/driver-network/bistro/offer', driver=network_id)
    check('Hiring offer rejects CSRF', w.post(owner, offer, {'pay': '10.10'}, remove=('__RequestVerificationToken',))[0] == 400)
    notice(w.post(owner, offer, {'pay': '10.10', 'notes': 'Synthetic delivery agreement'}), 'saved', 'Owner offers fixed pay through native form')
    hire_text = text(forms(owner, NETWORK)[1][1])
    check('Offer total rounds half-cent fee upward', '$0.51 network fee' in hire_text and '$10.61 per delivery' in hire_text)
    check('Offer alone grants no delivery access', w.web(DELIVERIES, client=driver)[0] == 403)
    notice(w.post(owner, offer, {'pay': '99.99'}), 'changed', 'Stale hiring form cannot change the offer')
    offered = driver_account()['hires'][0]
    declined = w.find_form(forms(driver, '/driver')[0], '/driver/manage/hires/' + offered['id'], response='decline')
    notice(w.post(driver, declined), 'saved', 'Driver can decline client offer')
    check('Declined offer still grants no delivery access', w.web(DELIVERIES, client=driver)[0] == 403)
    renewed = w.find_form(forms(owner, NETWORK)[0], '/driver-network/bistro/offer', driver=network_id)
    notice(w.post(owner, renewed), 'saved', 'Owner renews declined offer with displayed terms')
    accept = w.find_form(forms(driver, '/driver')[0], '/driver/manage/hires/' + offered['id'], response='accept')
    notice(w.post(driver, accept), 'saved', 'Driver accepts client offer')
    notice(w.post(driver, accept), 'changed', 'Stale driver acceptance is rejected')
    hire = driver_account()['hires'][0]
    check('Accepted agreement exposes only the hired client workspace', hire['status'] == 'active' and DELIVERIES in forms(driver, '/driver')[1][1]
          and w.web('/workspace/foreign/deliveries', client=driver)[0] == 403)
    check('Network driver cannot access client hiring controls', w.web(NETWORK, client=driver)[0] == 403)
    profile_form = w.find_form(forms(driver, DELIVERIES)[0], '/dispatch/profile', driver=hire['memberId'])
    notice(w.post(driver, profile_form, {'availability': 'available'}), 'dispatch-saved', 'Accepted driver activates own client availability')
    completed_id = complete_delivery(owner, driver, hire['memberId'], 'Network payment test guest')
    line = next(p for p in payment_lines() if p['id'] == completed_id)
    check('Completion records exact agreed pay fee total without starting checkout', line['status'] == 'pending_approval'
          and (line['driverPayCents'], line['platformFeeCents'], line['totalCents']) == (1010, 51, 1061) and not fake.sessions)
    disabled_forms, disabled = forms(owner, PAYMENTS)
    check('Default disabled payments show history and no approval controls', 'Payments are not enabled yet' in disabled[1]
          and not any(f['action'].endswith('/approve') for f in disabled_forms))
    earnings_forms, earnings = forms(driver, '/driver/earnings')
    check('Disabled driver earnings show unavailable setup and recorded delivery pay', '$10.10' in earnings[1]
          and 'Payout setup is currently unavailable' in earnings[1]
          and not any(f['action'].endswith('/payout') for f in earnings_forms))
    check('Other driver cannot see completed earnings', completed_id not in json.dumps(api('/api/v1/drivers/payments', 'outsider')))

    # Restart only this owned API with the same private database and a loopback Stripe fake.
    old_api.terminate()
    old_api.wait(timeout=20)
    s.PROCESSES.remove(old_api)
    api_env.update({'DriverPayments__Environment': 'sandbox', 'DriverPayments__RestrictedKey': KEY,
        'DriverPayments__WebhookSecret': SECRET, 'DriverPayments__PublicBaseUrl': w.WEB,
        'DriverPayments__ApiBaseUrl': fake.url, 'DriverPayments__AllowLocalTestProvider': 'true',
        'DriverPayments__SetupEnabled': 'true', 'DriverPayments__PaymentsEnabled': 'true'})
    w.launch('TideCasa.Api', s.API, api_env)
    setup = setup_payout(driver)
    ready_earnings = forms(driver, '/driver/earnings')[1][1]
    check('Driver earnings clearly identify test mode and Stripe balance timing', 'Test mode' in ready_earnings and 'bank account' in ready_earnings)
    fake.link_url = 'https://connect.stripe.com.attacker.invalid/setup'
    new_setup = w.find_form(forms(driver, '/driver/earnings')[0], '/driver/manage/payout')
    unsafe = w.post(driver, new_setup, {'confirm_us': 'true'})
    check('Unsafe hosted payout URL cannot redirect the browser', unsafe[2].get('Location', '').startswith('/driver/earnings?notice='), (unsafe[0], unsafe[2].get('Location', '')))
    fake.link_url = 'https://connect.stripe.com/setup/synthetic_driver'
    approve_forms, payable_page = forms(owner, PAYMENTS)
    approve = w.find_form(approve_forms, '/driver-payments/bistro/' + completed_id + '/approve')
    check('Owner approval shows exact immutable driver pay and total', approve['fields']['pay_cents'] == '1010'
          and '$10.61' in payable_page[1] and 'I approve' in payable_page[1] and 'Test mode' in payable_page[1])
    check('Payment approval rejects missing antiforgery', w.post(owner, approve, {'confirm_payment': 'true'}, remove=('__RequestVerificationToken',))[0] == 400)
    check('Payment approval rejects cross-site origin', w.post(owner, approve, {'confirm_payment': 'true'}, headers={'Origin': 'https://other.example.invalid'})[0] == 400)
    notice(w.post(owner, approve), 'confirm-payment', 'Owner must explicitly confirm exact payment total')
    check('Unconfirmed approval cannot create checkout', not fake.sessions)
    manager_forms, manager_page = forms(manager, PAYMENTS)
    check('Manager can review payment but sees no approval controls', '$10.61' in manager_page[1] and 'Only the business owner' in manager_page[1]
          and not any(f['action'].endswith(('/approve', '/review')) for f in manager_forms))
    manager_token = forms(manager, NETWORK)[0][0]['fields']['__RequestVerificationToken']
    notice(w.web(approve['action'], {**approve['fields'], '__RequestVerificationToken': manager_token, 'confirm_payment': 'true'}, manager, {'Origin': w.WEB}),
           'denied', 'Forged manager approval is denied by the server')
    notice(w.post(owner, approve, {'confirm_payment': 'true', 'pay_cents': '9999'}), 'payment-amount', 'Network delivery pay cannot be changed at approval')
    checkout = w.post(owner, approve, {'confirm_payment': 'true'})
    check('Approved payment opens only Stripe hosted checkout', checkout[0] in (302, 303)
          and checkout[2].get('Location', '').startswith('https://checkout.stripe.com/c/pay/'))
    check('Manual approval creates one checkout with exact amount and fee', len(fake.sessions) == 1
          and next(iter(fake.sessions.values()))['amount_total'] == 1061 and next(iter(fake.sessions.values()))['_fee'] == 51)
    notice(w.post(owner, approve, {'confirm_payment': 'true'}), 'changed', 'Stale repeated approval cannot create duplicate payment')
    check('Repeat approval creates no second checkout', len(fake.sessions) == 1)
    session_id = next(iter(fake.sessions))
    fake.pay(session_id)
    refresh = w.find_form(forms(owner, PAYMENTS)[0], '/driver-payments/bistro/' + completed_id + '/refresh')
    notice(w.post(owner, refresh), 'refreshed', 'Owner refreshes confirmed provider payment')
    check('Paid delivery is described as Stripe credit and loses approval control', 'Credited to driver Stripe account' in forms(driver, '/driver/earnings')[1][1]
          and not any(f['action'].endswith('/' + completed_id + '/approve') for f in forms(owner, PAYMENTS)[0]))

    add_direct = w.find_form(forms(owner, DELIVERIES)[0], '/dispatch/driver')
    notice(w.post(owner, add_direct, {'name': 'Synthetic Direct Driver', 'email': 'direct@example.invalid'}), 'dispatch-saved', 'Owner adds own driver without network hiring')
    direct = w.login('direct')
    direct_forms, _ = forms(direct, DELIVERIES)
    direct_profile = w.find_form(direct_forms, '/dispatch/profile')
    direct_id = direct_profile['fields']['driver']
    notice(w.post(direct, direct_profile, {'availability': 'available'}), 'dispatch-saved', 'Own driver sets availability')
    setup_payout(direct)
    own_id = complete_delivery(owner, direct, direct_id, 'Direct payment test guest')
    own = next(p for p in payment_lines() if p['id'] == own_id)
    check('Own completed delivery starts with no agreed amount and no network fee', own['driverPayCents'] == 0 and own['platformFeeCents'] == 0 and own['source'] == 'own')
    review_form = w.find_form(forms(owner, PAYMENTS)[0], '/driver-payments/bistro/' + own_id + '/review')
    reviewed = w.post(owner, review_form, {'pay': '12.34'})
    check('Own payment entry navigates to a concrete review without charging', reviewed[0] in (302, 303)
          and 'pay=1234' in reviewed[2].get('Location', '') and len(fake.sessions) == 1)
    own_review, own_page = forms(owner, reviewed[2]['Location'])
    own_approval = w.find_form(own_review, '/driver-payments/bistro/' + own_id + '/approve')
    check('Own payment review shows exact zero-fee total and approval', own_approval['fields']['pay_cents'] == '1234'
          and 'I approve $12.34' in text(own_page[1]) and '$0.00 to BarTide' in text(own_page[1]))
    own_checkout = w.post(owner, own_approval, {'confirm_payment': 'true'})
    check('Own driver payment opens protected hosted checkout', own_checkout[2].get('Location', '').startswith('https://checkout.stripe.com/'))
    own_session = next(session for session in fake.sessions.values() if session['id'] != session_id)
    check('Own driver checkout has no network fee', own_session['amount_total'] == 1234 and own_session['_fee'] == 0)
    notice(w.post(driver, w.find_form(forms(driver, '/driver')[0], '/driver/manage/profile'), remove=('listed',)), 'saved', 'Driver can unlist while retaining accepted work')
    check('Unlisting preserves active agreement and deliveries', driver_account()['hires'][0]['status'] == 'active' and forms(driver, DELIVERIES)[1][0] == 200)
    end = w.find_form(forms(owner, NETWORK)[0], '/driver-network/bistro/end', hire=hire['id'])
    notice(w.post(owner, end), 'confirm-end', 'Ending hire requires explicit confirmation')
    notice(w.post(owner, end, {'confirm_end': 'true'}), 'saved', 'Owner ends hire after deliveries complete')
    check('Ended hire removes order access but retains earned payment history', w.web(DELIVERIES, client=driver)[0] == 403
          and 'Credited to driver Stripe account' in forms(driver, '/driver/earnings')[1][1])
    check('All driver money stays isolated and database-consistent', s.sql('PRAGMA foreign_key_check') == []
          and len(fake.sessions) == 2 and all(call['account'] is None for call in fake.calls))
    # This independent high-volume scenario gets a fresh local rate-limit window;
    # the database, hosted checkout fake and browser sessions remain unchanged.
    current_api = next(p for p in reversed(s.PROCESSES) if p.poll() is None and 'TideCasa.Api.dll' in ' '.join(p.args))
    current_api.terminate(); current_api.wait(timeout=20); s.PROCESSES.remove(current_api)
    w.launch('TideCasa.Api', s.API, api_env)
    check_payment_pagination(owner, direct, direct_id)
    return {'owner': 'alice@example.invalid', 'networkDriver': 'bob@example.invalid', 'directDriver': 'direct@example.invalid',
        'manager': 'staff@example.invalid', 'password': s.PASSWORD, 'signinUrl': w.WEB + '/driver/signin',
        'networkUrl': w.WEB + NETWORK, 'paymentsUrl': w.WEB + PAYMENTS, 'earningsUrl': w.WEB + '/driver/earnings'}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--hold', action='store_true')
    parser.add_argument('--hold-seconds', type=int, default=1800)
    args = parser.parse_args()
    metadata = s.RUN / 'driver-network-web-fixture.json'
    results = s.RUN / 'driver-network-web-results.json'
    stop = s.RUN / 'stop-fixture'
    try:
        credentials = run_checks()
        creds_file = s.RUN / 'synthetic-credentials.json'
        creds_file.write_text(json.dumps(credentials, indent=2), encoding='utf-8')
        metadata.write_text(json.dumps({'syntheticOnly': True, 'running': args.hold, 'web': w.WEB, 'api': s.API,
            'database': str(s.DB), 'credentialsFile': str(creds_file), 'stopFile': str(stop), 'checksPassed': len(s.RESULTS),
            'processIds': [p.pid for p in s.PROCESSES]}, indent=2), encoding='utf-8')
        results.write_text(json.dumps(s.RESULTS, indent=2), encoding='utf-8')
        print(str(len(s.RESULTS)) + ' driver network web checks passed.', flush=True)
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
        fake.close()
        for log in s.LOGS:
            log.close()
        results.write_text(json.dumps(s.RESULTS, indent=2), encoding='utf-8')
        if metadata.exists():
            value = json.loads(metadata.read_text(encoding='utf-8'))
            value['running'] = False
            metadata.write_text(json.dumps(value, indent=2), encoding='utf-8')
        print('Driver network web evidence: ' + str(s.RUN), flush=True)


if __name__ == '__main__':
    main()
