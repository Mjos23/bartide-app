"""Real isolated API/web/SQLite; only the local identity provider is a fixture. Never sends email."""
import sys
import shutil
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'scripts'))
import restaurant_test_support as s
import importlib.util
s.RUN = ROOT / '.tools/email-tracking-checks' / s.datetime.now(s.timezone.utc).strftime('%Y%m%d-%H%M%S-%f')
s.RUN.mkdir(parents=True)
s.DB = s.RUN / 'synthetic.db'
spec = importlib.util.spec_from_file_location('email_web_helpers', ROOT / 'scripts/verify-management-web.py')
h = importlib.util.module_from_spec(spec); spec.loader.exec_module(h)
from restaurant_test_support import *
WEB = h.WEB
completed = False

def launch(project, address):
    env = os.environ.copy()
    for name in list(env):
        if any(word in name.upper() for word in ('STRIPE','RESEND','SUPABASE','AUTH__','ORDERING__','STORAGE__','API__','DATAPROTECTION__','NOTIFICATIONS__','MEDIA__','WEBPUSH__','SERVICEBILLING__','MERCHANTPAYMENTS__','PUBLICDEMO__')): env.pop(name)
    env.update({'ASPNETCORE_ENVIRONMENT':'Development','DOTNET_ENVIRONMENT':'Development','ASPNETCORE_URLS':address,
                'Storage__Provider':'Sqlite','Storage__DatabasePath':str(DB),'Auth__Enabled':'true','Auth__AllowLocalTestProvider':'true',
                'Auth__SupabaseUrl':f'http://127.0.0.1:{PROVIDER.server_port}','Auth__PublishableKey':KEY,
                'Auth__PlatformOwnerUserId':'supabase:'+PLATFORM,'Api__BaseUrl':API+'/',
                'DataProtection__Provider':'FileSystem','DataProtection__KeysPath':str(RUN/'keys'),
                'ReverseProxy__KnownClientProxy':'127.0.0.1','Notifications__Mode':'disabled','WebPush__Enabled':'false',
                'Stripe__CheckoutEnabled':'false','Stripe__InvoicesEnabled':'false','ServiceBilling__CheckoutEnabled':'false',
                'MerchantPayments__CheckoutEnabled':'false','MerchantPayments__OnboardingEnabled':'false',
                'Media__Provider':'disabled','PublicDemo__Enabled':'false','DOTNET_PROCESSOR_COUNT':'2'})
    log=(RUN/(project+'.log')).open('w',encoding='utf-8'); LOGS.append(log)
    runtime=RUN/'runtime'/project
    shutil.copytree(ROOT/project/'bin/Debug/net10.0',runtime)
    proc=subprocess.Popen([str(SDK),str(runtime/(project+'.dll'))],cwd=ROOT/project,env=env,
        stdin=subprocess.DEVNULL,stdout=log,stderr=subprocess.STDOUT,creationflags=subprocess.CREATE_NO_WINDOW if os.name=='nt' else 0)
    PROCESSES.append(proc)
    until=time.monotonic()+120
    while time.monotonic()<until:
        if proc.poll() is not None: raise RuntimeError('Fixture stopped: '+project)
        try:
            with urllib.request.urlopen(address+'/health',timeout=3) as response:
                if response.status==200:return proc
        except (OSError,urllib.error.URLError):pass
        time.sleep(.3)
    raise TimeoutError(project)

def click(client,path,origin=WEB):
    request=urllib.request.Request(WEB+'/email-events/click',data=json.dumps({'path':path}).encode(),headers={'Origin':origin,'Content-Type':'application/json'})
    try: response=client.open(request,timeout=20)
    except urllib.error.HTTPError as error: response=error
    with response:return response.status

