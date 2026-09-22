"""Tenant-bound manager, bartender and server checks using real C# / isolated SQLite.

Only the loopback identity provider and people are synthetic. No production data,
provider credentials, external calls, deployment or real payment activity.
TIDE_ROLE_POSTGRES=1 uses the existing loopback PostgreSQL fixture and a new schema.
"""
from pathlib import Path
from datetime import timedelta
import json
import os
import shutil
import uuid
import restaurant_test_support as s

source = Path(os.environ.get('TIDE_TEST_BUILD_ROOT', str(s.ROOT))).resolve()
s.RUN = s.ROOT / '.tools/restaurant-role-verification' / s.datetime.now(s.timezone.utc).strftime('%Y%m%d-%H%M%S-%f')
s.RUN.mkdir(parents=True)
s.DB = s.RUN / 'synthetic.db'
runtime = s.RUN / 'runtime'
for project in ('TideCasa.Api', 'TideCasa.Blazor'):
    shutil.copytree(source / project / 'bin/Debug/net10.0', runtime / project / 'bin/Debug/net10.0')
s.ROOT = runtime
os.environ['TIDE_TEST_BUILD_ROOT'] = str(runtime)
for key in list(os.environ):
    if any(value in key.upper() for value in ('CONNECTIONSTRINGS', 'REVERSEPROXY__', 'WEBPUSH__', 'BUSINESSPUSH__', 'DATAPROTECTION__')):
        os.environ.pop(key)
roles = ('manager', 'bartender', 'server', 'kitchen', 'driver')
postgres = None
if os.environ.get('TIDE_ROLE_POSTGRES') == '1':
    from postgres_test_support import PostgresFixture
    postgres = PostgresFixture('restaurant-roles')
    postgres.install_namespace(vars(s))
for n, role in enumerate((*roles, 'manager-two'), 501):
    s.USERS[role+'@example.invalid'] = {'id':str(uuid.UUID(int=n)), 'email':role+'@example.invalid',
        'email_confirmed_at':'2026-01-01T00:00:00Z', 'is_anonymous':False,
        'user_metadata':{'full_name':role.title(), 'role':'owner', 'isPlatformOwner':True}}

def api(suffix, body=None, person='manager', tenant='bistro'):
    return s.call('/api/v1/tenants/'+tenant+'/'+suffix, body, tokens[person])

def ok(label, response, code=200):
    s.check(label, response[0] == code, response[:3])
    return response[1]

def request(**data):
    return {'requestId':str(uuid.uuid4()), **data}

