"""Real API/storage payment workflow against synthetic loopback identity and Stripe boundaries."""
import concurrent.futures
import copy
import hashlib
import hmac
import json
import os
import re
import shutil
import subprocess
import sys
import time
import uuid
import restaurant_test_support as s
from driver_payment_test_provider import DriverStripeFake, KEY, SECRET

fake = DriverStripeFake()
tokens = {}
root = '/api/v1/tenants/bistro/'
env = {'DeliveryDispatch__WorkerEnabled':'false', 'DriverPayments__WorkerEnabled':'false',
       'DriverPayments__Environment':'sandbox', 'DriverPayments__RestrictedKey':KEY,
       'DriverPayments__WebhookSecret':SECRET, 'DriverPayments__PublicBaseUrl':'https://payments.example.invalid',
       'DriverPayments__ApiBaseUrl':fake.url, 'DriverPayments__AllowLocalTestProvider':'true',
       'DriverPayments__SetupEnabled':'true', 'DriverPayments__PaymentsEnabled':'true'}
source = s.Path(os.environ.get('TIDE_TEST_BUILD_ROOT',str(s.ROOT)))
runtime = source if '--in-place-build' in sys.argv else s.RUN / 'payment-runtime'
if runtime != source:
    shutil.copytree(source/'TideCasa.Api/bin/Debug/net10.0',runtime/'TideCasa.Api/bin/Debug/net10.0')
os.environ['TIDE_TEST_BUILD_ROOT']=str(runtime)
completed=False
pg=None
schema=None
schema_owned=False

def api(path,body=None,who='alice',status=200,label=None):
    result=s.call(path,body,tokens[who])
    s.check(label or path+' '+str(status),result[0]==status,result[:3])
    return result[1]

def payments():return api(root+'driver-payments')['payments']
def payment(ident):return next(p for p in payments() if p['id']==ident)
def order(ident):return next(x for x in api(root+'ordering/operations')['orders'] if x['order']['receipt']['orderId']==ident)
def action(ident,name,who='alice',status=200,**extra):
    return api(root+'ordering/orders/'+ident,{'expectedVersion':order(ident)['order']['version'],'action':name,**extra},who,status)
def place(member,who):
    request=s.order_request({'items':[{'itemId':'dish-1','quantity':2}],'fulfillment':'delivery','deliveryZip':'33101','paymentMethod':'staff'})
    result=s.submit(request)
    s.check('Synthetic delivery placed',result[0]==201,result[:3]);ident=result[1]['orderId']
    action(ident,'assign-driver',driverId=member)
    for state in ('accepted','preparing','ready'):action(ident,state)
    action(ident,'acknowledge-delivery',who)
    action(ident,'out_for_delivery',who)
    return ident,request
def approve(ident,amount,status=200,**extra):
    row=payment(ident)
    return api(root+'driver-payments/'+ident+'/approve',{'expectedVersion':row['version'],'driverPayCents':amount,'confirmPayment':True,**extra},status=status)
def setup(who,status=200):
    return api('/api/v1/drivers/payout-setup',{'requestKey':str(uuid.uuid4()),'confirmUsIndividual':True},who,status)
def signed_event(session,valid=True):
    event={'id':'evt_synthetic_'+uuid.uuid4().hex,'type':'checkout.session.completed','livemode':False,
           'data':{'object':fake.sessions[session]}}
    raw=json.dumps(event).encode();stamp=str(int(time.time()))
    signature=hmac.new(SECRET.encode(),stamp.encode()+b'.'+raw,hashlib.sha256).hexdigest()
    return s.call('/api/stripe/driver-payments/webhook',event,headers={'Stripe-Signature':'t='+stamp+',v1='+(signature if valid else '0'*64)})

