"""Exercise client onboarding through an isolated real API and native Blazor forms.

The identity provider is a loopback fixture. All tenants, menu data, enrollment
records and approvals are synthetic. No provider account, message, payment or
deployment is created. Build the API and Web first; TIDE_TEST_BUILD_ROOT selects
an already built candidate. Evidence is retained without authentication tokens.
"""
import copy
from datetime import datetime, timedelta, timezone
import hashlib
import html
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import traceback

import restaurant_test_support as s

s.RUN = s.ROOT / '.tools/client-onboarding-verification' / datetime.now(timezone.utc).strftime('%Y%m%d-%H%M%S-%f')
s.RUN.mkdir(parents=True, exist_ok=False)
s.DB = s.RUN / 'synthetic.db'
spec = importlib.util.spec_from_file_location('onboarding_forms', Path(__file__).with_name('verify-management-web.py'))
w = importlib.util.module_from_spec(spec)
spec.loader.exec_module(w)
w.RUN = s.RUN
TOKENS = {}
TENANT = 'onboarding'
check = s.check


def api(operation='', body=None, person='alice', tenant=TENANT):
    suffix = '/' + operation if operation else ''
    return s.call('/api/v1/tenants/' + tenant + '/onboarding' + suffix, body, TOKENS.get(person))


def ok(name, result):
    check(name, result[0] == 200 and isinstance(result[1], dict), result[:3])
    return result[1]


def workspace(tenant=TENANT, person='alice'):
    return ok('Read private onboarding ' + tenant, api(tenant=tenant, person=person))


def save(section, value, tenant=TENANT, person='alice'):
    current = workspace(tenant, person)
    return ok('Save onboarding ' + section, api(section, {'expectedRevision': current['revision'], section: value}, person, tenant))


def build(tenant=TENANT):
    current = workspace(tenant)
    return ok('Build private release ' + tenant, api('build', {'expectedRevision': current['revision']}, tenant=tenant))


def approval(release):
    return {'releaseId': release['id'], 'fingerprint': release['fingerprint'], 'confirmed': True}


def approve(release, tenant=TENANT):
    return ok('Owner approves exact preview ' + tenant, api('approve', approval(release), tenant=tenant))


def publication(status='active', tenant=TENANT, reviewed=True):
    version = s.sql('SELECT version FROM bartide_customers WHERE id=?', (tenant,))[0][0]
    return s.call('/api/v1/owner/launch-review/' + tenant + '/transition',
                  {'expectedVersion': version, 'status': status, 'reviewedWithCustomer': reviewed}, TOKENS['platform'])


def seed_private(tenant=TENANT, owner=None):
    s.seed(tenant, owner or s.ALICE, 'draft', False)
    stored = s.menu()
    stored['items'] = []
    s.sql('UPDATE bartide_customers SET menu_json=? WHERE id=?', (json.dumps(stored), tenant))
    s.sql('DELETE FROM bartide_enhanced_configs WHERE tenant_id=?', (tenant,))


def business():
    return {'name': 'Harbor & Hearth Synthetic', 'publicEmail': 'hello@example.invalid',
            'phone': '+1 305 555 0101', 'address': '123 Synthetic Harbor Lane', 'city': 'Miami',
            'region': 'FL', 'postalCode': '33101', 'country': 'US', 'timeZone': 'America/New_York',
            'website': 'https://example.invalid',
            'hours': [{'day': day, 'closed': False, 'opens': '11:00', 'closes': '23:00', 'overnight': False}
                      for day in range(7)]}


def brand():
    return {'style': 'coastal', 'accent': '#157A83', 'introduction': 'Fresh food by the harbor.\nEveryone is welcome.',
            'logoChoice': 'text', 'logoPhotoId': None, 'coverPhotoId': None}


