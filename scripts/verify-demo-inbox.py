"""Verify demo inquiry privacy, owner forms, and bounded notification delivery.

Runs copied API/Blazor binaries, a private SQLite fixture, synthetic verified users,
and an in-process loopback Resend substitute. Never sends email or contacts a real
provider. Direct SQL changes below are explicit clock/crash/history fixtures.
"""
import importlib.util
import shutil
from datetime import timedelta
import restaurant_test_support as support

support.RUN=support.ROOT/'.tools/demo-inbox-verification'/support.datetime.now(support.timezone.utc).strftime('%Y%m%d-%H%M%S-%f')
support.RUN.mkdir(parents=True)
support.DB=support.RUN/'synthetic.db'
spec=importlib.util.spec_from_file_location('demo_web_helpers',support.ROOT/'scripts/verify-management-web.py')
helpers=importlib.util.module_from_spec(spec);spec.loader.exec_module(helpers)
from restaurant_test_support import *
web,browser,forms_for,find_form,post,login=(getattr(helpers,n) for n in ('web','browser','forms_for','find_form','post','login'))
WEB=helpers.WEB
runtime=RUN/'runtime'
build_root=Path(os.environ.get('TIDE_TEST_BUILD_ROOT',str(ROOT))).resolve()
for project in ('TideCasa.Api','TideCasa.Blazor'):
    shutil.copytree(build_root/project/'bin/Debug/net10.0',runtime/project/'bin/Debug/net10.0', ignore=shutil.ignore_patterns("libSkiaSharp.pdb"))
support.ROOT=runtime
MAIL_LOCK=threading.Lock(); MAIL_REQUESTS=[]; MAIL_DELIVERIES={}; MAIL_PLANS={}; MAIL_GATES={}
MAIL_KEY='synthetic-local-notification-key'


class FakeResend(BaseHTTPRequestHandler):
    def log_message(self,*args):pass
    def do_POST(self):
        if self.headers.get('Transfer-Encoding','').lower()=='chunked':
            chunks=[]
            while True:
                size=int(self.rfile.readline().split(b';',1)[0].strip(),16)
                if not size:
                    while self.rfile.readline().strip():pass
                    break
                chunks.append(self.rfile.read(size));self.rfile.read(2)
            raw=b''.join(chunks)
        else:raw=self.rfile.read(int(self.headers.get('Content-Length','0')))
        key=self.headers.get('Idempotency-Key','')
        ident=key.removeprefix('demo-request/')
        with MAIL_LOCK:
            MAIL_REQUESTS.append({'id':ident,'key':key,'raw':raw.decode(),'path':self.path,
                                  'authorized':self.headers.get('Authorization')=='Bearer '+MAIL_KEY})
            plan=MAIL_PLANS.get(ident,[])
            mode=plan.pop(0) if plan else 200
            if mode in (200,'drop','hold'):
                previous=MAIL_DELIVERIES.get(key)
                if previous and previous['raw']!=raw:
                    mode=409
                elif not previous:
                    MAIL_DELIVERIES[key]={'id':str(uuid.uuid5(uuid.NAMESPACE_URL,key)), 'raw':raw}
        if mode=='hold':
            gate=MAIL_GATES[ident];gate['entered'].set();gate['release'].wait(timeout=60)
            mode=200
        if mode=='drop':
            try:self.connection.shutdown(socket.SHUT_RDWR)
            except OSError:pass
            self.connection.close();return
        status=int(mode)
        body={'id':MAIL_DELIVERIES[key]['id']} if status==200 else {'error':'Synthetic provider failure'}
        try:
            self.send_response(status);self.send_header('Content-Type','application/json');self.end_headers();self.wfile.write(json.dumps(body).encode())
        except (BrokenPipeError,ConnectionResetError,ConnectionAbortedError,OSError):pass


MAIL=ThreadingHTTPServer(('127.0.0.1',0),FakeResend)
threading.Thread(target=MAIL.serve_forever,daemon=True).start()


def stamp(offset=0):return (datetime.now(timezone.utc)+timedelta(seconds=offset)).isoformat()
def wait_for(name,predicate,timeout=12):
    deadline=time.monotonic()+timeout
    while time.monotonic()<deadline:
        result=predicate()
        if result:return result
        time.sleep(.1)
    raise AssertionError('Timed out: '+name)
def state(ident):
    rows=sql('SELECT state,attempts,first_attempt_at,payload_json,lease_id,provider_id,error_code FROM tide_demo_notifications WHERE request_id=?',(ident,))
    return rows[0] if rows else None
def requests(ident):
    with MAIL_LOCK:return [r.copy() for r in MAIL_REQUESTS if r['id']==ident]
