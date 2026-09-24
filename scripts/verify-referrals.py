"""Verify retired recruitment routes and existing referral members on API/SSR fixtures.

Copies compiled binaries, uses a local identity provider, and never sends mail or
contacts Stripe. Existing member and ledger fixtures are explicitly seeded SQL;
they are not new applications or proof of provider payments.
"""
import importlib.util
import shutil
import restaurant_test_support as support

support.RUN = support.ROOT / '.tools/referral-verification' / support.datetime.now(support.timezone.utc).strftime('%Y%m%d-%H%M%S-%f')
support.RUN.mkdir(parents=True)
support.DB = support.RUN / 'synthetic.db'
spec = importlib.util.spec_from_file_location('referral_web_helpers', support.ROOT / 'scripts/verify-management-web.py')
helpers = importlib.util.module_from_spec(spec); spec.loader.exec_module(helpers)
from restaurant_test_support import *
web, browser, forms_for, find_form, post, saved, login, launch = (getattr(helpers, n) for n in ('web','browser','forms_for','find_form','post','saved','login','launch'))
WEB = helpers.WEB
runtime = RUN / 'runtime'
build_root = Path(os.environ.get('TIDE_TEST_BUILD_ROOT',str(ROOT))).resolve()
for project in ('TideCasa.Api','TideCasa.Blazor'):
    shutil.copytree(build_root/project/'bin/Debug/net10.0',runtime/project/'bin/Debug/net10.0', ignore=shutil.ignore_patterns("libSkiaSharp.pdb"))
support.ROOT = runtime
os.environ['TIDE_TEST_BUILD_ROOT'] = str(runtime)
OFF = {'DOTNET_ENVIRONMENT':'Development','Notifications__Mode':'disabled','Notifications__ResendApiKey':'',
       'Media__Provider':'disabled','ServiceBilling__RestrictedKey':'','ServiceBilling__CheckoutEnabled':'false',
       'ServiceBilling__LiveEnabled':'false','ServiceBilling__DevelopmentApiBase':'','MerchantPayments__RestrictedKey':'',
       'MerchantPayments__OnboardingEnabled':'false','MerchantPayments__CheckoutEnabled':'false','MerchantPayments__ApiBaseUrl':'',
       'ServiceBilling__GuestCheckoutEnabled':'false','WebPush__Enabled':'false','PublicDemo__Enabled':'false',
       'Storage__Provider':'Sqlite','ConnectionStrings__Application':'','DataProtection__Provider':'FileSystem',
       'DOTNET_PROCESSOR_COUNT':'1'}
PATH='/api/v1/referrals'
TERMS='2026-09-19'
RETIRED_APPLICATION={'name':'Alice <script>alert(1)</script> Synthetic','introduction':'I enjoy technology and helping our local businesses prepare their apps.','acceptedTerms':True,'termsVersion':TERMS}


def seed_existing_profile(person, name):
    """Model a historical accepted profile using the schema/store's pending defaults."""
    user=USERS[person+'@example.invalid']
    ident=str(uuid.uuid5(uuid.NAMESPACE_URL,'tide-referral-verification:'+person))
    sql("""INSERT INTO tide_referral_profiles(
        id,user_id,email,name,introduction,status,code,discount_percent,
        terms_version,terms_accepted_at,review_token,created_at,updated_at)
        VALUES(?,?,?,?,?,'pending',NULL,0,?,?,'',?,?)""",
        (ident,'supabase:'+user['id'],user['email'],name,RETIRED_APPLICATION['introduction'],TERMS,NOW,NOW,NOW))
    return ident


def dashboard(token):
    response=call(PATH,token=token)
    assert response[0]==200,response[:3]
    return response[1]


def review(profile,token='',**changes):
    return call(PATH+'/'+profile+'/review',{'expectedReviewToken':token,'status':'active','code':'ALICE-TEST','discountPercent':10,**changes},tokens['platform'])


