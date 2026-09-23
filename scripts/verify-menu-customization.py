"""Real, isolated ordering API checks for ingredient removals and item requests."""
from restaurant_test_support import *

try:
    launch()
    seed('bistro')
    seed('foreign', BOB)
    signed = call('/api/v1/auth/signin', {'email': 'alice@example.invalid', 'password': PASSWORD})
    check('Owner signs into synthetic restaurant', signed[0] == 200)
    owner = signed[1]['accessToken']
    root = '/api/v1/tenants/bistro/'
    editor = call(root + 'menu', token=owner)[1]
    item = next(value for value in editor['items'] if value['id'] == 'dish-1')
    item['ingredients'] = ['Beef', 'Cheddar', 'Tomato']
    saved = call(root + 'menu/items', {'expectedVersion': editor['version'], 'item': item}, owner)
    check('Owner saves ingredient list', saved[0] == 200, saved[:3])
    public = call('/api/v1/restaurants/bistro/menu')[1]
    check('Public menu lists configured ingredients', public['items'][0]['ingredients'] == item['ingredients'])
    for label, values in [('duplicates', ['Beef', 'beef']), ('blank', [' ']), ('too long', ['x' * 61]), ('too many', [str(n) for n in range(21)])]:
        response = call(root + 'menu/items', {'expectedVersion': saved[1]['version'], 'item': {**item, 'ingredients': values}}, owner)
        check('Menu rejects ' + label + ' ingredients', response[0] == 400)
    legacy = {key: value for key, value in item.items() if key != 'ingredients'}
    saved = call(root + 'menu/items', {'expectedVersion': saved[1]['version'], 'item': legacy}, owner)
    check('Older menu editor preserves ingredient list', saved[0] == 200 and saved[1]['items'][0]['ingredients'] == item['ingredients'])
    _, ordinary = valid_quote()
    selection = {'items': [{'itemId': 'dish-1', 'quantity': 2, 'removedIngredients': ['Tomato', 'Cheddar'], 'specialRequest': 'Extra extra crispy\nSauce on the side'}], 'fulfillment': 'pickup', 'paymentMethod': 'staff'}
    selected, quote_value = valid_quote(selection)
    line = quote_value['lines'][0]
    check('Quote carries canonical restaurant ingredient removals', line['removedIngredients'] == ['Cheddar', 'Tomato'])
    check('Quote carries item request unchanged', line['specialRequest'] == selection['items'][0]['specialRequest'])
    check('Removals keep unit price and quantity arithmetic', line['unitCents'] == ordinary['lines'][0]['unitCents'] == 1001 and quote_value['subtotalCents'] == 2002)
    for label, changes in [('unknown ingredient', {'removedIngredients': ['Foreign sauce']}), ('duplicates', {'removedIngredients': ['Tomato', 'Tomato']}), ('control character', {'specialRequest': 'bad\x00request'}), ('long request', {'specialRequest': 'x' * 241})]:
        bad = {**selection, 'items': [{**selection['items'][0], **changes}]}
        check('Quote rejects ' + label, call('/api/v1/restaurants/bistro/quote', bad)[0] == 400)
    foreign = call('/api/v1/restaurants/foreign/quote', selection)
    check('Ingredient names cannot cross restaurant menus', foreign[0] == 400)
    request = order_request(selection)
    modified = copy.deepcopy(request)
    modified['order']['items'][0]['specialRequest'] = 'A different request'
    check('Changing customization invalidates prior quote', submit(modified)[0] == 409)
    response = submit(request)
    check('Customized order is created', response[0] == 201, response[:3])
    receipt = response[1]
    check('Receipt retains quoted modifications', receipt['quote']['lines'][0] == line)
    check('Receipt still excludes private contact and tracking data', receipt_private(receipt))
    repeated = submit(request)
    check('Exact customized retry returns same single order', repeated[0] == 200 and repeated[1]['orderId'] == receipt['orderId'] and count_orders() == 1)
    check('Changed customization conflicts with used request key', submit(modified)[0] == 409)
    tracked = call('/api/v1/restaurants/bistro/track', {'orderId': receipt['orderId'], 'trackingKey': request['trackingKey']})
    check('Private tracking retains modifications', tracked[0] == 200 and tracked[1]['quote']['lines'][0] == line)
    check('Wrong tracking key cannot view requests', call('/api/v1/restaurants/bistro/track', {'orderId': receipt['orderId'], 'trackingKey': '0' * 64})[0] == 404)
    board = call(root + 'ordering/operations', token=owner)[1]
    check('Staff order board receives modifications and whole-order request', board['orders'][0]['order']['receipt']['quote']['lines'][0] == line and board['orders'][0]['order']['note'] == request['note'])
    payload = json.loads(sql('SELECT payload_json FROM bartide_enhanced_orders WHERE id=?', (receipt['orderId'],))[0][0])
    check('Legacy order payload also retains modifications', payload['lines'][0]['removed_ingredients'] == ['Cheddar', 'Tomato'] and payload['lines'][0]['special_request'] == line['specialRequest'])
    payload.pop('dotnet_receipt')
    sql('UPDATE bartide_enhanced_orders SET payload_json=? WHERE id=?', (json.dumps(payload), receipt['orderId']))
    tracked = call('/api/v1/restaurants/bistro/track', {'orderId': receipt['orderId'], 'trackingKey': request['trackingKey']})
    check('Historical receipt fallback preserves modifications', tracked[0] == 200 and tracked[1]['quote']['lines'][0] == line)
    editor = call(root + 'menu', token=owner)[1]
    cleared = call(root + 'menu/items', {'expectedVersion': editor['version'], 'item': {**item, 'ingredients': []}}, owner)
    check('Manager can explicitly clear ingredients', cleared[0] == 200 and cleared[1]['items'][0]['ingredients'] == [])
    check('Removing menu ingredient stops new removal choices', call('/api/v1/restaurants/bistro/quote', selection)[0] == 400)
    tracked = call('/api/v1/restaurants/bistro/track', {'orderId': receipt['orderId'], 'trackingKey': request['trackingKey']})
    check('Menu edits do not rewrite historical order requests', tracked[0] == 200 and tracked[1]['quote']['lines'][0] == line)
finally:
    for proc in PROCESSES:
        if proc.poll() is None:
            proc.terminate()
            try: proc.wait(timeout=20)
            except subprocess.TimeoutExpired: proc.kill(); proc.wait(timeout=10)
    PROVIDER.shutdown(); PROVIDER.server_close()
    for log in LOGS: log.close()
    (RUN / 'menu-customization-results.json').write_text(json.dumps(RESULTS, indent=2), encoding='utf-8')
    print('Menu customization evidence: ' + str(RUN), flush=True)
print(str(len(RESULTS)) + ' menu customization checks passed.', flush=True)