def notification_config(**overrides):
    return {'Notifications__Mode':'local-test','Notifications__AllowLocalProvider':'true',
        'Notifications__LocalEndpoint':f'http://127.0.0.1:{MAIL.server_port}/emails',
        'Notifications__ResendApiKey':MAIL_KEY,'Notifications__From':'Tide Casa <hello@auth.tide.casa>',
        'Notifications__To':'hello@tide.casa','Notifications__PublicBaseUrl':'https://tide.casa',
        'Notifications__MaxAlertsPerDay':'40',**overrides}
def launch_app(project='TideCasa.Api',address=API,extra=None,label='api',expect_failure=None):
    env=os.environ.copy()
    for name in list(env):
        if any(word in name.upper() for word in ('STRIPE','RESEND','SUPABASE','CLOUDFLARE','AUTH__','ORDERING__','STORAGE__','API__','DATAPROTECTION__','NOTIFICATIONS__','MEDIA__','WEBPUSH__','SERVICEBILLING__','MERCHANTPAYMENTS__')):env.pop(name)
    env.update({'ASPNETCORE_ENVIRONMENT':'Development','ASPNETCORE_URLS':address,
        'Storage__DatabasePath':str(DB),'Auth__Enabled':'true','Auth__AllowLocalTestProvider':'true',
        'Auth__SupabaseUrl':f'http://127.0.0.1:{PROVIDER.server_port}','Auth__PublishableKey':KEY,
        'Auth__PlatformOwnerUserId':'supabase:'+PLATFORM,'Stripe__CheckoutEnabled':'false',
        'Stripe__InvoicesEnabled':'false','Ordering__PublicBaseUrl':WEB,
        'WebPush__Enabled':'false','ServiceBilling__CheckoutEnabled':'false','ServiceBilling__LiveEnabled':'false',
        'MerchantPayments__CheckoutEnabled':'false','MerchantPayments__OnboardingEnabled':'false',
        'ReverseProxy__KnownClientProxy':'127.0.0.1',**(extra or {})})
    env['DOTNET_ENVIRONMENT']=env['ASPNETCORE_ENVIRONMENT']
    logfile=RUN/(label+'.log');log=logfile.open('w',encoding='utf-8');LOGS.append(log)
    proc=subprocess.Popen([str(SDK),str(runtime/project/'bin/Debug/net10.0'/(project+'.dll'))],cwd=runtime/project,
        env=env,stdout=log,stderr=subprocess.STDOUT,creationflags=subprocess.CREATE_NO_WINDOW if os.name=='nt' else 0)
    PROCESSES.append(proc)
    if expect_failure:
        try:proc.wait(timeout=25)
        except subprocess.TimeoutExpired:raise AssertionError('Unsafe configuration unexpectedly stayed running: '+label)
        check(label+' rejected at startup',proc.returncode!=0 and expect_failure in logfile.read_text(encoding='utf-8',errors='replace'))
        return proc
    deadline=time.monotonic()+60
    opener=urllib.request.build_opener(urllib.request.ProxyHandler({}))
    while time.monotonic()<deadline:
        if proc.poll() is not None:raise RuntimeError('Fixture stopped: '+str(logfile))
        try:
            with opener.open(address+'/health',timeout=2) as response:
                if response.status==200:return proc
        except (OSError,urllib.error.URLError):pass
        time.sleep(.25)
    raise TimeoutError('Fixture startup '+label)
def stop(proc):
    if proc.poll() is None:
        proc.terminate()
        try:proc.wait(timeout=15)
        except subprocess.TimeoutExpired:proc.kill();proc.wait(timeout=10)
def inquiry(**changes):
    ident=str(uuid.uuid4())
    return {'id':ident,'name':'Synthetic <script>alert(1)</script> Lead','business':'Private Test Business '+ident[:6],
        'email':'synthetic-'+ident+'@example.invalid','phone':'+1 305 555 0100','city':'Test City',
        'businessType':'bartide','preferredTimes':'Tuesday afternoon','timeZone':'Eastern Time',
        'goals':'PRIVATE-INQUIRY-DETAILS\nOwner follow-up needed.','contactWebsite':'',**changes}
def submit_inquiry(body=None):
    body=body or inquiry();response=call('/api/v1/demo-requests',body)
    check('Synthetic demo request saved',response[0]==201,response[:3]);return body


