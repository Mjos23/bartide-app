"""Real API/SQLite rewards checks. Identity provider and all people/orders are synthetic.

Historical visits and a pre-migration points balance are explicitly seeded fixtures.
No provider calls, money movement, real customer data, or preview database access.
"""
import restaurant_test_support as fixture
import shutil
from datetime import timedelta
fixture.RUN = fixture.ROOT / '.tools/rewards-verification' / fixture.datetime.now(fixture.timezone.utc).strftime('%Y%m%d-%H%M%S-%f')
fixture.RUN.mkdir(parents=True)
fixture.DB = fixture.RUN / 'synthetic.db'
from restaurant_test_support import *

# Load isolated copies so another batch can rebuild without locking shared binaries.
runtime = RUN / 'runtime'
build_root = Path(os.environ.get('TIDE_TEST_BUILD_ROOT', str(ROOT))).resolve()
shutil.copytree(build_root / 'TideCasa.Api/bin/Debug/net10.0', runtime / 'TideCasa.Api/bin/Debug/net10.0', ignore=shutil.ignore_patterns("libSkiaSharp.pdb"))
fixture.ROOT = runtime


def req(**values):
    return {'requestId': str(uuid.uuid4()), **values}


try:
    launch()
    seed('bistro'); seed('foreign', BOB); seed('paused', ALICE, 'paused')
    tokens = {}
    for person in ('alice', 'bob', 'staff', 'platform'):
        response = call('/api/v1/auth/signin', {'email': person+'@example.invalid', 'password': PASSWORD})
        check('Verified synthetic login '+person, response[0] == 200)
        tokens[person] = response[1]['accessToken']
    owner, customer, staff = tokens['alice'], tokens['bob'], tokens['staff']
    management = '/api/v1/tenants/bistro/rewards'
    wallet = '/api/v1/restaurants/bistro/rewards'
    sql('INSERT INTO bartide_enhanced_members(id,tenant_id,name,email,user_id,role,active,created_at) VALUES(?,?,?,?,?,?,1,?)',
        ('kitchen', 'bistro', 'Kitchen Test', 'staff@example.invalid', 'supabase:'+STAFF, 'kitchen', NOW))
    for path in (management, wallet, management+'/employee'):
        check('Anonymous rewards access denied '+path, call(path)[0] == 401)
    check('Customer cannot inspect owner rewards workspace', call(management, token=customer)[0] == 403)
    check('Staff cannot inspect owner rewards workspace', call(management, token=staff)[0] == 403)
    check('Foreign owner cannot inspect another business', call('/api/v1/tenants/foreign/rewards', token=owner)[0] == 403)
    check('Paused business hides rewards', call('/api/v1/restaurants/paused/rewards', token=customer)[0] == 404)
    response = call(wallet, token=customer)
    check('Signed-in nonmember can inspect program without enrollment', response[0] == 200 and response[1]['memberId'] is None)
    join = req(name='Synthetic Customer')
    response = call(wallet+'/join', join, customer)
    check('Customer joins under its verified app identity', response[0] == 200, response[:3])
    member = response[1]['id']
    check('Membership durable ID binds verified identity', sql('SELECT user_id FROM tide_loyalty_members WHERE id=?', (member,))[0][0] == 'supabase:'+BOB)
    check('Join exact retry preserves membership', call(wallet+'/join', join, customer)[1]['id'] == member)
    check('Join new request also preserves membership', call(wallet+'/join', req(name='Different Name'), customer)[1]['id'] == member)
    check('Conflicting request key rejected', call(wallet+'/join', {**join, 'name':'Conflicting Name'}, customer)[0] == 409)
    staff_member = call(wallet+'/join', req(name='Other Synthetic Customer'), staff)[1]['id']
    foreign_member = call('/api/v1/restaurants/foreign/rewards/join', req(name='Foreign Customer'), customer)[1]['id']

    def rule(kind='punch-card', threshold=3, **changes):
        return req(expectedVersion=-1, title='Synthetic '+kind, reward='One test item, fulfilled by staff.',
                   kind=kind, threshold=threshold, itemId='dish-1' if kind=='punch-card' else None,
                   timeZoneId='America/New_York', active=True, **changes)

    punch_request = rule()
    for token in (customer, staff):
        check('Nonowner cannot create reward rule', call(management+'/rules', punch_request, token)[0] == 403)
    for change in ({'threshold':0}, {'threshold':10001}, {'itemId':'foreign-item'}, {'timeZoneId':'not/a/zone'}, {'kind':'cash-payment'}, {'title':''}):
        check('Invalid reward rule rejected '+next(iter(change)), call(management+'/rules', {**punch_request, **change, 'requestId':str(uuid.uuid4())}, owner)[0] == 400)
    response = call(management+'/rules', punch_request, owner)
    check('Owner creates immutable punch-card rule', response[0] == 200, response[:3]); punch = response[1]['id']
    check('Rule creation retry creates only one rule', call(management+'/rules', punch_request, owner)[1]['id'] == punch and sql('SELECT COUNT(*) FROM tide_reward_rules')[0][0] == 1)
    check('Public customer rules expose no owner records', 'employees' not in call(wallet, token=customer)[1])
    visits_request = rule('monthly-visits', 3)
    visits = call(management+'/rules', visits_request, owner)[1]['id']
    points_request = rule('points', 100)
    points_rule = call(management+'/rules', points_request, owner)[1]['id']
    check('Monthly visits cannot exceed calendar days', call(management+'/rules', {**visits_request, 'requestId':str(uuid.uuid4()), 'threshold':32}, owner)[0] == 400)

    def qualify(rule_id=punch, **changes):
        return req(memberId=member, ruleId=rule_id, source='verified-purchase', sourceReference='receipt-'+uuid.uuid4().hex,
                   units=1, itemId='dish-1', note='Owner checked paid receipt.', **changes)

    q = qualify()
    for token in (customer, staff):
        check('Customer and staff cannot self-award qualifications', call(management+'/qualifications', q, token)[0] == 403)
    check('Cross-business member cannot receive qualifications', call(management+'/qualifications', {**q, 'memberId':foreign_member}, owner)[0] == 404)
    response = call(management+'/qualifications', q, owner)
    check('Owner verifies one paid item', response[0] == 200, response[:3]); first_qualification = response[1]['id']
    check('Qualification exact retry changes nothing', call(management+'/qualifications', q, owner)[1]['id'] == first_qualification)
    check('Same source cannot be counted with another request ID', call(management+'/qualifications', {**q, 'requestId':str(uuid.uuid4())}, owner)[0] == 409)
    check('Same source cannot be moved to another member', call(management+'/qualifications', {**q, 'requestId':str(uuid.uuid4()), 'memberId':staff_member}, owner)[0] == 409)
    w = call(wallet, token=customer)[1]
    check('Punch progress has two units remaining', next(p for p in w['progress'] if p['ruleId']==punch)['unitsNeeded'] == 2 and not w['rewards'])
    second = qualify(); response = call(management+'/qualifications', {**second, 'units':2}, owner)
    check('Third paid item issues one reward', response[0] == 200 and len(call(wallet, token=customer)[1]['rewards']) == 1)
    reward = call(wallet, token=customer)[1]['rewards'][0]
    check('Other customer cannot inspect reward', not call(wallet, token=staff)[1]['rewards'])
    check('Other customer cannot request reward by guessed ID', call(wallet+'/redemptions/'+reward['id']+'/request', req(), staff)[0] == 404)
    redemption = req()
    responses = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        responses = list(pool.map(lambda _:call(wallet+'/redemptions/'+reward['id']+'/request', redemption, customer), range(4)))
    check('Concurrent redemption retries return same reward', all(r[0]==200 and r[1]['id']==reward['id'] for r in responses))
    check('Only one issued reward exists after retries', sql('SELECT COUNT(*) FROM tide_reward_issued')[0][0] == 1)
    check('Customer cannot mark own reward fulfilled', call(management+'/redemptions/'+reward['id']+'/resolve', req(action='fulfill', note='Attempt'), customer)[0] == 403)
    resolved = req(action='fulfill', note='Staff provided the test reward after checking membership.')
    check('Owner fulfills requested reward', call(management+'/redemptions/'+reward['id']+'/resolve', resolved, owner)[0] == 200)
    check('Fulfillment retry is safe', call(management+'/redemptions/'+reward['id']+'/resolve', resolved, owner)[0] == 200)
    check('Fulfilled reward cannot be requested again', call(wallet+'/redemptions/'+reward['id']+'/request', req(), customer)[0] == 409)
    check('Second fulfillment with a new key rejected', call(management+'/redemptions/'+reward['id']+'/resolve', req(action='fulfill', note='Again'), owner)[0] == 409)
    check('Customer cannot void qualifications', call(management+'/qualifications/'+first_qualification+'/void', req(reason='Attempt'), customer)[0] == 403)
    check('Owner voids qualification with audit', call(management+'/qualifications/'+first_qualification+'/void', req(reason='Verified receipt was refunded.'), owner)[0] == 200)
    check('Voiding preserves consumed history', call(wallet, token=customer)[1]['rewards'][0]['state'] == 'fulfilled')
    q = qualify(); q['units'] = 4
    response = call(management+'/qualifications', q, owner); reversible_qualification = response[1]['id']
    check('New qualifying units issue next ordinal only', len(call(wallet, token=customer)[1]['rewards']) == 2)
    available = next(r for r in call(wallet, token=customer)[1]['rewards'] if r['state']=='available')
    prior_issued_id = available['id']
    check('Owner correction can suppress unused entitlement', call(management+'/qualifications/'+reversible_qualification+'/void', req(reason='Receipt corrected.'), owner)[0] == 200)
    check('Voided entitlement cannot redeem', call(wallet+'/redemptions/'+available['id']+'/request', req(), customer)[0] == 409)

    # Real order endpoints establish unpaid facts, then the owner records money received.
    order = order_request({'items':[{'itemId':'dish-1','quantity':4}], 'fulfillment':'pickup', 'paymentMethod':'staff'})
    order_id = submit(order)[1]['orderId']
    paid_qualification = req(memberId=member, ruleId=punch, source='paid-order', sourceReference=order_id,
                             units=99, itemId='dish-1', note='Owner verified this member owns the paid receipt.')
    check('Unpaid order never qualifies', call(management+'/qualifications', paid_qualification, owner)[0] == 409)
    check('Owner records in-person payment through order workflow', call('/api/v1/tenants/bistro/ordering/orders/'+order_id, {'expectedVersion':0, 'action':'mark-paid', 'paymentCollected':True}, owner)[0] == 200)
    response = call(management+'/qualifications', paid_qualification, owner)
    check('Paid order earns stored quantity, ignoring caller units', response[0] == 200 and sql('SELECT units FROM tide_reward_qualifications WHERE id=?',(response[1]['id'],))[0][0] == 4)
    check('Paid order source cannot be awarded twice', call(management+'/qualifications', {**paid_qualification, 'requestId':str(uuid.uuid4())}, owner)[0] == 409)
    available = next(r for r in call(wallet, token=customer)[1]['rewards'] if r['state']=='available')
    check('Re-earned entitlement restores the original unique ordinal',available['id']==prior_issued_id)
    check('Available paid-order entitlement may be requested', call(wallet+'/redemptions/'+available['id']+'/request', req(), customer)[0] == 200)
    raw = json.loads(sql('SELECT payload_json FROM bartide_enhanced_orders WHERE id=?',(order_id,))[0][0])
    raw['payment_status'] = 'refunded'
    sql('UPDATE bartide_enhanced_orders SET payload_json=? WHERE id=?',(json.dumps(raw),order_id))
    check('Refunded order suppresses pending reward before fulfillment', call(management+'/redemptions/'+available['id']+'/resolve', req(action='fulfill',note='Try after refund'), owner)[0] == 409)
    check('Customer wallet refresh persists refund suppression', next(r for r in call(wallet, token=customer)[1]['rewards'] if r['id']==available['id'])['state'] == 'void')

    # A distinct rule version starts fresh, but the original fulfilled terms survive.
    changed = {**punch_request, 'requestId':str(uuid.uuid4()), 'expectedVersion':0, 'threshold':9, 'reward':'A new nine-item reward.'}
    check('Owner creates next immutable rule version', call(management+'/rules/'+punch, changed, owner)[0] == 200)
    check('Stale concurrent rule edit rejected', call(management+'/rules/'+punch, {**changed,'requestId':str(uuid.uuid4())}, owner)[0] == 409)
    w = call(wallet, token=customer)[1]
    check('Changed rule starts fresh progress', next(p for p in w['progress'] if p['ruleId']==punch)['qualifiedUnits']==0)
    check('Historical reward retains its original terms', next(r for r in w['rewards'] if r['id']==reward['id'])['reward']==punch_request['reward'])
    check('Rule version cannot replay an old paid order source', call(management+'/qualifications', {**paid_qualification,'requestId':str(uuid.uuid4())}, owner)[0] == 409)
    current_version=next(r for r in w['rules'] if r['id']==punch)['versionId']
    sql("INSERT INTO tide_reward_qualifications(id,tenant_id,rule_id,version_id,member_id,source_key,source_kind,source_reference,units,item_id,period,state,note,actor,occurred_at) VALUES(?,'bistro',?,?,?,'future-fixture','verified-purchase','future-fixture',9,'dish-1','lifetime','eligible','Synthetic future clock-skew fixture','synthetic-owner','2099-01-01T12:00:00Z')",
        (str(uuid.uuid4()),punch,current_version,member))
    check('Future-dated stored qualification cannot issue a reward early',next(p for p in call(wallet,token=customer)[1]['progress'] if p['ruleId']==punch)['qualifiedUnits']==0)

    visit = req(memberId=member, ruleId=visits, source='verified-visit', sourceReference='visit-today', units=1, itemId=None, note='Owner verified today’s in-person visit.')
    check('Owner records one verified visit', call(management+'/qualifications', visit, owner)[0] == 200)
    stored = sql('SELECT local_day,period,occurred_at,version_id FROM tide_reward_qualifications WHERE rule_id=?',(visits,))[0]
    # America/New_York is UTC-4 in this September fixture run. More generally calculate
    # through PowerShell/.NET only for timezone representation, not eligibility logic.
    expected_day = subprocess.check_output(['powershell','-NoProfile','-Command',
        "[TimeZoneInfo]::ConvertTime([DateTimeOffset]::Parse('"+stored[2]+"'),[TimeZoneInfo]::FindSystemTimeZoneById('Eastern Standard Time')).ToString('yyyy-MM-dd')"], text=True).strip()
    check('Visit local-day key follows saved timezone', stored[0] == expected_day and stored[1] == expected_day[:7])
    with concurrent.futures.ThreadPoolExecutor(max_workers=3) as pool:
        responses = list(pool.map(lambda _:call(management+'/qualifications',{**visit,'requestId':str(uuid.uuid4()),'sourceReference':'visit-'+uuid.uuid4().hex},owner),range(3)))
    check('Concurrent different references cannot duplicate local-day visit', all(r[0]==409 for r in responses))
    # Seed the previous month so this check is stable even on the first day of a
    # month. These are trusted historical fixtures, never public backdated inputs.
    historical_period=(datetime.fromisoformat(stored[0]).replace(day=1)-timedelta(days=1)).strftime('%Y-%m')
    for day in ('01','02','03'):
        local_day = historical_period+'-'+day
        sql("INSERT INTO tide_reward_qualifications(id,tenant_id,rule_id,version_id,member_id,source_key,source_kind,source_reference,units,local_day,period,state,note,actor,occurred_at) VALUES(?,'bistro',?,?,?,?,?,'historical-visit',1,?,?,'eligible','Synthetic migrated verified visit','synthetic-owner',?)",
            (str(uuid.uuid4()),visits,stored[3],member,'historical-'+day,'verified-visit',local_day,historical_period,local_day+'T16:00:00Z'))
    w = call(wallet, token=customer)[1]
    check('Three separate same-month visits issue exactly one monthly reward', len([r for r in w['rewards'] if r['ruleId']==visits])==1)
    # An old month never rolls into the current monthly progress.
    sql("INSERT INTO tide_reward_qualifications(id,tenant_id,rule_id,version_id,member_id,source_key,source_kind,source_reference,units,local_day,period,state,note,actor,occurred_at) VALUES(?,'bistro',?,?,?,?,?,'old-visit',1,'2020-01-01','2020-01','eligible','Synthetic historical visit','synthetic-owner','2020-01-01T16:00:00Z')",
        (str(uuid.uuid4()),visits,stored[3],member,'old-month','verified-visit'))
    check('Earlier calendar month is excluded from current progress', next(p for p in call(wallet,token=customer)[1]['progress'] if p['ruleId']==visits)['qualifiedUnits']==1)
    extra_days=[historical_period+'-'+str(n).zfill(2) for n in (4,5,6)]
    for day in extra_days:
        sql("INSERT INTO tide_reward_qualifications(id,tenant_id,rule_id,version_id,member_id,source_key,source_kind,source_reference,units,local_day,period,state,note,actor,occurred_at) VALUES(?,'bistro',?,?,?,?,?,'extra-visit',1,?,?,'eligible','Synthetic historical visit','synthetic-owner',?)",
            (str(uuid.uuid4()),visits,stored[3],member,'extra-'+day,'verified-visit',day,historical_period,day+'T16:00:00Z'))
    check('Six visits do not issue a second monthly reward',len([r for r in call(wallet,token=customer)[1]['rewards'] if r['ruleId']==visits])==1)

    sql("INSERT INTO tide_loyalty_ledger(id,tenant_id,member_id,event_key,delta,kind,reason,state,actor,created_at) VALUES(?,'bistro',?,'legacy-opening',40,'verified','Synthetic migrated balance','recorded','legacy-owner',?)",(str(uuid.uuid4()),member,NOW))
    check('Existing customer point balance preserved',call(wallet,token=customer)[1]['points']==40)
    award = req(memberId=member,points=160,sourceReference='verified-points-receipt',reason='Verified purchase points.')
    check('Customer cannot self-award points',call(management+'/points',award,customer)[0]==403)
    check('Owner adds verified points without replacing legacy balance',call(management+'/points',award,owner)[0]==200 and call(wallet,token=customer)[1]['points']==200)
    check('Same point source with new key cannot award again',call(management+'/points',{**award,'requestId':str(uuid.uuid4())},owner)[0]==409)
    check('Point award retry cannot duplicate balance',call(management+'/points',award,owner)[0]==200 and call(wallet,token=customer)[1]['points']==200)
    points_redeem = req(ruleId=points_rule)
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        responses=list(pool.map(lambda _:call(wallet+'/points-redemptions',points_redeem,customer),range(4)))
    check('Concurrent point redemption retries reserve exactly once',all(r[0]==200 for r in responses) and len({r[1]['id'] for r in responses})==1 and call(wallet,token=customer)[1]['points']==100)
    point_reward=responses[0][1]['id']
    check('New key cannot create duplicate pending points request',call(wallet+'/points-redemptions',req(ruleId=points_rule),customer)[0]==409)
    canceled=req(action='cancel',note='Customer changed their mind before fulfillment.')
    check('Owner cancellation returns reserved points',call(management+'/redemptions/'+point_reward+'/resolve',canceled,owner)[0]==200 and call(wallet,token=customer)[1]['points']==200)
    check('Cancellation retry returns points only once',call(management+'/redemptions/'+point_reward+'/resolve',canceled,owner)[0]==200 and call(wallet,token=customer)[1]['points']==200)
    check('Different cancellation key cannot return points twice',call(management+'/redemptions/'+point_reward+'/resolve',req(action='cancel',note='Again'),owner)[0]==409)
    point_reward=call(wallet+'/points-redemptions',req(ruleId=points_rule),customer)[1]['id']
    check('Points reward can be fulfilled without second deduction',call(management+'/redemptions/'+point_reward+'/resolve',req(action='fulfill',note='Reward handed to member.'),owner)[0]==200 and call(wallet,token=customer)[1]['points']==100)
    check('Customer cannot spend another customer balance',call(wallet+'/points-redemptions',req(ruleId=points_rule),staff)[0]==409)

    employee=req(memberId='kitchen',delta=30,sourceReference='team-recognition-1',reason='Great onboarding support.')
    check('Employee cannot grant own rewards',call(management+'/employee-points',employee,staff)[0]==403)
    check('Owner grants separate employee rewards',call(management+'/employee-points',employee,owner)[0]==200)
    sql("UPDATE bartide_enhanced_members SET user_id=NULL WHERE id='kitchen'")
    ew=call(management+'/employee',token=staff)
    check('Active staff views own employee balance',ew[0]==200 and ew[1]['points']==30 and len(ew[1]['ledger'])==1)
    check('Direct employee API binds verified identity without prior account-page visit',sql("SELECT user_id FROM bartide_enhanced_members WHERE id='kitchen'")[0][0]=='supabase:'+STAFF)
    sql("UPDATE bartide_enhanced_members SET email='changed@example.invalid' WHERE id='kitchen'")
    check('Established employee identity does not depend on mutable roster email',call(management+'/employee',token=staff)[0]==200)
    check('Employee points never change customer wallet',call(wallet,token=staff)[1]['points']==0)
    check('Customer cannot read employee ledger',call(management+'/employee',token=customer)[0]==403)
    check('Duplicate employee source cannot award twice',call(management+'/employee-points',{**employee,'requestId':str(uuid.uuid4())},owner)[0]==409)
    check('Employee debit cannot overdraw',call(management+'/employee-points',req(memberId='kitchen',delta=-31,sourceReference='too-much',reason='Invalid debit.'),owner)[0]==409)
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        responses=list(pool.map(lambda n:call(management+'/employee-points',req(memberId='kitchen',delta=-20,sourceReference='team-spend-'+str(n),reason='Owner verified fulfillment.'),owner),range(2)))
    check('Concurrent employee debits cannot overdraw balance',sorted(r[0] for r in responses)==[200,409] and call(management+'/employee',token=staff)[1]['points']==10)
    sql("UPDATE bartide_enhanced_members SET active=0 WHERE id='kitchen'")
    check('Revoked staff immediately loses employee rewards access',call(management+'/employee',token=staff)[0]==403)
    check('Owner cannot grant new points to revoked staff',call(management+'/employee-points',req(memberId='kitchen',delta=5,sourceReference='revoked',reason='Denied.'),owner)[0]==404)
    workspace=call(management,token=owner)
    check('Owner sees customer and employee summaries separately',workspace[0]==200 and len(workspace[1]['members'])==2 and workspace[1]['employees'][0]['points']==10 and not workspace[1]['employees'][0]['active'])
    check('Owner audit records include authenticated actor',all(a['actor']=='supabase:'+ALICE or a['actor']=='synthetic-owner' for a in workspace[1]['qualifications']))
    check('Platform owner can inspect workspace without changing membership',call(management,token=tokens['platform'])[0]==200 and sql('SELECT COUNT(*) FROM tide_loyalty_members WHERE tenant_id=?',('bistro',))[0][0]==2)
    sql("INSERT INTO tide_loyalty_ledger(id,tenant_id,member_id,event_key,delta,kind,reason,state,actor,created_at) VALUES(?,'bistro',?,'legacy-limit',1000000,'verified','Synthetic capped balance','recorded','legacy-owner',?)",(str(uuid.uuid4()),staff_member,NOW))
    check('Customer point grants preserve legacy million-point cap',call(management+'/points',req(memberId=staff_member,points=1,sourceReference='over-limit',reason='Must not exceed existing cap.'),owner)[0]==409)
    check('SQLite relationships remain valid',sql('PRAGMA foreign_key_check')==[])
    before=call(wallet,token=customer)[1]
    PROCESSES[-1].terminate(); PROCESSES[-1].wait(timeout=20); launch()
    after=call(wallet,token=customer)[1]
    check('Rewards and balances survive API restart',before==after)
finally:
    for proc in PROCESSES:
        if proc.poll() is None:
            proc.terminate()
            try:proc.wait(timeout=20)
            except subprocess.TimeoutExpired:proc.kill();proc.wait(timeout=10)
    PROVIDER.shutdown();PROVIDER.server_close()
    for log in LOGS:log.close()
    (RUN/'results.json').write_text(json.dumps(RESULTS,indent=2),encoding='utf-8')
    print('Rewards evidence: '+str(RUN),flush=True)
print(str(len(RESULTS))+' rewards checks passed.',flush=True)