try:
    s.launch()
    s.seed('bistro'); s.seed('foreign', s.BOB)
    tokens = {}
    for person in ('alice', 'bob', *roles, 'manager-two'):
        session = ok('Synthetic sign-in '+person, s.call('/api/v1/auth/signin', {'email':person+'@example.invalid', 'password':s.PASSWORD}))
        tokens[person] = session['accessToken']
        s.check('Metadata grants no platform ownership '+person, not session['user']['isPlatformOwner'])

    members = {}
    for role in (*roles, 'manager-two'):
        team = ok('Owner grants '+role, api('team/members', {'name':role.title(), 'email':role+'@example.invalid', 'role':'manager' if role=='manager-two' else role}, 'alice'))
        members[role] = next(m['id'] for m in team['members'] if m['email']==role+'@example.invalid')
    ok('First direct manager menu visit binds invitation', api('menu'))
    s.check('Manager association is durable', s.sql('SELECT user_id FROM bartide_enhanced_members WHERE id=?',(members['manager'],))[0][0]=='supabase:'+s.USERS['manager@example.invalid']['id'])
    for role in roles:
        desk = ok('Direct operations for '+role, api('ordering/operations', person=role))
        s.check('Operations preserves real role '+role, desk['role']==role)
        account = ok('Account for '+role, s.call('/api/v1/account', token=tokens[role]))
        access = next(w for w in account['workspaces'] if w['tenantId']=='bistro')
        s.check('Staff never becomes owner '+role, not access['isOwner'] and access['staffRole']==role)
        s.check('Only manager can edit '+role, access['canEdit']==(role=='manager') and access['canPrepare']==(role=='manager'))
        s.check('Foreign operations denied '+role, api('ordering/operations', person=role, tenant='foreign')[0]==403)

    managed = ('menu','ordering/settings','ordering','team','rewards','events','media','posts')
    for feature in managed:
        # Simulate first use of each direct API without a preceding account-page visit.
        s.sql('UPDATE bartide_enhanced_members SET user_id=NULL WHERE id=?', (members['manager'],))
        ok('Manager daily workspace '+feature, api(feature))
        s.check('Direct workspace binding is durable '+feature, s.sql('SELECT user_id FROM bartide_enhanced_members WHERE id=?', (members['manager'],))[0][0]=='supabase:'+s.USERS['manager@example.invalid']['id'])
        s.check('Manager foreign workspace denied '+feature, api(feature, tenant='foreign')[0] in (403,404))
    for person in roles:
        for feature in ('billing','payments/connect'):
            s.check('Owner-only '+feature+' denied to '+person, api(feature, person=person)[0]==403)
        for route in ('/api/v1/owner/launch-review','/api/v1/owner/sales'):
            s.check('Platform-only '+route+' denied to '+person, s.call(route,token=tokens[person])[0]==403)
    for person in ('bartender','server','kitchen','driver'):
        for feature in ('menu','ordering/settings','rewards','events','media','posts'):
            s.check('Nonmanager denied '+feature+' '+person, api(feature,person=person)[0]==403)
        team = ok('Own team '+person,api('team',person=person))
        s.check('Staff team conceals manager controls and emails '+person, not team['canManage'] and all(m['email'] is None for m in team['members']))
        ok('Own employee reward wallet '+person,api('rewards/employee',person=person))

    s.check('Manager cannot grant another manager',api('team/members',{'name':'Denied','email':'denied@example.invalid','role':'manager'})[0]==403)
    for target in ('manager','manager-two'):
        s.check('Manager cannot alter manager access '+target,api('team/members/'+members[target],{'expectedActive':True,'active':False})[0]==403)
    added=ok('Manager grants daily staff',api('team/members',{'name':'New server','email':'new-server@example.invalid','role':'server'}))
    added_id=next(m['id'] for m in added['members'] if m['email']=='new-server@example.invalid')
    ok('Manager pauses daily staff',api('team/members/'+added_id,{'expectedActive':True,'active':False}))
    s.check('Server cannot grant staff',api('team/members',{'name':'Denied','email':'denied@example.invalid','role':'server'},'server')[0]==403)
    s.check('Owner rejects unsupported role',api('team/members',{'name':'Denied','email':'denied@example.invalid','role':'admin'},'alice')[0]==400)

    menu=ok('Manager opens menu editor',api('menu'))
    profile={**menu['profile'],'tagline':'Managed locally'}
    ok('Manager saves scoped menu profile',api('menu/profile',{'expectedVersion':menu['version'],'profile':profile}))
    s.check('Stale manager edit rejected',api('menu/profile',{'expectedVersion':menu['version'],'profile':profile})[0]==409)
    s.check('Manager edit never replaces owner identity',s.sql("SELECT user_id FROM bartide_customers WHERE id='bistro'")[0][0]=='supabase:'+s.ALICE)

    course_body={'title':'Opening the beach bar','description':'Synthetic training','published':False}
    team=ok('Manager creates training draft',api('team/courses',course_body))
    course=team['courses'][0]['id']
    lesson_body={'courseId':course,'title':'Opening check','description':'Practice service checks','videoUrl':'https://youtu.be/dQw4w9WgXcQ','position':1}
    team=ok('Manager creates lesson',api('team/lessons',lesson_body)); lesson=team['lessons'][0]['id']
    for person in roles:
        ok('Manager assigns course '+person,api('team/courses/'+course+'/assignments',{'memberId':members[person],'active':True,'expectedVersion':-1}))
    ok('Manager publishes training',api('team/courses/'+course,{**course_body,'published':True,'expectedVersion':0}))
    for person in roles:
        team=ok('Staff records own training '+person,api('team/lessons/'+lesson+'/progress',{'completed':True},person))
        s.check('Progress binds own member '+person,any(p['memberId']==members[person] and p['completed'] for p in team['progress']))
    s.check('Owner cannot impersonate learner',api('team/lessons/'+lesson+'/progress',{'completed':True},'alice')[0]==403)

    staff_order=s.order_request()
    receipt=ok('Guest places pay-staff order',s.submit(staff_order),201); oid=receipt['orderId']; path='ordering/orders/'+oid
    s.check('Bartender cannot record collected payment',api(path,{'expectedVersion':0,'action':'mark-paid','paymentCollected':True},'bartender')[0]==409)
    s.check('Server cannot skip acceptance',api(path,{'expectedVersion':0,'action':'ready'},'server')[0]==409)
    ok('Server accepts order',api(path,{'expectedVersion':0,'action':'accepted'},'server'))
    s.check('Server cannot prepare whole order',api(path,{'expectedVersion':1,'action':'preparing'},'server')[0]==409)
    ok('Bartender prepares whole order',api(path,{'expectedVersion':1,'action':'preparing'},'bartender'))
    ok('Bartender marks whole order ready',api(path,{'expectedVersion':2,'action':'ready'},'bartender'))
    s.check('Server cannot complete unpaid order',api(path,{'expectedVersion':3,'action':'completed'},'server')[0]==409)
    s.check('Server collection requires confirmation',api(path,{'expectedVersion':3,'action':'mark-paid'},'server')[0]==400)
    ok('Server records collected payment',api(path,{'expectedVersion':3,'action':'mark-paid','paymentCollected':True},'server'))
    s.check('Manager cannot cancel paid order',api(path,{'expectedVersion':4,'action':'cancelled'})[0]==409)
    ok('Server completes paid ready pickup',api(path,{'expectedVersion':4,'action':'completed'},'server'))
    payload=json.loads(s.sql('SELECT payload_json FROM bartide_enhanced_orders WHERE id=?',(oid,))[0][0])
    s.check('Audit records actual server identity',payload['history'][-1]['actor_id']=='supabase:'+s.USERS['server@example.invalid']['id'])
    s.check('Guest tracking reflects completed order',s.call('/api/v1/restaurants/bistro/track',{'orderId':oid,'trackingKey':staff_order['trackingKey']})[1]['status']=='completed')

    award=request(memberId=members['server'],delta=20,sourceReference='role-check',reason='Training completed')
    ok('Manager awards employee points',api('rewards/employee-points',award))
    s.check('Manager cannot award own points',api('rewards/employee-points',request(memberId=members['manager'],delta=20,sourceReference='self-denied',reason='Must be owner approved'))[0]==403)
    s.check('Server cannot self-award',api('rewards/employee-points',request(memberId=members['server'],delta=20,sourceReference='self-denied',reason='Denied'),'server')[0]==403)
    ok('Owner may award manager points',api('rewards/employee-points',request(memberId=members['manager'],delta=20,sourceReference='owner-approved',reason='Owner approved'),'alice'))

    start=s.datetime.now(s.timezone.utc)+timedelta(hours=1)
    event={'requestKey':str(uuid.uuid4()),'expectedVersion':0,'title':'Local live music','details':'Synthetic event','location':'Fictional beach patio','startsAt':start.isoformat(),'endsAt':(start+timedelta(hours=2)).isoformat(),'timeZone':'America/New_York','capacity':5,'published':True}
    created=ok('Manager publishes event',api('events',event)); eid=created['id']
    guest=ok('Customer reserves event',s.call('/api/v1/restaurants/bistro/events/'+eid+'/rsvp',{'requestKey':str(uuid.uuid4()),'expectedVersion':-1,'attending':True},tokens['bob']))
    ok('Manager checks in guest',api('events/'+eid+'/check-in',{'requestKey':str(uuid.uuid4()),'userId':'supabase:'+s.BOB,'expectedVersion':guest['version']}))
    ok('Manager drafts business update',api('posts',{'requestKey':str(uuid.uuid4()),'title':'Patio music','body':'Synthetic local update'}))

    # Durable binding survives roster-email edits; a changed invitation cannot replace it.
    s.sql('UPDATE bartide_enhanced_members SET email=? WHERE id=?',('changed@example.invalid',members['manager']))
    ok('Manager identity survives roster email edit',api('events'))
    ok('Owner pauses manager',api('team/members/'+members['manager'],{'expectedActive':True,'active':False},'alice'))
    for feature in (*managed,'ordering/operations','rewards/employee'):
        s.check('Paused manager immediately loses '+feature,api(feature)[0]==403)
    ok('Owner restores manager',api('team/members/'+members['manager'],{'expectedActive':False,'active':True},'alice'))
    ok('Restored manager retains durable identity',api('events'))
    s.check('Database relationships remain valid',s.sql('PRAGMA foreign_key_check')==[])
finally:
    for proc in s.PROCESSES:
        if proc.poll() is None: proc.terminate(); proc.wait(timeout=20)
    s.PROVIDER.shutdown(); s.PROVIDER.server_close()
    for log in s.LOGS: log.close()
    if postgres: postgres.cleanup()
    (s.RUN/'provider.json').write_text(json.dumps({'provider':'PostgreSQL' if postgres else 'SQLite',
        'loopbackOnly':True, 'postgresVersion':postgres.server_version if postgres else None,
        'schemaRemoved':True if postgres else None,
        'binarySha256':postgres.binaries if postgres else {}},indent=2),encoding='utf-8')
    (s.RUN/'results.json').write_text(json.dumps(s.RESULTS,indent=2),encoding='utf-8')
    print('Evidence: '+str(s.RUN),flush=True)