try:
    launch('TideCasa.Api',API,{**OFF,'Storage__DatabasePath':str(DB),'Auth__Enabled':'true',
        'Auth__AllowLocalTestProvider':'true','Auth__SupabaseUrl':f'http://127.0.0.1:{PROVIDER.server_port}',
        'Auth__PublishableKey':KEY,'Auth__PlatformOwnerUserId':'supabase:'+PLATFORM,
        'Ordering__PublicBaseUrl':WEB,'ReverseProxy__KnownClientProxy':'127.0.0.1'})
    tokens={}
    for person in ('alice','bob','staff','platform'):
        response=call('/api/v1/auth/signin',{'email':person+'@example.invalid','password':PASSWORD})
        check('Verified synthetic API sign-in '+person,response[0]==200)
        tokens[person]=response[1]['accessToken']
    check('Anonymous referral dashboard is private',call(PATH)[0]==401)
    for person,token in [('anonymous',None),*tokens.items()]:
        check('Retired API application route returns 404 for '+person,call(PATH+'/apply',RETIRED_APPLICATION,token)[0]==404)
    check('Retired application requests create no profiles',sql('SELECT COUNT(*) FROM tide_referral_profiles')[0][0]==0)
    for person in ('alice','bob','staff'):
        data=dashboard(tokens[person]);check('No application exposure for '+person,data['profile'] is None and data['applications']==[] and not data['owner'])
    alice_id=seed_existing_profile('alice',RETIRED_APPLICATION['name'])
    own=dashboard(tokens['alice'])['profile']
    check('Existing pending profile retains accepted terms',own['id']==alice_id and own['status']=='pending' and own['code'] is None and own['termsVersion']==TERMS and own['termsAcceptedAt']==NOW)
    check('Existing profile resolves by verified session ID',sql('SELECT user_id,email FROM tide_referral_profiles WHERE id=?',(alice_id,))[0]==('supabase:'+ALICE,'alice@example.invalid'))
    check('Other customer cannot read Alice profile',dashboard(tokens['bob'])['profile'] is None)
    for person in ('alice','bob','staff'):
        check('Nonowner cannot review '+person,call(PATH+'/'+alice_id+'/review',{'expectedReviewToken':'','status':'active','code':'FORGED','discountPercent':100},tokens[person])[0]==403)
    bob_id=seed_existing_profile('bob','Bob Synthetic')
    check('Existing members receive only their own profile',dashboard(tokens['bob'])['profile']['id']==bob_id and dashboard(tokens['alice'])['profile']['id']==alice_id)
    owner=dashboard(tokens['platform'])
    check('Only platform owner receives review snapshots and applicant emails',owner['owner'] and len(owner['applications'])==2 and all('reviewToken' in row and 'email' in row for row in owner['applications']))
    check('Applicant response excludes review tokens, email and user IDs',all(field not in json.dumps(dashboard(tokens['alice'])) for field in ('reviewToken','userId','alice@example.invalid','bob@example.invalid')))
    for change in ({'expectedReviewToken':'stale'},{'code':None},{'code':'X'},{'code':'bad code'},{'discountPercent':101},{'status':'paid'}):
        response=review(alice_id,**change)
        check('Invalid owner review rejected '+next(iter(change)),response[0] in (400,409))
    check('Owner normalizes approved code uppercase',review(alice_id,code='  Alice-test  ')[0]==200 and dashboard(tokens['alice'])['profile']['code']=='ALICE-TEST')
    current=sql('SELECT review_token FROM tide_referral_profiles WHERE id=?',(alice_id,))[0][0]
    check('Owner review audit stores actor and immutable snapshot',sql('SELECT reviewer_id,status,code,discount_percent FROM tide_referral_reviews WHERE id=?',(current,))[0]==('supabase:'+PLATFORM,'active','ALICE-TEST',10))
    check('Case-insensitive code collision rejected',review(bob_id,code='alice-test')[0]==409)
    check('Assigned code cannot change',review(alice_id,current,code='DIFFERENT')[0]==409)
    check('Assigned code cannot be cleared',review(alice_id,current,status='paused',code=None)[0]==409)
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        changes=list(pool.map(lambda discount:review(alice_id,current,discountPercent=discount),[5,15]))
    check('Owner compare-and-swap allows only one concurrent review',sorted(r[0] for r in changes)==[200,409] and sql('SELECT COUNT(*) FROM tide_referral_reviews WHERE profile_id=?',(alice_id,))[0][0]==2)
    current=sql('SELECT review_token FROM tide_referral_profiles WHERE id=?',(alice_id,))[0][0]
    before=sql('SELECT status,code,discount_percent,review_token FROM tide_referral_profiles WHERE id=?',(alice_id,))[0]
    sql("CREATE TRIGGER synthetic_review_failure BEFORE INSERT ON tide_referral_reviews BEGIN SELECT RAISE(ABORT,'synthetic'); END")
    response=review(alice_id,current,status='paused')
    check('Audit failure rolls back the profile update',response[0]>=400 and sql('SELECT status,code,discount_percent,review_token FROM tide_referral_profiles WHERE id=?',(alice_id,))[0]==before)
    sql('DROP TRIGGER synthetic_review_failure')
    before=sql('SELECT * FROM tide_referral_profiles WHERE id=?',(alice_id,))[0]
    check('Retired reapplication cannot alter an approved profile',call(PATH+'/apply',{**RETIRED_APPLICATION,'name':'Changed'},tokens['alice'])[0]==404 and sql('SELECT * FROM tide_referral_profiles WHERE id=?',(alice_id,))[0]==before)
    sql('UPDATE tide_referral_profiles SET email=? WHERE id=?',('platform@example.invalid',alice_id))
    check('Matching email never grants profile ownership',dashboard(tokens['platform'])['profile'] is None and dashboard(tokens['alice'])['profile']['id']==alice_id)
    check('Retired route cannot create or claim a matching-email profile',call(PATH+'/apply',{**RETIRED_APPLICATION,'name':'Platform','userId':'supabase:'+ALICE,'profileId':alice_id},tokens['platform'])[0]==404 and dashboard(tokens['platform'])['profile'] is None and sql('SELECT COUNT(*) FROM tide_referral_profiles')[0][0]==2)
    sql('UPDATE tide_referral_profiles SET email=? WHERE id=?',('alice@example.invalid',alice_id))
    # Ledger fixtures exercise display only; payment-authoritative writes are tested separately.
    for ident,profile,kind,environment,gross,refunded,commission in [('live-initial',alice_id,'initial','live',60000,10000,10000),('live-month',alice_id,'recurring','live',5000,0,500),('sandbox-hidden',alice_id,'initial','sandbox',99000,0,19800),('other-profile',bob_id,'initial','live',70000,0,14000)]:
        sql('INSERT INTO tide_referral_sales(event_id,order_id,profile_id,kind,environment,gross_cents,refunded_cents,commission_cents,source_revision,paid_at,updated_at) VALUES(?,?,?,?,?,?,?,?,0,?,?)',(ident,'PRIVATE-CUSTOMER-ORDER-'+ident,profile,kind,environment,gross,refunded,commission,NOW,NOW))
    data=dashboard(tokens['alice'])
    check('Only own live sales contribute totals',len(data['sales'])==2 and data['totals']=={'saleCount':2,'netSalesCents':55000,'commissionCents':10500})
    check('Ledger display excludes buyer/order/provider identifiers',all(value not in json.dumps(data) for value in ('PRIVATE-CUSTOMER','eventId','orderId','sandbox-hidden','other-profile','bob@example.invalid')))
    check('No public self-award endpoint exists',call(PATH+'/sales',{'commissionCents':999999},tokens['alice'])[0]==404)
    check('Pausing preserves live ledger history',review(alice_id,current,status='paused')[0]==200 and dashboard(tokens['alice'])['totals']==data['totals'])
    current=sql('SELECT review_token FROM tide_referral_profiles WHERE id=?',(alice_id,))[0][0]
    check('Paused code cannot transfer to another applicant',review(bob_id,code='ALICE-TEST')[0]==409)
    check('Owner may reactivate same code',review(alice_id,current)[0]==200)
    launch('TideCasa.Blazor',WEB,{**OFF,'Api__BaseUrl':API+'/','DataProtection__KeysPath':str(RUN/'keys')})
    for path in ('/careers','/careers/'):
        public=web(path)
        check('Retired careers page returns 404 '+path,public[0]==404 and 'noindex' in public[2].get('X-Robots-Tag',''))
    for path in ('/','/restaurant','/book-a-demo'):
        public=web(path)
        check('Public navigation has no hiring link '+path,public[0]==200 and '/careers' not in public[1] and '/account/referrals/apply' not in public[1])
    sitemap=web('/sitemap.xml')
    check('Public sitemap omits retired careers page',sitemap[0]==200 and '/careers' not in sitemap[1])
    check('Anonymous private workspace requires sign-in',web('/account/referrals')[0] in (302,303,401))
    alice_web,bob_web,staff_web,owner_web=(login(p) for p in ('alice','bob','staff','platform'))
    forms,page=forms_for(alice_web,'/account/referrals','Existing member private profile renders')
    check('Member HTML is private and encoded','no-store' in page[2].get('Cache-Control','') and '<script>alert(1)</script>' not in page[1] and '&lt;script&gt;' in page[1])
    check('Member cannot see owner review controls or another member email','/review' not in page[1] and 'bob@example.invalid' not in page[1] and all(token not in page[1] for token in TOKENS))
    check('Existing member retains commission terms in private workspace',all(text in page[1] for text in ('id="commission-terms"','20%','10%','automatic payouts')) and '/careers' not in page[1])
    check('Share link retains code for reviewed checkout','/purchase/restaurant?ref=ALICE-TEST' in page[1] and 'alone does not confirm a discount' in page[1])
    staff_forms,staff_page=forms_for(staff_web,'/account/referrals','Nonmember workspace renders without recruitment')
    check('Nonmember receives no recruitment form',not any(form['action'].endswith('/apply') for form in staff_forms) and 'no referral profile linked' in staff_page[1] and 'Send my application' not in staff_page[1])
    check('Commission terms stay in the existing-member workspace','id="commission-terms"' not in staff_page[1])
    staff_account_forms,staff_account=forms_for(staff_web,'/account')
    check('Account retains neutral existing-member access','Referral workspace' in staff_account[1] and '/account/referrals' in staff_account[1])
    staff_csrf=find_form(staff_account_forms,'/auth/session/signout')['fields']['__RequestVerificationToken']
    cached_form={'action':'/account/referrals/apply','fields':{'__RequestVerificationToken':staff_csrf,**RETIRED_APPLICATION,'acceptedTerms':'true'}}
    check('Cached native application submission returns 404',post(staff_web,cached_form)[0]==404)
    check('Retired native submission creates no member record',dashboard(tokens['staff'])['profile'] is None and sql('SELECT COUNT(*) FROM tide_referral_profiles')[0][0]==2)
    owner_forms,page=forms_for(owner_web,'/account/referrals','Platform owner reviews render')
    check('Owner retains commission terms without recruitment','id="commission-terms"' in page[1] and '/careers' not in page[1] and not any(form['action'].endswith('/apply') for form in owner_forms))
    check('All owner forms carry antiforgery',all('__RequestVerificationToken' in f['fields'] for f in owner_forms))
    owner_form=find_form(owner_forms,'/'+alice_id+'/review')
    before=sql('SELECT review_token FROM tide_referral_profiles WHERE id=?',(alice_id,))[0][0]
    check('Cross-origin review cannot mutate profile',post(owner_web,owner_form,{'status':'paused'},headers={'Origin':'https://foreign.example.invalid'})[0]==400)
    response=post(owner_web,owner_form,{'status':'paused'},remove=('__RequestVerificationToken',))
    check('Missing review CSRF cannot mutate profile','notice=expired' in response[2].get('Location','') and sql('SELECT review_token FROM tide_referral_profiles WHERE id=?',(alice_id,))[0][0]==before)
    check('Nonowner forged owner URL rejected',post(staff_web,owner_form,{'status':'paused','__RequestVerificationToken':staff_csrf})[0]==403)
    saved(post(owner_web,owner_form,{'status':'paused'}),'Platform owner changes status through native form')
    check('Stale native review requires refresh','notice=changed' in post(owner_web,owner_form,{'status':'active'})[2].get('Location',''))
    page=web('/account/referrals',client=alice_web)
    check('Paused member sees history but no active share control','paused for new purchases' in page[1] and 'Open my referral link' not in page[1] and '$105.00' in page[1])
    check('Referral fixtures preserve foreign keys',sql('PRAGMA foreign_key_check')==[])
finally:
    for proc in PROCESSES:
        if proc.poll() is None:
            proc.terminate()
            try:proc.wait(timeout=20)
            except subprocess.TimeoutExpired:proc.kill();proc.wait(timeout=10)
    PROVIDER.shutdown();PROVIDER.server_close()
    for log in LOGS:log.close()
    (RUN/'results.json').write_text(json.dumps(RESULTS,indent=2),encoding='utf-8')
    print('Referral evidence: '+str(RUN),flush=True)
print(str(len(RESULTS))+' referral checks passed.',flush=True)
