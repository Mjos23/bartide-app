"""Real API and native Blazor form onboarding with synthetic accounts only."""
import importlib.util
import shutil
import restaurant_test_support as support

support.RUN = support.ROOT / '.tools/workspace-verification' / support.datetime.now(support.timezone.utc).strftime('%Y%m%d-%H%M%S-%f')
support.RUN.mkdir(parents=True)
support.DB = support.RUN / 'synthetic.db'
spec = importlib.util.spec_from_file_location('web_helpers', support.ROOT / 'scripts/verify-management-web.py')
helpers = importlib.util.module_from_spec(spec)
spec.loader.exec_module(helpers)
from restaurant_test_support import *
web, forms_for, find_form, post, login, launch = (getattr(helpers, n) for n in ('web','forms_for','find_form','post','login','launch'))
WEB = helpers.WEB
source = Path(os.environ.get('TIDE_TEST_BUILD_ROOT', str(ROOT)))
runtime = RUN / 'runtime'
for project in ('TideCasa.Api','TideCasa.Blazor'):
    shutil.copytree(source / project / 'bin/Debug/net10.0', runtime / project / 'bin/Debug/net10.0', ignore=shutil.ignore_patterns("libSkiaSharp.pdb"))
support.ROOT = runtime

try:
    launch('TideCasa.Api', API, {'Storage__DatabasePath':str(DB), 'Auth__Enabled':'true',
        'Auth__AllowLocalTestProvider':'true', 'Auth__SupabaseUrl':f'http://127.0.0.1:{PROVIDER.server_port}',
        'Auth__PublishableKey':KEY, 'Auth__PlatformOwnerUserId':'supabase:'+PLATFORM,
        'Notifications__Mode':'disabled', 'ReverseProxy__KnownClientProxy':'127.0.0.1'})
    launch('TideCasa.Blazor', WEB, {'Api__BaseUrl':API+'/', 'DataProtection__KeysPath':str(RUN/'keys')})
    path = '/api/v1/account/workspace'
    body = {'requestId':str(uuid.uuid4()),'businessName':'Synthetic <script>Restaurant</script>', 'contactName':'Alice',
        'area':'Example City','plan':'restaurant','acknowledged':True,'notes':'Food and drinks\n'+('食'*1900)}
    check('Anonymous registration requires verified authentication',call(path,body)[0]==401)
    check('Anonymous start page requires sign-in',web('/start/restaurant')[0] in (302,303,401))
    start='/start/restaurant?appStores=true&referralCode=COAST-20'
    challenge=web(start)
    returned=urllib.parse.parse_qs(urllib.parse.urlparse(challenge[2].get('Location','')).query).get('return_to',[''])[0]
    check('Sign-in challenge preserves only checkout choices',returned==start,returned)
    gate=helpers.browser()
    gate_forms,_=forms_for(gate,'/signin?return_to='+urllib.parse.quote(start),'Checkout sign-in page renders')
    gate_form=find_form(gate_forms,'/auth/session/signin')
    check('Sign-in form preserves store choice and referral',gate_form['fields'].get('return_to')==start)
    signed=post(gate,gate_form,{'email':'staff@example.invalid','password':PASSWORD})
    check('Sign-in returns to exact reviewed checkout choices',signed[2].get('Location')==start)
    for unsafe,expected in [('/start/restaurant?referralCode=https%3A%2F%2Fevil.invalid&appStores=true','/start/restaurant?appStores=true'),('/start/restaurant?referralCode=A&referralCode=B&return_to=//evil.invalid','/start/restaurant'),('/account?referralCode=COAST-20','/account'),('/start/restaurant?appStores=true&referralCode=COAST-20#//evil.invalid',start)]:
        forms,_=forms_for(helpers.browser(),'/signin?return_to='+urllib.parse.quote(unsafe))
        check('Unsafe or unrelated redirect parameters are discarded',find_form(forms,'/auth/session/signin')['fields']['return_to']==expected)
    a = call('/api/v1/auth/signin', {'email':'alice@example.invalid','password':PASSWORD})
    check('Synthetic API sign-in succeeds',a[0]==200,a[:2]); token=a[1]['accessToken']
    for changes in ({'acknowledged':False},{'plan':'free-premium'},{'businessName':''},{'website':'javascript:alert(1)'},{'website':'https://user:pass@example.invalid'},{'requestId':'../tenant'}):
        check('Invalid registration rejected '+str(changes),call(path,{**body,**changes},token)[0]==400)
    check('Invalid requests create no workspace',sql('SELECT COUNT(*) FROM bartide_customers')[0][0]==0)
    with concurrent.futures.ThreadPoolExecutor(max_workers=6) as pool:
        responses=list(pool.map(lambda _:call(path,body,token),range(6)))
    check('Concurrent registration returns one workspace',all(r[0] in (200,201) for r in responses),[r[:2] for r in responses])
    check('Exactly one concurrent registration is created',sum(r[0]==201 for r in responses)==1)
    row=sql('SELECT id,slug,status,user_id,vertical,requested_plan,setup_notes,menu_json FROM bartide_customers')[0]
    check('Only one server-owned draft exists',len(sql('SELECT id FROM bartide_customers'))==1 and row[2:6]==('draft','supabase:'+ALICE,'bartide','enhanced'))
    check('Draft preserves multilingual multiline notes',row[6]==body['notes'])
    check('Draft starts with empty menu and one category',len(json.loads(row[7])['items'])==0 and len(json.loads(row[7])['categories'])==1)
    check('No service purchase or paid-feature activation on registration',sql('SELECT COUNT(*) FROM tide_service_orders')[0][0]==0 and sql('SELECT COUNT(*) FROM bartide_enhanced_configs')[0][0]==0)
    replay=call(path,{**body,'requestId':str(uuid.uuid4()),'businessName':'Do not overwrite existing','plan':'business'},token)
    check('Existing account continues original workspace without mutation',replay[0]==200 and replay[1]['tenantId']==row[0] and sql('SELECT name FROM bartide_customers')[0][0]==body['businessName'])
    access=call('/api/v1/account',token=token)
    check('Owner association exposes explicit ownership',access[1]['workspaces'][0]['isOwner'] is True)
    owner=login('alice'); new=login('bob')
    page=web('/start/restaurant?appStores=true&referralCode=COAST-20',client=owner)
    check('Existing restaurant continues to onboarding and preserves store choice',page[0]==200 and '/onboarding?appStores=true' in page[1] and '/account/workspace' not in page[1])
    check('Existing workspace name safely encoded','<script>Restaurant</script>' not in page[1] and '&lt;script&gt;' in page[1])
    check('Existing workspace retains referral through package review','referralCode=COAST-20' in page[1])
    forms,response=forms_for(new,'/start/business?appStores=true&referralCode=COAST-20','New business registration form renders')
    form=find_form(forms,'/account/workspace')
    form['fields']={k:('' if v is None else v) for k,v in form['fields'].items()}
    check('Form is private and includes CSRF token','no-store' in response[2].get('Cache-Control','') and '__RequestVerificationToken' in form['fields'])
    values={'businessName':'Synthetic Business','contactName':'Bob','area':'Example City','notes':'Planning\nDetails','acknowledged':'true'}
    check('Foreign origin cannot register business',post(new,form,values,headers={'Origin':'https://other.example.invalid'})[0]==400)
    bad=post(new,form,values,remove=('__RequestVerificationToken',))
    check('Missing antiforgery cannot register business','notice=expired' in bad[2].get('Location','') and sql('SELECT COUNT(*) FROM bartide_customers')[0][0]==1)
    result=post(new,form,values)
    check('Native form creates business and redirects to package review',result[0] in (302,303) and '/billing?appStores=true' in result[2].get('Location',''),result[:2])
    check('New business retains referral through package review','referralCode=COAST-20' in result[2].get('Location',''))
    check('Business vertical stored independently of restaurant',sql('SELECT vertical,status FROM bartide_customers WHERE user_id=?',('supabase:'+BOB,))[0]==('tide-casa','draft'))
    check('Native form retry does not duplicate workspace',post(new,form,values)[0] in (302,303) and sql('SELECT COUNT(*) FROM bartide_customers')[0][0]==2)
    sql("UPDATE bartide_customers SET status='paused' WHERE user_id=?",('supabase:'+ALICE,))
    account=web('/account',client=owner)
    check('Paused owner keeps billing access link','/billing' in account[1] and '/operations' not in account[1])
    check('Registration maintains all foreign keys',sql('PRAGMA foreign_key_check')==[])
finally:
    for proc in PROCESSES:
        if proc.poll() is None:
            proc.terminate()
            try:proc.wait(timeout=20)
            except subprocess.TimeoutExpired:proc.kill();proc.wait(timeout=10)
    PROVIDER.shutdown();PROVIDER.server_close()
    for log in LOGS:log.close()
    (RUN/'results.json').write_text(json.dumps(RESULTS,indent=2),encoding='utf-8')
    print('Workspace evidence: '+str(RUN),flush=True)
print(str(len(RESULTS))+' workspace checks passed.',flush=True)
