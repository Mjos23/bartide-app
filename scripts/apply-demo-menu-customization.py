"""Fill fictional Gulf Lantern ingredient lists using its ordinary manager forms.

Explicitly supports only demo.tide.casa and a loopback preview. Existing prices,
photos, descriptions and availability are read back and preserved. Safe to rerun.
"""
import argparse
from html.parser import HTMLParser
import http.cookiejar
import json
from pathlib import Path
import urllib.parse
import urllib.request

ROOT = Path(__file__).resolve().parents[1]

class Forms(HTMLParser):
    def __init__(self):
        super().__init__(); self.forms=[]; self.current=None; self.textarea=None; self.select=None
    def handle_starttag(self, tag, attrs):
        a=dict(attrs)
        if tag=='form': self.current={'action':a.get('action',''),'fields':{}}; self.forms.append(self.current)
        if self.current is None: return
        if tag=='input' and 'name' in a and (a.get('type') not in ('checkbox','radio') or 'checked' in a):
            self.current['fields'][a['name']]=a.get('value','')
        if tag=='textarea' and 'name' in a: self.textarea=a['name']; self.current['fields'][self.textarea]=''
        if tag=='select' and 'name' in a: self.select=a['name']
        if tag=='option' and self.select:
            if self.select not in self.current['fields'] or 'selected' in a: self.current['fields'][self.select]=a.get('value','')
    def handle_data(self, data):
        if self.current is not None and self.textarea: self.current['fields'][self.textarea]+=data
    def handle_endtag(self, tag):
        if tag=='form': self.current=None
        if tag=='textarea': self.textarea=None
        if tag=='select': self.select=None

def main():
    parser=argparse.ArgumentParser(); parser.add_argument('--base',required=True); options=parser.parse_args()
    uri=urllib.parse.urlparse(options.base)
    assert ((uri.scheme=='https' and uri.hostname=='demo.tide.casa' and uri.port in (None,443)) or
        (uri.scheme=='http' and uri.hostname=='127.0.0.1')) and uri.path in ('','/') and not uri.username and not uri.query and not uri.fragment, 'Only the fictional demo or loopback is allowed.'
    base=options.base.rstrip('/'); opener=urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
    def request(path,body=None):
        assert path.startswith('/') and not path.startswith('//')
        req=urllib.request.Request(base+path, data=None if body is None else urllib.parse.urlencode(body).encode(),
            headers={} if body is None else {'Content-Type':'application/x-www-form-urlencoded','Origin':base})
        with opener.open(req,timeout=60) as response:
            result=response.read().decode(); assert urllib.parse.urlparse(response.url).netloc==uri.netloc
            return result,response.url
    def read_forms(path):
        raw,_=request(path); parsed=Forms(); parsed.feed(raw); return parsed.forms
    switch=next(form for form in read_forms('/sample-bar') if form['fields'].get('person_key')=='manager' or uri.hostname=='127.0.0.1' and form['fields'].get('email')=='manager@gulf-lantern.example.invalid')
    assert switch['action']==('/auth/session/signin' if uri.hostname=='127.0.0.1' else '/auth/session/demo-switch')
    _,landing=request(switch['action'],switch['fields']); assert urllib.parse.urlparse(landing).path=='/account'
    fixture=json.loads((ROOT/'fixtures/gulf-lantern.json').read_text(encoding='utf-8'))
    editor='/workspace/gulf-lantern/menu'; prefix='/restaurant-management/gulf-lantern/'
    def current(kind,ident):
        found=[form for form in read_forms(editor) if form['action']==prefix+kind and form['fields'].get('id')==ident]
        assert len(found)==1,'Expected one current '+kind+' form: '+ident
        return found[0]
    def save(form):
        _,url=request(form['action'],form['fields'])
        assert urllib.parse.parse_qs(urllib.parse.urlparse(url).query).get('notice')==['saved'], 'Menu changed while updating; rerun after reviewing it.'
    category=current('categories','share')
    assert category['fields']['name'] in ('For the table','Popular items'), 'Category was edited; review before replacing its label.'
    if category['fields']['name']!='Popular items': category['fields']['name']='Popular items'; save(category)
    changed=0
    for item in fixture['menu']:
        form=current('items',item['id']); fields=form['fields']
        assert 'ingredients' in fields, 'Deploy the new ingredient editor before applying this update.'
        expected=item['ingredients']; existing=[line.strip() for line in fields['ingredients'].splitlines() if line.strip()]
        assert not existing or existing==expected, 'Ingredient list was edited; review before replacing: '+item['id']
        if existing!=expected:
            fields['ingredients']='\n'.join(expected); save(form); changed+=1
        verified=current('items',item['id'])['fields']
        assert [line.strip() for line in verified['ingredients'].splitlines() if line.strip()]==expected
        for key in ('name','description','price','price_label','photo_id','category_id','available'):
            assert verified.get(key)==fields.get(key), 'Unrelated item detail changed: '+key
    print(f'Verified 20 ingredient lists and Popular items; updated {changed} ingredient lists. Prices, photos and availability preserved.',flush=True)

if __name__=='__main__': main()
