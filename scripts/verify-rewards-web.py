"""Synthetic rewards browser-form contract checks against real copied API/Blazor builds.

Native HTTP form submission, cookies, antiforgery and rendered HTML are exercised.
This is not a visual browser check and does not contact external providers.
"""
import importlib.util
import shutil
import restaurant_test_support as support

support.RUN = support.ROOT / '.tools/rewards-web-verification' / support.datetime.now(support.timezone.utc).strftime('%Y%m%d-%H%M%S-%f')
support.RUN.mkdir(parents=True)
support.DB = support.RUN / 'synthetic.db'
spec = importlib.util.spec_from_file_location('management_web_helpers', support.ROOT / 'scripts/verify-management-web.py')
helpers = importlib.util.module_from_spec(spec)
spec.loader.exec_module(helpers)
from restaurant_test_support import *
web, browser, forms_for, find_form, post, saved, login, launch = (getattr(helpers, name) for name in ('web','browser','forms_for','find_form','post','saved','login','launch'))
WEB = helpers.WEB
runtime = RUN / 'runtime'
build_root = Path(os.environ.get('TIDE_TEST_BUILD_ROOT', str(ROOT))).resolve()
for project in ('TideCasa.Api', 'TideCasa.Blazor'):
    shutil.copytree(build_root / project / 'bin/Debug/net10.0', runtime / project / 'bin/Debug/net10.0', ignore=shutil.ignore_patterns("libSkiaSharp.pdb"))
support.ROOT = runtime


def count(table):
    return sql('SELECT COUNT(*) FROM '+table)[0][0]


