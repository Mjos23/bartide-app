"""Populate only the running, fictional Gulf Lantern preview with Friday practice.

This is a resumable functional rehearsal, not a load benchmark. The separate
verify-friday-load.py script measures a disposable clone with stable client IPs.
No real payment, message delivery or live service is used here.
"""
import argparse
from collections import Counter
from datetime import datetime, timezone
import importlib.util
import json
from pathlib import Path
import sqlite3
import uuid

spec = importlib.util.spec_from_file_location('sample', Path(__file__).with_name('run-sample-bar.py'))
s = importlib.util.module_from_spec(spec)
spec.loader.exec_module(s)
parser = argparse.ArgumentParser()
parser.add_argument('--tables-only', action='store_true')
args = parser.parse_args()
assert s.API == 'http://127.0.0.1:5520' and s.WEB == 'http://127.0.0.1:5521'
assert s.BAR['fictional'] and s.BAR['id'] == 'gulf-lantern'
assert (s.RUN / 'running.json').exists() and (s.RUN / 'seed-complete.json').exists()
with sqlite3.connect('file:' + s.DB.as_posix() + '?mode=ro', uri=True) as db:
    assert db.execute('SELECT id FROM bartide_customers').fetchall() == [('gulf-lantern',)]
    backup = s.RUN / 'before-friday-rehearsal.db'
    if not backup.exists():
        with sqlite3.connect(backup) as dest: db.backup(dest)

owner = s.identity('owner')
workspace = s.call(s.BASE + '/ordering', token=owner)
for number in range(1, 13):
    label = 'Table ' + str(number)
    table = next((t for t in workspace['tables'] if t['label'] == label), None)
    if table is None:
        workspace = s.call(s.BASE + '/ordering/tables', {
            'expectedVersion': workspace['configVersion'], 'label': label}, owner)
    elif not table['enabled']:
        raise RuntimeError(label + ' was disabled; inspect it before running practice.')
tables = {t['label']: t for t in workspace['tables']}
print('Ready: Table 1 through Table 12; existing Patio 1 preserved.', flush=True)
if args.tables_only: raise SystemExit(0)

marker = s.RUN / 'friday-rehearsal.json'
state = json.loads(marker.read_text()) if marker.exists() else {
    'runId': str(uuid.uuid4()), 'startedAt': datetime.now(timezone.utc).isoformat(),
    'description': '30 fictional Friday service orders, compressed into one rehearsal; no real money collected.',
    'orders': [], 'activities': [], 'tables': [{**t, 'orderUrl': s.WEB + '/order/gulf-lantern?table=' + t['token']}
        for label, t in tables.items() if label.startswith('Table ')]}
def save(): marker.write_text(json.dumps(state, indent=2), encoding='utf-8')
save()
if state.get('completedAt'):
    print('This rehearsal already completed. Existing records retained: ' + str(marker))
    raise SystemExit(0)
tokens = {p['key']: (owner if p['key'] == 'owner' else s.identity(p['key'])) for p in s.BAR['people']}
people = [p for p in s.BAR['people'] if p['role'] == 'customer']
seed = json.loads((s.RUN / 'seed-complete.json').read_text())
menu = s.call(s.GUEST + '/menu')['items']

def key(label): return str(uuid.uuid5(uuid.UUID(state['runId']), label))
def operation(order_id):
    board = s.call(s.BASE + '/ordering/operations', token=owner)
    return next(o for o in board['orders'] if o['order']['receipt']['orderId'] == order_id)