def service(ordering=True):
    return {'ordering': ordering, 'dineIn': False, 'pickup': ordering, 'delivery': False,
            'payStaff': ordering, 'requestCards': False, 'tips': False, 'taxBasisPoints': 700 if ordering else None,
            'tableLabels': [], 'deliveryZips': [], 'deliveryFeeCents': 0, 'deliveryMinimumCents': 0,
            'deliveryCapacity': 1, 'pickupInstructions': 'Collect at the front counter.',
            'paymentInstructions': 'Pay our team at collection.'}


def team():
    return {'ownerHandlesOrders': True, 'orderContact': 'Harbor owner, +1 305 555 0101',
            'eventsLater': True, 'rewardsLater': True, 'trainingLater': True}


def menu_item(price=1299):
    return {'id': 'house-burger', 'categoryId': 'food', 'name': 'Harbor Burger',
            'description': 'A freshly prepared house burger.', 'priceCents': price, 'priceLabel': None, 'available': True}


def put_item(price=1299):
    editor = ok('Read menu editor', s.call('/api/v1/tenants/' + TENANT + '/menu', token=TOKENS['alice']))
    return ok('Save actual menu item', s.call('/api/v1/tenants/' + TENANT + '/menu/items',
              {'expectedVersion': editor['version'], 'item': menu_item(price)}, TOKENS['alice']))


def put_profile_hours(hours):
    path = '/api/v1/tenants/' + TENANT + '/menu'
    editor = ok('Read profile editor before hours change', s.call(path, token=TOKENS['alice']))
    return ok('Save actual menu profile hours', s.call(path + '/profile',
              {'expectedVersion': editor['version'], 'profile': {**editor['profile'], 'hours': hours}}, TOKENS['alice']))


def financial_counts():
    return {table: s.sql('SELECT COUNT(*) FROM ' + table)[0][0] for table in
            ('bartide_enhanced_orders', 'tide_restaurant_payment_attempts', 'tide_service_orders',
             'tide_merchant_accounts', 'tide_merchant_refunds', 'tide_service_refunds')}


def revision_unchanged(revision):
    return workspace()['revision'] == revision


