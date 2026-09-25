"""Local real API/web discovery checks. Synthetic data only, no location/provider services."""
import importlib.util
import json
import sys
import time
import restaurant_test_support as s

spec = importlib.util.spec_from_file_location('discovery_web', s.Path(__file__).with_name('verify-management-web.py'))
w = importlib.util.module_from_spec(spec)
spec.loader.exec_module(w)
tokens = {}
completed = False

def ok(label, response, status=200):
    s.check(label, response[0] == status, response[:3])
    return response[1]

def nearby(**changes):
    return s.call('/api/v1/restaurants/nearby', {'latitude': 25.77, 'longitude': -80.19, **changes})

def listing(tenant, lat, lon, listed=True):
    raw = json.loads(s.sql('SELECT menu_json FROM bartide_customers WHERE id=?', (tenant,))[0][0])
    raw['venue']['directory'] = {'listed': listed, 'address': '123 Fictional Street', 'latitude': lat, 'longitude': lon}
    s.sql('UPDATE bartide_customers SET menu_json=? WHERE id=?', (json.dumps(raw), tenant))

def save(body, person='alice'):
    return s.call('/api/v1/tenants/bistro/menu/directory', body, tokens.get(person))

try:
    s.launch({'DeliveryDispatch__WorkerEnabled': 'false'})
    for tenant in ['bistro', 'near', 'far', 'unlisted', 'missing', 'broken', 'disabled', 'paused', 'dine-only']:
        s.seed(tenant)
    s.seed('draft', status='draft'); s.seed('business', vertical='tide-casa'); s.seed('foreign', owner=s.BOB)
    for person in ('alice', 'bob', 'staff'):
        tokens[person] = ok('Synthetic login ' + person, s.call('/api/v1/auth/signin', {'email': person + '@example.invalid', 'password': s.PASSWORD}))['accessToken']
    s.sql('INSERT INTO bartide_enhanced_members(id,tenant_id,name,email,user_id,role,active,created_at) VALUES(?,?,?,?,?,?,1,?)',
        ('kitchen', 'bistro', 'Kitchen', 'staff@example.invalid', 'supabase:' + s.STAFF, 'kitchen', s.NOW))
    request = {'expectedVersion': 0, 'location': {'listed': True, 'address': '123 Fictional Street', 'latitude': 25.78, 'longitude': -80.19}}
    ok('Anonymous cannot list a restaurant', save(request, 'none'), 401)
    ok('Another owner cannot list this restaurant', save(request, 'bob'), 403)
    ok('Kitchen staff cannot edit listing', save(request, 'staff'), 403)
    ok('Listed restaurant must have coordinates', save({**request, 'location': {**request['location'], 'latitude': None}}), 400)
    ok('Listed restaurant must have an address', save({**request, 'location': {**request['location'], 'address': ''}}), 400)
    edited = ok('Owner lists restaurant', save(request))
    s.check('Saved location appears in editor', edited['directory'] == request['location'] and edited['version'] == 1)
    ok('Stale location edit cannot overwrite changes', save(request), 409)
    profile_edit = ok('Ordinary profile edit preserves listing', s.call('/api/v1/tenants/bistro/menu/profile',
        {'expectedVersion':edited['version'], 'profile':edited['profile']}, tokens['alice']))
    s.check('Directory metadata survives unrelated profile save', profile_edit['directory'] == request['location'])
    for tenant, lat in [('near', 25.771), ('far', 25.80), ('paused', 25.79), ('unlisted', 25.77), ('disabled', 25.77), ('draft',25.77), ('business',25.77), ('dine-only',25.77)]:
        listing(tenant, lat, -80.19, tenant != 'unlisted')
    listing('broken', 250, -80.19)
    s.alter_config(lambda c: c.update(enabled=False), 'disabled')
    s.alter_config(lambda c: c.update(accepting_orders=False), 'paused')
    s.alter_config(lambda c: (c.update(delivery_enabled=False), c['checkout'].update(pickup_enabled=False)), 'dine-only')
    result = nearby()
    data = ok('Guest can search with location', result)
    s.check('Only public listed orderable restaurant types appear, closest first', [r['slug'] for r in data['restaurants']] == ['near', 'bistro', 'paused', 'far'], data)
    s.check('Paused restaurant retains distance position and reports availability', data['restaurants'][2]['acceptingOrders'] is False)
    s.check('Known one degree latitude distance uses miles', 68.9 < ok('One degree search', nearby(latitude=24.771))['restaurants'][0]['distanceMiles'] < 69.3)
    s.check('Search response is uncached and excludes private fields', 'no-store' in result[3].get('Cache-Control', '') and s.PRIVATE not in result[2]
        and all(set(r) == {'slug','name','address','tagline','distanceMiles','acceptingOrders','pickupEnabled','deliveryEnabled'} for r in data['restaurants']))
    for values in ({}, {'latitude': None}, {'latitude': 91}, {'longitude': -181}, {'offset': -1}, {'search': 'x'*81}):
        response = s.call('/api/v1/restaurants/nearby', values) if not values else nearby(**values)
        ok('Reject invalid location/search ' + str(values), response, 400)
    match = ok('Restaurant name filter', nearby(search='BISTRO'))
    s.check('Name filter is case insensitive', [r['slug'] for r in match['restaurants']] == ['bistro'])
    listing('near', 25.78, -80.19)
    tie = ok('Equal distances', nearby())
    s.check('Exactly equal distances use stable slug order', [r['slug'] for r in tie['restaurants']][:2] == ['bistro','near'])
    listing('near', 25.771, -80.19)
    # Full checkout follows the discovered slug, with existing authoritative validation.
    slug = data['restaurants'][0]['slug']
    order = s.order_request(tenant=slug)
    ok('Discovered restaurant accepts direct order', s.submit(order, slug), 201)
    for n in range(52):
        tenant = 'page-' + str(n).zfill(2); s.seed(tenant); listing(tenant, 26 + n * .01, -80.19)
    first = ok('First nearby page', nearby()); second = ok('Next nearby page', nearby(offset=50))
    combined = first['restaurants'] + second['restaurants']
    s.check('Pagination covers every restaurant without duplicates in distance order', len(combined) == 56
        and len({r['slug'] for r in combined}) == 56 and first['hasMore'] and not second['hasMore']
        and [r['distanceMiles'] for r in combined] == sorted(r['distanceMiles'] for r in combined))
    # Two points across the date line must be nearby, not almost a world apart.
    listing('near', 0, -179.99)
    crossing = ok('International date line search', nearby(latitude=0, longitude=179.99, search='near'))
    s.check('Distance crosses date line correctly', 1.3 < crossing['restaurants'][0]['distanceMiles'] < 1.5)
    listing('near', 25.771, -80.19)
    w.launch('TideCasa.Blazor', w.WEB, {'Api__BaseUrl': s.API + '/', 'DataProtection__KeysPath': str(s.RUN / 'keys'), 'DOTNET_PROCESSOR_COUNT': '1'})
    page = w.web('/restaurants')
    s.check('Customer discovery loads without sign in', page[0] == 200 and 'Find restaurants near me' in page[1] and 'closest first' in page[1])
    s.check('Driver signup entry explains restaurant approval', 'href="/driver/signin?return_to=%2Fdriver"' in page[1] and 'Each restaurant must offer you work' in page[1])
    driver_entry = w.web('/driver/signin?return_to=%2Fdriver')
    s.check('Driver registration entry returns to driver workspace', 'Create your driver account' in driver_entry[1] and '/signup?return_to=%2Fdriver' in driver_entry[1])
    s.check('Discovery has no automatic location request on initial render', 'restaurantDiscovery.locate' not in page[1])
    owner = w.login('alice')
    forms, _ = w.forms_for(owner, '/workspace/bistro/menu')
    form = w.find_form(forms, '/directory')
    response = w.post(owner, form, {'latitude':'25.779','longitude':'-80.191','address':'456 Fictional Road','listed':'true'})
    w.saved(response, 'Owner saves location through protected native form')
    s.check('Owner form writes actual location', json.loads(s.sql("SELECT menu_json FROM bartide_customers WHERE id='bistro'")[0][0])['venue']['directory']['latitude'] == 25.779)
    ok('CSRF cross-site listing change rejected', w.post(owner, form, {'latitude':'0'}, headers={'Origin':'https://other.example.invalid'}), 400)
    stale = w.post(owner, form, {'latitude':'0'})
    s.check('Stale form reports conflict', 'notice=changed' in stale[2].get('Location',''))
    forms, _ = w.forms_for(owner, '/workspace/bistro/menu')
    form = w.find_form(forms, '/directory')
    invalid = w.post(owner, form, {'latitude':'NaN'})
    s.check('Nonfinite location gets understandable validation', 'notice=location' in invalid[2].get('Location',''))
    unlisted = w.post(owner, form, {'listed':''})
    w.saved(unlisted, 'Owner removes restaurant from nearby discovery')
    s.check('Unlisting takes effect immediately', not ok('Search unlisted restaurant', nearby(search='bistro'))['restaurants'])
    forms, _ = w.forms_for(owner, '/workspace/bistro/menu')
    w.saved(w.post(owner,w.find_form(forms,'/directory'),{'listed':'true'}),'Restore synthetic listing')
    completed = True
    meta = {'web':w.WEB, 'run':str(s.RUN), 'stop':str(s.RUN/'stop-fixture'), 'checks':len(s.RESULTS)}
    (s.RUN/'discovery-fixture.json').write_text(json.dumps(meta,indent=2),encoding='utf-8')
    print(json.dumps(meta),flush=True)
    if '--hold' in sys.argv:
        while not (s.RUN/'stop-fixture').exists():
            if any(proc.poll() is not None for proc in s.PROCESSES): raise RuntimeError('Fixture stopped')
            time.sleep(1)
finally:
    for proc in s.PROCESSES:
        if proc.poll() is None:
            proc.terminate()
            try: proc.wait(timeout=20)
            except s.subprocess.TimeoutExpired: proc.kill(); proc.wait(timeout=10)
    s.PROVIDER.shutdown(); s.PROVIDER.server_close()
    for log in s.LOGS: log.close()
    (s.RUN/'discovery-results.json').write_text(json.dumps({'completed':completed,'checks':s.RESULTS},indent=2),encoding='utf-8')
    print('Discovery evidence: ' + str(s.RUN),flush=True)
