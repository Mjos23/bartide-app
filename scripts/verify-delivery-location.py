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
    if "--simulate" in sys.argv: env["SampleBar__Enabled"]="true"
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
    gps='/api/v1/tenants/bistro/delivery-location'
    def gps_settings(enabled):
        board=call(gps,token=owner)[1]
        return call(gps+'/settings',{'expectedVersion':board['version'],'enabled':enabled},owner)
    check('Location starts disabled',call(gps,token=owner)[1]['enabled'] is False)
    check('Driver cannot enable GPS',call(gps+'/settings',{'expectedVersion':0,'enabled':True},driver)[0]==403)
    check('Manager enables location feature',gps_settings(True)[0]==200)
    request,receipt=place();ident=receipt['orderId']
    def start(token=driver,i=ident,consent=True):return call(gps+'/'+i+'/start',{'consent':consent},token)
    check('No tracking before collection',start()[0]==409)
    check('Owner cannot impersonate driver consent',start(owner)[0]==403)
    act(ident,'assign-driver',driverId='bistro-driver')
    act(ident,'acknowledge-delivery',driver)
    for state in ('accepted','preparing','ready'):act(ident,state)
    act(ident,'out_for_delivery',driver)
    check('Opt in required',start(consent=False)[0]==400)
    check('Another driver cannot share this delivery',start(second)[0]==409)
    started=start();check('Assigned driver starts session',started[0]==200,started[:3]);session=started[1]['session']
    simulated='--simulate' in sys.argv
    check('Server selects actual or simulated mode',started[1]['simulated']==simulated)
    def point(seq=1,sessionid=None,coords=None):
        data={'session':sessionid or session,'sequence':seq}
        if coords is not None:data['point']=coords
        return call(gps+'/'+ident+'/point',data,driver)
    def coords(lat=27.72,at=None):return {'latitude':lat,'longitude':-82.74,'accuracyMeters':15,'capturedAt':at or datetime.now(timezone.utc).isoformat()}
    check('Wrong session rejected',point(sessionid='0'*64,coords=None if simulated else coords())[0]==409)
    if simulated:check('Shared demo refuses actual device coordinates',point(coords=coords())[0]==403)
    else:
        check('Missing coordinates rejected',point()[0]==400)
        check('Impossible coordinates rejected',point(coords=coords(91))[0]==400)
        check('Old offline location rejected',point(coords=coords(at='2020-01-01T00:00:00Z'))[0]==400)
        check('Future location rejected',point(coords=coords(at='2099-01-01T00:00:00Z'))[0]==400)
    accepted=point(coords=None if simulated else coords());check('Fresh point accepted',accepted[0]==200,accepted[:3])
    check('Accurate freshness is exposed',accepted[1]['state']=='recent' and accepted[1]['point']['accuracyMeters']>0)
    check('Duplicate sequence rejected',point(coords=None if simulated else coords())[0]==409)
    check('Update rate limited separately from orders',point(2,coords=None if simulated else coords())[0]==429)
    track='/api/v1/restaurants/bistro/delivery-location'
    key={'orderId':ident,'trackingKey':request['trackingKey']}
    view=call(track,key);check('Own customer sees assigned driver position',view[0]==200 and view[1]['location']['point'] is not None)
    serialized=json.dumps(view[1]);check('Location view contains no contact info or sharing credentials',all(x not in serialized for x in (session,'session_hash','user_id','customerName','address','trackingKey')))
    check('Tracking secret required',call(track,{**key,'trackingKey':'f'*64})[0]==404)
    check('Cross-restaurant customer denied',call('/api/v1/restaurants/foreign/delivery-location',key)[0]==404)
    check('Cross-restaurant staff denied',call('/api/v1/tenants/foreign/delivery-location',token=owner)[0]==403)
    check('Other driver has no access to point',call(gps,token=second)[1]['locations']==[])
    board=call(gps,token=owner)[1];check('Manager sees batch of current restaurant only',len(board['locations'])==1 and board['locations'][0]['orderId']==ident)
    payload=sql('SELECT payload_json FROM bartide_enhanced_orders WHERE id=?',(ident,))[0][0]
    check('GPS does not enlarge order audit JSON','latitude' not in payload and 'longitude' not in payload)
    from datetime import timedelta
    old=(datetime.now(timezone.utc)-timedelta(seconds=70)).isoformat()
    stored=coords(at=old);sql('UPDATE tide_delivery_locations SET received_at=?,position_json=? WHERE order_id=?',(old,json.dumps(stored),ident))
    check('Stale point explicitly marked',call(track,key)[1]['location']['state']=='stale')
    fresh=point(2,coords=None if simulated else coords());check('Fresh update restores freshness',fresh[0]==200 and fresh[1]['state']=='recent')
    req2,rec2=place();id2=rec2['orderId']
    act(id2,'assign-driver',driverId='bistro-driver');act(id2,'acknowledge-delivery',driver)
    for state in ('accepted','preparing','ready'):act(id2,state)
    act(id2,'out_for_delivery',driver)
    check('One active customer leg per driver',start(i=id2)[0]==409)
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        resets=list(pool.map(lambda _:start(),range(2)))
    check('Concurrent restarts leave one session',all(r[0]==200 for r in resets) and sql('SELECT COUNT(*) FROM tide_delivery_locations WHERE tenant_id=?',('bistro',))[0][0]==1)
    check('Previous session invalid after restart',point(3,coords=None if simulated else coords())[0]==409)
    session=start()[1]['session'];check('Current session can upload',point(coords=None if simulated else coords())[0]==200)
    check('Manager disable succeeds',gps_settings(False)[0]==200)
    check('Disable immediately deletes coordinates',sql('SELECT COUNT(*) FROM tide_delivery_locations')[0][0]==0 and call(track,key)[1]['location'] is None)
    check('Disabled old session rejects new points',point(3,coords=None if simulated else coords())[0]==409)
    gps_settings(True);session=start()[1]['session'];point(coords=None if simulated else coords())
    act(ident,'assign-driver',driverId='bistro-second',deliveryNote='Synthetic shift change')
    check('Reassignment deletes previous location',sql('SELECT COUNT(*) FROM tide_delivery_locations WHERE order_id=?',(ident,))[0][0]==0 and call(track,key)[1]['location'] is None)
    check('Previous driver upload denied after reassignment',point(4,coords=None if simulated else coords())[0]==409)
    session=start(i=id2)[1]['session']
    call(gps+'/'+id2+'/point',{'session':session,'sequence':1,**({} if simulated else {'point':coords()})},driver)
    act(id2,'confirm-delivery',driver)
    check('Handoff immediately deletes coordinates',sql('SELECT COUNT(*) FROM tide_delivery_locations WHERE order_id=?',(id2,))[0][0]==0)
    act(ident,'acknowledge-delivery',second);act(ident,'out_for_delivery',second)
    session=start(second)[1]['session']
    check('Stop is safe to repeat',all(call(gps+'/'+ident+'/stop',{'session':session},second)[0]==200 for _ in range(2)))
    check('Stopped point removed',call(track,key)[1]['location'] is None)
    session=start(second)[1]['session']
    sql('UPDATE tide_delivery_locations SET received_at=? WHERE order_id=?',((datetime.now(timezone.utc)-timedelta(minutes=6)).isoformat(),ident))
    check('Expired location hidden before cleanup',call(track,key)[1]['location'] is None)
    check('Expired session rejects update',call(gps+'/'+ident+'/point',{'session':session,'sequence':1,**({} if simulated else {'point':coords()})},second)[0]==409)
    session=start(second)[1]['session']
    act(ident,'cancelled')
    check('Cancellation deletes sharing session',sql('SELECT COUNT(*) FROM tide_delivery_locations')[0][0]==0)
    tenants=['gps-site-'+str(n) for n in range(20)]
    for tenant in tenants:
        seed(tenant);members(tenant,False)
        alter_config(lambda c:c.update(delivery_workflow_enabled=True,delivery_location_enabled=True),tenant)
    def location_site(tenant):
        req,rec=place(tenant);i=rec['orderId'];v=0
        for action,token,extra in [('assign-driver',owner,{'driverId':tenant+'-driver'}),('acknowledge-delivery',driver,{}),('accepted',owner,{}),('preparing',owner,{}),('ready',owner,{}),('out_for_delivery',driver,{})]:
            response=call('/api/v1/tenants/'+tenant+'/ordering/orders/'+i,{'action':action,'expectedVersion':v,**extra},token)
            assert response[0]==200,response[:3];v+=1
        route='/api/v1/tenants/'+tenant+'/delivery-location'
        session=call(route+'/'+i+'/start',{'consent':True},driver)[1]['session']
        updated=call(route+'/'+i+'/point',{'session':session,'sequence':1,**({} if simulated else {'point':coords()})},driver)
        assert updated[0]==200,updated[:3]
        own=call('/api/v1/restaurants/'+tenant+'/delivery-location',{'orderId':i,'trackingKey':req['trackingKey']})
        assert own[0]==200 and own[1]['location']['orderId']==i,own[:3]
        neighbor=tenants[(tenants.index(tenant)+1)%20]
        assert call('/api/v1/restaurants/'+neighbor+'/delivery-location',{'orderId':i,'trackingKey':req['trackingKey']})[0]==404
        visible=call(route,token=driver)[1]['locations'];assert len(visible)==1 and visible[0]['orderId']==i
        assert call('/api/v1/tenants/'+neighbor+'/delivery-location/'+i+'/point',{'session':session,'sequence':2,**({} if simulated else {'point':coords()})},driver)[0]==409
        return i
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:ids=list(pool.map(location_site,tenants))
    check('20 active restaurant GPS sessions remain isolated',len(set(ids))==20)
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
    (RUN/'location-results.json').write_text(json.dumps({'provider':'postgres' if pg else 'sqlite','checks':RESULTS},indent=2),encoding='utf-8')
    print('Delivery evidence: '+str(RUN),flush=True)
print(str(len(RESULTS))+' location checks passed.',flush=True)
