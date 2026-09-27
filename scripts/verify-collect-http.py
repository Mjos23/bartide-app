"""Synthetic Collect journey against a caller-started loopback preview. Never contacts production."""
from datetime import datetime,timedelta,timezone
from html.parser import HTMLParser
from pathlib import Path
import html,http.cookiejar,json,re,sys,urllib.error,urllib.parse,urllib.request

BASE=sys.argv[1] if len(sys.argv)>1 else 'http://127.0.0.1:5286'
assert urllib.parse.urlsplit(BASE).hostname in ('localhost','127.0.0.1','::1')
OUT=Path(sys.argv[2]) if len(sys.argv)>2 else Path('.tools/collect-http')
OUT.mkdir(parents=True,exist_ok=True)
class NoRedirect(urllib.request.HTTPRedirectHandler):
 def redirect_request(self,*a,**kw):return None
class Browser:
 def __init__(self):self.jar=http.cookiejar.CookieJar();self.client=urllib.request.build_opener(urllib.request.ProxyHandler({}),NoRedirect(),urllib.request.HTTPCookieProcessor(self.jar))
 def request(self,path,data=None,headers=None):
  h={'Origin':BASE} if data is not None else {}
  h.update(headers or {})
  body=urllib.parse.urlencode(data).encode() if data is not None else None
  if body is not None:h['Content-Type']='application/x-www-form-urlencoded'
  try:response=self.client.open(urllib.request.Request(BASE+path,data=body,headers=h),timeout=20)
  except urllib.error.HTTPError as e:response=e
  return response.status,response.read().decode('utf-8'),dict(response.headers)
class Inputs(HTMLParser):
 def __init__(self,s):super().__init__();self.values={};self.feed(s)
 def handle_starttag(self,tag,attrs):
  a=dict(attrs)
  if tag=='input' and a.get('type')=='hidden' and 'name'in a:self.values.setdefault(a['name'],a.get('value',''))
results=[]
def check(name,passed):
 results.append({'name':name,'passed':bool(passed)});print(('PASS ' if passed else 'FAIL ')+name,flush=True)
 if not passed:raise AssertionError(name)
def get(browser,path):
 status,page,headers=browser.request(path);check('GET '+path.split('?')[0],status==200);return page,headers
def post(browser,pagepath,action,fields={},expected=302):
 page,_=get(browser,pagepath);values=Inputs(page).values
 data={k:values[k] for k in ('__RequestVerificationToken','revision') if k in values};data.update(fields)
 status,text,headers=browser.request('/collect/action/'+action,data)
 if status!=expected:(OUT/'last-failure.html').write_text(text,encoding='utf-8')
 check('POST '+action+' returns '+str(expected),status==expected)
 return status,text,headers
def switch(browser,role):return post(browser,'/collect/account','switch',{'role':role})
def start(browser,role):
 page,_=get(browser,'/collect');data={'__RequestVerificationToken':Inputs(page).values['__RequestVerificationToken'],'role':role}
 status,_,headers=browser.request('/collect/start',data);check('Independent '+role+' preview starts',status==302);return headers['Location']

