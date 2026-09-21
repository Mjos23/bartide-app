"""Verify restaurant management through rendered native Blazor forms.

Uses an isolated real API/web/database with a local fake identity provider. Never
contacts real accounts, the owner preview, Stripe, email services or production.
Build API and Blazor first. --hold keeps this synthetic fixture available for a
browser walkthrough; create the reported stop file to cleanly stop its children.
"""
import argparse
from datetime import datetime, timezone
import html
from html.parser import HTMLParser
import http.cookiejar
import json
import os
from pathlib import Path
import subprocess
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

import restaurant_test_support as support


WEB = 'http://127.0.0.1:' + str(support.port())
RUN = support.RUN
STOP = RUN / 'stop-fixture'
META = RUN / 'web-fixture.json'


class Forms(HTMLParser):
    """Read successful HTML controls rather than recreating application forms."""
    def __init__(self, document):
        super().__init__(convert_charrefs=True)
        self.forms = []
        self.current = self.textarea = self.select = self.option = None
        self.feed(document)

    def handle_starttag(self, tag, attributes):
        attrs = dict(attributes)
        if tag == 'form':
            self.current = {'action': attrs.get('action', ''), 'fields': {}, 'controls': []}
            self.forms.append(self.current)
        if self.current is None:
            return
        if tag == 'input' and attrs.get('name'):
            self.current['controls'].append(attrs)
            kind = attrs.get('type', 'text')
            if 'disabled' not in attrs and kind not in ('submit', 'button', 'file') and (kind not in ('checkbox', 'radio') or 'checked' in attrs):
                self.current['fields'][attrs['name']] = attrs.get('value', 'on' if kind in ('checkbox', 'radio') else '')
        if tag == 'textarea' and attrs.get('name'):
            self.current['controls'].append(attrs)
            self.textarea = [attrs['name'], '']
        if tag == 'select' and attrs.get('name'):
            self.current['controls'].append(attrs)
            self.select = {'name': attrs['name'], 'first': None, 'selected': None}
        if tag == 'option' and self.select is not None:
            self.option = [attrs, '']

    def handle_data(self, data):
        if self.textarea is not None:
            self.textarea[1] += data
        if self.option is not None:
            self.option[1] += data

    def handle_endtag(self, tag):
        if tag == 'textarea' and self.textarea is not None and self.current is not None:
            self.current['fields'][self.textarea[0]] = self.textarea[1]
            self.textarea = None
        if tag == 'option' and self.option is not None and self.select is not None:
            attrs, text = self.option
            value = attrs.get('value', text)
            if self.select['first'] is None:
                self.select['first'] = value
            if 'selected' in attrs:
                self.select['selected'] = value
            self.option = None
        if tag == 'select' and self.select is not None and self.current is not None:
            self.current['fields'][self.select['name']] = self.select['selected'] if self.select['selected'] is not None else self.select['first'] or ''
            self.select = None
        if tag == 'form':
            self.current = None


def check(name, condition, detail=None):
    support.check(name, condition, detail)


def browser():
    jar = http.cookiejar.CookieJar()
    return urllib.request.build_opener(urllib.request.ProxyHandler({}), support.NoRedirect(), urllib.request.HTTPCookieProcessor(jar))


def web(path, fields=None, client=None, headers=None):
    assert path.startswith('/') and not path.startswith('//'), 'Fixture requests must stay on the local web host'
    request_headers = dict(headers or {})
    encoded = None
    if fields is not None:
        encoded = urllib.parse.urlencode(fields).encode()
        request_headers.setdefault('Content-Type', 'application/x-www-form-urlencoded')
    request = urllib.request.Request(WEB + path, data=encoded, headers=request_headers)
    try:
        response = (client or browser()).open(request, timeout=45)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        return response.status, response.read().decode('utf-8', errors='replace'), response.headers


def forms_for(client, page, name=None):
    response = web(page, client=client)
    check(name or 'Page loads ' + page, response[0] == 200, response[:2])
    return Forms(response[1]).forms, response


