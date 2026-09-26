"""Collect enrollment uses the existing verified identity and never opens real financial intake."""
import html,http.cookiejar,re,urllib.parse,urllib.request

def run(ctx):
 check,call=ctx['check'],ctx['call'];ctx['clear_limits']()
 jar=http.cookiejar.CookieJar()
 browser=urllib.request.build_opener(urllib.request.ProxyHandler({}),ctx['NoRedirect'](),urllib.request.HTTPCookieProcessor(jar))
 def get(path):return call(path,base=ctx['WEB'],client=browser)
 def fields(path,action):
  status,page,headers=get(path);check('Collect '+action+' form is branded',status==200 and 'collect.css' in page)
  form=next(m.group(2) for m in re.finditer(r'<form\b([^>]*)>(.*?)</form>',page,re.S) if 'action="/auth/session/'+action+'"' in m.group(1))
  return {html.unescape(k):html.unescape(v) for k,v in re.findall(r'<input(?=[^>]*type="hidden")(?=[^>]*name="([^"]+)")(?=[^>]*value="([^"]*)")[^>]*>',form)}
 def post(action,data):return call('/auth/session/'+action,data,base=ctx['WEB'],client=browser,form=True,headers={'Origin':ctx['WEB']})
 email='bob@example.invalid';password=ctx['users'][email]['password']
 data=fields('/collect/signup','signup');status,_,headers=post('signup',{**data,'email':email,'password':password})
 check('Collect signup requires verification and keeps own entrance',status in (302,303) and headers.get('Location','').startswith('/collect/verify-email?') and not any(c.name=='TideCasa.Auth' for c in jar))
 data=fields('/collect/verify-email','verify');status,_,headers=post('verify',{**data,'email':email,'code':'123456'})
 check('Collect verified identity returns to Collect account',status in (302,303) and headers.get('Location')=='/collect/account')
 status,page,headers=get('/collect/account');check('Real identity cannot accidentally enter fictional financial workspace',status==200 and email in page and 'financial' in page.lower() and 'name="income"' not in page and 'type="file"' not in page)
 check('Restaurant identity does not grant creditor workspace',get('/collect/analytics')[0] in (302,404))
 data=fields('/collect/account','signout');status,_,headers=post('signout',data)
 check('Collect logout clears real identity and retains Collect entrance',status in (302,303) and headers.get('Location','').startswith('/collect/signin?') and not any(c.name=='TideCasa.Auth' for c in jar))
 data=fields('/collect/signin','signin');status,_,headers=post('signin',{**data,'email':email,'password':password})
 check('Collect existing identity signs in',status in (302,303) and headers.get('Location')=='/collect/account')
 data=fields('/collect/forgot-password','forgot');status,_,headers=post('forgot',{**data,'email':email})
 check('Collect recovery stays in Collect',status in (302,303) and headers.get('Location','').startswith('/collect/reset-password?'))
