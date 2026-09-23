"""Real API, web cookies, storage and order privacy; synthetic loopback identities only."""
import importlib.util
import sys
import restaurant_test_support as s
from restaurant_test_support import *

spec = importlib.util.spec_from_file_location('web_helpers', ROOT/'scripts/verify-management-web.py')
h = importlib.util.module_from_spec(spec); spec.loader.exec_module(h)
pg = None; schema = None; env = {}
try:
    if '--postgres' in sys.argv:
        sys.path.insert(0,str(ROOT/'.tools/postgres-python'))
        import psycopg
        from psycopg import sql as pgsql
        cfg=json.loads((ROOT/'.tools/postgresql-17-test/fixture.json').read_text(encoding='utf-8-sig'))
        assert cfg['host']=='127.0.0.1'
        creds={k:cfg[k] for k in ('host','port','user','password')};creds.update(dbname=cfg['database'],sslmode='disable')
        schema='tide_customer_'+uuid.uuid4().hex[:16];pg=psycopg
        with pg.connect(**creds,autocommit=True) as db: db.execute(pgsql.SQL('CREATE SCHEMA {}').format(pgsql.Identifier(schema)))
        def query(statement,values=()):
            with pg.connect(**creds,options='-c search_path='+schema) as db:
                cur=db.execute(statement.replace('?','%s'),values);return cur.fetchall() if cur.description else []
        sql=s.sql=query
        def quote(v):return '"'+str(v).replace('"','""')+'"'
        env={'Storage__Provider':'PostgreSql','Storage__PostgresSchema':schema,'Storage__CreatePostgresSchema':'false',
             'ConnectionStrings__Application':';'.join(n+'='+quote(creds[k]) for n,k in [('Host','host'),('Port','port'),('Database','dbname'),('Username','user'),('Password','password')])+';SSL Mode=Disable;Maximum Pool Size=8'}
    s.launch(env); seed('bistro');seed('foreign',PLATFORM)
    tokens={p:call('/api/v1/auth/signin',{'email':p+'@example.invalid','password':PASSWORD})[1]['accessToken'] for p in ('alice','bob','staff')}
    root='/api/v1/restaurants/bistro/my-orders'
    check('Anonymous cannot list customer orders',call(root)[0]==401)
    request=order_request(); receipt=submit(request)[1]; proof={'orderId':receipt['orderId'],'trackingKey':request['trackingKey']}
    check('Guest orders are not matched by name or phone',call(root,token=tokens['bob'])[1]['orders']==[])
    check('Anonymous cannot save a receipt',call(root,proof)[0]==401)
    check('Wrong receipt key fails',call(root,{**proof,'trackingKey':'0'*64},tokens['bob'])[0]==404)
    check('Foreign restaurant rejects receipt proof',call('/api/v1/restaurants/foreign/my-orders',proof,tokens['bob'])[0]==404)
    check('Customer saves their verified receipt',call(root,proof,tokens['bob'])[0]==200)
    check('Saving again is idempotent',call(root,proof,tokens['bob'])[0]==200)
    check('Another account cannot take an already saved receipt',call(root,proof,tokens['staff'])[0]==409)
    mine=call(root,token=tokens['bob'])
    check('Customer sees exactly their receipt',mine[0]==200 and [o['orderId'] for o in mine[1]['orders']]==[receipt['orderId']])
    check('Customer response hides contact details and private keys',receipt_private(mine[1]))
    check('Owner identity does not inherit customer order history',call(root,token=tokens['alice'])[1]['orders']==[])
    check('Other customer has an empty list',call(root,token=tokens['staff'])[1]['orders']==[])
    check('Other restaurant has an empty list',call('/api/v1/restaurants/foreign/my-orders',token=tokens['bob'])[1]['orders']==[])
    operations='/api/v1/tenants/bistro/ordering/operations'
    entry=next(o for o in call(operations,token=tokens['alice'])[1]['orders'] if o['order']['receipt']['orderId']==receipt['orderId'])
    action='/api/v1/tenants/bistro/ordering/orders/'+receipt['orderId']
    # Existing staff transition route; status updates must preserve receipt ownership.
    for state in ('accepted','preparing'):
        entry=next(o for o in call(operations,token=tokens['alice'])[1]['orders'] if o['order']['receipt']['orderId']==receipt['orderId'])
        response=call(action,{'expectedVersion':entry['order']['version'],'action':state},tokens['alice'])
        check('Staff moves order to '+state,response[0]==200,response[:2])
    check('Customer reads latest cooking progress',call(root,token=tokens['bob'])[1]['orders'][0]['status']=='preparing')
    h.launch('TideCasa.Blazor',h.WEB,{'Api__BaseUrl':API+'/','DataProtection__KeysPath':str(RUN/'keys')})
    customer=h.login('bob');other=h.login('staff')
    web=lambda path,body=None,client=customer,headers=None:h.web(path,body,client,{'Origin':h.WEB,**(headers or {})} if body is not None else headers)
    check('Customer home requires sign-in',h.web('/customer/bistro')[0] in (302,303,401))
    page=web('/customer/bistro')
    check('Customer home contains cooking progress and rewards',page[0]==200 and 'Cooking' in page[1] and '/rewards/bistro' in page[1] and receipt['number'] in page[1])
    check('Other account cannot see receipt in HTML',receipt['number'] not in web('/customer/bistro',client=other)[1])
    check('Status response is small and authenticated',web('/customer-orders/bistro/status')[0]==200 and 'Cooking' in web('/customer-orders/bistro/status')[1])
    session=json.loads(web('/customer-orders/bistro/session')[1])
    fields={'__RequestVerificationToken':session['token'],'user':session['user'],'order_id':proof['orderId'],'tracking_key':proof['trackingKey']}
    check('Signed-in native save succeeds',web('/customer-orders/bistro/save',fields)[0]==200)
    check('Save rejects missing antiforgery',web('/customer-orders/bistro/save',{k:v for k,v in fields.items() if k!='__RequestVerificationToken'})[0]==400)
    check('Save rejects changed identity',web('/customer-orders/bistro/save',{**fields,'user':'someone-else'})[0]==400)
    check('Save rejects foreign origin',web('/customer-orders/bistro/save',fields,headers={'Origin':'https://other.invalid'})[0]==400)
    cache=web('/customer/bistro')[2].get('Cache-Control','')
    check('Customer page is not cached','no-store' in cache,cache)
    check('Menu has customer and staff entrances',all(x in web('/order/bistro')[1] for x in ('My orders &amp; rewards','Staff login','Get the App')))
    check('Customer entrance has signup and sign-in',all(x in web('/access/bistro/customer')[1] for x in ('/signup?return_to=','/signin?return_to=')))
    check('Staff entrance explains invitation requirement','same email' in web('/access/bistro/staff')[1])
    check('Customer cannot operate the restaurant',web('/workspace/bistro/operations')[0]==403)
    check('Invalid access audience unavailable',web('/access/bistro/admin')[0]==404)
finally:
    for proc in PROCESSES:
        if proc.poll() is None:
            proc.terminate()
            try:proc.wait(20)
            except subprocess.TimeoutExpired:proc.kill();proc.wait(10)
    PROVIDER.shutdown();PROVIDER.server_close()
    for log in LOGS:log.close()
    if pg and schema:
        with pg.connect(**creds,autocommit=True) as db:db.execute(pgsql.SQL('DROP SCHEMA {} CASCADE').format(pgsql.Identifier(schema)))
    (RUN/'customer-orders-results.json').write_text(json.dumps(RESULTS,indent=2))
    print('Evidence: '+str(RUN),flush=True)
print(str(len(RESULTS))+' customer order checks passed.',flush=True)
