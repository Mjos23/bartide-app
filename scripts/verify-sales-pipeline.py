"""Exercise private sales workflow on isolated synthetic API/Blazor/SQLite.

No real identity, email, Stripe, push or customer data. --hold leaves a local
synthetic owner page for browser review until its reported stop file exists.
"""
import argparse
import html
import importlib.util
from datetime import timedelta
import restaurant_test_support as s

s.RUN = s.ROOT / '.tools/sales-pipeline-verification' / s.datetime.now(s.timezone.utc).strftime('%Y%m%d-%H%M%S-%f')
s.RUN.mkdir(parents=True)
s.DB = s.RUN / 'synthetic.db'
spec = importlib.util.spec_from_file_location('sales_web_helpers', s.ROOT / 'scripts/verify-management-web.py')
h = importlib.util.module_from_spec(spec)
spec.loader.exec_module(h)
from restaurant_test_support import *
web, forms_for, find_form, post, login = (getattr(h, name) for name in ('web', 'forms_for', 'find_form', 'post', 'login'))
WEB = h.WEB
BASE = '/api/v1/owner/sales'
STOP = RUN / 'stop-fixture'
completed = False


def launch(project, address):
    env = os.environ.copy()
    for name in list(env):
        if any(word in name.upper() for word in ('STRIPE', 'RESEND', 'SUPABASE', 'CLOUDFLARE', 'AUTH__', 'ORDERING__', 'STORAGE__', 'API__', 'DATAPROTECTION__', 'NOTIFICATIONS__', 'MEDIA__', 'WEBPUSH__', 'SERVICEBILLING__', 'MERCHANTPAYMENTS__')):
            env.pop(name)
    env.update({'ASPNETCORE_ENVIRONMENT': 'Development', 'DOTNET_ENVIRONMENT': 'Development', 'ASPNETCORE_URLS': address,
                'Storage__DatabasePath': str(DB), 'Auth__Enabled': 'true', 'Auth__AllowLocalTestProvider': 'true',
                'Auth__SupabaseUrl': f'http://127.0.0.1:{PROVIDER.server_port}', 'Auth__PublishableKey': KEY,
                'Auth__PlatformOwnerUserId': 'supabase:' + PLATFORM, 'Api__BaseUrl': API + '/',
                'Ordering__PublicBaseUrl': WEB, 'DataProtection__KeysPath': str(RUN / 'keys'),
                'ReverseProxy__KnownClientProxy': '127.0.0.1', 'Notifications__Mode': 'disabled', 'WebPush__Enabled': 'false',
                'Stripe__CheckoutEnabled': 'false', 'Stripe__InvoicesEnabled': 'false', 'ServiceBilling__CheckoutEnabled': 'false',
                'MerchantPayments__CheckoutEnabled': 'false', 'MerchantPayments__OnboardingEnabled': 'false'})
    log = (RUN / (project + '-' + str(len(PROCESSES)) + '.log')).open('w', encoding='utf-8'); LOGS.append(log)
    binary = Path(os.environ.get('TIDE_TEST_BUILD_ROOT', str(ROOT))) / project / 'bin/Debug/net10.0' / (project + '.dll')
    proc = subprocess.Popen([str(SDK), str(binary)], cwd=ROOT / project, env=env, stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT,
                            creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
    PROCESSES.append(proc)
    deadline = time.monotonic() + 60
    while time.monotonic() < deadline:
        if proc.poll() is not None: raise RuntimeError('Fixture stopped: ' + project)
        try:
            with urllib.request.urlopen(address + '/health', timeout=2) as response:
                if response.status == 200: return proc
        except (OSError, urllib.error.URLError): pass
        time.sleep(.2)
    raise TimeoutError(project)


def stop(proc):
    if proc.poll() is None:
        proc.terminate()
        try: proc.wait(timeout=15)
        except subprocess.TimeoutExpired: proc.kill(); proc.wait(timeout=10)


def prospect(**changes):
    key = str(uuid.uuid4())
    return {'requestKey': key, 'name': 'Synthetic Owner', 'business': 'Test Bar ' + key[:6], 'email': key + '@example.invalid',
            'phone': '', 'city': 'Test City', 'vertical': 'bartide', 'source': 'personal introduction', 'referralCode': '',
            'privateNote': 'Initial private conversation.', **changes}


def update(lead, **changes):
    return {'requestKey': str(uuid.uuid4()), 'expectedVersion': lead['version'], 'stage': lead['stage'], 'nextAction': lead['nextAction'],
            'followUpDate': lead['followUpDate'], 'assignee': lead['assignee'], 'privateNote': lead['privateNote'], 'workspaceId': lead['workspaceId'], **changes}


def inquiry(**changes):
    key = str(uuid.uuid4())
    return {'id': key, 'name': 'Synthetic Demo Contact', 'business': 'Demo Bar ' + key[:6], 'email': key + '@example.invalid',
            'phone': '', 'city': 'Test City', 'businessType': 'bartide', 'preferredTimes': 'Tuesday afternoon', 'timeZone': 'Eastern Time',
            'goals': 'Rewards and table payments', 'contactWebsite': '', **changes}


def get_lead(ident):
    response = call(BASE + '/' + urllib.parse.quote(ident, safe=''), token=owner)
    check('Owner can read prospect detail', response[0] == 200, response[:3])
    return response[1]


try:
    api = launch('TideCasa.Api', API)
    seed('bistro')
    tokens = {}
    for person in ('alice', 'bob', 'staff', 'platform'):
        response = call('/api/v1/auth/signin', {'email': person + '@example.invalid', 'password': PASSWORD})
        check('Synthetic identity verified: ' + person, response[0] == 200)
        tokens[person] = response[1]['accessToken']
    owner = tokens['platform']
    empty_pipeline = call(BASE, token=owner)
    check('Empty pipeline has four zero global queue counts', empty_pipeline[0] == 200
          and empty_pipeline[1].get('summary') == {'toPitch': 0, 'pitched': 0, 'followUp': 0, 'unplanned': 0})
    check('Anonymous cannot read sales data', call(BASE)[0] == 401)
    check('Anonymous cannot create a prospect', call(BASE, prospect())[0] == 401)
    for person in ('alice', 'bob', 'staff'):
        check('Nonowner cannot list sales data: ' + person, call(BASE, token=tokens[person])[0] == 403)
        check('Nonowner cannot add a prospect: ' + person, call(BASE, prospect(), tokens[person])[0] == 403)
    first = prospect(phone='202-555-0100', privateNote='Initial <script>alert(1)</script> note')
    responses = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        responses = list(pool.map(lambda _: call(BASE, first, owner), range(4)))
    check('Concurrent identical creates save one prospect', all(r[0] == 200 for r in responses) and sql('SELECT COUNT(*) FROM tide_leads')[0][0] == 1)
    ident = responses[0][1]['lead']['id']
    check('Private create is not cached', 'no-store' in responses[0][3].get('Cache-Control', ''))
    check('One create audit entry and operation', sql('SELECT COUNT(*) FROM tide_sales_history')[0][0] == 1 and sql('SELECT COUNT(*) FROM tide_sales_operations')[0][0] == 1)
    check('Replay cannot substitute different details', call(BASE, {**first, 'business': 'Changed'}, owner)[0] == 409)
    same = call(BASE, {**first, 'requestKey': str(uuid.uuid4()), 'business': '  ' + first['business'].upper() + ' ', 'email': first['email'].upper(), 'source': 'Changed source', 'privateNote': 'Do not overwrite'}, owner)
    check('Exact normalized business and email reuse original prospect', same[0] == 200 and same[1]['lead']['id'] == ident)
    check('Matching prospect keeps original source and notes', same[1]['lead']['source'] == first['source'] and same[1]['lead']['privateNote'] == first['privateNote'])
    check('Duplicate create explicitly identifies the existing prospect', same[1].get('existingProspect') is True)
    different = call(BASE, {**first, 'requestKey': str(uuid.uuid4()), 'business': 'A different business'}, owner)
    check('Same email at a different business is not merged', different[0] == 200 and different[1]['lead']['id'] != ident)
    empty = prospect(email='')
    a, b = call(BASE, empty, owner), call(BASE, {**empty, 'requestKey': str(uuid.uuid4())}, owner)
    check('No-email manual prospects are not guessed to match', a[0] == b[0] == 200 and a[1]['lead']['id'] != b[1]['lead']['id'])
    for changes in ({'name': ''}, {'business': 'x' * 151}, {'email': 'invalid'}, {'referralCode': 'UNKNOWN-CODE'}, {'privateNote': 'x' * 4001}, {'source': 'bad\x01'}, {'requestKey': str(uuid.UUID(int=0))}):
        check('Invalid prospect rejected: ' + next(iter(changes)), call(BASE, prospect(**changes), owner)[0] == 400)
    check('Search treats SQL-looking text as text', call(BASE + '?search=' + urllib.parse.quote("' OR 1=1 --"), token=owner)[1]['total'] == 0)
    page1 = call(BASE + '?page=1&pageSize=2', token=owner)[1]
    page2 = call(BASE + '?page=2&pageSize=2', token=owner)[1]
    check('Pagination is bounded and nonoverlapping', len(page1['leads']) == len(page2['leads']) == 2 and not ({x['id'] for x in page1['leads']} & {x['id'] for x in page2['leads']}))
    check('Oversized page rejected', call(BASE + '?pageSize=500', token=owner)[0] == 400)
    detail = get_lead(ident)
    for person in ('alice', 'bob', 'staff'):
        check('Nonowner cannot read a known prospect: ' + person, call(BASE + '/' + ident, token=tokens[person])[0] == 403)
        check('Nonowner cannot edit known prospect: ' + person, call(BASE + '/' + ident, update(detail['lead']), tokens[person])[0] == 403)
    mutation = update(detail['lead'], stage='contacted', nextAction='Agree a demo time', followUpDate='2000-01-01', assignee='Michael', privateNote='Second conversation', workspaceId='bistro')
    result = call(BASE + '/' + ident, mutation, owner)
    check('Owner saves follow-up and workspace reference', result[0] == 200 and result[1]['lead']['version'] == 1 and result[1]['lead']['workspaceId'] == 'bistro', result[:3])
    check('Workspace reference does not change ownership', sql('SELECT user_id FROM bartide_customers WHERE id=?', ('bistro',))[0][0] == 'supabase:' + ALICE)
    check('Both initial and new conversation notes are retained', {first['privateNote'], 'Second conversation'} <= {x['privateNote'] for x in result[1]['history']})
    check('Exact update replay does not increment version', call(BASE + '/' + ident, mutation, owner)[1]['lead']['version'] == 1)
    check('Stale version rejected', call(BASE + '/' + ident, update(detail['lead'], nextAction='stale'), owner)[0] == 409)
    check('Overdue filter finds due prospect', ident in [x['id'] for x in call(BASE + '?due=overdue', token=owner)[1]['leads']])
    current = result[1]['lead']
    for changes in ({'stage': 'paid'}, {'followUpDate': 'tomorrow'}, {'followUpDate': '2026-02-31'}, {'workspaceId': 'missing'}, {'assignee': 'x' * 101}, {'nextAction': 'x' * 241}):
        check('Invalid update rejected: ' + next(iter(changes)), call(BASE + '/' + ident, update(current, **changes), owner)[0] == 400)
    requests = [update(current, nextAction='First competitor'), update(current, nextAction='Second competitor')]
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        competing = list(pool.map(lambda body: call(BASE + '/' + ident, body, owner), requests))
    check('Competing edits cannot overwrite each other', sorted(r[0] for r in competing) == [200, 409])
    current = get_lead(ident)['lead']
    unicode_note = '船' * 4000
    unicode_result = call(BASE + '/' + ident, update(current, privateNote=unicode_note), owner)
    check('Maximum Unicode note survives JSON body limits', unicode_result[0] == 200 and unicode_result[1]['lead']['privateNote'] == unicode_note, unicode_result[:1])
    demo = inquiry(email=first['email'], business=first['business'])
    response = call('/api/v1/demo-requests', demo)
    check('Public demo capture succeeds', response[0] == 201, response[:3])
    check('Demo receipt exposes no private sales data', first['email'] not in response[2] and 'privateNote' not in response[2])
    linked = get_lead(ident)
    check('Demo links to unique existing prospect without changing attribution', linked['lead']['demoRequestCount'] == 1 and linked['lead']['source'] == first['source'] and linked['lead']['privateNote'] == unicode_note)
    check('Demo requested time remains a request', linked['demoRequests'][0]['status'] == 'requested' and linked['demoRequests'][0]['preferredTimes'] == demo['preferredTimes'])
    check('Omitted demo texting preference defaults to false without inheriting lead phone',
          linked['demoRequests'][0]['canText'] is False and linked['demoRequests'][0]['phone'] == ''
          and linked['lead']['phone'] == first['phone'])
    check('Duplicate demo retry does not create another link', call('/api/v1/demo-requests', demo)[0] == 200 and sql('SELECT COUNT(*) FROM tide_sales_demo_links WHERE request_id=?', (demo['id'],))[0][0] == 1)
    stored_omitted = sql('SELECT request_json,payload_hash FROM demo_requests WHERE id=?', (demo['id'],))[0]
    prior_demo_json = json.dumps({key[0].upper() + key[1:]: value for key, value in demo.items()}, separators=(',', ':'))
    check('Omitted preference preserves the original serialized payload and hash',
          stored_omitted == (prior_demo_json, hashlib.sha256(prior_demo_json.encode()).hexdigest().upper())
          and 'CanText' not in stored_omitted[0])
    check('Explicit false replays a request whose preference was omitted',
          call('/api/v1/demo-requests', {**demo, 'canText': False})[0] == 200)

    invalid_phone_count = sql('SELECT COUNT(*) FROM demo_requests')[0][0]
    for label, phone in [('blank', ''), ('missing', None), ('too short', '123456'), ('too long', '1234567890123456'),
                         ('non-ASCII digits', '１２３４５６７'), ('extension', '2025550100ext5'),
                         ('slash', '202/555/0100'), ('misplaced plus', '202+5550100'),
                         ('repeated plus', '++12025550100'), ('internal tab', '202\t5550100')]:
        rejected = call('/api/v1/demo-requests', inquiry(phone=phone, canText=True))
        check('Texting opt-in rejects an unusable phone: ' + label, rejected[0] == 400
              and 'Enter a phone number we can text, or leave the texting option unchecked.' in
              rejected[1].get('errors', {}).get('Phone', []))
    check('Rejected texting preferences create no request records', sql('SELECT COUNT(*) FROM demo_requests')[0][0] == invalid_phone_count)

    text_demo = inquiry(email=first['email'], business=first['business'], phone='  +1 (202) 555-0144  ',
                        preferredTimes='Wednesday morning', canText=True)
    text_saved = call('/api/v1/demo-requests', text_demo)
    check('Demo request saves a usable scheduling-text preference', text_saved[0] == 201)
    check('Exact opted-in retry is idempotent', call('/api/v1/demo-requests', text_demo)[0] == 200
          and sql('SELECT COUNT(*) FROM tide_sales_demo_links WHERE request_id=?', (text_demo['id'],))[0][0] == 1)
    check('Changing texting preference cannot replace an existing request',
          call('/api/v1/demo-requests', {**text_demo, 'canText': False})[0] == 409)
    declined_demo = inquiry(email=first['email'], business=first['business'], phone='202.555.0177',
                            preferredTimes='Thursday evening', canText=False)
    check('Separate request saves an explicit declined texting preference', call('/api/v1/demo-requests', declined_demo)[0] == 201)
    declined_without_flag = {key: value for key, value in declined_demo.items() if key != 'canText'}
    check('Omitted flag replays an explicitly declined request', call('/api/v1/demo-requests', declined_without_flag)[0] == 200)
    preference_detail = get_lead(ident)
    preference_requests = {item['id']: item for item in preference_detail['demoRequests']}
    check('Each linked demo retains its own phone, time and texting choice',
          preference_requests[text_demo['id']]['canText'] is True
          and preference_requests[text_demo['id']]['phone'] == text_demo['phone'].strip()
          and preference_requests[text_demo['id']]['preferredTimes'] == text_demo['preferredTimes']
          and preference_requests[declined_demo['id']]['canText'] is False
          and preference_requests[declined_demo['id']]['phone'] == declined_demo['phone']
          and preference_requests[declined_demo['id']]['preferredTimes'] == declined_demo['preferredTimes']
          and preference_requests[demo['id']]['canText'] is False
          and preference_detail['lead']['phone'] == first['phone'])
    check('Opted-in preference is present in the saved request payload',
          json.loads(sql('SELECT request_json FROM demo_requests WHERE id=?', (text_demo['id'],))[0][0])['CanText'] is True)
    for phone in ('5550101', '+123456789012345'):
        check('Texting preference accepts the digit-count boundary: ' + str(sum(c.isascii() and c.isdigit() for c in phone)),
              call('/api/v1/demo-requests', inquiry(phone=phone, canText=True))[0] == 201)
    check('Declining texting does not impose the opt-in phone rule',
          call('/api/v1/demo-requests', inquiry(phone='Call the office', canText=False))[0] == 201)
    counts = [sql('SELECT COUNT(*) FROM ' + table)[0][0] for table in ('tide_leads', 'demo_requests', 'tide_sales_history')]
    sql("CREATE TRIGGER fail_sales_demo_notification BEFORE INSERT ON tide_demo_notifications BEGIN SELECT RAISE(ABORT,'synthetic rollback'); END")
    failed = call('/api/v1/demo-requests', inquiry())
    sql('DROP TRIGGER fail_sales_demo_notification')
    check('Demo, lead and outbox creation roll back together', failed[0] == 500 and counts == [sql('SELECT COUNT(*) FROM ' + table)[0][0] for table in ('tide_leads', 'demo_requests', 'tide_sales_history')])
    check('Disabled email worker does not send alerts', sql("SELECT COUNT(*) FROM tide_demo_notifications WHERE attempts<>0")[0][0] == 0)

    # Rehearse a preserved pre-feature request and an arbitrary imported lead ID.
    legacy = inquiry()
    legacy_json = {key[0].upper() + key[1:]: value for key, value in legacy.items()}
    legacy_json_text = json.dumps(legacy_json, separators=(',', ':'))
    legacy_hash = hashlib.sha256(legacy_json_text.encode()).hexdigest().upper()
    sql("INSERT INTO demo_requests(id,payload_hash,email_hash,request_json,status,created_at) VALUES(?,?,?,?,?,?)",
        (legacy['id'], legacy_hash, hashlib.sha256(legacy['email'].encode()).hexdigest().upper(), legacy_json_text, 'scheduled', NOW))
    sql("INSERT INTO tide_demo_inbox(request_id,version,note,updated_at,updated_by) VALUES(?,1,?,?,?)", (legacy['id'], 'Prior private demo note', NOW, 'synthetic-owner'))
    legacy_id = 'legacy-lead-17'
    sql("INSERT INTO tide_leads(id,name,business,email,phone,vertical,stage,notes,follow_up,version,updated_at,source,city) VALUES(?,?,?,?,?,?,?,?,?,0,?,?,?)", (legacy_id, 'Imported Contact', 'Imported Bar', 'imported@example.invalid', '', 'bartide', 'legacy-stage', 'Imported conversation', '', NOW, 'legacy source', 'Test'))
    stop(api); api = launch('TideCasa.Api', API)
    imported = get_lead(legacy_id)
    check('Imported stage, source and notes stay intact', imported['lead']['stage'] == 'legacy-stage' and imported['lead']['source'] == 'legacy source' and imported['lead']['privateNote'] == 'Imported conversation')
    backfilled_id = sql('SELECT lead_id FROM tide_sales_demo_links WHERE request_id=?', (legacy['id'],))[0][0]
    backfilled = get_lead(backfilled_id)
    check('Prior demo status and notes survive reconciliation', backfilled['lead']['stage'] == 'demo-scheduled' and 'Prior private demo note' in json.dumps(backfilled))
    check('Pre-texting stored payload and hash accept omitted and explicit-false retries',
          call('/api/v1/demo-requests', legacy)[0] == 200
          and call('/api/v1/demo-requests', {**legacy, 'canText': False})[0] == 200
          and sql('SELECT request_json,payload_hash,status FROM demo_requests WHERE id=?', (legacy['id'],))[0]
              == (legacy_json_text, legacy_hash, 'scheduled'))
    check('Old linked request remains a declined texting preference', backfilled['demoRequests'][0]['canText'] is False
          and backfilled['demoRequests'][0]['phone'] == legacy['phone'])
    restored_preferences = {item['id']: item for item in get_lead(ident)['demoRequests']}
    check('Opted-in and declined request preferences survive API restart',
          restored_preferences == preference_requests and call('/api/v1/demo-requests', text_demo)[0] == 200)
    modified = call(BASE + '/' + legacy_id, update(imported['lead'], privateNote='New imported follow-up'), owner)
    check('First imported edit preserves earlier notes in history', modified[0] == 200 and {'Imported conversation', 'New imported follow-up'} <= {x['privateNote'] for x in modified[1]['history']})

    # Exercise actionable queues with an explicit cohort. The sets below are
    # business expectations, independent of the store's SQL and existing leads.
    before_queues = call(BASE, token=owner)[1]['summary']
    queue_today = datetime.now(timezone.utc).date().isoformat()
    queue_future = (datetime.now(timezone.utc).date() + timedelta(days=7)).isoformat()
    queue_cases = [
        ('new-unplanned', 'new', '', ''),
        ('contacted-future', 'contacted', 'Call the owner', queue_future),
        ('pitched-today', 'pitched', 'Answer questions', queue_today),
        ('pitched-overdue', 'pitched', 'Check the decision', '2000-01-01'),
        ('proposal-no-action', 'proposal', '', queue_future),
        ('scheduled-no-date', 'demo-scheduled', 'Confirm the time', ''),
        ('completed-future', 'demo-completed', 'Send the proposal', queue_future),
        ('enrolled-old', 'enrolled', '', '2000-01-01'),
        ('closed-old', 'closed', '', '2000-01-01'),
    ]
    queue_ids = {}
    for label, stage, action, follow_up in queue_cases:
        created = call(BASE, prospect(business='Queue Rehearsal ' + label), owner)
        check('Queue example can be created: ' + label, created[0] == 200)
        queue_ids[label] = created[1]['lead']['id']
        if stage != 'new' or action or follow_up:
            changed = call(BASE + '/' + queue_ids[label], update(created[1]['lead'], stage=stage,
                nextAction=action, followUpDate=follow_up), owner)
            check('Queue example keeps the chosen stage and next step: ' + label, changed[0] == 200
                  and changed[1]['lead']['stage'] == stage and changed[1]['lead']['followUpDate'] == follow_up)
            if stage == 'pitched':
                check('Pitched stage transition is retained in private history: ' + label,
                      any('new → pitched' in activity['summary'] for activity in changed[1]['history']))
    expected_queue_ids = {
        'to-pitch': {queue_ids['new-unplanned'], queue_ids['contacted-future']},
        'pitched': {queue_ids['pitched-today'], queue_ids['pitched-overdue']},
        'follow-up': {queue_ids['pitched-today'], queue_ids['pitched-overdue']},
        'unplanned': {queue_ids['new-unplanned'], queue_ids['proposal-no-action'], queue_ids['scheduled-no-date']},
    }
    expected_summary = {key: before_queues[key] + delta for key, delta in
                        {'toPitch': 2, 'pitched': 2, 'followUp': 2, 'unplanned': 3}.items()}
    for queue, expected_ids in expected_queue_ids.items():
        query = urllib.parse.urlencode({'queue': queue, 'search': 'Queue Rehearsal', 'pageSize': 50})
        listed = call(BASE + '?' + query, token=owner)
        check('Queue selects exactly its intended prospects: ' + queue, listed[0] == 200
              and listed[1]['total'] == len(expected_ids) and {lead['id'] for lead in listed[1]['leads']} == expected_ids)
        check('Queue summary remains global despite search and selected queue: ' + queue,
              listed[1]['summary'] == expected_summary and 'no-store' in listed[3].get('Cache-Control', ''))
    all_queues = call(BASE + '?queue=all&search=Queue%20Rehearsal&pageSize=50', token=owner)[1]
    blank_queue = call(BASE + '?queue=&search=Queue%20Rehearsal&pageSize=50', token=owner)[1]
    check('All and blank queues preserve unrestricted results including enrolled and closed',
          all_queues == blank_queue and all_queues['total'] == len(queue_cases)
          and {lead['id'] for lead in all_queues['leads']} == set(queue_ids.values()))
    combined = call(BASE + '?queue=follow-up&search=Queue%20Rehearsal&stage=pitched&due=today', token=owner)[1]
    check('Queue combines with search, stage and due filters', combined['total'] == 1
          and combined['leads'][0]['id'] == queue_ids['pitched-today'] and combined['summary'] == expected_summary)
    paged_queue = call(BASE + '?queue=to-pitch&search=Queue%20Rehearsal&pageSize=1&page=2', token=owner)[1]
    check('Queue pagination bounds results without changing global counts', paged_queue['total'] == 2
          and len(paged_queue['leads']) == 1 and paged_queue['summary'] == expected_summary)
    missing_queue = call(BASE + '?queue=pitched&search=no-such-queue-example', token=owner)[1]
    check('Empty filtered page still reports global queue counts', missing_queue['total'] == 0
          and missing_queue['leads'] == [] and missing_queue['summary'] == expected_summary)
    for invalid_queue in ('unknown', "' OR 1=1 --", 'x' * 31):
        check('Invalid queue is rejected: ' + invalid_queue, call(BASE + '?queue=' + urllib.parse.quote(invalid_queue), token=owner)[0] == 400)
    total_links = sql('SELECT COUNT(*) FROM tide_sales_demo_links')[0][0]
    stop(api); api = launch('TideCasa.Api', API)
    check('Repeated startup does not duplicate links', sql('SELECT COUNT(*) FROM tide_sales_demo_links')[0][0] == total_links)
    check('New additive migration recorded once', sql('SELECT COUNT(*) FROM tide_feature_migrations WHERE version=9')[0][0] == 1)
    check('Foreign keys remain valid', not sql('PRAGMA foreign_key_check'))
    pitched_after_restart = get_lead(queue_ids['pitched-today'])
    check('Pitched stage and history survive API restart', pitched_after_restart['lead']['stage'] == 'pitched'
          and any('new → pitched' in activity['summary'] for activity in pitched_after_restart['history']))
    check('Global queue counts survive API restart', call(BASE + '?pageSize=1', token=owner)[1]['summary'] == expected_summary)

    frontend = launch('TideCasa.Blazor', WEB)
    check('Anonymous private page redirects to sign-in', web('/owner/sales')[0] == 302)
    browser_owner = login('platform')
    browser_alice = login('alice')
    check('Normal business owner cannot open platform sales page', web('/owner/sales', client=browser_alice)[0] == 403)
    for label, route in [('linked sales detail', '/owner/sales/' + ident), ('demo inbox', '/owner/demo-requests')]:
        preference_page = web(route, client=browser_owner)
        preference_html = html.unescape(preference_page[1])
        check('Owner sees per-request scheduling-text preferences in ' + label, preference_page[0] == 200
              and 'Demo scheduling texts:' in preference_html
              and 'Allowed for this request at ' + text_demo['phone'].strip() in preference_html
              and 'Not opted in for this request. Use email.' in preference_html
              and 'Allowed for this request at ' + declined_demo['phone'] not in preference_html
              and 'Allowed for this request at ' + first['phone'] not in preference_html
              and 'no-store' in preference_page[2].get('Cache-Control', ''))
    page = web('/owner/sales', client=browser_owner)
    check('Owner list renders privately', page[0] == 200 and 'no-store' in page[2].get('Cache-Control', '') and 'Sales pipeline' in page[1])
    check('Owner work lists show all four queue links', all('href="/owner/sales?queue=' + queue + '"' in page[1]
          for queue in ('to-pitch', 'pitched', 'follow-up', 'unplanned')))
    queue_page = web('/owner/sales?queue=pitched&search=Queue%20Rehearsal', client=browser_owner)
    check('Native search retains the active work list', 'name="queue" value="pitched"' in queue_page[1]
          and 'Queue Rehearsal pitched-today' in queue_page[1] and 'Queue Rehearsal contacted-future' not in queue_page[1])
    check('Invalid work list shows actionable feedback', 'Choose valid search filters.' in
          web('/owner/sales?queue=invalid', client=browser_owner)[1])
    account = web('/account', client=browser_owner)
    check('Platform owner gets a sales pipeline entry', '/owner/sales' in account[1])
    normal_account = web('/account', client=browser_alice)
    check('Business owner has grouped tools without platform sales access', '/owner/sales' not in normal_account[1] and all(x in normal_account[1] for x in ('Setup &amp; care', 'Daily work', 'Bring people back', '/operations', '/ordering', '/billing', '/payments')))
    forms, _ = forms_for(browser_owner, '/owner/sales')
    create = find_form(forms, '/owner/sales/create')
    native = post(browser_owner, create, {'name': 'Native Contact', 'business': 'Native Browser Bar', 'email': 'native@example.invalid', 'source': 'demo conversation', 'privateNote': '<script>private</script>'})
    check('Native form creates prospect and opens detail', native[0] == 302 and native[2].get('Location', '').startswith('/owner/sales/') and 'notice=created' in native[2].get('Location', ''), native[:2])
    location = native[2]['Location'].split('?')[0]
    duplicate = post(browser_owner, create, {'requestKey': str(uuid.uuid4()), 'name': 'Native Contact', 'business': 'Native Browser Bar', 'email': 'native@example.invalid', 'source': 'new source', 'privateNote': 'PRIVATE-UNSAVED-MARKER'})
    check('Duplicate form opens existing record with an accurate notice', duplicate[2].get('Location') == location + '?notice=existing')
    existing_page = web(duplicate[2]['Location'], client=browser_owner)
    check('Duplicate notice explains preserved notes and source', 'original details, source and notes were retained' in existing_page[1] and 'PRIVATE-UNSAVED-MARKER' not in existing_page[1])
    for fields, code, message in [({'email': 'invalid'}, 'email', 'Enter a valid email address or leave it blank.'), ({'referralCode': 'UNKNOWN-CODE'}, 'referral', 'referral code or leave it blank.')]:
        rejected = post(browser_owner, create, {'requestKey': str(uuid.uuid4()), 'name': 'Private Name', 'business': 'Private Business', 'email': 'valid@example.invalid', 'source': 'manual', 'privateNote': 'PRIVATE-UNSAVED-MARKER', **fields})
        check('Validation redirect contains only reviewed code: ' + code, rejected[2].get('Location') == '/owner/sales?notice=' + code)
        check('Validation gives actionable feedback: ' + code, message in web(rejected[2]['Location'], client=browser_owner)[1])
    forms, detail_page = forms_for(browser_owner, location)
    check('Private notes are HTML encoded', '&lt;script&gt;private&lt;/script&gt;' in detail_page[1] and '<script>private</script>' not in detail_page[1])
    check('Native owner form offers the pitched stage', 'value="pitched"' in detail_page[1])
    edit = find_form(forms, '/save')
    invalid_date = post(browser_owner, edit, {'followUpDate': '2026-02-30'})
    check('Invalid calendar date has specific private-safe feedback', invalid_date[2].get('Location') == location + '?notice=date' and 'Choose a valid follow-up date.' in web(invalid_date[2]['Location'], client=browser_owner)[1])
    check('Missing CSRF cannot mutate sales record', 'notice=expired' in post(browser_owner, edit, remove=('__RequestVerificationToken',))[2].get('Location', ''))
    check('Cross-site native post rejected', post(browser_owner, edit, headers={'Origin': 'https://untrusted.example.invalid'})[0] == 400)
    check('Nonowner native post rejected', post(browser_alice, edit)[0] == 403)
    repeated = list(edit['fields'].items()) + [('stage', 'closed')]
    check('Repeated form fields rejected', web(edit['action'], repeated, browser_owner, {'Origin': WEB})[0] == 400)
    saved = post(browser_owner, edit, {'stage': 'pitched', 'nextAction': 'Confirm walkthrough', 'assignee': 'Michael', 'followUpDate': datetime.now(timezone.utc).date().isoformat(), 'privateNote': unicode_note})
    check('Native follow-up and long Unicode note save', saved[0] == 302 and 'notice=saved' in saved[2].get('Location', ''), saved[:2])
    check('Native exact replay is safe', 'notice=saved' in post(browser_owner, edit, {'stage': 'pitched', 'nextAction': 'Confirm walkthrough', 'assignee': 'Michael', 'followUpDate': datetime.now(timezone.utc).date().isoformat(), 'privateNote': unicode_note})[2].get('Location', ''))
    check('Native stale form cannot replace newer details', 'notice=changed' in post(browser_owner, edit, {'requestKey': str(uuid.uuid4()), 'stage': 'closed'})[2].get('Location', ''))
    check('Saved follow-up appears in today filter', 'Native Browser Bar' in web('/owner/sales?due=today', client=browser_owner)[1])
    native_detail = get_lead(location.rsplit('/', 1)[1])
    check('Native form persists pitched stage with private history', native_detail['lead']['stage'] == 'pitched'
          and any('new → pitched' in activity['summary'] for activity in native_detail['history']))
    check('Native pitched prospect joins the pitched queue', native_detail['lead']['id'] in
          {lead['id'] for lead in call(BASE + '?queue=pitched', token=owner)[1]['leads']})
    completed = True
    if argparse.ArgumentParser().parse_known_args()[1] == ['--hold']:
        (RUN / 'browser-fixture.json').write_text(json.dumps({'web': WEB, 'api': API, 'stop': str(STOP)}, indent=2), encoding='utf-8')
        print('BROWSER FIXTURE ' + WEB + '/owner/sales; stop file: ' + str(STOP), flush=True)
        until = time.monotonic() + 600
        while time.monotonic() < until and not STOP.exists(): time.sleep(.5)
finally:
    for process in reversed(PROCESSES): stop(process)
    PROVIDER.shutdown()
    for log in LOGS: log.close()
    (RUN / 'results.json').write_text(json.dumps(RESULTS, indent=2), encoding='utf-8')
    (RUN / 'completion.json').write_text(json.dumps({'completed': completed, 'checks': len(RESULTS)}, indent=2), encoding='utf-8')
    (RUN / 'binary-sha256.json').write_text(json.dumps({str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest() for p in [ROOT / project / 'bin/Debug/net10.0' / (project + '.dll') for project in ('TideCasa.Api', 'TideCasa.Blazor', 'TideCasa.Contracts')]}, indent=2), encoding='utf-8')
    print('Evidence: ' + str(RUN), flush=True)
