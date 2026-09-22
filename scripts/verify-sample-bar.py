"""Acceptance checks for the running isolated Gulf Lantern fixture (ports 5520/5521).
Uses only fictional identities and the local API, with real cookie/CSRF forms.
Creates one new test order per run; leaves fixture people/menu/events intact.
"""
from datetime import datetime, timezone
import html
import http.cookiejar
import importlib.util
import json
from pathlib import Path
import re
import urllib.error
import urllib.parse
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('sample', ROOT / 'scripts/run-sample-bar.py')
s = importlib.util.module_from_spec(spec); spec.loader.exec_module(s)
RESULTS = []
WEB_CLIENT = '127.42.' + str(1 + uuid.uuid4().int % 200) + '.' + str(1 + uuid.uuid4().int % 200)


def check(label, value):
    RESULTS.append({'check': label, 'passed': bool(value)})
    print(('PASS ' if value else 'FAIL ') + label, flush=True)
    if not value: raise AssertionError(label)


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args): return None


def web(client, path, data=None):
    # The isolated web host trusts only its loopback proxy. Give this synthetic
    # test cohort a separate loopback address; keep real rate-limit code enabled.
    headers = {'Origin': s.WEB, 'X-Forwarded-For': WEB_CLIENT}
    if data is not None:
        data = urllib.parse.urlencode(data).encode(); headers['Content-Type'] = 'application/x-www-form-urlencoded'
    req = urllib.request.Request(s.WEB + path, data=data, headers=headers)
    try: response = client.open(req, timeout=30)
    except urllib.error.HTTPError as error: response = error
    with response: return response.status, response.read().decode('utf-8', errors='replace'), response.headers


def denied(path, token):
    try: s.call(path, token=token)
    except RuntimeError as error: return ': 403 ' in str(error) or ': 404 ' in str(error)
    return False


