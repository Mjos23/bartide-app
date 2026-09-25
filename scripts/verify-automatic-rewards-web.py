"""Authenticated browser-to-API reward linking with real cookies and antiforgery."""
import restaurant_test_support as s
import importlib.util,json,uuid,time,sys
s.RUN=s.ROOT/'.tools/pricing-rewards-20260925/automatic-rewards-web';s.RUN.mkdir(exist_ok=True,parents=True);s.DB=s.RUN/'synthetic.db'
if s.DB.exists():raise RuntimeError('Use a fresh fixture directory.')
spec=importlib.util.spec_from_file_location('reward_web',s.ROOT/'scripts/verify-management-web.py');w=importlib.util.module_from_spec(spec);spec.loader.exec_module(w)
from restaurant_test_support import *
try:
    launch();seed('bistro')
    w.launch('TideCasa.Blazor',w.WEB,{'Api__BaseUrl':API+'/','DataProtection__KeysPath':str(RUN/'keys')})
    owner=call('/api/v1/auth/signin',{'email':'alice@example.invalid','password':PASSWORD})[1]['accessToken']
    customer=call('/api/v1/auth/signin',{'email':'bob@example.invalid','password':PASSWORD})[1]['accessToken']
    wallet='/api/v1/restaurants/bistro/rewards';management='/api/v1/tenants/bistro/rewards'
    call(wallet+'/join',{'requestId':str(uuid.uuid4()),'name':'Browser Test'},customer)
    call(management+'/rules',{'requestId':str(uuid.uuid4()),'expectedVersion':-1,'title':'Buy two dishes','reward':'One test dish','kind':'punch-card','threshold':2,'itemId':'dish-1','timeZoneId':'America/New_York','active':True},owner)
    browser=w.login('bob');base='/rewards/orders/bistro'
    check('Anonymous linking session is denied without redirect',w.web(base+'/session')[0]==401)
    session_response=w.web(base+'/session',client=browser);check('Authenticated linking session is private',session_response[0]==200 and 'no-store' in session_response[2].get('Cache-Control',''))
    session=json.loads(session_response[1]);request=order_request();placed=submit(request);ident=placed[1]['orderId']
    form={'action':base+'/link','fields':{'__RequestVerificationToken':session['token'],'user':session['user'],'order_id':ident,'tracking_key':request['trackingKey']}}
    check('Cross-site automatic linking rejected',w.post(browser,form,{},headers={'Origin':'https://other.example.invalid'})[0]==400)
    check('Missing CSRF cannot link an order',w.post(browser,form,{},remove=['__RequestVerificationToken'])[0]==400)
    check('Changed login identity cannot reuse a linking session',w.post(browser,form,{'user':'supabase:'+ALICE})[0]==400)
    result=w.post(browser,form,{})
    check('Signed-in browser receipt links without a staff form',result[0]==200 and json.loads(result[1])['linked'],result[:2])
    check('Linking retries are idempotent',w.post(browser,form,{})[0]==200)
    version=sql('SELECT version FROM bartide_enhanced_orders WHERE id=?',(ident,))[0][0]
    call('/api/v1/tenants/bistro/ordering/orders/'+ident,{'expectedVersion':version,'action':'mark-paid','paymentCollected':True},owner)
    response=w.web('/rewards/bistro',client=browser)
    check('Rewards page automatically shows earned spending points',response[0]==200 and '10 reward points' in response[1] and 'Staff verify eligible purchases' not in response[1])
    check('Ordering page exposes rewards account path and automatic binding script', '/rewards/bistro' in w.web('/order/bistro',client=browser)[1] and 'order-rewards.js' in w.web('/order/bistro',client=browser)[1])
    (RUN/'preview.json').write_text(json.dumps({'web':w.WEB,'api':API,'database':str(DB),'stopFile':str(RUN/'stop'),'customerEmail':'bob@example.invalid','syntheticPassword':PASSWORD}),encoding='utf-8')
    if '--hold' in sys.argv:
        print('PREVIEW '+w.WEB,flush=True)
        while not (RUN/'stop').exists():time.sleep(1)
finally:
    for proc in PROCESSES:
        if proc.poll() is None:proc.terminate();proc.wait(timeout=20)
    PROVIDER.shutdown();PROVIDER.server_close()
    for log in LOGS:log.close()
    (RUN/'results.json').write_text(json.dumps(RESULTS,indent=2),encoding='utf-8')
print(str(len(RESULTS))+' automatic rewards browser form checks passed.')