def run_api():
    s.launch({'ServiceBilling__CheckoutEnabled': 'false', 'ServiceBilling__Environment': 'sandbox',
              'MerchantPayments__OnboardingEnabled': 'false', 'MerchantPayments__CheckoutEnabled': 'false',
              'WebPush__Enabled': 'false', 'SampleBar__Enabled': 'false', 'PublicDemo__Enabled': 'false'})
    for person in ('alice', 'bob', 'staff', 'platform'):
        result = ok('Synthetic authentication ' + person, s.call('/api/v1/auth/signin',
                    {'email': person + '@example.invalid', 'password': s.PASSWORD}))
        TOKENS[person] = result['accessToken']
    seed_private()
    seed_private('foreign', s.BOB)
    seed_private('native')
    before = s.sql('SELECT status,version,menu_json FROM bartide_customers WHERE id=?', (TENANT,))[0]
    check('Anonymous onboarding read denied', api(person=None)[0] == 401)
    check('Foreign tenant owner read denied', api(person='bob')[0] == 403)
    current = workspace()
    check('Initial private workspace belongs to owner', current['isOwner'] and current['status'] == 'draft')
    check('Onboarding read never activates or modifies public menu', s.sql('SELECT status,version,menu_json FROM bartide_customers WHERE id=?', (TENANT,))[0] == before)
    check('Lazy configuration is durable and private', s.sql('SELECT COUNT(*) FROM bartide_enhanced_configs WHERE tenant_id=?', (TENANT,))[0][0] == 1)
    missing = {x['code'] for x in current['checks'] if x['state'] == 'needs_attention'}
    check('Initial checklist identifies missing contact address hours and menu', {'business_contact', 'business_address', 'business_hours', 'menu_items'} <= missing, current['checks'])
    check('Incomplete private workspace cannot be approved', not current['canApprove'] and not current['approvalCurrent'])
    check('Initial onboarding includes exact next actions', all(x['code'] and x['message'] and x['actionPath'].startswith('/') for x in current['checks']))
    check('Draft branded site remains unavailable publicly', s.call('/api/v1/bars/' + TENANT)[0] == 404)
    check('Draft ordering remains unavailable publicly', s.call('/api/v1/restaurants/' + TENANT + '/menu')[0] == 404)
    check('Private API responses cannot be cached', 'no-store' in api()[3].get('Cache-Control', ''))
    incomplete = build()
    check('Incomplete details can preview but cannot be approved', incomplete['current'] and not incomplete['canApprove'])
    check('Incomplete release cannot be approved through API', api('approve', approval(incomplete))[0] == 409)
    current = workspace()
    s.alter_config(lambda config: config.update(preserve_onboarding_test={'futureSetting': [1, True, 'keep']}), TENANT)
    current = workspace()

    partial = copy.deepcopy(current['business'])
    partial.update(name='Partial Harbor Details', publicEmail='', phone='', address='', city='', region='', postalCode='', website='')
    first = save('business', partial)
    check('Partial draft persists without invented contact details', first['business']['name'] == partial['name'] and first['business']['address'] == '' and first['revision'] > current['revision'])
    check('Missing information remains actionable after save', any(x['section'] == 'business' and x['state'] != 'ready' for x in first['checks']))
    stale = api('business', {'expectedRevision': current['revision'], 'business': business()})
    check('Stale business save cannot overwrite a newer draft', stale[0] == 409 and workspace()['business']['name'] == partial['name'])
    check('Anonymous save denied', api('business', {'expectedRevision': first['revision'], 'business': business()}, person=None)[0] == 401)
    check('Foreign save denied', api('business', {'expectedRevision': first['revision'], 'business': business()}, person='bob')[0] == 403)
    invalid = [dict(business(), website='javascript:alert(1)'), dict(business(), publicEmail='not-an-email'),
               dict(business(), timeZone='Not/A_TimeZone'), dict(business(), country='ZZ'),
               dict(business(), hours=[{'day': 0, 'closed': False, 'opens': '99:00', 'closes': '23:00', 'overnight': False}])]
    for index, value in enumerate(invalid):
        result = api('business', {'expectedRevision': first['revision'], 'business': value})
        check('Invalid business value rejected safely ' + str(index), result[0] == 400, result[:3])
    check('Invalid writes preserve draft revision', revision_unchanged(first['revision']))
    full = save('business', business())
    check('Seven-day hours and timezone persist exactly', full['business']['hours'] == business()['hours'] and full['business']['timeZone'] == 'America/New_York')
    put_profile_hours('Private profile override: Friday evenings only.')
    changed_hours = workspace()
    check('Private profile hours mismatch requires business-hours review', next(c for c in changed_hours['checks'] if c['code'] == 'business_hours')['state'] == 'needs_attention')
    full = save('business', business())
    check('Saving structured business hours restores matching readiness', next(c for c in full['checks'] if c['code'] == 'business_hours')['state'] == 'ready')
    before_invalid = full['revision']
    for value in [dict(brand(), accent='red;url'), dict(brand(), style='untrusted'), dict(brand(), logoPhotoId=str(s.uuid.uuid4()), logoChoice='photo')]:
        check('Invalid or foreign branding rejected', api('brand', {'expectedRevision': before_invalid, 'brand': value})[0] == 400)
    check('Invalid branding leaves revision unchanged', revision_unchanged(before_invalid))
    save('brand', brand())
    save('service', service(False))
    save('team', team())
    check('Menu-only draft does not require payment processing', not workspace()['service']['requestCards'])
    put_item()
    save('service', service())
    requested_cards = save('service', dict(service(), requestCards=True))
    check('Requested card payments visibly await live verification', next(c for c in requested_cards['checks'] if c['code'] == 'merchant_payments')['state'] == 'waiting')
    save('service', service())
    check('Ordering preparation remains private', s.call('/api/v1/restaurants/' + TENANT + '/menu')[0] == 404)
    check('Draft business owner can access ordering settings', s.call('/api/v1/tenants/' + TENANT + '/ordering/settings', token=TOKENS['alice'])[0] == 200)
    ordering_path = '/api/v1/tenants/' + TENANT + '/ordering'
    pickup_only = workspace()
    check('Pickup-only setup starts without enabled table ordering', not pickup_only['service']['dineIn'] and pickup_only['service']['tableLabels'] == [])
    ordering = ok('Read private table management', s.call(ordering_path, token=TOKENS['alice']))
    ok('Create first enabled table through actual management API', s.call(ordering_path + '/tables',
       {'expectedVersion': ordering['configVersion'], 'label': 'Patio 1'}, TOKENS['alice']))
    table_setup = workspace()
    check('First managed table updates onboarding dine-in and labels', table_setup['service']['dineIn'] and 'Patio 1' in table_setup['service']['tableLabels'])
    check('Managed table satisfies onboarding table readiness', next(c for c in table_setup['checks'] if c['code'] == 'service_tables')['state'] == 'ready')

    s.sql('INSERT INTO bartide_enhanced_members(id,tenant_id,name,email,user_id,role,active,created_at) VALUES(?,?,?,?,?,?,1,?)',
          ('onboarding-manager', TENANT, 'Synthetic Manager', 'staff@example.invalid', 'supabase:' + s.STAFF, 'manager', s.NOW))
    manager = workspace(person='staff')
    check('Authorized manager can prepare private draft without ownership', not manager['isOwner'])
    manager_team = team()
    manager_team['orderContact'] = 'Synthetic Manager, +1 305 555 0102'
    save('team', manager_team, person='staff')

    release = build()
    same = build()
    check('Repeated build of identical source returns same release and fingerprint', same['id'] == release['id'] and same['fingerprint'] == release['fingerprint'])
    preview = ok('Private owner preview loads', api('preview'))
    check('Preview contains exact selected business, brand and menu', preview['business'] == business() and preview['brand'] == brand() and len(preview['items']) == 1 and preview['items'][0]['priceCents'] == 1299)
    check('Complete preview identifies template and current source', bool(preview['templateVersion']) and preview['current'] and preview['canApprove'])
    check('Foreign preview denied', api('preview', person='bob')[0] == 403)
    check('Anonymous preview denied', api('preview', person=None)[0] == 401)
    prior_counts = financial_counts()
    practice_request = {**approval(release), 'order': {'items': [{'itemId': 'house-burger', 'quantity': 2}], 'fulfillment': 'pickup', 'paymentMethod': 'staff'}}
    practice_request.pop('confirmed')
    practice = ok('Practice uses selected private menu', api('practice', practice_request))
    quote = practice['quote']
    check('Practice computes cents and tax independently', (quote['subtotalCents'], quote['taxCents'], quote['deliveryFeeCents'], quote['tipCents'], quote['totalCents']) == (2598, 182, 0, 0, 2780), quote)
    check('Practice does not create any order, account, refund or payment', financial_counts() == prior_counts)
    check('Practice quote cannot be submitted as a real order', not quote['canSubmit'])
    check('Practice requires private authorization', api('practice', practice_request, person='bob')[0] == 403)
    check('Practice rejects stale release fingerprint', api('practice', {**practice_request, 'fingerprint': '0' * 64})[0] == 409)
    check('Owner approval requires explicit confirmation', api('approve', {**approval(release), 'confirmed': False})[0] == 400)
    check('Approval rejects unknown release', api('approve', {**approval(release), 'releaseId': str(s.uuid.uuid4())})[0] == 409)
    check('Approval rejects wrong fingerprint', api('approve', {**approval(release), 'fingerprint': '0' * 64})[0] == 409)
    check('Foreign owner cannot approve release', api('approve', approval(release), person='bob')[0] == 403)
    check('Manager cannot give owner launch approval', api('approve', approval(release), person='staff')[0] == 403)
    approved = approve(release)
    check('Approval records exact release durably', approved['approvalCurrent'] and approved['approvedAt'] and approved['releaseId'] == release['id'])
    check('Owner approval alone does not publish draft', s.call('/api/v1/bars/' + TENANT)[0] == 404)

    put_item(1499)
    check('Menu edit invalidates existing approval', not workspace()['approvalCurrent'])
    check('Old preview cannot be approved after menu edit', api('approve', approval(release))[0] == 409)
    release = build()
    check('Changed menu produces a different preview fingerprint', release['fingerprint'] != preview['fingerprint'])
    approve(release)

    settings_path = '/api/v1/tenants/' + TENANT + '/ordering/settings'
    settings = ok('Read prepared operating settings', s.call(settings_path, token=TOKENS['alice']))
    changed = {**settings, 'expectedVersion': settings['version'], 'pickupInstructions': 'Collect at the side counter.'}
    changed.pop('version')
    ok('Operating settings can change while private', s.call(settings_path, changed, TOKENS['alice']))
    check('Operating change invalidates previous owner approval', not workspace()['approvalCurrent'])
    check('Stale operating preview cannot be approved', api('approve', approval(release))[0] == 409)
    release = build()
    approve(release)

    run_web()

    now = datetime.now(timezone.utc)
    s.sql("INSERT INTO tide_service_orders(id,tenant_id,environment,status,request_json,initial_cents,monthly_cents,total_cents,created_at,updated_at) VALUES(?,?,'live','paid','{}',60000,14900,60000,?,?)",
          ('synthetic-onboarding-service', TENANT, s.NOW, s.NOW))
    s.sql("UPDATE bartide_customers SET status='building',enrolled_at=?,build_ready_at=? WHERE id=?",
          (now.isoformat(), (now + timedelta(days=30)).isoformat(), TENANT))
    check('Platform cannot launch inside paid 30-day build period', publication()[0] == 409)
    check('Early launch denial keeps site private', s.call('/api/v1/bars/' + TENANT)[0] == 404)
    s.sql('UPDATE bartide_customers SET enrolled_at=?,build_ready_at=? WHERE id=?',
          ((now - timedelta(days=31)).isoformat(), (now - timedelta(days=1)).isoformat(), TENANT))
    missing_invoice = workspace()
    check('Paid order without matching initial invoice is not payment-ready', next(c for c in missing_invoice['checks'] if c['code'] == 'plan_payment')['state'] != 'ready')
    check('Mature paid order cannot publish without matching initial invoice', publication()[0] == 409)
    s.sql("INSERT INTO tide_service_invoices(id,order_id,kind,amount_cents,status,paid_at,updated_at) VALUES(?,?,'initial',60000,'paid',?,?)",
          ('synthetic-onboarding-invoice', 'synthetic-onboarding-service', s.NOW, s.NOW))
    s.sql('UPDATE tide_service_orders SET initial_invoice_id=? WHERE id=?',
          ('synthetic-onboarding-invoice', 'synthetic-onboarding-service'))
    invoice_ready = workspace()
    check('Matching paid initial invoice establishes payment readiness', next(c for c in invoice_ready['checks'] if c['code'] == 'plan_payment')['state'] == 'ready')
    for label, refunded, pending in [('pending refund', 0, 100), ('completed refund', 100, 0)]:
        s.sql('UPDATE tide_service_invoices SET refunded_cents=?,refund_pending_cents=? WHERE id=?',
              (refunded, pending, 'synthetic-onboarding-invoice'))
        refunded_setup = workspace()
        check('Payment readiness rejects ' + label, next(c for c in refunded_setup['checks'] if c['code'] == 'plan_payment')['state'] != 'ready')
        check('Mature publication rejects ' + label, publication()[0] == 409)
    s.sql('UPDATE tide_service_invoices SET refunded_cents=0,refund_pending_cents=0 WHERE id=?',
          ('synthetic-onboarding-invoice',))
    restored_payment = workspace()
    check('Synthetic invoice restoration restores payment readiness', next(c for c in restored_payment['checks'] if c['code'] == 'plan_payment')['state'] == 'ready')
    changed_brand = dict(brand(), introduction='Approved details, revised after the first preview.')
    save('brand', changed_brand)
    check('Elapsed build period does not bypass current customer approval', publication()[0] == 409)
    release = build()
    approve(release)
    check('Platform still must confirm reviewed launch', publication(reviewed=False)[0] == 400)
    published = ok('Reviewed mature and approved app publishes', publication())
    check('Publication sets active status', published['status'] == 'active')
    public = ok('Public branded app is available after launch', s.call('/api/v1/bars/' + TENANT))
    check('Published site contains approved branding and actual menu', public['business'] == business() and public['brand'] == changed_brand and public['items'][0]['name'] == 'Harbor Burger' and public['items'][0]['priceCents'] == 1499)
    check('Published payload excludes internal approval and private markers', s.PRIVATE not in json.dumps(public) and not ({'approvedAt', 'fingerprint', 'isOwner', 'team', 'releaseId'} & public.keys()))
    public_html = w.web('/bar/' + TENANT)
    check('Public app renders actual business and prices', public_html[0] == 200 and 'Harbor &amp; Hearth Synthetic' in public_html[1] and 'Harbor Burger' in public_html[1] and '14.99' in public_html[1])
    manifest_path = '/bar/' + TENANT + '/manifest.webmanifest'
    check('Published HTML selects its business install manifest', 'href="' + manifest_path + '"' in public_html[1])
    manifest_response = w.web(manifest_path)
    check('Active business install manifest is available', manifest_response[0] == 200 and 'application/manifest+json' in manifest_response[2].get('Content-Type', ''), manifest_response[:2])
    manifest = json.loads(manifest_response[1])
    check('Install manifest returns to the same named business', manifest['name'] == business()['name'] and manifest['short_name'] == business()['name'] and manifest['start_url'] == '/bar/' + TENANT and manifest['display'] == 'standalone')
    check('Install manifest contains no private onboarding metadata', s.PRIVATE not in manifest_response[1] and all(token not in manifest_response[1] for token in TOKENS.values()) and 'fingerprint' not in manifest_response[1])
    updated_phone = '+1 305 555 0199'
    live_settings = ok('Read live operating settings', s.call(settings_path, token=TOKENS['alice']))
    live_change = {**live_settings, 'expectedVersion': live_settings['version'], 'contactPhone': updated_phone}
    live_change.pop('version')
    ok('Update live phone through actual operating settings', s.call(settings_path, live_change, TOKENS['alice']))
    updated_hours = 'Wednesday through Sunday, 17:00 to 23:30.'
    put_profile_hours(updated_hours)
    live_site = ok('Read published site after workspace maintenance', s.call('/api/v1/bars/' + TENANT))
    check('Published phone follows current operating settings', live_site['business']['phone'] == updated_phone)
    check('Published hours follow current menu profile', live_site['hoursText'] == updated_hours)
    maintained_html = w.web('/bar/' + TENANT)
    check('Published HTML displays current phone and hours', maintained_html[0] == 200 and updated_phone in html.unescape(maintained_html[1]) and updated_hours in html.unescape(maintained_html[1]))
    check('Public availability still obeys platform pause', publication('paused')[0] == 200 and s.call('/api/v1/bars/' + TENANT)[0] == 404)
    check('Paused business cannot serve an install manifest', w.web(manifest_path)[0] == 404)
    check('Previously reviewed app can resume', publication('active', reviewed=False)[0] == 200 and s.call('/api/v1/bars/' + TENANT)[0] == 200)
    check('Resumed business install manifest keeps its destination', json.loads(w.web(manifest_path)[1])['start_url'] == '/bar/' + TENANT)
    check('Database relationships remain intact', s.sql('PRAGMA foreign_key_check') == [])
    config = json.loads(s.sql('SELECT settings_json FROM bartide_enhanced_configs WHERE tenant_id=?', (TENANT,))[0][0])
    check('Onboarding preserves unrelated future configuration', config['preserve_onboarding_test'] == {'futureSetting': [1, True, 'keep']})