try:
    if '--postgres' in sys.argv:
        sys.path.insert(0,str(s.ROOT/'.tools/postgres-python'))
        import psycopg
        from psycopg import sql as pgsql
        supplied=json.loads((s.ROOT/'.tools/postgresql-17-test/fixture.json').read_text(encoding='utf-8-sig'))
        assert supplied['host'] in ('127.0.0.1','localhost','::1') and 1024<=int(supplied['port'])<=65535
        creds={key:supplied[key] for key in ('host','port','user','password')}
        creds.update(dbname=supplied['database'],sslmode='disable',connect_timeout=5)
        schema='tide_driverpay_'+uuid.uuid4().hex[:16]
        pg=psycopg
        with pg.connect(**creds,autocommit=True) as db:
            db.execute(pgsql.SQL('CREATE SCHEMA {}').format(pgsql.Identifier(schema)))
        schema_owned=True
        def pg_query(statement,values=()):
            with pg.connect(**creds,options='-c search_path='+schema) as db:
                db.execute('SELECT pg_advisory_xact_lock(hashtext(current_database()),hashtext(current_schema()))')
                cur=db.execute(statement.replace('?','%s'),values)
                return cur.fetchall() if cur.description else []
        s.sql=pg_query
        def quoted(value):return '"'+str(value).replace('"','""')+'"'
        env.update({'Storage__Provider':'PostgreSql','Storage__PostgresSchema':schema,'Storage__CreatePostgresSchema':'false',
            'ConnectionStrings__Application':';'.join(n+'='+quoted(creds[k]) for n,k in
                [('Host','host'),('Port','port'),('Database','dbname'),('Username','user'),('Password','password')])
                +';SSL Mode=Disable;Pooling=true;Maximum Pool Size=8'})
    manager_id=str(uuid.uuid4())
    s.USERS['manager@example.invalid']={'id':manager_id,'email':'manager@example.invalid','email_confirmed_at':s.NOW,'is_anonymous':False,'user_metadata':{'full_name':'Synthetic Manager'}}
    s.launch(env)
    for who in ('alice','bob','staff','platform','manager'):
        response=s.call('/api/v1/auth/signin',{'email':who+'@example.invalid','password':s.PASSWORD})
        s.check('Verified signin '+who,response[0]==200);tokens[who]=response[1]['accessToken']
    s.seed('bistro');s.seed('foreign',s.PLATFORM)
    s.alter_config(lambda c:c.update(delivery_workflow_enabled=True,delivery_capacity=30))
    profile=api('/api/v1/drivers/me',{'expectedVersion':0,'name':'Network Driver','bio':'Synthetic local driver','deliveryZips':['33101'],'listed':True,'capacity':10},'bob')['profile']
    hire=api(root+'driver-network/offers',{'driverId':profile['id'],'payPerDeliveryCents':1000,'notes':'Agreed per completed delivery','expectedVersion':0})['hires'][0]
    accepted=api('/api/v1/drivers/hires/'+hire['id']+'/respond',{'expectedVersion':hire['version'],'accept':True},'bob')['hires'][0]
    network=accepted['memberId']
    board=api(root+'delivery-dispatch',who='bob');driver=board['drivers'][0]
    api(root+'delivery-dispatch/drivers/'+network,{'expectedVersion':driver['version'],'availability':'available','capacity':driver['capacity'],'deliveryZips':driver['deliveryZips']},'bob')
    team=api(root+'team/members',{'name':'Own Driver','email':'staff@example.invalid','role':'driver'})
    own=next(m['id'] for m in team['members'] if m['email']=='staff@example.invalid')
    api(root+'ordering/operations',who='staff')
    api(root+'team/members',{'name':'Manager','email':'manager@example.invalid','role':'manager'})
    api(root+'driver-payments',who='manager')
    api('/api/v1/drivers/payout-setup',{'requestKey':str(uuid.uuid4()),'confirmUsIndividual':False},'bob',400)
    fake.fail_once.add('/v2/core/accounts')
    setup('bob',503)
    s.check('Uncertain recipient create retains one provider account',len(fake.accounts)==1)
    link=setup('bob')
    s.check('Recipient retry reuses original durable key',len(fake.accounts)==1 and link['url'].startswith('https://connect.stripe.com/'))
    setup('staff')
    s.check('Driver accounts use distinct payment recipients',len(fake.accounts)==2)
    net_order,tracking=place(network,'bob')
    s.check('No payable before actual handoff',payments()==[])
    action(net_order,'confirm-delivery','bob')
    debt=payment(net_order)
    s.check('Network handoff records exact gross fee without charging',debt['source']=='network' and debt['status']=='pending_approval'
            and (debt['driverPayCents'],debt['platformFeeCents'],debt['totalCents'])==(1000,50,1050) and not fake.sessions)
    action(net_order,'confirm-delivery','bob',409)
    s.check('Duplicate handoff cannot create a second debt',len(payments())==1)
    api(root+'driver-payments/'+net_order+'/approve',{'expectedVersion':0,'driverPayCents':1000,'confirmPayment':True},'manager',403)
    approve(net_order,1000,400,confirmPayment=False)
    approve(net_order,999,409)
    api('/api/v1/tenants/foreign/driver-payments',who='alice',status=403)
    api(root+'driver-payments',who='bob',status=403)
    s.check('Rejected approvals never open checkout',not fake.sessions)
    req={'expectedVersion':0,'driverPayCents':1000,'confirmPayment':True}
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        results=list(pool.map(lambda _:s.call(root+'driver-payments/'+net_order+'/approve',req,tokens['alice']),range(2)))
    s.check('Concurrent approvals create one checkout',sorted(r[0] for r in results)==[200,409] and len(fake.sessions)==1,[r[:3] for r in results])
    session=next(iter(fake.sessions))
    s.check('Client funds exact agreed pay plus5 percent',fake.sessions[session]['amount_total']==1050 and fake.sessions[session]['_fee']==50)
    s.check('Checkout creation is not payment completion',payment(net_order)['status']=='open')
    manager=api(root+'driver-payments',who='manager')
    s.check('Manager sees no approval permission or checkout URL',not manager['canApprove'] and all(p['checkoutUrl'] is None for p in manager['payments']))
    before=len(fake.sessions);approve(net_order,1000)
    s.check('Resume approval reuses existing checkout',len(fake.sessions)==before)
    fake.pay(session)
    s.check('Unsigned webhook cannot mark payment paid',signed_event(session,False)[0]==400 and payment(net_order)['status']=='open')
    s.check('Signed webhook reconciles known attempt',signed_event(session)[0]==202)
    s.check('Paid state requires matched charge transfer and fee',payment(net_order)['status']=='paid')
    s.check('Duplicate webhook is safe',signed_event(session)[0]==202 and len(fake.sessions)==1)
    approve(net_order,1000,409)
    tracked=s.call('/api/v1/restaurants/bistro/track',{'orderId':net_order,'trackingKey':tracking['trackingKey']})[1]
    s.check('Driver payment never changes food order collection',tracked['paymentStatus']=='unpaid' and tracked['status']=='delivered')
    own_order,_=place(own,'staff');action(own_order,'confirm-delivery','staff')
    own_debt=payment(own_order)
    s.check('Own delivery has no network fee and waits for client amount',own_debt['source']=='own' and own_debt['driverPayCents']==0 and own_debt['platformFeeCents']==0)
    created=approve(own_order,800);own_session=next(k for k,v in fake.sessions.items() if v['client_reference_id']==own_order)
    s.check('Own driver checkout allocates full approved amount',created['payment']['totalCents']==800 and fake.sessions[own_session]['_fee']==0)
    fake.sessions[own_session].update(status='expired',payment_status='unpaid',url=None)
    api(root+'driver-payments/'+own_order+'/refresh',{})
    s.check('Known expired checkout permits one replacement attempt',payment(own_order)['status']=='expired')
    replacement=approve(own_order,800)
    replacement_session=next(k for k,v in fake.sessions.items() if v['client_reference_id']==own_order and k!=own_session)
    fake.pay(replacement_session)
    api(root+'driver-payments/'+own_order+'/refresh',{})
    s.check('Own driver pays no application fee',payment(own_order)['status']=='paid' and fake.sessions[replacement_session]['_fee']==0)
    bob=api('/api/v1/drivers/payments',who='bob');staff=api('/api/v1/drivers/payments',who='staff')
    s.check('Earnings isolate each driver and hide hosted checkout URLs',[p['id'] for p in bob['payments']]==[net_order]
            and [p['id'] for p in staff['payments']]==[own_order] and all(p['checkoutUrl'] is None for p in bob['payments']+staff['payments']))
    broken_order,_=place(own,'staff');action(broken_order,'confirm-delivery','staff')
    fake.fail_once.add('/v1/checkout/sessions');approve(broken_order,700,503)
    count=len(fake.sessions);approve(broken_order,700)
    s.check('Uncertain checkout retry preserves one charge attempt',len(fake.sessions)==count)
    broken_session=next(k for k,v in fake.sessions.items() if v['client_reference_id']==broken_order)
    intent=fake.pay(broken_session);intent['transfer_data']['destination']='acct_wrongdriver'
    api(root+'driver-payments/'+broken_order+'/refresh',{},status=409)
    s.check('Wrong payment destination is quarantined for review',payment(broken_order)['status']=='review')
    approve(broken_order,700,409)
    net_intent=fake.intents[fake.sessions[session]['payment_intent']]
    net_intent['latest_charge']['amount_refunded']=1050;net_intent['latest_charge']['refunded']=True
    api(root+'driver-payments/'+net_order+'/refresh',{})
    s.check('Provider refund removes paid status and cannot silently repay',payment(net_order)['status']=='refunded')
    approve(net_order,1000,409)
    own_intent=fake.intents[fake.sessions[replacement_session]['payment_intent']];own_intent['latest_charge']['disputed']=True
    api(root+'driver-payments/'+own_order+'/refresh',{})
    s.check('Disputes are explicit rather than treated as ordinary paid',payment(own_order)['status']=='disputed')
    s.check('Public receipt excludes compensation and internal provider data','network_assignment' not in json.dumps(tracked) and 'driverPayCents' not in json.dumps(tracked))
    # An unavailable provider object must rotate behind other recovery work.
    retry_order,_=place(own,'staff');action(retry_order,'confirm-delivery','staff')
    approve(retry_order,600)
    retry_session=next(k for k,v in fake.sessions.items() if v['client_reference_id']==retry_order)
    saved_retry=fake.sessions.pop(retry_session)
    s.sql("UPDATE tide_driver_payment_attempts SET updated_at=? WHERE payment_id=?",('2000-01-01T00:00:00+00:00',retry_order))
    api(root+'driver-payments/'+retry_order+'/refresh',{},status=503)
    retry_row=s.sql('SELECT updated_at,lease_key,lease_until FROM tide_driver_payment_attempts WHERE payment_id=?',(retry_order,))[0]
    s.check('Failed provider reads rotate recovery fairly and release their lease',retry_row[0]>'2000-01-02' and retry_row[1] is None and retry_row[2] is None)
    fake.sessions[retry_session]=saved_retry
    uncertain_order,_=place(own,'staff');action(uncertain_order,'confirm-delivery','staff')
    fake.fail_once.add('/v1/checkout/sessions');approve(uncertain_order,500,503)
    s.sql('UPDATE tide_driver_payment_attempts SET created_at=? WHERE payment_id=?',('2000-01-01T00:00:00+00:00',uncertain_order))
    calls=len(fake.calls)
    api(root+'driver-payments/'+uncertain_order+'/refresh',{})
    s.check('Expired idempotency window quarantines uncertain creation without replay',payment(uncertain_order)['status']=='review' and len(fake.calls)==calls)
    approve(uncertain_order,500,409)
    # Seed high-volume history to exercise public paging without hundreds of artificial handoffs.
    older_order,_=place(own,'staff');action(older_order,'confirm-delivery','staff')
    own_user=s.sql('SELECT user_id FROM tide_driver_payables WHERE id=?',(older_order,))[0][0]
    filler=[]
    for n in range(501):
        ident=str(uuid.uuid4());filler.append(ident)
        s.sql('INSERT INTO tide_driver_payables(id,tenant_id,order_id,member_id,user_id,driver_name,source,agreed_pay_cents,completed_at,order_number) VALUES(?,?,?,?,?,?,?,?,?,?)',
              (ident,'bistro',ident,own,own_user,'Synthetic Own Driver','own',600,'2099-01-01T00:00:00+00:00','HISTORY-'+str(n)))
    first=api(root+'driver-payments');second=api(root+'driver-payments?page=1')
    s.check('Payment history pages retain older pending obligations',first['page']==0 and first['hasMore'] and len(first['payments'])==500
            and second['page']==1 and not second['hasMore'] and any(p['id']==older_order for p in second['payments'])
            and not set(p['id'] for p in first['payments']).intersection(p['id'] for p in second['payments']))
    earnings=api('/api/v1/drivers/payments?page=1',who='staff')
    s.check('Driver can browse older private earnings',earnings['page']==1 and any(p['id']==older_order for p in earnings['payments']) and all(p['checkoutUrl'] is None for p in earnings['payments']))
    api(root+'driver-payments?page=-1',status=400)
    api('/api/v1/drivers/payments?page=100001',who='staff',status=400)
    old_paid=api(root+'driver-payments/'+older_order+'/approve',{'expectedVersion':0,'driverPayCents':600,'confirmPayment':True,'returnPage':1})
    s.check('Older payment exact lookup can approve beyond first page',old_paid['payment']['id']==older_order and old_paid['payment']['status']=='open')
    old_session=next(v for v in fake.sessions.values() if v['client_reference_id']==older_order)
    s.check('Hosted checkout returns to older history page','page=1' in old_session['success_url'] and 'page=1' in old_session['cancel_url'])
    old_manager=api(root+'driver-payments/'+older_order+'/refresh',{},who='manager')
    s.check('Exact old-payment lookup still strips manager checkout URL',old_manager['url'] is None and old_manager['payment']['checkoutUrl'] is None)
    snapshot=payments()
    for proc in s.PROCESSES:
        if proc.poll() is None:proc.terminate();proc.wait(timeout=20)
    s.launch({**env,'DriverPayments__Environment':'live','DriverPayments__LivePaymentsVerified':'false'})
    live=api(root+'driver-payments')
    s.check('Sandbox payments cannot satisfy live obligations',not live['sandbox'] and not live['paymentsEnabled'] and all(p['status']=='pending_approval' for p in live['payments']))
    api(root+'driver-payments/'+net_order+'/approve',{'expectedVersion':0,'driverPayCents':1000,'confirmPayment':True},status=503)
    completed=True
finally:
    for proc in s.PROCESSES:
        if proc.poll() is None:
            proc.terminate()
            try:proc.wait(timeout=20)
            except subprocess.TimeoutExpired:proc.kill();proc.wait(timeout=10)
    s.PROVIDER.shutdown();s.PROVIDER.server_close();fake.close()
    for log in s.LOGS:log.close()
    if pg and schema_owned:
        assert re.fullmatch(r'tide_driverpay_[a-f0-9]{16}',schema)
        with pg.connect(**creds,autocommit=True) as db:
            db.execute(pgsql.SQL('DROP SCHEMA {} CASCADE').format(pgsql.Identifier(schema)))
    artifact={'completed':completed,'provider':'postgres' if pg else 'sqlite','checks':s.RESULTS,'apiSha256':hashlib.sha256((runtime/'TideCasa.Api/bin/Debug/net10.0/TideCasa.Api.dll').read_bytes()).hexdigest()}
    (s.RUN/'driver-payment-workflow-results.json').write_text(json.dumps(artifact,indent=2),encoding='utf-8')
    print('Payment workflow evidence: '+str(s.RUN),flush=True)
print(str(len(s.RESULTS))+' driver payment workflow checks passed.',flush=True)
