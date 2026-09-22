"""Phase-one delivery through real API/storage; all identities and orders are fictional.
Run after build; --postgres uses only the existing disposable loopback fixture.
"""
import sys
import re
import restaurant_test_support as s
from restaurant_test_support import *

pg = None
schema = None
schema_owned = False
env = {}
try:
    if '--postgres' in sys.argv:
        sys.path.insert(0, str(ROOT / '.tools/postgres-python'))
        import psycopg
        from psycopg import sql as pgsql
        supplied = json.loads((ROOT / '.tools/postgresql-17-test/fixture.json').read_text(encoding='utf-8-sig'))
        assert supplied['host'] in ('127.0.0.1', 'localhost', '::1') and 1024 <= int(supplied['port']) <= 65535
        creds = {k: supplied[k] for k in ('host','port','user','password')}
        creds.update(dbname=supplied['database'], sslmode='disable', connect_timeout=5)
        schema = 'tide_delivery_' + uuid.uuid4().hex[:16]
        pg = psycopg
        with pg.connect(**creds, autocommit=True) as db:
            db.execute(pgsql.SQL('CREATE SCHEMA {}').format(pgsql.Identifier(schema)))
        schema_owned = True
        def pg_query(statement, values=()):
            with pg.connect(**creds, options='-c search_path='+schema) as db:
                cur = db.execute(statement.replace('?', '%s'), values)
                return cur.fetchall() if cur.description else []
        sql = s.sql = pg_query
        def quoted(v): return '"' + str(v).replace('"','""') + '"'
        env = {'Storage__Provider':'PostgreSql', 'Storage__PostgresSchema':schema, 'Storage__CreatePostgresSchema':'false',
            'ConnectionStrings__Application': ';'.join(n+'='+quoted(creds[k]) for n,k in [('Host','host'),('Port','port'),('Database','dbname'),('Username','user'),('Password','password')])+';SSL Mode=Disable;Pooling=true;Maximum Pool Size=8'}
    launch(env)
    tokens = {}
    for who in ('alice','bob','staff','platform'):
        response = call('/api/v1/auth/signin', {'email':who+'@example.invalid','password':PASSWORD})
        check('Sign in '+who, response[0] == 200)
        tokens[who] = response[1]['accessToken']
    owner, driver, second = tokens['alice'], tokens['bob'], tokens['staff']
    def members(tenant, extra=True):
        for mid,person in [(tenant+'-driver',BOB)] + ([(tenant+'-second',STAFF),(tenant+'-unlinked',None)] if extra else []):
            sql('INSERT INTO bartide_enhanced_members(id,tenant_id,name,email,user_id,role,active,created_at) VALUES(?,?,?,?,?,?,1,?)',
                (mid,tenant,'Zoe Driver',mid+'@example.invalid','supabase:'+person if person else None,'driver',NOW))
    for tenant, person in [('bistro',ALICE),('foreign',PLATFORM)]:
        seed(tenant,person); members(tenant)
    root='/api/v1/tenants/bistro/ordering/'
    def settings(enabled):
        saved=call(root+'settings',token=owner)[1]
        response=call(root+'settings',{**saved,'expectedVersion':saved['version'],'deliveryWorkflowEnabled':enabled,'contactPhone':'(727) 555-0147'},owner)
        check('Manager saves delivery switch '+str(enabled),response[0]==200,response[:3])
    settings(True)
    check('Customer can contact configured restaurant',call('/api/v1/restaurants/bistro/menu')[1]['checkout']['contactPhone']=='(727) 555-0147')
    workspace=call(root+'operations',token=owner)[1]
    check('Unlinked drivers need setup and pickup instructions survive',not next(d for d in workspace['drivers'] if d['id']=='bistro-unlinked')['accountLinked'] and workspace['pickupInstructions']=='Ask at the counter')
    def place(tenant='bistro'):
        request=order_request({'items':[{'itemId':'dish-1','quantity':2}],'fulfillment':'delivery','deliveryZip':'33101','paymentMethod':'staff'},tenant)
        response=submit(request,tenant)
        check('Delivery placed at '+tenant,response[0]==201,response[:3])
        return request,response[1]
    def entry(ident, token=owner, tenant='bistro'):
        return next(x for x in call('/api/v1/tenants/'+tenant+'/ordering/operations',token=token)[1]['orders'] if x['order']['receipt']['orderId']==ident)
    def act(ident,action,token=owner,tenant='bistro',expected=200,version=None,**extra):
        if version is None: version=entry(ident,owner,tenant)['order']['version']
        response=call('/api/v1/tenants/'+tenant+'/ordering/orders/'+ident,{'action':action,'expectedVersion':version,**extra},token)
        check(action+' expects '+str(expected),response[0]==expected,response[:3]); return response
    request,receipt=place(); ident=receipt['orderId']
    check('New delivery has progress without private customer details',receipt['delivery'] is not None and receipt_private(receipt))
    act(ident,'acknowledge-delivery',driver,expected=403,version=0)
    act(ident,'assign-driver',driverId='foreign-driver',expected=400)
    act(ident,'assign-driver',driverId='bistro-unlinked',expected=400)
    act(ident,'assign-driver',driverId='bistro-driver')
    act(ident,'acknowledge-delivery',owner,expected=409)
    act(ident,'acknowledge-delivery',second,expected=403)
    for state in ('accepted','preparing','ready'): act(ident,state)
    act(ident,'out_for_delivery',driver,expected=409)
    act(ident,'acknowledge-delivery',driver)
    act(ident,'out_for_delivery',expected=400)
    act(ident,'out_for_delivery',driver)
    act(ident,'report-delivery-problem',driver,problemCode='address-issue',deliveryNote='SYNTHETIC PRIVATE HANDOFF')
    act(ident,'confirm-delivery',driver,expected=409)
    act(ident,'resolve-delivery-problem',driver,expected=409)
    act(ident,'resolve-delivery-problem',expected=400)
    act(ident,'resolve-delivery-problem',deliveryNote='Address clarified by customer')
    act(ident,'assign-driver',driverId='bistro-second',expected=400)
    act(ident,'assign-driver',driverId='bistro-second',deliveryNote='Shift change')
    reassigned=entry(ident)['order']['receipt']
    check('Reassignment returns to ready with acknowledgement reset',reassigned['status']=='ready' and reassigned['delivery']['acknowledgedAt'] is None and reassigned['delivery']['collectedAt'] is None)
    check('Former driver cannot read reassigned delivery',call(root+'operations',token=driver)[1]['orders']==[])
    act(ident,'confirm-delivery',driver,expected=403)
    act(ident,'acknowledge-delivery',second)
    act(ident,'out_for_delivery',second)
    act(ident,'mark-paid',second,expected=409,paymentCollected=True)
    version=entry(ident)['order']['version']
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        responses=list(pool.map(lambda _:call(root+'orders/'+ident,{'action':'confirm-delivery','expectedVersion':version},second),range(2)))
    check('Concurrent confirmation commits exactly once',sorted(r[0] for r in responses)==[200,409],[r[:3] for r in responses])
    recorded=json.loads(sql('SELECT payload_json FROM bartide_enhanced_orders WHERE id=?',(ident,))[0][0])
    check('One confirmation audit with driver identity',sum(h.get('action')=='confirm-delivery' for h in recorded['history'])==1 and recorded['delivery']['delivered_by']=='supabase:'+STAFF)
    result=call('/api/v1/restaurants/bistro/track',{'orderId':ident,'trackingKey':request['trackingKey']})[1]
    check('Delivered remains unpaid and public progress hides internal notes/actors',result['status']=='delivered' and result['paymentStatus']=='unpaid' and result['delivery']['deliveredAt'] and receipt_private(result) and 'PRIVATE HANDOFF' not in json.dumps(result) and 'delivered_by' not in json.dumps(result))
    check('Wrong tracking secret cannot read receipt',call('/api/v1/restaurants/bistro/track',{'orderId':ident,'trackingKey':'0'*64})[0]==404)
    check('Other restaurant tracking cannot read receipt',call('/api/v1/restaurants/foreign/track',{'orderId':ident,'trackingKey':request['trackingKey']})[0]==404)
    alter_config(lambda c:c.update(delivery_capacity=1))
    req2,rec2=place(); id2=rec2['orderId']
    check('Physically delivered order frees capacity before payment',rec2['status']=='new')
    act(ident,'mark-paid',expected=400)
    act(ident,'mark-paid',paymentCollected=True)
    check('Reconciliation completes delivered order',entry(ident)['order']['receipt']['status']=='completed')
    act(id2,'assign-driver',driverId='bistro-driver')
    act(id2,'acknowledge-delivery',driver)
    sql("UPDATE bartide_enhanced_members SET active=0 WHERE id='bistro-driver'")
    check('Paused driver loses reads',call(root+'operations',token=driver)[0]==403)
    act(id2,'report-delivery-problem',driver,expected=403,problemCode='unable-to-deliver')
    sql("UPDATE bartide_enhanced_members SET active=1 WHERE id='bistro-driver'")
    act(id2,'report-delivery-problem',driver,problemCode='customer-unavailable')
    act(id2,'cancelled')
    check('Cancellation clears active problem',entry(id2)['order']['receipt']['delivery']['problemCode'] is None)
    req3,rec3=place(); id3=rec3['orderId'];settings(False)
    check('Enhanced orders remain manageable with feature off',entry(id3)['deliveryWorkflowEnabled'])
    act(id3,'assign-driver',driverId='bistro-driver');act(id3,'acknowledge-delivery',driver)
    for state in ('accepted','preparing','ready'):act(id3,state)
    act(id3,'mark-paid',paymentCollected=True);act(id3,'out_for_delivery',driver);act(id3,'confirm-delivery',driver)
    check('Paid delivery completes at handoff',entry(id3)['order']['receipt']['status']=='completed')
    req4,rec4=place();id4=rec4['orderId']
    check('Switch off keeps new deliveries on legacy workflow',rec4.get('delivery') is None and not entry(id4)['deliveryWorkflowEnabled'])
    check('Expired authentication rejected',call(root+'operations',token='expired.invalid.token')[0]==401)
    act(id4,'cancelled')

    # Twenty independent restaurants, four bounded workers; this is isolation,
    # persistence and flow correctness, not a production capacity benchmark.
    tenants=['delivery-'+str(n) for n in range(20)]
    for tenant in tenants:
        seed(tenant);members(tenant,False)
        alter_config(lambda c:c.update(delivery_workflow_enabled=True),tenant)
    started=time.monotonic()
    def simulate(tenant):
        req,rec=place(tenant);i=rec['orderId'];v=0
        for action,token,extra in [('assign-driver',owner,{'driverId':tenant+'-driver'}),('acknowledge-delivery',driver,{}),('accepted',owner,{}),('preparing',owner,{}),('ready',owner,{}),('out_for_delivery',driver,{}),('confirm-delivery',driver,{})]:
            r=call('/api/v1/tenants/'+tenant+'/ordering/orders/'+i,{'action':action,'expectedVersion':v,**extra},token)
            assert r[0]==200,(tenant,action,r[:3]);v+=1
        neighbor=tenants[(tenants.index(tenant)+1)%20]
        assert call('/api/v1/restaurants/'+neighbor+'/track',{'orderId':i,'trackingKey':req['trackingKey']})[0]==404
        assert call('/api/v1/tenants/'+neighbor+'/ordering/orders/'+i,{'action':'acknowledge-delivery','expectedVersion':v},driver)[0]==404
        visible=call('/api/v1/tenants/'+tenant+'/ordering/operations',token=driver)[1]['orders']
        assert len(visible)==1 and visible[0]['order']['receipt']['orderId']==i
        assert visible[0]['order']['receipt']['status']=='delivered' and visible[0]['order']['receipt']['paymentStatus']=='unpaid'
        return i
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool: ids=list(pool.map(simulate,tenants))
    check('20 simultaneous restaurant workflows remain isolated',len(set(ids))==20)
    print('20-location rehearsal elapsed seconds: '+str(round(time.monotonic()-started,2)),flush=True)
finally:
    for proc in PROCESSES:
        if proc.poll() is None:
            proc.terminate()
            try:proc.wait(timeout=20)
            except subprocess.TimeoutExpired:proc.kill();proc.wait(timeout=10)
    PROVIDER.shutdown();PROVIDER.server_close()
    for log in LOGS:log.close()
    if pg and schema_owned:
        assert re.fullmatch(r'tide_delivery_[a-f0-9]{16}',schema)
        with pg.connect(**creds,autocommit=True) as db:
            db.execute(pgsql.SQL('DROP SCHEMA {} CASCADE').format(pgsql.Identifier(schema)))
    (RUN/'delivery-results.json').write_text(json.dumps({'provider':'postgres' if pg else 'sqlite','checks':RESULTS},indent=2),encoding='utf-8')
    print('Delivery evidence: '+str(RUN),flush=True)
print(str(len(RESULTS))+' delivery checks passed.',flush=True)
