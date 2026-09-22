"""Exercise native role-switch forms on only the fictional demo or loopback.

Creates/revokes fictional sessions; does not submit orders or change venue content.
"""
import argparse
from datetime import datetime, timezone
from html.parser import HTMLParser
import http.cookiejar
import json
from pathlib import Path
import urllib.error, urllib.parse, urllib.request

ROOT=Path(__file__).resolve().parents[1]
class Forms(HTMLParser):
    def __init__(self): super().__init__(); self.forms=[]; self.current=None
    def handle_starttag(self,tag,attrs):
        a=dict(attrs)
        if tag=='form': self.current={'action':a.get('action',''),'fields':{}}; self.forms.append(self.current)
        if tag=='input' and self.current is not None and 'name' in a: self.current['fields'][a['name']]=a.get('value','')
    def handle_endtag(self,tag):
        if tag=='form': self.current=None

def main():
    parser=argparse.ArgumentParser(); parser.add_argument('--base',required=True); parser.add_argument('--person'); options=parser.parse_args()
    uri=urllib.parse.urlparse(options.base)
    assert (uri.scheme=='https' and uri.hostname=='demo.tide.casa') or (uri.scheme=='http' and uri.hostname=='127.0.0.1')
    assert uri.path in ('','/') and not uri.username and not uri.query and not uri.fragment
    base=options.base.rstrip('/'); jar=http.cookiejar.CookieJar(); opener=urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar))
    checks=[]
    def check(label,passed):
        checks.append({'check':label,'passed':bool(passed)}); print(('PASS ' if passed else 'FAIL ')+label,flush=True); assert passed,label
    def request(path,body=None,origin=None):
        head={}
        if body is not None: head={'Content-Type':'application/x-www-form-urlencoded','Origin':origin or base}
        req=urllib.request.Request(base+path,data=None if body is None else urllib.parse.urlencode(body).encode(),headers=head)
        try:r=opener.open(req,timeout=45)
        except urllib.error.HTTPError as e:r=e
        with r:return r.status,r.read(),r.geturl(),dict(r.headers)
    run=ROOT/'.tools/hosted-demo-verification'/datetime.now(timezone.utc).strftime('%Y%m%d-%H%M%S');run.mkdir(parents=True)
    try:
        status,raw,url,head=request('/')
        check('Demo opens its sample hub',status==200 and url.endswith('/sample-bar') and b'Gulf Lantern' in raw)
        check('Demo is marked fictional and not indexed',b'Fictional sample bar' in raw and 'noindex' in head.get('X-Robots-Tag',''))
        check('Public hub contains no password inputs',b'name="password"' not in raw)
        status,raw,_,_=request('/sample-bar/gulf-lantern.webmanifest'); manifest=json.loads(raw)
        check('Phone shortcut opens Gulf Lantern hub',status==200 and manifest['start_url']=='/sample-bar' and manifest['display']=='standalone')
        people=json.loads((ROOT/'fixtures/gulf-lantern.json').read_text())['people']
        if options.person:
            people=[person for person in people if person['key']==options.person]
            assert len(people)==1,'Unknown fictional person'
        for person in people:
            status,raw,_,_=request('/sample-bar'); forms=Forms();forms.feed(raw.decode())
            found=[f for f in forms.forms if f['fields'].get('person_key')==person['key']]
            check(person['key']+' has a one-tap switch form',status==200 and len(found)==1 and found[0]['action']=='/auth/session/demo-switch')
            form=found[0]
            status,raw,url,_=request(form['action'],form['fields'])
            if status != 200 or not url.endswith('/rewards/gulf-lantern' if person['role']=='customer' else '/account'):
                print(json.dumps({'person':person['key'],'status':status,'landingPath':urllib.parse.urlparse(url).path}),flush=True)
            check(person['key']+' signs into its actual app view',status==200 and (url.endswith('/rewards/gulf-lantern') if person['role']=='customer' else url.endswith('/account')) and b'Gulf Lantern' in raw)
            status,raw,_,_=request('/account')
            check(person['key']+' account identity matches',status==200 and person['email'].encode() in raw and b'Platform owner' not in raw)
        status,raw,_,_=request('/sample-bar');forms=Forms();forms.feed(raw.decode()); form=next(f for f in forms.forms if 'person_key' in f['fields'])
        fields={**form['fields']};fields.pop('__RequestVerificationToken',None)
        check('Role switch rejects missing antiforgery token',request(form['action'],fields)[0]==400)
        check('Role switch rejects another site origin',request(form['action'],form['fields'],'https://example.invalid')[0]==400)
        for route in ('/owner/sales','/signup','/purchase/business'):
            check('Demo hides '+route,request(route)[0]==404)
        status,raw,_,_=request('/order/gulf-lantern')
        check('Functional order page renders table entry',status==200 and b'table' in raw.lower() and b'Gulf Lantern' in raw)
        for route in ('/events/gulf-lantern','/updates/gulf-lantern'):
            check('Sample page renders '+route,request(route)[0]==200)
    finally:
        (run/'report.json').write_text(json.dumps({'base':base,'personFilter':options.person,'checks':checks,'passed':bool(checks) and all(c['passed'] for c in checks)},indent=2))
        print('Evidence: '+str(run),flush=True)
if __name__=='__main__':main()