try:
 b=Browser();page,headers=get(b,'/collect')
 check('Private pages prohibit caching and framing','no-store' in headers.get('Cache-Control','') and headers.get('X-Frame-Options')=='DENY')
 check('API homepage explains the stack and keeps checkout pending','What is an API?' in page and 'learn.tide.casa/csharp/' in page and 'checkout is pending' in page and 'href="https://tide.casa/purchase/' not in page)
 check('Missing CSRF rejected',b.request('/collect/start',{'role':'debtor'})[0]==400)
 token=Inputs(page).values['__RequestVerificationToken']
 check('Cross-origin start rejected',b.request('/collect/start',{'role':'debtor','__RequestVerificationToken':token},{'Origin':'https://elsewhere.invalid'})[0]==400)
 start(b,'debtor');personal,_=get(b,'/collect/profile');check('Independent profile is explicitly private','private' in personal.lower())
 page,_=get(b,'/collect/directory?q=Cedar');check('Creditor search narrows results','Cedar Services' in page and 'href="/c/northstar-credit"' not in page)
 office='/c/northstar-credit';post(b,office,'inquire',{'creditor_id':'sample-creditor-1','consent':'yes'})
 page,_=get(b,'/collect/inquiries');check('Inquiry without profile stays undisclosed','No financial profile shared' in page)
 post(b,'/c/cedar-services','inquire',{'creditor_id':'sample-creditor-2','consent':'yes','share_profile':'yes'})
 page,_=get(b,'/collect/inquiries');check('Second inquiry includes explicitly selected snapshot','Profile snapshot shared' in page)
 post(b,'/collect/profile','profile',{'target':'personal','income':'2900','period':'biweekly','basis':'net','housing':'1400','utilities':'240','food':'450','transport':'280','health':'150','other':'0'})
 page,_=get(b,'/collect/inquiries');check('Editing private profile preserves past disclosures','$1,800.00' in page and '$2,900.00' not in page)
 page,_=get(b,'/collect/account');cases=re.findall(r'href="/collect/case/([a-f0-9]{32})"',page);check('Debtor sees only own account',len(set(cases))==1)
 case=cases[0];path='/collect/case/'+case
 stranger=Browser();start(stranger,'debtor');check('Another preview tenant cannot read case',stranger.request(path)[0]==404)
 post(b,path,'sample-evidence',{'case_id':case,'kind':'paystub'});post(b,path,'sample-evidence',{'case_id':case,'kind':'budget'})
 page,_=get(b,path);docs=re.findall(r'/collect/document/'+case+r'/([a-f0-9]{32})',page);check('Paystub and handwritten-budget sample supported',len(set(docs))==2)
 document_revision=Inputs(page).values['revision']
 status,document,headers=b.request('/collect/document/'+case+'/'+docs[0]);check('Private sample download stays safe',status==200 and 'FICTIONAL SAMPLE' in document and 'attachment' in headers.get('Content-Disposition',''))
 page,_=get(b,path);check('Document access audit does not stale open forms',Inputs(page).values['revision']==document_revision)
 post(b,path,'review',{'case_id':case,'evidence_id':docs[0],'status':'document-reviewed'},404)
 switch(b,'reviewer');check('Creditor cannot open private personal profile',b.request('/collect/profile')[0]==404)
 page,_=get(b,'/collect/inquiries');check('Creditor sees only shared inquiry snapshots','$1,800.00' in page and '$2,900.00' not in page)
 post(b,path,'review',{'case_id':case,'evidence_id':docs[0],'status':'document-reviewed'})
 tomorrow=(datetime.now(timezone.utc)+timedelta(days=7)).strftime('%Y-%m-%d')
 offer={'case_id':case,'amount':'1800.01','installments':'6','first_date':tomorrow,'terms':'Fictional settlement terms: no additional platform fee. Review before acceptance.'}
 post(b,path,'offer',offer,404)
 switch(b,'admin');page,_=get(b,'/collect/account');other=next(x for x in re.findall(r'href="/collect/case/([a-f0-9]{32})"',page) if x!=case)
 post(b,path,'offer',offer);page,_=get(b,path);offer_id=re.search(r'name="offer_id" value="([a-f0-9]{32})"',page).group(1)
 post(b,path,'accept',{'case_id':case,'offer_id':offer_id,'consent':'yes'},403)
 switch(b,'debtor');check('Debtor cannot read another debtor',b.request('/collect/case/'+other)[0]==404)
 post(b,path,'accept',{'case_id':case,'offer_id':offer_id},400)
 post(b,path,'accept',{'case_id':case,'offer_id':offer_id,'consent':'yes'})
 page,_=get(b,'/collect/agreement/'+case);check('Agreement freezes creditor and exact cents','Northstar' in page and '$1,800.01' in page)
 post(b,path,'payment',{'case_id':case,'installment':'1'});page,_=get(b,path);payment=re.search(r'name="payment_id" value="([a-f0-9]{32})"',page).group(1)
 post(b,path,'payment',{'case_id':case,'installment':'1'});page,_=get(b,path);check('Repeated payment request preserves one attempt',len(set(re.findall(r'name="payment_id" value="([a-f0-9]{32})"',page)))==1)
 for state in ['succeeded','settled','returned']:post(b,path,'payment-event',{'case_id':case,'payment_id':payment,'state':state})
 page,_=get(b,path);check('Returned payment shown with simulation distinction','returned' in page and 'simulat' in page.lower())
 post(b,path,'dispute',{'case_id':case,'category':'dispute'});post(b,path,'payment',{'case_id':case,'installment':'2'},409)
 post(b,path,'preference',{'case_id':case,'opt_out':'true'})
 switch(b,'admin');page,_=get(b,'/collect/analytics');check('Analytics distinguishes missing comparison evidence','missing' in page.lower() or 'not comparable' in page.lower())
 page,_=get(b,'/collect/client');check('Client pricing matches authorized prices','$150' in page and '$199' in page and '$349' in page and '$1,500' not in page)
 stale=Inputs(page).values;post(b,'/collect/client','brand',{'name':'Harbor <script>alert(1)</script>','slug':'harbor-pilot','accent':'#245c4f','kind':'collection-agency'})
 status,_,_=b.request('/collect/action/brand',{**stale,'name':'Old edit','slug':'old','accent':'#245c4f','kind':'collection-agency'});check('Stale form cannot overwrite office',status==409)
 page,_=get(b,'/collect/client');check('Office text is HTML encoded','<script>alert(1)</script>' not in page and '&lt;script&gt;' in page)
 get(b,'/c/harbor-pilot')
 for slug in ['signin','signup','verify-email','forgot-password','reset-password']:
  page,_=get(Browser(),'/collect/'+slug);check('Auth form preserves Collect return: '+slug,'/collect/account' in html.unescape(page))
 for host in ['bar.tide.casa','order.tide.casa']:
  status,page,_=b.request('/',headers={'Host':host});check('Existing host homepage preserved: '+host,status==200 and 'Your creditor workflow API.' not in page)
 status,page,_=b.request('/',headers={'Host':'collect.tide.casa'});check('Collect domain opens own homepage',status==200 and 'Your creditor workflow API.' in page)
 for host,label in [('fit.tide.casa','Your fitness API.'),('driver.tide.casa','Your delivery API.')]:
  status,page,_=b.request('/',headers={'Host':host});check('Public vertical host routes correctly: '+host,status==200 and label in page and 'learn.tide.casa/csharp/' in page and 'purchase/business' in page)
 check('Collect host does not expose restaurant workspace',b.request('/restaurants',headers={'Host':'collect.tide.casa'})[0] in (302,404))
 for cookie in b.jar:
  if cookie.name=='Tide.Collect.Preview':check('Preview identity is HttpOnly and cannot select external domain',(cookie.has_nonstandard_attr('HttpOnly') or cookie.has_nonstandard_attr('httponly')) and not cookie.domain_specified)
 print(json.dumps({'passed':True,'checks':len(results)}))
finally:
 (OUT/'summary.json').write_text(json.dumps({'passed':all(x['passed'] for x in results),'checks':results},indent=2),encoding='utf-8')
