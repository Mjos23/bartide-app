"""Synthetic real-API verification of menu editing and restaurant operations."""
from restaurant_test_support import *

try:
    launch()
    for args in [('bistro',), ('foreign', BOB), ('paused', ALICE, 'paused'), ('draft', ALICE, 'draft'), ('basic', ALICE, 'active', False)]:
        seed(*args)
    for member, person, role in [('kitchen', STAFF, 'kitchen'), ('driver', BOB, 'driver')]:
        sql('INSERT INTO bartide_enhanced_members(id,tenant_id,name,email,user_id,role,active,created_at) VALUES(?,?,?,?,?,?,1,?)',
            (member, 'bistro', member.title(), member+'@example.invalid', 'supabase:'+person, role, NOW))
    tokens = {}
    for person in ('alice','bob','staff','platform'):
        response = call('/api/v1/auth/signin', {'email':person+'@example.invalid','password':PASSWORD})
        check('Synthetic login '+person, response[0]==200)
        tokens[person]=response[1]['accessToken']
    owner=tokens['alice']; kitchen=tokens['staff']; driver=tokens['bob']
    root='/api/v1/tenants/bistro/'
    for suffix in ['menu','ordering/settings','ordering/operations']:
        check('Anonymous cannot read '+suffix,call(root+suffix)[0]==401)
    for token in [kitchen,driver]:
        check('Staff cannot edit menu',call(root+'menu',token=token)[0]==403)
        check('Staff cannot edit ordering settings',call(root+'ordering/settings',token=token)[0]==403)
    check('Other tenant owner cannot view menu',call('/api/v1/tenants/foreign/menu',token=owner)[0]==403)
    check('Paused menu cannot be edited',call('/api/v1/tenants/paused/menu',token=owner)[0]==404)
    check('Draft owner can prepare menu',call('/api/v1/tenants/draft/menu',token=owner)[0]==200)
    check('Basic unenrolled owner can edit menu',call('/api/v1/tenants/basic/menu',token=owner)[0]==200)
    check('Basic unenrolled owner cannot manage enhanced settings',call('/api/v1/tenants/basic/ordering/settings',token=owner)[0]==403)
    editor=call(root+'menu',token=owner)[1]
    check('Editor includes stored availability, not temporary blocked flag',next(i for i in editor['items'] if i['id']=='dish-22')['available'])
    profile={'name':'Updated Test Bistro','area':'Local test','tagline':'Fresh food','hours':'Monday\nFriday','website':'https://example.invalid','serviceNote':'Ask staff.'}
    response=call(root+'menu/profile',{'expectedVersion':editor['version'],'profile':profile},owner)
    check('Owner can save profile with version increment',response[0]==200 and response[1]['version']==1,response[:3])
    check('Public menu reflects business name',call('/api/v1/restaurants/bistro/menu')[1]['name']==profile['name'])
    check('Stale profile cannot overwrite changes',call(root+'menu/profile',{'expectedVersion':0,'profile':profile},owner)[0]==409)
    for website in ['http://example.invalid','javascript:alert(1)','https://name:secret@example.invalid']:
        bad={**profile,'website':website}
        check('Profile rejects unsafe website '+website.split(':')[0],call(root+'menu/profile',{'expectedVersion':1,'profile':bad},owner)[0]==400)
    current=call(root+'menu',token=owner)[1]
    response=call(root+'menu/categories',{'expectedVersion':current['version'],'category':{'id':'new-category','name':'New category'}},owner)
    check('Owner adds category',response[0]==200 and len(response[1]['categories'])==2)
    item={'id':'new-item','categoryId':'new-category','name':'New dish','description':'Notes\nSecond line','priceCents':1250,'priceLabel':None,'available':True}
    response=call(root+'menu/items',{'expectedVersion':response[1]['version'],'item':item},owner)
    check('Owner adds priced item with multiline notes',response[0]==200,response[:3])
    version=response[1]['version']
    for bad in [{**item,'priceCents':-1},{**item,'priceCents':1000001},{**item,'priceCents':None,'priceLabel':''},{**item,'categoryId':'missing'}]:
        check('Invalid menu item does not save',call(root+'menu/items',{'expectedVersion':version,'item':bad},owner)[0]==400)
    check('Cannot remove populated category',call(root+'menu/categories/new-category/remove',{'expectedVersion':version},owner)[0]==400)
    quoted, before=valid_quote()
    original=next(i for i in call(root+'menu',token=owner)[1]['items'] if i['id']=='dish-1')
    response=call(root+'menu/items',{'expectedVersion':version,'item':{**original,'priceCents':1200}},owner)
    check('Menu price update succeeds',response[0]==200)
    request=order_request(quoted);request['quoteFingerprint']=before['fingerprint']
    check('Edited prices invalidate guest old quote',submit(request)[0]==409)
    raw=json.loads(sql('SELECT menu_json FROM bartide_customers WHERE id=?',('bistro',))[0][0])
    check('Menu mutation preserves unknown properties',raw['private_test_marker']==PRIVATE and raw['venue']['private_test_marker']==PRIVATE)
    version=response[1]['version']
    response=call(root+'menu/items/new-item/remove',{'expectedVersion':version},owner)
    check('Owner removes item',response[0]==200 and all(i['id']!='new-item' for i in response[1]['items']))
    response=call(root+'menu/categories/new-category/remove',{'expectedVersion':response[1]['version']},owner)
    check('Owner removes empty category',response[0]==200)
    check('Last category cannot be removed',call(root+'menu/categories/food/remove',{'expectedVersion':response[1]['version']},owner)[0]==400)
    settings=call(root+'ordering/settings',token=owner)[1]
    change={**settings,'expectedVersion':settings['version'],'deliveryFeeCents':500,'tipsEnabled':False,'pickupInstructions':'Collect here.\nAsk the host.'};change.pop('version')
    response=call(root+'ordering/settings',change,owner)
    check('Owner updates delivery and tip settings',response[0]==200 and not response[1]['tipsEnabled'])
    check('Stale settings rejected',call(root+'ordering/settings',change,owner)[0]==409)
    change['expectedVersion']=response[1]['version']
    for bad in [{**change,'taxBasisPoints':None},{**change,'taxBasisPoints':2501},{**change,'deliveryZips':[]},{**change,'deliveryZips':['33101','33101']},{**change,'deliveryCapacity':0}]:
        check('Invalid ordering configuration rejected',call(root+'ordering/settings',bad,owner)[0]==400)
    raw=json.loads(sql('SELECT settings_json FROM bartide_enhanced_configs WHERE tenant_id=?',('bistro',))[0][0])
    check('Settings preserve table and unknown configuration',len(raw['checkout']['tables'])==3 and raw['preserve_future_config']['nested']==[1,True,'keep'])
    change['tipsEnabled']=True
    check('Tips may be enabled again',call(root+'ordering/settings',change,owner)[0]==200)

    dine,quoted=valid_quote(fulfillment='dine-in',tableToken=TABLE,tipPercent=20)
    request=order_request(dine); receipt=submit(request)[1]; ident=receipt['orderId']
    actions=root+'ordering/orders/'+ident
    check('Kitchen sees incoming order',any(i['order']['receipt']['orderId']==ident for i in call(root+'ordering/operations',token=kitchen)[1]['orders']))
    check('Driver cannot see unassigned orders',call(root+'ordering/operations',token=driver)[1]['orders']==[])
    check('Driver cannot mutate unassigned order',call(actions,{'expectedVersion':0,'action':'accepted'},driver)[0]==403)
    check('Kitchen cannot record payment',call(actions,{'expectedVersion':0,'action':'mark-paid','paymentCollected':True},kitchen)[0]==409)
    check('Order cannot skip status',call(actions,{'expectedVersion':0,'action':'ready'},owner)[0]==409)
    for version,status in enumerate(['accepted','preparing','ready']):
        check('Kitchen transition '+status,call(actions,{'expectedVersion':version,'action':status},kitchen)[0]==200)
    check('Unpaid order cannot be completed',call(actions,{'expectedVersion':3,'action':'completed'},owner)[0]==409)
    check('Payment recording requires explicit confirmation',call(actions,{'expectedVersion':3,'action':'mark-paid'},owner)[0]==400)
    check('Owner records collected payment',call(actions,{'expectedVersion':3,'action':'mark-paid','paymentCollected':True},owner)[0]==200)
    check('Stale payment recording conflicts',call(actions,{'expectedVersion':3,'action':'mark-paid','paymentCollected':True},owner)[0]==409)
    check('Paid order cannot be cancelled without refund',call(actions,{'expectedVersion':4,'action':'cancelled'},owner)[0]==409)
    check('Paid dine-in may complete',call(actions,{'expectedVersion':4,'action':'completed'},kitchen)[0]==200)
    response=call('/api/v1/restaurants/bistro/track',{'orderId':ident,'trackingKey':request['trackingKey']})
    check('Guest tracking reflects fulfillment and paid-in-person status privately',response[1]['status']=='completed' and response[1]['paymentStatus']=='paid_in_person' and receipt_private(response[1]))
    stored=json.loads(sql('SELECT payload_json FROM bartide_enhanced_orders WHERE id=?',(ident,))[0][0])
    check('Operations preserve quote and tip with actor audit',stored['tip_cents']==240 and stored['history'][-1]['actor_id']=='supabase:'+STAFF)

    delivery,_=valid_quote(fulfillment='delivery',deliveryZip='33101',items=[{'itemId':'dish-1','quantity':2}])
    request=order_request(delivery);ident=submit(request)[1]['orderId'];actions=root+'ordering/orders/'+ident
    check('Foreign driver assignment rejected',call(actions,{'expectedVersion':0,'action':'assign-driver','driverId':'missing'},owner)[0]==400)
    check('Owner assigns active local driver',call(actions,{'expectedVersion':0,'action':'assign-driver','driverId':'driver'},owner)[0]==200)
    check('Assigned driver sees only its delivery',len(call(root+'ordering/operations',token=driver)[1]['orders'])==1)
    for version,status in enumerate(['accepted','preparing','ready'],1):
        check('Delivery kitchen transition '+status,call(actions,{'expectedVersion':version,'action':status},kitchen)[0]==200)
    check('Assigned driver dispatches',call(actions,{'expectedVersion':4,'action':'out_for_delivery'},driver)[0]==200)
    check('Unpaid delivery cannot complete',call(actions,{'expectedVersion':5,'action':'completed'},driver)[0]==409)
    check('Owner records driver collected full payment',call(actions,{'expectedVersion':5,'action':'mark-paid','paymentCollected':True},owner)[0]==200)
    check('Assigned driver completes paid delivery',call(actions,{'expectedVersion':6,'action':'completed'},driver)[0]==200)
    sql("UPDATE bartide_enhanced_members SET active=0 WHERE id='driver'")
    check('Revoked driver loses access immediately',call(root+'ordering/operations',token=driver)[0]==403)
    request=order_request();ident=submit(request)[1]['orderId'];actions=root+'ordering/orders/'+ident
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        responses=list(pool.map(lambda action:call(actions,{'expectedVersion':0,'action':action},owner),['accepted','cancelled']))
    check('Concurrent order mutations commit exactly once',sorted(r[0] for r in responses)==[200,409])
    check('Feature migration has recorded first script',sql('SELECT COUNT(*) FROM tide_feature_migrations')[0][0]>=1)
    check('Database relationships remain valid',sql('PRAGMA foreign_key_check')==[])
finally:
    for proc in PROCESSES:
        if proc.poll() is None:
            proc.terminate()
            try:proc.wait(timeout=20)
            except subprocess.TimeoutExpired:proc.kill();proc.wait(timeout=10)
    PROVIDER.shutdown();PROVIDER.server_close()
    for log in LOGS:log.close()
    (RUN/'results.json').write_text(json.dumps(RESULTS,indent=2),encoding='utf-8')
    print('Management evidence: '+str(RUN),flush=True)
print(str(len(RESULTS))+' management checks passed.',flush=True)