try:
    api=launch_app(label='api-disabled-default')
    seed('bistro')
    tokens={}
    for person in ('alice','bob','staff','platform'):
        response=call('/api/v1/auth/signin',{'email':person+'@example.invalid','password':PASSWORD})
        check('Verified synthetic login '+person,response[0]==200);tokens[person]=response[1]['accessToken']
    inbox='/api/v1/owner/demo-requests';platform=tokens['platform']
    check('Anonymous cannot read private demo inbox',call(inbox)[0]==401)
    for person in ('alice','bob','staff'):
        check('Nonplatform role cannot read inquiry PII '+person,call(inbox,token=tokens[person])[0]==403)
    first=submit_inquiry();ident=first['id']
    check('Anonymous success receipt excludes contact details',all(value not in call('/api/v1/demo-requests',first)[2] for value in (first['email'],first['business'],first['goals'])))
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        repeats=list(pool.map(lambda _:call('/api/v1/demo-requests',first),range(4)))
    check('Concurrent duplicate inquiry creates one request/outbox row',all(r[0]==200 for r in repeats) and sql('SELECT COUNT(*) FROM demo_requests')[0][0]==1 and sql('SELECT COUNT(*) FROM tide_demo_notifications')[0][0]==1)
    check('Same inquiry ID with altered details conflicts',call('/api/v1/demo-requests',{**first,'goals':'Changed'})[0]==409)
    time.sleep(.6)
    check('Default notifications remain disabled with durable pending work',state(ident)[0]=='pending' and state(ident)[1]==0 and requests(ident)==[])
    contents=call(inbox,token=platform)
    check('Platform owner sees private inquiry and disconnected notice',contents[0]==200 and contents[1]['notificationsEnabled'] is False and contents[1]['inquiries'][0]['request']['email']==first['email'])
    check('Restaurant owner cannot change inquiry status',call(inbox+'/'+ident,{'expectedVersion':0,'status':'closed','note':'Unauthorized'},tokens['alice'])[0]==403)
    for bad in ({'status':'emailed'},{'expectedVersion':-1},{'note':'x'*2001},{'note':'control\u0001'}):
        check('Invalid inbox update rejected '+next(iter(bad)),call(inbox+'/'+ident,{'expectedVersion':0,'status':'contacted','note':'Private note',**bad},platform)[0]==400)
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        responses=list(pool.map(lambda status:call(inbox+'/'+ident,{'expectedVersion':0,'status':status,'note':'Synthetic private note'},platform),['contacted','closed']))
    check('Concurrent owner updates use compare-and-swap',sorted(r[0] for r in responses)==[200,409])
    check('Owner update actor is durably audited',sql('SELECT version,updated_by FROM tide_demo_inbox WHERE request_id=?',(ident,))[0]==(1,'supabase:'+PLATFORM))
    check('Status change never confirms appointment publicly',call('/api/v1/demo-requests',first)[1]['status']=='requested')
    sql("CREATE TRIGGER synthetic_outbox_failure BEFORE INSERT ON tide_demo_notifications BEGIN SELECT RAISE(ABORT,'synthetic outbox failure'); END")
    failed=inquiry();response=call('/api/v1/demo-requests',failed)
    check('Outbox failure rolls back inquiry atomically',response[0]>=500 and sql('SELECT COUNT(*) FROM demo_requests WHERE id=?',(failed['id'],))[0][0]==0)
    sql('DROP TRIGGER synthetic_outbox_failure')

    launch_app('TideCasa.Blazor',WEB,{'Api__BaseUrl':API+'/','DataProtection__KeysPath':str(RUN/'keys')},'web')
    page='/owner/demo-requests'
    check('Anonymous owner inbox page requires sign-in',web(page)[0] in (302,303,401))
    owner_web=login('platform');restaurant_web=login('alice')
    check('Restaurant account cannot load platform inbox page',web(page,client=restaurant_web)[0]==403)
    forms,response=forms_for(owner_web,page,'Platform owner native inbox renders')
    check('Inquiry page is private and safely HTML encoded','no-store' in response[2].get('Cache-Control','') and '<script>alert(1)</script>' not in response[1] and '&lt;script&gt;alert(1)&lt;/script&gt;' in response[1])
    form=find_form(forms,'/'+ident)
    before=sql('SELECT version FROM tide_demo_inbox WHERE request_id=?',(ident,))[0][0]
    check('Cross-origin owner inbox mutation rejected',post(owner_web,form,{'status':'scheduled'},headers={'Origin':'https://other.example.invalid'})[0]==400)
    no_csrf=post(owner_web,form,{'status':'scheduled'},remove=('__RequestVerificationToken',))
    check('Missing antiforgery cannot change inbox','notice=expired' in no_csrf[2].get('Location','') and sql('SELECT version FROM tide_demo_inbox WHERE request_id=?',(ident,))[0][0]==before)
    response=post(owner_web,form,{'status':'scheduled','note':'Owner agreed on a time.\n'+('私'*1900)})
    check('Owner saves maximum Unicode follow-up via native form','notice=saved' in response[2].get('Location','') and sql('SELECT status FROM demo_requests WHERE id=?',(ident,))[0][0]=='scheduled')
    check('Stale native update is rejected','notice=changed' in post(owner_web,form,{'status':'closed'})[2].get('Location',''))
    check('Inbox-only changes do not send notifications',requests(ident)==[] and state(ident)[1]==0)

    # Sending and provider failures, using a local fake service with stable idempotency.
    stop(api)
    MAIL_PLANS[ident]=[500,429,200]
    api=launch_app(extra=notification_config(),label='api-sender')
    wait_for('retry then accepted',lambda:state(ident)[0]=='sent')
    history=requests(ident)
    check('500 and429 responses retry under same idempotency key',len(history)==3 and len({r['key'] for r in history})==1 and history[0]['key']=='demo-request/'+ident)
    check('Retries retain identical durable payload bytes',len({r['raw'] for r in history})==1 and state(ident)[3]==history[0]['raw'])
    check('One logical provider delivery despite retries',len([key for key in MAIL_DELIVERIES if key=='demo-request/'+ident])==1 and state(ident)[1]==3 and state(ident)[5] is not None)
    payload=json.loads(history[0]['raw'])
    check('Alert goes only to approved owner address through authenticated local provider',payload['to']==['hello@tide.casa'] and all(r['authorized'] and r['path']=='/emails' for r in history))
    check('Email alert excludes contact PII and includes private inbox link',set(payload)=={'from','to','subject','text'} and all(v not in payload['text'] for v in (first['email'],first['phone'],first['name'],first['business'],'PRIVATE-INQUIRY-DETAILS')) and 'https://tide.casa/owner/demo-requests' in payload['text'])
    check('Sent state means provider acceptance, not appointment confirmation',call(inbox,token=platform)[1]['inquiries'][0]['notificationState']=='sent' and 'No appointment has been confirmed' in payload['text'])
    late=call('/api/v1/demo-requests',first);time.sleep(.6)
    check('Duplicate form after notification sent cannot send another email',late[0]==200 and len(requests(ident))==3)

    uncertain=inquiry();MAIL_PLANS[uncertain['id']]=['drop',200];submit_inquiry(uncertain)
    wait_for('uncertain response recovery',lambda:state(uncertain['id'])[0]=='sent')
    history=requests(uncertain['id'])
    check('Accepted-but-lost response reuses idempotency and payload',len(history)==2 and history[0]['raw']==history[1]['raw'] and history[0]['key']==history[1]['key'])
    check('Uncertain response cannot create duplicate accepted messages',len([k for k in MAIL_DELIVERIES if k=='demo-request/'+uncertain['id']])==1)

    crash=inquiry();MAIL_PLANS[crash['id']]=['hold',200]
    gate={'entered':threading.Event(),'release':threading.Event()};MAIL_GATES[crash['id']]=gate
    submit_inquiry(crash);check('Provider accepted held notification',gate['entered'].wait(timeout=10))
    check('Sending work is durably leased before provider response',state(crash['id'])[0]=='sending' and state(crash['id'])[4] is not None)
    frozen_payload=state(crash['id'])[3]
    stop(api);gate['release'].set()
    sql('UPDATE tide_demo_notifications SET lease_until=? WHERE request_id=?',(stamp(-5),crash['id']))
    api=launch_app(extra=notification_config(Notifications__PublicBaseUrl='https://changed.example.invalid'),label='api-crash-recovery')
    wait_for('expired lease recovery',lambda:state(crash['id'])[0]=='sent')
    history=requests(crash['id'])
    check('Expired sending lease resumes after process restart',len(history)==2 and state(crash['id'])[1]==2)
    check('Configuration change cannot alter previously attempted mail payload',state(crash['id'])[3]==frozen_payload and history[0]['raw']==history[1]['raw'] and 'changed.example.invalid' not in history[1]['raw'])

    rejected=inquiry();MAIL_PLANS[rejected['id']]=[400];submit_inquiry(rejected)
    wait_for('permanent provider failure review',lambda:state(rejected['id'])[0]=='review')
    check('Permanent provider rejection stops for review',len(requests(rejected['id']))==1 and state(rejected['id'])[6]=='provider_400')

    # Prepare historical/due work with sending explicitly disabled.
    stop(api);api=launch_app(label='api-prepare-history')
    old=submit_inquiry();exhausted=submit_inquiry();fresh=submit_inquiry();retry=submit_inquiry()
    sql("UPDATE tide_demo_notifications SET state='pending',attempts=1,first_attempt_at=?,next_attempt_at=? WHERE request_id=?",(stamp(-21*3600),stamp(-1),old['id']))
    sql("UPDATE tide_demo_notifications SET state='pending',attempts=12,first_attempt_at=?,next_attempt_at=? WHERE request_id=?",(stamp(-3600),stamp(-1),exhausted['id']))
    # Current-day first attempts already exceed the cap of one. Fresh is older
    # than retry so a naive oldest-row selection would block the due retry.
    sql("UPDATE tide_demo_notifications SET state='pending',attempts=1,first_attempt_at=?,next_attempt_at=?,created_at=? WHERE request_id=?",(stamp(-30),stamp(-1),stamp(-1),retry['id']))
    sql('UPDATE tide_demo_notifications SET created_at=?,next_attempt_at=? WHERE request_id=?',(stamp(-60),stamp(-1),fresh['id']))
    stop(api);api=launch_app(extra=notification_config(Notifications__MaxAlertsPerDay='1'),label='api-capped')
    wait_for('20-hour review',lambda:state(old['id'])[0]=='review')
    wait_for('attempt limit review',lambda:state(exhausted['id'])[0]=='review')
    wait_for('retry beyond new-alert cap',lambda:state(retry['id'])[0]=='sent')
    check('Twenty-hour uncertain send stops without provider attempt',requests(old['id'])==[] and state(old['id'])[1]==1)
    check('Twelve-attempt ceiling stops without provider attempt',requests(exhausted['id'])==[] and state(exhausted['id'])[1]==12)
    check('Daily cap holds new alerts pending without discarding inquiries',state(fresh['id'])[0]=='pending' and state(fresh['id'])[1]==0 and requests(fresh['id'])==[])
    check('Daily cap still permits existing idempotent retry',len(requests(retry['id']))==1 and state(retry['id'])[1]==2)
    check('Owner inbox reports manual-review delivery state',next(i for i in call(inbox,token=platform)[1]['inquiries'] if i['request']['id']==old['id'])['notificationState']=='review')
    check('Review transition keeps inquiry delivery mirror consistent',sql('SELECT notification_state FROM demo_requests WHERE id=?',(old['id'],))[0][0]=='review')
    # Move consumed day slots into yesterday, as a clock-rollover fixture.
    sql('UPDATE tide_demo_notifications SET first_attempt_at=? WHERE first_attempt_at IS NOT NULL',(stamp(-26*3600),))
    wait_for('daily quota reset',lambda:state(fresh['id'])[0]=='sent')
    check('Pending request sends when a new-day quota slot opens',len(requests(fresh['id']))==1)
    stop(api)

    # Production and external-host local-test mode must fail before any network call.
    before_mail=len(MAIL_REQUESTS)
    for label,extra in [('production-local-provider',{'ASPNETCORE_ENVIRONMENT':'Production','Auth__Enabled':'false'}),
        ('unapproved-local-provider',{'Notifications__AllowLocalProvider':'false'}),
        ('nonloopback-local-provider',{'Notifications__LocalEndpoint':'http://example.invalid/emails'}),
        ('credentialed-local-provider',{'Notifications__LocalEndpoint':f'http://name:secret@127.0.0.1:{MAIL.server_port}/emails'}),
        ('query-local-provider',{'Notifications__LocalEndpoint':f'http://127.0.0.1:{MAIL.server_port}/emails?token=test'})]:
        launch_app(address='http://127.0.0.1:'+str(port()),extra=notification_config(**extra),label=label,expect_failure='Local notifications require an explicit Development loopback provider')
    check('Rejected notification configurations made no provider requests',len(MAIL_REQUESTS)==before_mail)
    check('Demo inbox/outbox foreign keys remain valid',sql('PRAGMA foreign_key_check')==[])
finally:
    for gate in MAIL_GATES.values():gate['release'].set()
    for proc in PROCESSES:stop(proc)
    PROVIDER.shutdown();PROVIDER.server_close();MAIL.shutdown();MAIL.server_close()
    for log in LOGS:log.close()
    (RUN/'results.json').write_text(json.dumps(RESULTS,indent=2),encoding='utf-8')
    # Captures contain synthetic records and a boolean auth result, never keys.
    (RUN/'synthetic-mail-evidence.json').write_text(json.dumps(MAIL_REQUESTS,indent=2),encoding='utf-8')
    print('Demo inbox evidence: '+str(RUN),flush=True)
print(str(len(RESULTS))+' demo inbox checks passed.',flush=True)