def main():
    seed = json.loads((s.RUN / 'seed-complete.json').read_text())
    tokens = {p['key']: s.identity(p['key']) for p in s.BAR['people']}
    menu = s.call(s.GUEST + '/menu')
    check('Exactly 20 orderable menu items', len(menu['items']) == 20 and all(i['available'] for i in menu['items']))
    check('All 20 items have attached photos', all(i['photoId'] for i in menu['items']))
    check('18 distinct photos shared by 20 menu entries', len({i['photoId'] for i in menu['items']}) == 18)
    check('Real card checkout disabled', not menu['checkout']['phonePaymentAvailable'])
    for photo in {i['photoId'] for i in menu['items']}:
        with urllib.request.build_opener(urllib.request.ProxyHandler({})).open(s.WEB + '/media/' + photo) as r:
            check('Attached photo is a served JPEG ' + photo[:8], r.status == 200 and r.headers.get_content_type() == 'image/jpeg' and len(r.read()) > 1000)
    team = s.call(s.BASE + '/team', token=tokens['owner'])
    check('Five staff plus a separate general manager', len(team['members']) == 6 and sum(m['role'] == 'manager' for m in team['members']) == 1)
    check('Six scheduled shifts and a published assigned course', len(team['shifts']) == 6 and len(team['assignments']) == 6 and team['courses'][0]['published'])
    events = s.call(s.GUEST + '/events')
    check('Three published public events', len(events['events']) == 3)
    rewards = s.call(s.BASE + '/rewards', token=tokens['owner'])
    check('Five saved customer reward memberships', len(rewards['members']) == 5)
    for p in s.BAR['people']:
        token = tokens[p['key']]
        account = s.call('/api/v1/account', token=token)
        check(p['name'] + ' is not a platform administrator', not account['user']['isPlatformOwner'])
        if p['role'] == 'customer':
            wallet = s.call(s.GUEST + '/rewards', token=token)
            check(p['name'] + ' has their own saved reward wallet', wallet['memberId'] is not None and wallet['points'] > 0)
            check(p['name'] + ' cannot read the staff order board', denied(s.BASE + '/ordering/operations', token))
        else:
            desk = s.call(s.BASE + '/ordering/operations', token=token)
            check(p['name'] + ' sees the correct operations role', desk['role'] == p['role'])
            if p['role'] != 'owner':
                check(p['name'] + ' cannot open owner billing', denied(s.BASE + '/billing', token))
                own = s.call(s.BASE + '/team', token=token)
                check(p['name'] + ' resolves a linked staff account', own['memberId'] is not None)
        client = urllib.request.build_opener(urllib.request.ProxyHandler({}), urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()), NoRedirect())
        status, body, headers = web(client, '/sample-bar')
        csrf = re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"', body)
        check(p['name'] + ' gets a protected switch form', status == 200 and csrf is not None)
        result = web(client, '/auth/session/signin', {'email': p['email'], 'password': s.BAR['password'],
            'return_to': '/account', '__RequestVerificationToken': html.unescape(csrf.group(1))})
        check(p['name'] + ' signs in through native cookie form', result[0] == 302 and result[2].get('Location') == '/account')
        status, page, _ = web(client, '/account')
        check(p['name'] + ' account page renders their identity', status == 200 and p['name'] in page)
        if p['role'] not in ('owner', 'customer'):
            check(p['name'] + ' has no billing link in navigation', '/billing' not in page and '/payments' not in page)
        routes = ['/rewards/gulf-lantern', '/events/gulf-lantern', '/updates/gulf-lantern'] if p['role'] == 'customer' else ['/workspace/gulf-lantern/operations', '/workspace/gulf-lantern/team']
        if p['role'] in ('owner', 'manager'):
            routes += ['/workspace/gulf-lantern/' + x for x in ('menu','ordering','media','rewards','events','posts')]
        for route in routes:
            status, page, _ = web(client, route)
            check(p['name'] + ' opens ' + route, status == 200 and 'temporarily unavailable' not in page and 'Unhandled exception' not in page)
            if route.endswith('/team'):
                check(p['name'] + ' team section links retain the workspace', all('href="' + route + '#' + section + '"' in page for section in ('team','schedule','messages','training')))
    # A complete unpaid pickup handoff, with an independent total calculation.
    order = {'items': [{'itemId':'smoked-fish-dip','quantity':2}], 'fulfillment':'pickup','paymentMethod':'staff','tipPercent':20}
    quote = s.call(s.GUEST + '/quote', order)
    check('Server quote adds 2200 subtotal + 154 tax + 440 tip', quote['totalCents'] == 2794)
    body = {'requestKey':str(uuid.uuid4()),'trackingKey':uuid.uuid4().hex+uuid.uuid4().hex,
        'order':order,'quoteFingerprint':quote['fingerprint'],'customerName':'Gulf Lantern QA guest','phone':'727-555-0199','note':'Isolated acceptance test'}
    receipt = s.call(s.GUEST + '/orders', body)
    repeated = s.call(s.GUEST + '/orders', body)
    check('Repeated order request does not duplicate a check', receipt['orderId'] == repeated['orderId'])
    def change(key, action, collected=False):
        desk = s.call(s.BASE + '/ordering/operations', token=tokens[key])
        item = next(x for x in desk['orders'] if x['order']['receipt']['orderId'] == receipt['orderId'])
        return s.call(s.BASE + '/ordering/orders/' + receipt['orderId'],
            {'expectedVersion':item['order']['version'],'action':action,'paymentCollected':collected},tokens[key])
    change('server-maya','accepted'); check('Maya accepts a guest order', True)
    change('bartender','preparing'); check('Bartender starts preparation', True)
    change('kitchen','ready'); check('Kitchen marks whole order ready', True)
    change('server-eli','mark-paid',True); check('Eli confirms fictional staff payment', True)
    completed = change('server-maya','completed')
    final = next(x['order']['receipt'] for x in completed['orders'] if x['order']['receipt']['orderId'] == receipt['orderId'])
    check('Order finishes with paid-in-person status', final['status'] == 'completed' and final['paymentStatus'] == 'paid_in_person')
    # Give the driver a real assigned fixture order to make that perspective useful.
    delivery = next(x for x in seed['orders'] if x['person'] == 'quinn')
    desk = s.call(s.BASE + '/ordering/operations', token=tokens['manager'])
    item = next(x for x in desk['orders'] if x['order']['receipt']['orderId'] == delivery['orderId'])
    if item['driverId'] is None:
        s.call(s.BASE + '/ordering/orders/' + delivery['orderId'], {'expectedVersion':item['order']['version'],
            'action':'assign-driver','driverId':seed['members']['driver']},tokens['manager'])
    desk = s.call(s.BASE + '/ordering/operations', token=tokens['driver'])
    check('Driver sees only their assigned delivery', len(desk['orders']) == 1 and desk['orders'][0]['order']['receipt']['orderId'] == delivery['orderId'])
    public = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    status, page, _ = web(public, '/order/gulf-lantern')
    phone = re.search(r'<input[^>]*name="payment"[^>]*value="phone"[^>]*>', page)
    check('Unavailable phone-payment control is disabled', phone is not None and 'disabled' in phone.group(0))
    status, page, _ = web(public, '/sample-bar')
    check('Section links stay on the sample bar route', 'href="/sample-bar#perspectives"' in page and 'href="#perspectives"' not in page)
    check('Sample person switch link appears in test mode', 'Switch sample person' in page)


try:
    main()
finally:
    output = s.RUN / ('acceptance-' + datetime.now(timezone.utc).strftime('%Y%m%d-%H%M%S') + '.json')
    output.write_text(json.dumps({'checks':RESULTS,'passed':sum(x['passed'] for x in RESULTS),'total':len(RESULTS)},indent=2),encoding='utf-8')
    print(str(output), flush=True)