def find_form(forms, suffix=None, **fields):
    matches = [form for form in forms if (suffix is None or form['action'].endswith(suffix))
               and all(form['fields'].get(key) == value for key, value in fields.items())]
    if len(matches) != 1:
        raise AssertionError('Expected one rendered form: ' + str((suffix, fields, len(matches))))
    return matches[0]


def post(client, form, changes=None, remove=(), headers=None):
    payload = {**form['fields'], **(changes or {})}
    for key in remove:
        payload.pop(key, None)
    return web(form['action'], payload, client, {'Origin': WEB, **(headers or {})})


def saved(response, name):
    check(name, response[0] in (302, 303) and 'notice=saved' in response[2].get('Location', ''), response[:2])


def login(person):
    client = browser()
    forms, _ = forms_for(client, '/signin', 'Login form renders for ' + person)
    form = find_form(forms, '/auth/session/signin')
    response = post(client, form, {'email': person + '@example.invalid', 'password': support.PASSWORD})
    check('Native sign-in succeeds for ' + person, response[0] in (302, 303) and response[2].get('Location', '').startswith('/account'), response[:2])
    return client


def launch(project, address, extra):
    env = os.environ.copy()
    for name in list(env):
        if any(word in name.upper() for word in ('STRIPE', 'RESEND', 'SUPABASE', 'CLOUDFLARE', 'AUTH__', 'ORDERING__', 'STORAGE__', 'SERVICEBILLING__', 'MERCHANTPAYMENTS__', 'NOTIFICATIONS__', 'MEDIA__', 'API__', 'DATAPROTECTION__')):
            env.pop(name)
    env.update({'ASPNETCORE_ENVIRONMENT': 'Development', 'ASPNETCORE_URLS': address,
                'Stripe__CheckoutEnabled': 'false', 'Stripe__InvoicesEnabled': 'false', **extra})
    output = (RUN / (project + '.web-verification.log')).open('w', encoding='utf-8')
    support.LOGS.append(output)
    proc = subprocess.Popen([str(support.SDK), str(Path(os.environ.get('TIDE_TEST_BUILD_ROOT', str(support.ROOT))) / project / 'bin/Debug/net10.0' / (project + '.dll'))],
                            cwd=support.ROOT / project, env=env, stdout=output, stderr=subprocess.STDOUT,
                            creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
    support.PROCESSES.append(proc)
    deadline = time.monotonic() + 180
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    while time.monotonic() < deadline:
        if proc.poll() is not None:
            raise RuntimeError(project + ' stopped; inspect its fixture log')
        try:
            with opener.open(address + '/health', timeout=3) as response:
                if response.status == 200:
                    return
        except (OSError, urllib.error.URLError):
            pass
        time.sleep(.5)
    raise TimeoutError('Fixture startup: ' + project)


def menu_state():
    row = support.sql("SELECT version,menu_json FROM bartide_customers WHERE id='bistro'")[0]
    return row[0], json.loads(row[1])


def order_state(ident):
    row = support.sql('SELECT version,payload_json FROM bartide_enhanced_orders WHERE id=?', (ident,))[0]
    return row[0], json.loads(row[1])


def order_form(client, order_id, action):
    forms, response = forms_for(client, '/workspace/bistro/operations')
    return find_form(forms, '/orders/' + order_id, action=action), response


def run_checks():
    launch('TideCasa.Api', support.API, {'Storage__DatabasePath': str(support.DB),
        'Auth__Enabled': 'true', 'Auth__AllowLocalTestProvider': 'true',
        'Auth__SupabaseUrl': f'http://127.0.0.1:{support.PROVIDER.server_port}', 'Auth__PublishableKey': support.KEY,
        'Auth__PlatformOwnerUserId': 'supabase:' + support.PLATFORM,
        'Ordering__PublicBaseUrl': WEB, 'ReverseProxy__KnownClientProxy': '127.0.0.1'})
    for args in [('bistro',), ('foreign', support.BOB), ('draft', support.ALICE, 'draft'), ('basic', support.ALICE, 'active', False)]:
        support.seed(*args)
    for member, person, role in [('kitchen', support.STAFF, 'kitchen'), ('driver', support.BOB, 'driver')]:
        support.sql('INSERT INTO bartide_enhanced_members(id,tenant_id,name,email,user_id,role,active,created_at) VALUES(?,?,?,?,?,?,1,?)',
                    (member, 'bistro', 'Synthetic ' + member.title(), member + '@example.invalid', 'supabase:' + person, role, support.NOW))
    launch('TideCasa.Blazor', WEB, {'Api__BaseUrl': support.API + '/', 'DataProtection__KeysPath': str(RUN / 'keys')})
    check('Anonymous menu management requires sign-in', web('/workspace/bistro/menu')[0] in (302, 303, 401))
    check('Anonymous operations require sign-in', web('/workspace/bistro/operations')[0] in (302, 303, 401))
    owner, kitchen, driver = login('alice'), login('staff'), login('bob')
    forms, response = forms_for(owner, '/workspace/bistro/menu', 'Owner menu and settings render together')
    check('Management page cannot be cached and supports same-origin forms', 'no-store' in response[2].get('Cache-Control', '') and response[2].get('Referrer-Policy') == 'same-origin')
    check('Menu markup contains no provider token or private stored marker', support.PRIVATE not in response[1] and all(token not in response[1] for token in support.TOKENS))
    check('All mutation forms render antiforgery tokens', len(forms) > 20 and all('__RequestVerificationToken' in form['fields'] for form in forms))
    check('Owner cannot view another owner menu', web('/workspace/foreign/menu', client=owner)[0] == 403)
    check('Draft owner can prepare its menu', web('/workspace/draft/menu', client=owner)[0] == 200)
    basic = web('/workspace/basic/menu', client=owner)
    check('Unenrolled menu still renders when settings unavailable', basic[0] == 200 and 'Save restaurant details' in basic[1] and 'Ordering settings are not available' in basic[1] and '/restaurant-management/basic/settings' not in basic[1])

    item = find_form(forms, '/items', id='dish-1')
    version_before, _ = menu_state()
    check('Foreign origin mutation is rejected', post(owner, item, {'price': '12.34'}, headers={'Origin': 'https://other.example.invalid'})[0] == 400)
    check('Opaque origin mutation is rejected', post(owner, item, {'price': '12.34'}, headers={'Origin': 'null'})[0] == 400)
    check('Cross-site fetch mutation is rejected', post(owner, item, {'price': '12.34'}, headers={'Sec-Fetch-Site': 'cross-site'})[0] == 400)
    check('Missing antiforgery mutation is rejected', post(owner, item, {'price': '12.34'}, remove=('__RequestVerificationToken',))[0] == 400)
    check('Missing origin and referrer is rejected', web(item['action'], item['fields'], owner)[0] == 400)
    duplicate_fields = list(item['fields'].items()) + [('version', item['fields']['version'])]
    check('Duplicate form fields are rejected', web(item['action'], duplicate_fields, owner, {'Origin': WEB})[0] == 400)
    check('CSRF failures leave menu unchanged', menu_state()[0] == version_before)
    invalid = post(owner, item, {'price': '12.345'})
    check('Fractions smaller than a cent do not save', invalid[0] in (302, 303) and 'notice=amount' in invalid[2].get('Location', '') and menu_state()[0] == version_before)
    saved(post(owner, item, {'price': '12.34'}), 'Native item form saves a dollar price')
    version, stored = menu_state()
    check('Dollar amount converts exactly to 1234 cents and increments version once', version == version_before + 1 and next(i for i in stored['items'] if i['id'] == 'dish-1')['price_cents'] == 1234)
    stale = post(owner, item, {'price': '98.76'})
    check('Stale menu form cannot overwrite new price', 'notice=changed' in stale[2].get('Location', '') and menu_state()[0] == version)

    forms, _ = forms_for(owner, '/workspace/bistro/menu')
    item = find_form(forms, '/items', id='dish-1')
    large_item = {**item['fields'], 'name': '菜' * 160, 'description': '餐' * 1000}
    saved(post(owner, item, large_item), 'Maximum-length multilingual item survives native form encoding')
    check('Multilingual item persists without truncation', len(next(i for i in menu_state()[1]['items'] if i['id'] == 'dish-1')['description']) == 1000)
    forms, _ = forms_for(owner, '/workspace/bistro/menu')
    profile = find_form(forms, '/profile')
    large_profile = {'name': '館' * 160, 'area': '城' * 160, 'tagline': '食' * 250, 'hours': '時' * 500,
                     'service_note': '客' * 1000, 'website': 'https://example.invalid/?q=' + 'a' * (2048 - len('https://example.invalid/?q='))}
    saved(post(owner, profile, large_profile), 'Maximum-length multilingual profile survives native form encoding')
    forms, _ = forms_for(owner, '/workspace/bistro/menu')
    profile = find_form(forms, '/profile')
    saved(post(owner, profile, {'name': 'Synthetic Bistro Web', 'area': 'Local fixture', 'tagline': 'Synthetic walkthrough',
                              'hours': 'Tuesday 11 am–10 pm\nWednesday 11 am–10 pm', 'service_note': 'Local synthetic data only.', 'website': 'https://example.invalid'}),
          'Multiline opening hours save through the native profile form')
    forms, _ = forms_for(owner, '/workspace/bistro/menu')
    item = find_form(forms, '/items', id='dish-1')
    saved(post(owner, item, {'name': 'Synthetic dish 1', 'description': 'Local fixture dish'}), 'Restore a readable synthetic menu item')

    forms, _ = forms_for(owner, '/workspace/bistro/menu')
    category = next(form for form in forms if form['action'].endswith('/categories') and form['fields']['id'].startswith('category-'))
    category_id = category['fields']['id']
    saved(post(owner, category, {'name': 'Web specials'}), 'New category uses its rendered stable identifier')
    forms, _ = forms_for(owner, '/workspace/bistro/menu')
    add_item = next(form for form in forms if form['action'].endswith('/items') and form['fields']['id'].startswith('item-'))
    item_id = add_item['fields']['id']
    saved(post(owner, add_item, {'name': 'Display-only catch', 'category_id': category_id, 'price': '', 'price_label': 'Market price'}), 'Display-only priced item saves through native form')
    forms, _ = forms_for(owner, '/workspace/bistro/menu')
    removal = find_form(forms, '/items/' + item_id + '/remove')
    before = menu_state()[0]
    declined = post(owner, removal)
    check('Removal requires its explicit confirmation checkbox', 'notice=confirm-remove' in declined[2].get('Location', '') and menu_state()[0] == before)
    saved(post(owner, removal, {'confirm_remove': 'true'}), 'Confirmed item removal succeeds')
    forms, _ = forms_for(owner, '/workspace/bistro/menu')
    saved(post(owner, find_form(forms, '/categories/' + category_id + '/remove'), {'confirm_remove': 'true'}), 'Confirmed empty category removal succeeds')

    forms, _ = forms_for(owner, '/workspace/bistro/menu')
    settings = find_form(forms, '/settings')
    saved(post(owner, settings, {'tax_percent': '7.25', 'delivery_fee': '4.25', 'delivery_minimum': '10.00', 'delivery_capacity': '4',
                                'delivery_zips': '33101, 33102', 'pickup_instructions': 'Collect at the counter.\nAsk the host.'}), 'Ordering settings save through native fields')
    stored_settings = json.loads(support.sql("SELECT settings_json FROM bartide_enhanced_configs WHERE tenant_id='bistro'")[0][0])
    check('Tax percent and delivery dollars convert exactly', stored_settings['tax_basis_points'] == 725 and stored_settings['delivery_fee_cents'] == 425 and stored_settings['delivery_minimum_cents'] == 1000)
    check('Stale settings require review', 'notice=changed' in post(owner, settings)[2].get('Location', ''))

    dine_request = support.order_request({'items': [{'itemId': 'dish-1', 'quantity': 1}], 'fulfillment': 'dine-in', 'paymentMethod': 'staff', 'tableToken': support.TABLE, 'tipPercent': 20}, customerName='Synthetic Table Guest', note='A synthetic table order')
    dine = support.submit(dine_request)
    check('Synthetic table order is created for operation checks', dine[0] == 201, dine[:3])
    dine_id = dine[1]['orderId']
    delivery_request = support.order_request({'items': [{'itemId': 'dish-1', 'quantity': 2}], 'fulfillment': 'delivery', 'paymentMethod': 'staff', 'deliveryZip': '33101'}, customerName='Synthetic Delivery Guest', note='A synthetic delivery order')
    delivery = support.submit(delivery_request)
    check('Synthetic delivery order is created for operation checks', delivery[0] == 201, delivery[:3])
    delivery_id = delivery[1]['orderId']
    owner_forms, owner_page = forms_for(owner, '/workspace/bistro/operations')
    check('Owner sees both orders and tips', dine_id in owner_page[1] and delivery_id in owner_page[1] and '$2.47' in owner_page[1])
    check('Owner has management navigation and payment confirmation', '/workspace/bistro/menu' in owner_page[1] and any(f['fields'].get('action') == 'mark-paid' for f in owner_forms)
          and 'payment_collected' in owner_page[1])
    kitchen_forms, kitchen_page = forms_for(kitchen, '/workspace/bistro/operations')
    check('Kitchen sees fulfillment actions without payment, cancellation or owner navigation', any(f['fields'].get('action') == 'accepted' for f in kitchen_forms)
          and not any(f['fields'].get('action') in ('mark-paid', 'cancelled', 'assign-driver') for f in kitchen_forms)
          and '/workspace/bistro/menu' not in kitchen_page[1] and '/workspace/bistro/ordering' not in kitchen_page[1])
    check('Kitchen menu editing is denied', web('/workspace/bistro/menu', client=kitchen)[0] == 403)
    driver_forms, driver_page = forms_for(driver, '/workspace/bistro/operations')
    check('Driver cannot see unassigned orders or owner links', dine_id not in driver_page[1] and delivery_id not in driver_page[1]
          and '/workspace/bistro/menu' not in driver_page[1] and '/workspace/bistro/ordering' not in driver_page[1])

    kitchen_accept = find_form(kitchen_forms, '/orders/' + dine_id, action='accepted')
    forbidden = post(kitchen, kitchen_accept, {'action': 'mark-paid', 'payment_collected': 'true'})
    check('Forged kitchen payment action is rejected server-side', 'notice=conflict' in forbidden[2].get('Location', '') and order_state(dine_id)[1]['payment_status'] == 'unpaid')
    saved(post(kitchen, kitchen_accept), 'Kitchen accepts an order through its native form')
    check('Old order form conflicts after one committed change', 'notice=changed' in post(kitchen, kitchen_accept)[2].get('Location', ''))
    for action in ('preparing', 'ready'):
        form, _ = order_form(kitchen, dine_id, action)
        saved(post(kitchen, form), 'Kitchen advances order to ' + action)
    paid_form, _ = order_form(owner, dine_id, 'mark-paid')
    missing = post(owner, paid_form)
    check('Payment recording requires a checked collection confirmation', 'notice=confirm-payment' in missing[2].get('Location', '') and order_state(dine_id)[1]['payment_status'] == 'unpaid')
    saved(post(owner, paid_form, {'payment_collected': 'true'}), 'Owner records full payment actually collected from synthetic guest')
    complete_form, _ = order_form(kitchen, dine_id, 'completed')
    saved(post(kitchen, complete_form), 'Kitchen completes a paid table order')
    check('Payment and completed status persist together', order_state(dine_id)[1]['status'] == 'completed' and order_state(dine_id)[1]['payment_status'] == 'paid_in_person')

    assignment, _ = order_form(owner, delivery_id, 'assign-driver')
    saved(post(owner, assignment, {'driver_id': 'driver'}), 'Owner assigns delivery driver through native select')
    driver_forms, driver_page = forms_for(driver, '/workspace/bistro/operations')
    check('Assigned driver sees only its own delivery', 'Synthetic Delivery Guest' in driver_page[1] and 'Synthetic Table Guest' not in driver_page[1])
    for action in ('accepted', 'preparing', 'ready'):
        form, _ = order_form(kitchen, delivery_id, action)
        saved(post(kitchen, form), 'Kitchen moves delivery to ' + action)
    dispatch, _ = order_form(driver, delivery_id, 'out_for_delivery')
    saved(post(driver, dispatch), 'Assigned driver dispatches its delivery')
    driver_forms, _ = forms_for(driver, '/workspace/bistro/operations')
    check('Unpaid delivery exposes no completion action', not any(f['fields'].get('action') == 'completed' for f in driver_forms))
    paid_form, _ = order_form(owner, delivery_id, 'mark-paid')
    saved(post(owner, paid_form, {'payment_collected': 'true'}), 'Owner records collected delivery payment')
    complete, _ = order_form(driver, delivery_id, 'completed')
    saved(post(driver, complete), 'Assigned driver completes the paid delivery')
    support.sql("UPDATE bartide_enhanced_members SET active=0 WHERE id='driver'")
    check('Revoked driver loses SSR operations access immediately', web('/workspace/bistro/operations', client=driver)[0] == 403)
    support.sql("UPDATE bartide_enhanced_members SET active=1 WHERE id='driver'")
    check('Database remains consistent after web workflow', support.sql('PRAGMA foreign_key_check') == [])
    # Leave one new unpaid synthetic order for the optional real-browser walkthrough.
    walkthrough = support.submit(support.order_request(customerName='Synthetic Walkthrough Guest', note='Safe local order for a browser walkthrough'))
    check('Walkthrough fixture has a fresh unpaid order', walkthrough[0] == 201)
    return {'owner': 'alice@example.invalid', 'kitchen': 'staff@example.invalid', 'driver': 'bob@example.invalid',
            'password': support.PASSWORD, 'signinUrl': WEB + '/signin', 'menuUrl': WEB + '/workspace/bistro/menu',
            'operationsUrl': WEB + '/workspace/bistro/operations', 'tablesUrl': WEB + '/workspace/bistro/ordering',
            'orderUrl': WEB + '/order/bistro?table=' + support.TABLE}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--hold', action='store_true', help='Retain only this isolated fixture for browser verification')
    parser.add_argument('--hold-seconds', type=int, default=1800)
    args = parser.parse_args()
    completed = False
    try:
        credentials = run_checks()
        completed = True
        credential_file = RUN / 'synthetic-credentials.json'
        credential_file.write_text(json.dumps(credentials, indent=2), encoding='utf-8')
        metadata = {'syntheticOnly': True, 'running': bool(args.hold), 'controllerPid': os.getpid(),
                    'processIds': [proc.pid for proc in support.PROCESSES], 'api': support.API, 'web': WEB,
                    'credentialsFile': str(credential_file), 'database': str(support.DB), 'stopFile': str(STOP),
                    'checksPassed': len(support.RESULTS)}
        META.write_text(json.dumps(metadata, indent=2), encoding='utf-8')
        (RUN / 'web-results.json').write_text(json.dumps(support.RESULTS, indent=2), encoding='utf-8')
        print(f'{len(support.RESULTS)} management web checks passed.', flush=True)
        print('Fixture metadata: ' + str(META), flush=True)
        if args.hold:
            print('Synthetic browser fixture held at ' + WEB + '; create the stop file to clean up.', flush=True)
            deadline = time.monotonic() + max(1, min(args.hold_seconds, 7200))
            while not STOP.exists() and time.monotonic() < deadline:
                if any(proc.poll() is not None for proc in support.PROCESSES):
                    raise RuntimeError('A held fixture child stopped unexpectedly')
                time.sleep(.5)
    finally:
        for proc in support.PROCESSES:
            if proc.poll() is None:
                proc.terminate()
                try:
                    proc.wait(timeout=20)
                except subprocess.TimeoutExpired:
                    proc.kill()
                    proc.wait(timeout=10)
        support.PROVIDER.shutdown()
        support.PROVIDER.server_close()
        for log in support.LOGS:
            log.close()
        (RUN / 'web-results.json').write_text(json.dumps(support.RESULTS, indent=2), encoding='utf-8')
        if META.exists():
            metadata = json.loads(META.read_text(encoding='utf-8'))
            metadata['running'] = False
            META.write_text(json.dumps(metadata, indent=2), encoding='utf-8')
        print('Management web evidence: ' + str(RUN), flush=True)
        if not completed:
            print('Verification did not complete; all owned fixture processes were stopped.', flush=True)


if __name__ == '__main__':
    main()