def run_web():
    w.launch('TideCasa.Blazor', w.WEB, {'Api__BaseUrl': s.API + '/', 'DataProtection__KeysPath': str(s.RUN / 'keys'),
             'PublicDemo__Enabled': 'false', 'SampleBar__Enabled': 'false'})
    check('Unknown business cannot serve an install manifest', w.web('/bar/missing-business/manifest.webmanifest')[0] == 404)
    check('Draft business cannot serve an install manifest', w.web('/bar/' + TENANT + '/manifest.webmanifest')[0] == 404)
    page = '/workspace/native/onboarding'
    check('Anonymous native onboarding requires sign-in', w.web(page)[0] in (302, 303, 401))
    owner = w.login('alice')
    foreign = w.login('bob')
    check('Other owner cannot render private onboarding', w.web(page, client=foreign)[0] == 403)
    forms, response = w.forms_for(owner, page + '?appStores=true&referralCode=TESTREF')
    mutations = [f for f in forms if f['action'].startswith('/onboarding/native/')]
    check('Guided page renders business brand service and team native forms', {'business', 'brand', 'service', 'team'} <= {f['action'].rsplit('/', 1)[-1] for f in mutations})
    check('Every native onboarding form includes antiforgery', all(f['fields'].get('__RequestVerificationToken') for f in mutations))
    check('Private onboarding HTML has no-store and no identity tokens', 'no-store' in response[2].get('Cache-Control', '') and all(token not in response[1] for token in TOKENS.values()))
    form = w.find_form(forms, '/business')
    prior = workspace('native')['revision']
    check('Foreign-origin native save rejected', w.post(owner, form, {'name': 'Native Harbor'}, headers={'Origin': 'https://other.example.invalid'})[0] == 400)
    check('Opaque-origin native save rejected', w.post(owner, form, {'name': 'Native Harbor'}, headers={'Origin': 'null'})[0] == 400)
    check('Cross-site native save rejected', w.post(owner, form, {'name': 'Native Harbor'}, headers={'Sec-Fetch-Site': 'cross-site'})[0] == 400)
    no_csrf = w.post(owner, form, {'name': 'Native Harbor'}, remove=('__RequestVerificationToken',))
    check('Missing antiforgery does not save native form', no_csrf[0] == 400 or no_csrf[0] in (302, 303) and 'notice=expired' in no_csrf[2].get('Location', ''))
    duplicates = list(form['fields'].items()) + [('revision', form['fields']['revision'])]
    check('Duplicate native form fields rejected', w.web(form['action'], duplicates, owner, {'Origin': w.WEB})[0] == 400)
    check('Rejected native forms preserve revision', workspace('native')['revision'] == prior)
    changed_fields = {'name': 'Native Harbor', 'publicEmail': '', 'phone': '', 'address': '', 'city': '', 'region': '', 'postalCode': '', 'website': ''}
    result = w.post(owner, form, changed_fields)
    check('Native partial draft saves with referral and add-on intent intact', result[0] in (302, 303) and 'notice=saved' in result[2].get('Location', '') and 'appStores=true' in result[2].get('Location', '') and 'referralCode=TESTREF' in result[2].get('Location', ''), result[:2])
    check('Native form updates durable workspace', workspace('native')['business']['name'] == 'Native Harbor')
    stale = w.post(owner, form, {'name': 'Stale native overwrite'})
    check('Stale native form redirects with conflict notice', stale[0] in (302, 303) and 'notice=stale' in stale[2].get('Location', ''))
    check('Stale native write preserves newer draft', workspace('native')['business']['name'] == 'Native Harbor')
    forms, _ = w.forms_for(owner, '/workspace/' + TENANT + '/onboarding')
    build_form = w.find_form(forms, '/build')
    built = w.post(owner, build_form)
    preview_path = '/workspace/' + TENANT + '/preview'
    check('Native build opens private app preview', built[0] in (302, 303) and built[2].get('Location', '') == preview_path)
    forms, response = w.forms_for(owner, preview_path)
    check('Preview renders customer business and actual menu', 'Harbor &amp; Hearth Synthetic' in response[1] and 'Harbor Burger' in response[1] and '14.99' in response[1])
    check('Private preview requires sign-in', w.web(preview_path)[0] in (302, 303, 401))
    check('Private preview rejects foreign owner', w.web(preview_path, client=foreign)[0] == 403)
    practice = w.find_form(forms, '/practice')
    before = financial_counts()
    result = w.post(owner, practice, {'itemId': 'house-burger', 'fulfillment': 'pickup'})
    check('Native practice renders a non-transactional result', result[0] == 200 and 'No live order was sent' in result[1] and 'Harbor Burger' in result[1], result[:2])
    check('Native practice leaves orders and payments unchanged', financial_counts() == before)
    approval_form = w.find_form(forms, '/approve')
    result = w.post(owner, approval_form, {'confirmed': 'true'})
    check('Native approval saves exact displayed release', result[0] in (302, 303) and 'notice=approved' in result[2].get('Location', ''))