try:
    launch('TideCasa.Api', API, {'Storage__DatabasePath':str(DB), 'Auth__Enabled':'true',
        'Auth__AllowLocalTestProvider':'true', 'Auth__SupabaseUrl':f'http://127.0.0.1:{PROVIDER.server_port}',
        'Auth__PublishableKey':KEY, 'Auth__PlatformOwnerUserId':'supabase:'+PLATFORM,
        'Ordering__PublicBaseUrl':WEB, 'ReverseProxy__KnownClientProxy':'127.0.0.1'})
    seed('bistro'); seed('foreign',BOB)
    sql('INSERT INTO bartide_enhanced_members(id,tenant_id,name,email,user_id,role,active,created_at) VALUES(?,?,?,?,?,?,1,?)',
        ('kitchen','bistro','Synthetic Kitchen','staff@example.invalid',None,'kitchen',NOW))
    launch('TideCasa.Blazor',WEB,{'Api__BaseUrl':API+'/', 'DataProtection__KeysPath':str(RUN/'keys')})
    owner_page='/workspace/bistro/rewards'; customer_page='/rewards/bistro'; staff_page='/workspace/bistro/my-rewards'
    for path in (owner_page,customer_page,staff_page):
        check('Anonymous rewards page requires sign-in '+path,web(path)[0] in (302,303,401))
    owner,customer,staff=login('alice'),login('bob'),login('staff')
    check('Customer cannot inspect owner rewards page',web(owner_page,client=customer)[0]==403)
    check('Employee cannot inspect owner rewards page',web(owner_page,client=staff)[0]==403)
    check('Other tenant owner cannot inspect rewards',web('/workspace/foreign/rewards',client=owner)[0]==403)
    forms,response=forms_for(customer,customer_page,'Customer join form renders')
    join=find_form(forms,'/join')
    check('Join form includes request and antiforgery tokens','request_id' in join['fields'] and '__RequestVerificationToken' in join['fields'])
    check('Cross-origin join is rejected',post(customer,join,{'name':'Blocked'},headers={'Origin':'https://other.example.invalid'})[0]==400)
    denied=post(customer,join,{'name':'Blocked'},remove=('__RequestVerificationToken',))
    check('Missing CSRF cannot enroll customer','notice=expired' in denied[2].get('Location','') and count('tide_loyalty_members')==0)
    saved(post(customer,join,{'name':'Synthetic <img src=x onerror=alert(1)> Customer'}),'Customer joins via native form')
    member=sql('SELECT id FROM tide_loyalty_members WHERE user_id=?',('supabase:'+BOB,))[0][0]
    saved(post(customer,join,{'name':'Synthetic <img src=x onerror=alert(1)> Customer'}),'Join duplicate submission safely replays')
    check('Native join creates only one membership',count('tide_loyalty_members')==1)

    forms,response=forms_for(owner,owner_page,'Owner reward forms render')
    check('Reward pages never cache or expose provider bearer tokens','no-store' in response[2].get('Cache-Control','') and all(token not in response[1] for token in TOKENS))
    check('Customer display name is HTML encoded','<img src=x onerror=alert(1)>' not in response[1] and '&lt;img' in response[1])
    check('All owner reward forms carry antiforgery',all('__RequestVerificationToken' in f['fields'] for f in forms))
    create=find_form(forms,'/rule',rule_id='')
    rule_values={'title':'Buy two <script>alert(1)</script>', 'reward':'Synthetic reward terms\n'+('食' * 900),
        'kind':'punch-card', 'threshold':'2', 'item_id':'dish-1','timezone':'America/New_York','active':'true'}
    check('Foreign-origin rule form rejected',post(owner,create,rule_values,headers={'Origin':'https://other.example.invalid'})[0]==400)
    check('Opaque origin rule form rejected',post(owner,create,rule_values,headers={'Origin':'null'})[0]==400)
    check('Cross-site fetch metadata rejected',post(owner,create,rule_values,headers={'Sec-Fetch-Site':'cross-site'})[0]==400)
    bad=post(owner,create,rule_values,remove=('__RequestVerificationToken',))
    check('Missing antiforgery cannot save rule','notice=expired' in bad[2].get('Location','') and count('tide_reward_rules')==0)
    bad=post(owner,create,{**rule_values,'threshold':'nine'})
    check('Nonnumeric threshold does not save','notice=invalid' in bad[2].get('Location','') and count('tide_reward_rules')==0)
    bad=post(owner,create,{**rule_values,'reward':'x'*70000})
    check('Oversized form does not mutate rules',bad[0] in (302,303,400,413) and count('tide_reward_rules')==0)
    saved(post(owner,create,rule_values),'Owner saves maximum-length multilingual terms through native form')
    rule_id=sql('SELECT id FROM tide_reward_rules')[0][0]
    check('Multiline Unicode terms stored accurately',sql('SELECT reward FROM tide_reward_rule_versions')[0][0]==rule_values['reward'])
    saved(post(owner,create,rule_values),'Rule form exact retry is idempotent')
    check('Native rule retry does not duplicate rows',count('tide_reward_rules')==1)
    forms,response=forms_for(customer,customer_page,'Customer rule progress renders')
    check('Stored reward title is safely HTML encoded','<script>alert(1)</script>' not in response[1] and '&lt;script&gt;alert(1)&lt;/script&gt;' in response[1])
    check('Owner-only controls absent from customer page','/rewards/forms/owner/' not in response[1])
    forms,response=forms_for(owner,owner_page)
    qualification=find_form(forms,'/qualify',rule_id=rule_id,source='verified-purchase')
    values={'member_id':member,'units':'2','reference':'receipt-web-1','note':'Owner verified payment\nand customer identity.'}
    declined=post(owner,qualification,values)
    check('Owner must confirm verified purchase','notice=confirm' in declined[2].get('Location','') and count('tide_reward_qualifications')==0)
    saved(post(owner,qualification,{**values,'confirmed':'true'}),'Confirmed purchase form issues earned reward')
    check('Purchase form creates exactly one reward',count('tide_reward_issued')==1)
    forms,response=forms_for(customer,customer_page)
    redeem=find_form(forms,'/request')
    saved(post(customer,redeem),'Customer requests earned reward through native form')
    saved(post(customer,redeem),'Redemption native retry preserves same request')
    check('Redemption retry does not consume twice',sql('SELECT state FROM tide_reward_issued')[0][0]=='requested' and count('tide_reward_issued')==1)
    forms,response=forms_for(owner,owner_page)
    fulfillment=find_form(forms,'/resolve')
    saved(post(owner,fulfillment,{'resolution':'fulfill','note':'Verified customer and gave reward.','confirmed':'true'}),'Owner fulfills pending reward through native form')
    check('Fulfilled reward state persisted',sql('SELECT state FROM tide_reward_issued')[0][0]=='fulfilled')
    forms,response=forms_for(customer,customer_page)
    check('Consumed reward no longer presents redemption action',not any(f['action'].endswith('/request') for f in forms) and '>Used<' in response[1])

    forms,response=forms_for(owner,owner_page)
    employee=find_form(forms,'/employee')
    saved(post(owner,employee,{'member_id':'kitchen','delta':'25','reference':'team-web-award','reason':'Helpful <script>test</script> training.','confirmed':'true'}),'Owner grants employee points through form')
    staff_forms,response=forms_for(staff,staff_page,'Staff own reward wallet renders')
    check('Direct staff reward visit binds verified identity',sql("SELECT user_id FROM bartide_enhanced_members WHERE id='kitchen'")[0][0]=='supabase:'+STAFF)
    check('Staff wallet shows its separate balance','25 points' in response[1] and '<script>test</script>' not in response[1] and '&lt;script&gt;test&lt;/script&gt;' in response[1])
    check('Staff wallet exposes no owner mutation forms',not any('/rewards/forms/owner/' in f['action'] for f in staff_forms))
    check('Customer cannot access employee reward page',web(staff_page,client=customer)[0]==403)
    staff_customer_forms,response=forms_for(staff,customer_page,'Employee customer enrollment remains separate')
    check('Employee points never create customer membership',any(f['action'].endswith('/join') for f in staff_customer_forms))
    # Use the employee's own CSRF token to prove server authorization, not a CSRF rejection.
    staff_csrf=staff_customer_forms[0]['fields']['__RequestVerificationToken']
    forged={**employee['fields'],'__RequestVerificationToken':staff_csrf,'request_id':str(uuid.uuid4()),'delta':'999','reference':'forged-valid-reference','reason':'Attempt to self-award.','confirmed':'true'}
    denied=web(employee['action'],forged,staff,{'Origin':WEB})
    check('Employee-crafted owner form cannot self-award',count('tide_employee_reward_ledger')==1 and 'do not have access' in urllib.parse.unquote(denied[2].get('Location','')))
    sql("UPDATE bartide_enhanced_members SET active=0 WHERE id='kitchen'")
    check('Revoked staff cookie loses employee wallet access immediately',web(staff_page,client=staff)[0]==403)
    check('Reward web fixture maintains foreign keys',sql('PRAGMA foreign_key_check')==[])
finally:
    for proc in PROCESSES:
        if proc.poll() is None:
            proc.terminate()
            try:proc.wait(timeout=20)
            except subprocess.TimeoutExpired:proc.kill();proc.wait(timeout=10)
    PROVIDER.shutdown();PROVIDER.server_close()
    for log in LOGS:log.close()
    (RUN/'results.json').write_text(json.dumps(RESULTS,indent=2),encoding='utf-8')
    print('Rewards web evidence: '+str(RUN),flush=True)
print(str(len(RESULTS))+' rewards web checks passed.',flush=True)