for index in range(30):
    if len(state['orders']) <= index:
        person = people[index % len(people)]
        fulfillment = 'delivery' if index in (7, 17) else 'pickup' if index in (5, 15) else 'dine-in'
        number = index % 12 + 1
        selected = {'items': [{'itemId': menu[index % len(menu)]['id'], 'quantity': 2},
                              {'itemId': menu[(index + 7) % len(menu)]['id'], 'quantity': 1}],
                    'fulfillment': fulfillment, 'paymentMethod': 'staff', 'tipPercent': [0, 15, 20][index % 3]}
        if fulfillment == 'dine-in':
            selected.update({'tableLabel': str(number)} if index % 2 == 0 else {'tableToken': tables['Table ' + str(number)]['token']})
        if fulfillment == 'delivery': selected['deliveryZip'] = '33706'
        quote = s.call(s.GUEST + '/quote', selected)
        request = {'requestKey': key('order-' + str(index)), 'trackingKey': uuid.uuid4().hex + uuid.uuid4().hex,
            'order': selected, 'quoteFingerprint': quote['fingerprint'], 'customerName': person['name'],
            'phone': '727-555-0147', 'address': 'Fictional patio guest house, St. Pete Beach' if fulfillment == 'delivery' else None,
            'note': 'Friday rehearsal ticket ' + str(index + 1) + ' of 30. Fictional food and payment.'}
        state['orders'].append({'index': index, 'customer': person['key'], 'request': request, 'done': False})
        save()
    entry = state['orders'][index]
    if entry['done']: continue
    request = entry['request']
    receipt = s.call(s.GUEST + '/orders', request)
    order_id = receipt['orderId']
    entry['orderId'] = order_id
    target = 'completed' if index < 20 else 'ready' if index < 24 else 'preparing' if index < 27 else 'accepted' if index < 29 else 'new'
    prep_person = 'bartender' if index % 2 else 'kitchen'
    server = 'server-maya' if index % 2 else 'server-eli'
    actions = [('accepted', server), ('preparing', prep_person), ('ready', prep_person)]
    if target != 'completed': actions = actions[:['new', 'accepted', 'preparing', 'ready'].index(target)]
    else:
        actions.append(('mark-paid', server))
        if request['order']['fulfillment'] == 'delivery':
            actions.extend([('assign-driver', 'manager'), ('out_for_delivery', 'driver'), ('completed', 'driver')])
        else: actions.append(('completed', server))
    current = operation(order_id)
    # Each ordered transition increments the version. Resume skips already-applied steps.
    for step, (action, actor) in enumerate(actions):
        if current['order']['version'] > step: continue
        body = {'expectedVersion': current['order']['version'], 'action': action}
        if action == 'mark-paid': body['paymentCollected'] = True  # fictional fixture only
        if action == 'assign-driver': body['driverId'] = seed['members']['driver']
        board = s.call(s.BASE + '/ordering/orders/' + order_id, body, tokens[actor])
        current = next(o for o in board['orders'] if o['order']['receipt']['orderId'] == order_id)
    tracked = s.call(s.GUEST + '/track', {'orderId': order_id, 'trackingKey': request['trackingKey']})
    assert tracked['status'] == target
    assert tracked['quote']['totalCents'] == receipt['quote']['totalCents']
    if target == 'completed': assert tracked['paymentStatus'] == 'paid_in_person'
    if request['order']['fulfillment'] == 'dine-in': assert tracked['quote']['tableLabel'].startswith('Table ')
    entry.update(done=True, finalReceipt=tracked)
    save()
    print(f"Ticket {index + 1:02d}: {entry['customer']} / {tracked['quote']['tableLabel'] or tracked['quote']['fulfillment']} / {target}", flush=True)

# Exercise account-bound activity separately: guest orders themselves use private receipt keys.
events = s.call(s.GUEST + '/events')['events']
for index, person in enumerate(people):
    activity = 'customer-' + person['key']
    if activity in state['activities']: continue
    customer = tokens[person['key']]
    wallet = s.call(s.GUEST + '/rewards', token=customer)
    s.call(s.BASE + '/rewards/points', {'requestId': key(activity + '-points'), 'memberId': wallet['memberId'],
        'points': 10, 'sourceReference': 'friday-' + state['runId'] + '-' + person['key'],
        'reason': 'Fictional Friday service rehearsal'}, tokens['manager'])
    mine = s.call(s.GUEST + '/events/mine', token=customer)['reservations']
    event = events[index % len(events)]
    existing = next((r for r in mine if r['event']['id'] == event['id']), None)
    if existing is None or existing['rsvp']['state'] not in ('confirmed', 'checked_in'):
        s.call(s.GUEST + '/events/' + event['id'] + '/rsvp', {'requestKey': key(activity + '-rsvp'),
            'expectedVersion': existing['rsvp']['version'] if existing else -1, 'attending': True}, customer)
    state['activities'].append(activity); save()

for actor, message in [('manager', 'Friday rehearsal: check table numbers, keep the patio handoff clear and ask staff about allergies.'),
                       ('bartender', 'Friday rehearsal: bar tickets are moving; ready drinks are on the handoff counter.'),
                       ('server-maya', 'Friday rehearsal: patio orders checked against table numbers. Payments here are fictional.')]:
    activity = 'message-' + actor
    if activity in state['activities']: continue
    s.call(s.BASE + '/team/messages', {'requestId': key(activity), 'text': message}, tokens[actor])
    state['activities'].append(activity); save()

state['statusCounts'] = dict(Counter(o['finalReceipt']['status'] for o in state['orders']))
state['totalCents'] = sum(o['finalReceipt']['quote']['totalCents'] for o in state['orders'])
state['completedAt'] = datetime.now(timezone.utc).isoformat()
save()
print(json.dumps({'statusCounts': state['statusCounts'], 'fictionalTotalCents': state['totalCents'], 'evidence': str(marker)}, indent=2))
