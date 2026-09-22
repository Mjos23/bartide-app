"""Exercise native role-switch forms on only the fictional demo or loopback.

Creates/revokes fictional sessions; does not submit orders or change venue content.
"""
import argparse
from datetime import datetime, timezone
from html.parser import HTMLParser
import http.cookiejar
import json
import re
from pathlib import Path
import urllib.error, urllib.parse, urllib.request

ROOT=Path(__file__).resolve().parents[1]
def page_has_email(raw,email):
    if email.encode() in raw: return True
    # The App Platform edge protects rendered email addresses. Check the exact
    # decoded identity, as its browser script does, rather than a display name.
    for value in re.findall(rb'data-cfemail="([0-9a-fA-F]+)"',raw):
        try:
            encoded=bytes.fromhex(value.decode())
            if encoded and bytes(byte ^ encoded[0] for byte in encoded[1:]).decode()==email: return True
        except (ValueError,UnicodeDecodeError): pass
    return False

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
        # Preserve HTTPMessage's case-insensitive lookup through the public proxy.
        with r:return r.status,r.read(),r.geturl(),r.headers
    run=ROOT/'.tools/hosted-demo-verification'/datetime.now(timezone.utc).strftime('%Y%m%d-%H%M%S');run.mkdir(parents=True)
    try:
        status,raw,url,head=request('/')
        check('Demo home opens the orderable menu',status==200 and url.endswith('/order/gulf-lantern') and b'Add one Smoked fish dip' in raw)
        check('Restaurant welcome leads directly to the menu',b'class="sample-order-intro"' in raw and raw.index(b'class="sample-order-intro"') < raw.index(b'id="ordering-menu"'))
        check('Additional perspectives appear below ordering',b'See additional views' in raw and raw.index(b'class="sample-perspectives-footer"') > raw.index(b'id="ordering-checkout"'))
        status,raw,url,head=request('/sample-bar')
        check('Perspective hub remains accessible',status==200 and b'Gulf Lantern' in raw)
        check('Hub menu photos open ordering',raw.count(b'class="sb-dish-order"')==20 and b'href="/order/gulf-lantern#item-' in raw)
        check('Demo is marked fictional and not indexed',b'Fictional sample bar' in raw and 'noindex' in head.get('X-Robots-Tag',''))
        check('Public hub contains no password inputs',b'name="password"' not in raw)
        status,raw,_,_=request('/sample-bar/gulf-lantern.webmanifest'); manifest=json.loads(raw)
        check('Phone shortcut opens direct Gulf Lantern ordering',status==200 and manifest['start_url']=='/order/gulf-lantern' and manifest['display']=='standalone')
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
            check(person['key']+' account identity matches',status==200 and page_has_email(raw,person['email']) and b'Platform owner' not in raw)
        status,raw,_,_=request('/sample-bar');forms=Forms();forms.feed(raw.decode()); form=next(f for f in forms.forms if 'person_key' in f['fields'])
        fields={**form['fields']};fields.pop('__RequestVerificationToken',None)
        check('Role switch rejects missing antiforgery token',request(form['action'],fields)[0]==400)
        check('Role switch rejects another site origin',request(form['action'],form['fields'],'https://example.invalid')[0]==400)
        for route in ('/owner/sales','/signup','/purchase/business'):
            check('Demo hides '+route,request(route)[0]==404)
        if uri.scheme=='https':
            for route in ('/engineering/','/engineering/index.html','/engineering/studio.js'):
                check('Internal studio is unavailable publicly '+route,request(route)[0]==404)
        status,raw,_,_=request('/order/gulf-lantern')
        check('Functional order page renders table entry',status==200 and b'table' in raw.lower() and b'Gulf Lantern' in raw)
        for route in ('/events/gulf-lantern','/updates/gulf-lantern'):
            check('Sample page renders '+route,request(route)[0]==200)
    finally:
        (run/'report.json').write_text(json.dumps({'base':base,'personFilter':options.person,'checks':checks,'passed':bool(checks) and all(c['passed'] for c in checks)},indent=2))
        print('Evidence: '+str(run),flush=True)
if __name__=='__main__':main()