try:
    launch('TideCasa.Api',API); launch('TideCasa.Blazor',WEB)
    owner=call('/api/v1/auth/signin',{'email':'platform@example.invalid','password':PASSWORD})[1]['accessToken']
    ordinary=call('/api/v1/auth/signin',{'email':'alice@example.invalid','password':PASSWORD})[1]['accessToken']
    base='/api/v1/owner/email-campaigns'
    check('Anonymous report denied',call(base)[0]==401)
    check('Authenticated nonowner report denied',call(base,token=ordinary)[0]==403)
    check('Anonymous campaign create denied',call(base,{'requestKey':str(uuid.uuid4()),'name':'No','isTest':True})[0]==401)
    browser=h.login('platform'); other=h.login('alice')
    page=h.web('/owner/email-results?tests=true',client=browser)
    check('Owner dashboard renders with no-store',page[0]==200 and 'no-store' in page[2].get('Cache-Control',''))
    check('Gmail unavailable metrics disclosed',all(text in page[1] for text in ('unavailable','TEST DATA','No visits recorded yet')))
    check('Nonowner dashboard denied',h.web('/owner/email-results',client=other)[0]==403)
    check('Owner account includes results link','/owner/email-results' in h.web('/account',client=browser)[1])
    visitor=h.browser()
    landing='/?campaign=7ae11d6ce57d4c1fb881ab6f84259bd0'
    response=h.web(landing,client=visitor)
    check('Tracked email lands on normal home page',response[0]==200 and '/email-tracking.js' in response[1])
    check('Protected tracking cookie HttpOnly', 'HttpOnly' in response[2].get('Set-Cookie','') or 'httponly' in response[2].get('Set-Cookie','').lower())
    check('Tracking landing cannot be cached','no-store' in response[2].get('Cache-Control',''))
    check('Demo and purchase click endpoints accept cookie',click(visitor,'demo')==204 and click(visitor,'purchase')==204)
    report=call(base+'?tests=true',token=owner)[1]
    c=report['campaigns'][0]
    check('Actual web session reaches persisted report',c['visits']==1 and c['demoClicks']==1 and c['purchaseClicks']==1)
    check('Actual web pathway preserves choices',any(p['path']=='Email → Home → Book a demo → Purchase page' for p in report['pathways']))
    h.web(landing,client=visitor)
    check('Repeated landing reuses 30-minute visit',call(base+'?tests=true',token=owner)[1]['campaigns'][0]['visits']==1)
    check('Cross-site click rejected',click(visitor,'contact','https://attacker.invalid')==400)
    check('Arbitrary event path rejected',click(visitor,'https://attacker.invalid')==400)
    click(h.browser(),'demo')
    check('No cookie does not fabricate a visit',call(base+'?tests=true',token=owner)[1]['campaigns'][0]['visits']==1)
    privacy=h.web(landing,client=h.browser(),headers={'Sec-GPC':'1'})
    check('Privacy optout creates no cookie or visit','TideCasa.EmailVisit' not in privacy[2].get('Set-Cookie','') and call(base+'?tests=true',token=owner)[1]['campaigns'][0]['visits']==1)
    forms,_=h.forms_for(browser,'/owner/email-results')
    form=h.find_form(forms,'/owner/email-results/create')
    check('Create without CSRF rejected','notice=expired' in h.post(browser,form,{'name':'Rejected','mode':'production'},remove=('__RequestVerificationToken',))[2].get('Location',''))
    check('Create from cross-site rejected',h.post(browser,form,{'name':'Rejected','mode':'production'},headers={'Origin':'https://attacker.invalid'})[0]==400)
    saved=h.post(browser,form,{'name':'September BarTide outreach','mode':'production'})
    check('Native owner form creates campaign',saved[0]==302 and 'notice=saved' in saved[2].get('Location',''))
    prod=call(base,token=owner)[1]
    check('Production excludes test visits',len(prod['campaigns'])==1 and prod['campaigns'][0]['visits']==0 and not prod['pathways'])
    final=h.web('/owner/email-results?tests=true',client=browser)
    check('Rendered dashboard displays recorded click journey','Book a demo' in final[1] and 'Purchase page' in final[1] and 'Email' in final[1])
    completed=True
    metadata={'web':WEB,'api':API,'stop':str(RUN/'stop-fixture'),'ownerEmail':'platform@example.invalid','password':PASSWORD,'testLanding':WEB+landing}
    (RUN/'browser-fixture.json').write_text(json.dumps(metadata,indent=2),encoding='utf-8')
    (RUN/'results.json').write_text(json.dumps(RESULTS,indent=2),encoding='utf-8')
    (RUN/'completion.json').write_text(json.dumps({'completed':completed,'checks':len(RESULTS)}),encoding='utf-8')
    print('BROWSER FIXTURE '+WEB+'/owner/email-results?tests=true; metadata: '+str(RUN/'browser-fixture.json'),flush=True)
    if '--hold' in sys.argv:
        until=time.monotonic()+1800
        while time.monotonic()<until and not (RUN/'stop-fixture').exists():time.sleep(.5)
finally:
    for process in reversed(PROCESSES):
        if process.poll() is None:
            process.terminate()
            try:process.wait(timeout=15)
            except subprocess.TimeoutExpired:process.kill();process.wait(timeout=10)
    PROVIDER.shutdown()
    for log in LOGS:log.close()
    (RUN/'results.json').write_text(json.dumps(RESULTS,indent=2),encoding='utf-8')
    (RUN/'completion.json').write_text(json.dumps({'completed':completed,'checks':len(RESULTS)}),encoding='utf-8')
    print('Evidence: '+str(RUN),flush=True)

