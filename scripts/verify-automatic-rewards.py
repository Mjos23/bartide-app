"""Automatic loyalty through the real API, synthetic identities, and an isolated database."""
import restaurant_test_support as s
from pathlib import Path
import json,uuid,concurrent.futures
s.RUN=s.ROOT/'.tools/pricing-rewards-20260925/automatic-rewards';s.RUN.mkdir(parents=True,exist_ok=True)
s.DB=s.RUN/'synthetic.db'
if s.DB.exists(): raise RuntimeError('Use a fresh fixture directory.')
from restaurant_test_support import *

def req(**fields):return {'requestId':str(uuid.uuid4()),**fields}
try:
    launch();seed('bistro');seed('foreign',BOB)
    tokens={p:call('/api/v1/auth/signin',{'email':p+'@example.invalid','password':PASSWORD})[1]['accessToken'] for p in ('alice','bob','staff')}
    owner,customer,other=tokens['alice'],tokens['bob'],tokens['staff']
    management='/api/v1/tenants/bistro/rewards';wallet='/api/v1/restaurants/bistro/rewards'
    member=call(wallet+'/join',req(name='Automatic rewards customer'),customer)[1]['id']
    call(wallet+'/join',req(name='Other customer'),other)
    def rule(kind,threshold,item=None):
        data=req(expectedVersion=-1,title='Automatic '+kind,reward='Synthetic reward',kind=kind,threshold=threshold,itemId=item,timeZoneId='America/New_York',active=True)
        result=call(management+'/rules',data,owner);check('Create '+kind,result[0]==200,result[:2]);return result[1]['id'],data
    punch,punch_data=rule('punch-card',2,'dish-1');visit,_=rule('monthly-visits',2);points,_=rule('points',20)
    def create(quantity=2,fulfillment='dine-in',item='dish-1',tenant='bistro'):
        selection={'items':[{'itemId':item,'quantity':quantity}],'fulfillment':fulfillment,'paymentMethod':'staff','tipPercent':25}
        if fulfillment=='dine-in':selection['tableToken']=TABLE if tenant=='bistro' else FOREIGN_TABLE
        request=order_request(selection,tenant=tenant);response=submit(request,tenant)
        check('Place synthetic '+fulfillment+' order',response[0]==201,response[:2])
        return response[1]['orderId'],{'orderId':response[1]['orderId'],'trackingKey':request['trackingKey']}
    def paid(ident):
        version=sql('SELECT version FROM bartide_enhanced_orders WHERE id=?',(ident,))[0][0]
        result=call('/api/v1/tenants/bistro/ordering/orders/'+ident,{'expectedVersion':version,'action':'mark-paid','paymentCollected':True},owner)
        check('Normal payment workflow records payment',result[0]==200,result[:2])
    def w():
        result=call(wallet,token=customer);check('Wallet loads automatically',result[0]==200,result[:2]);return result[1]
    def progress(value,rule):return next(p['qualifiedUnits'] for p in value['progress'] if p['ruleId']==rule)
    def payment(ident,status):
        payload=json.loads(sql('SELECT payload_json FROM bartide_enhanced_orders WHERE id=?',(ident,))[0][0]);payload['payment_status']=status
        sql('UPDATE bartide_enhanced_orders SET payload_json=? WHERE id=?',(json.dumps(payload),ident))
    ident,receipt=create()
    check('Anonymous cannot connect rewards',call(wallet+'/orders',receipt)[0]==401)
    check('Wrong receipt secret rejected',call(wallet+'/orders',{**receipt,'trackingKey':'0'*64},customer)[0]==404)
    check('Cross-tenant receipt rejected',call('/api/v1/restaurants/foreign/rewards/orders',receipt,customer)[0]==404)
    check('Private receipt connects authenticated customer',call(wallet+'/orders',receipt,customer)[0]==200)
    check('Unpaid order earns no points or punches',w()['points']==0 and progress(w(),punch)==0)
    check('Another account cannot claim linked receipt',call(wallet+'/orders',receipt,other)[0]==409)
    paid(ident);value=w()
    check('Two paid units automatically issue one punch reward',progress(value,punch)==2 and len([r for r in value['rewards'] if r['ruleId']==punch and r['state']=='available'])==1)
    check('20.02 food dollars earn 20 points, excluding tax and tip',value['points']==20)
    check('Paid dine-in order automatically earns a visit',progress(value,visit)==1)
    check('Other member never receives these points',call(wallet,token=other)[1]['points']==0)
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:responses=list(pool.map(lambda _:call(wallet,token=customer),range(4)))
    check('Concurrent wallet refreshes do not duplicate points or rewards',all(r[0]==200 and r[1]['points']==20 for r in responses))
    check('Exact receipt retries are harmless',call(wallet+'/orders',receipt,customer)[0]==200 and w()['points']==20)
    owner_view=call(management,token=owner)[1]
    check('Owner sees current automatically populated points',next(m['points'] for m in owner_view['members'] if m['id']==member)==20)
    second,receipt2=create(1);call(wallet+'/orders',receipt2,customer);paid(second);value=w()
    check('Another same-day order earns purchases but not a second visit',value['points']==30 and progress(value,punch)==3 and progress(value,visit)==1)
    third,receipt3=create(1,'pickup');call(wallet+'/orders',receipt3,customer);paid(third);value=w()
    check('Pickup earns points and purchases without a dine-in visit',value['points']==40 and progress(value,punch)==4 and progress(value,visit)==1)
    foreign,foreign_receipt=create(1,tenant='foreign')
    check('Foreign order never attaches to this tenant',call(wallet+'/orders',foreign_receipt,customer)[0]==404)
    # The independent payment fixture models authoritative processor refund state.
    payment(second,'refunded');value=w()
    check('Refund reverses purchase points and punches',value['points']==30 and progress(value,punch)==3)
    check('Other paid dine-in order continues to support the day visit',progress(value,visit)==1)
    payment(ident,'partially_refunded');value=w()
    check('Partial refund conservatively removes that order rewards',value['points']==10 and progress(value,punch)==1 and progress(value,visit)==0)
    payment(ident,'paid');value=w()
    check('Authoritative failed refund restoration counts once',value['points']==30 and progress(value,punch)==3 and progress(value,visit)==1)
    reserve=call(wallet+'/points-redemptions',req(ruleId=points),customer)
    check('Automatically earned points can reserve configured reward',reserve[0]==200,reserve[:2])
    payment(ident,'refunded');value=w()
    check('Refund cancels unsupported pending points reward and restores reservation',value['points']==10 and next(r['state'] for r in value['rewards'] if r['id']==reserve[1]['id'])=='cancelled')
    check('Refunded points cannot fund another redemption',call(wallet+'/points-redemptions',req(ruleId=points),customer)[0]==409)
    # Historical and future orders are not retroactive qualification claims.
    old,old_receipt=create(1);call(wallet+'/orders',old_receipt,customer);paid(old)
    sql("UPDATE bartide_enhanced_orders SET created_at='2020-01-01T00:00:00Z' WHERE id=?",(old,))
    check('Orders before membership earn nothing',w()['points']==10)
    paused={**punch_data,'requestId':str(uuid.uuid4()),'expectedVersion':0,'active':False}
    check('Pause rule preserves existing version',call(management+'/rules/'+punch,paused,owner)[0]==200)
    fourth,fourth_receipt=create(1);call(wallet+'/orders',fourth_receipt,customer);paid(fourth)
    before=sql('SELECT COUNT(*) FROM tide_reward_qualifications WHERE source_reference=? AND rule_id=?',(fourth,punch))[0][0]
    value=w();check('Paused purchase rule adds no punches while spending points continue',value['points']==20 and before==0 and sql('SELECT COUNT(*) FROM tide_reward_qualifications WHERE source_reference=? AND rule_id=?',(fourth,punch))[0][0]==0)
    sql("UPDATE bartide_enhanced_orders SET status='cancelled' WHERE id=?",(fourth,));check('Cancelled order earns no spending points',w()['points']==10)
    before=w();PROCESSES[-1].terminate();PROCESSES[-1].wait(timeout=20);launch();after=w()
    check('Automatic rewards survive restart without duplicate ledger entries',before==after)
finally:
    for proc in PROCESSES:
        if proc.poll() is None:proc.terminate();proc.wait(timeout=20)
    PROVIDER.shutdown();PROVIDER.server_close()
    for log in LOGS:log.close()
    (RUN/'results.json').write_text(json.dumps(RESULTS,indent=2),encoding='utf-8')
print(str(len(RESULTS))+' automatic rewards checks passed.')