def main():
    passed = False
    error = None
    metadata_path = s.RUN / 'browser-fixture.json'
    try:
        run_api()
        passed = True
        if os.environ.get('TIDE_ONBOARDING_HOLD') == '1':
            stop_file = s.RUN / 'stop-fixture'
            metadata = {'syntheticOnly': True, 'running': True, 'web': w.WEB, 'tenant': TENANT,
                        'draftTenant': 'native', 'ownerEmail': 'alice@example.invalid',
                        'managerEmail': 'staff@example.invalid', 'stopFile': str(stop_file),
                        'checksPassed': sum(row['passed'] for row in s.RESULTS), 'maximumHoldSeconds': 600}
            metadata_path.write_text(json.dumps(metadata, indent=2), encoding='utf-8')
            print('Synthetic browser fixture ready: ' + str(metadata_path), flush=True)
            deadline = time.monotonic() + 600
            while not stop_file.exists() and time.monotonic() < deadline:
                if any(process.poll() is not None for process in s.PROCESSES):
                    raise RuntimeError('An owned browser fixture process stopped unexpectedly')
                time.sleep(.5)
    except BaseException as exception:
        passed = False
        error = type(exception).__name__
        traceback.print_exc()
    finally:
        for process in s.PROCESSES:
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=20)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=10)
        s.PROVIDER.shutdown()
        s.PROVIDER.server_close()
        for log in s.LOGS:
            log.close()
        if metadata_path.exists():
            metadata = json.loads(metadata_path.read_text(encoding='utf-8'))
            metadata['running'] = False
            metadata_path.write_text(json.dumps(metadata, indent=2), encoding='utf-8')
        (s.RUN / 'results.json').write_text(json.dumps(s.RESULTS, indent=2), encoding='utf-8')
        binaries = {}
        build_root = Path(os.environ.get('TIDE_TEST_BUILD_ROOT', s.ROOT))
        for project in ('TideCasa.Api', 'TideCasa.Blazor'):
            binary = build_root / project / 'bin/Debug/net10.0' / (project + '.dll')
            if binary.exists():
                binaries[project] = hashlib.sha256(binary.read_bytes()).hexdigest()
        summary = {'passed': passed, 'errorType': error, 'passedChecks': sum(row['passed'] for row in s.RESULTS),
                   'failedChecks': [row['check'] for row in s.RESULTS if not row['passed']],
                   'syntheticOnly': True, 'providerCalls': 'loopback identity fixture only',
                   'allOwnedProcessesStopped': all(process.poll() is not None for process in s.PROCESSES),
                   'binarySha256': binaries, 'scriptSha256': hashlib.sha256(Path(__file__).read_bytes()).hexdigest()}
        (s.RUN / 'summary.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
        print(json.dumps(summary), flush=True)
        print('Client onboarding evidence: ' + str(s.RUN), flush=True)
    return 0 if passed else 1


if __name__ == '__main__':
    raise SystemExit(main())
