"""Exercise current Release through loopback Caddy, with isolated internal TLS.

No machine trust/hosts/firewall changes or external providers. --hold enables a
CUA browser check over Development-only loopback HTTP and a proxy restart signal.
"""
import argparse
from datetime import datetime, timezone
import hashlib
from html.parser import HTMLParser
import json
import os
from pathlib import Path
import socket
import ssl
import subprocess
import time
import urllib.error
import urllib.request
from urllib.parse import urljoin

ROOT = Path(__file__).resolve().parents[1]
SDK = ROOT / '.tools/dotnet-10.0.401/dotnet.exe'
CADDY = ROOT / '.tools/deployment-validation-tools/caddy.exe'
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--release', type=Path, required=True, help='Publish directory from a verified local release run.')
parser.add_argument('--hold', action='store_true', help='Keep the isolated loopback fixture open for browser verification.')
args = parser.parse_args()
RELEASE = args.release.resolve(strict=True)
if not RELEASE.is_relative_to(ROOT / '.tools/release-verification') or RELEASE.name != 'publish':
    parser.error('Release must be a publish directory inside local release verification evidence.')
RUN = ROOT / '.tools/proxy-verification' / datetime.now(timezone.utc).strftime('%Y%m%d-%H%M%S-%f')
RUN.mkdir(parents=True)
PROCESS, LOGS, CHECKS = [], [], []
result = {'completed': False, 'checks': CHECKS, 'limits': 'Windows loopback Caddy with internal test CA; browser Web is Development over loopback HTTP. Not public TLS, Linux, or hosted authentication/capacity proof.'}


def check(label, condition):
    CHECKS.append({'check': label, 'passed': bool(condition)})
    if not condition: raise AssertionError(label)
    print('PASS ' + label, flush=True)


def port():
    with socket.socket() as s:
        s.bind(('127.0.0.1', 0)); return s.getsockname()[1]


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args): return None


def request(url, context=None, data=None):
    opener = urllib.request.build_opener(NoRedirect, urllib.request.HTTPSHandler(context=context))
    try:
        response = opener.open(urllib.request.Request(url, data=data, headers={'Content-Type': 'application/json'} if data else {}), timeout=5)
    except urllib.error.HTTPError as error: response = error
    with response: return response.status, response.read(), dict(response.headers)


def start(name, command, env, cwd):
    log = (RUN / (name + '.log')).open('w', encoding='utf-8'); LOGS.append(log)
    process = subprocess.Popen(list(map(str, command)), cwd=cwd, env=env, stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW)
    PROCESS.append(process); return process


def stop(process):
    if process.poll() is None:
        process.terminate()
        try: process.wait(timeout=15)
        except subprocess.TimeoutExpired: process.kill(); process.wait(timeout=10)


def healthy(process, url):
    until = time.monotonic() + 60
    while time.monotonic() < until:
        if process.poll() is not None: raise RuntimeError('Owned process stopped; inspect log')
        try:
            if request(url)[0] == 200: return
        except (OSError, urllib.error.URLError): pass
        time.sleep(.2)
    raise TimeoutError(url)


class DemoTextChoice(HTMLParser):
    def __init__(self):
        super().__init__()
        self.checkboxes = []

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if tag == 'input' and attrs.get('type') == 'checkbox':
            self.checkboxes.append(attrs)


try:
    published = json.loads((RELEASE.parent / 'results.json').read_text(encoding='utf-8'))
    check('Selected Release has completed verification', published['completed'])
    hashes = {name: hashlib.sha256((RELEASE / name / (name + '.dll')).read_bytes()).hexdigest()
              for name in ('TideCasa.Api', 'TideCasa.Blazor')}
    check('Selected Release assemblies match publication', all(hashes[name] == published['packages'][name]['assemblySha256'] for name in hashes))
    result['release'] = str(RELEASE)
    result['assemblySha256'] = hashes
    api_port, web_port, http_port, tls_port, api_tls_port = [port() for _ in range(5)]
    api, web = f'http://127.0.0.1:{api_port}', f'http://127.0.0.1:{web_port}'
    proxy_http, proxy_tls, api_tls = f'http://127.0.0.1:{http_port}', f'https://localhost:{tls_port}', f'https://localhost:{api_tls_port}'
    env = os.environ.copy()
    for key in list(env):
        if any(word in key.upper() for word in ('STRIPE','RESEND','SUPABASE','CLOUDFLARE','AUTH__','MEDIA__','STORAGE__','NOTIFICATIONS__','WEBPUSH__','SERVICEBILLING__','MERCHANTPAYMENTS__','API__','DATAPROTECTION__','ORDERING__','ASPNETCORE_','DOTNET_ENVIRONMENT')): env.pop(key)
    env.update({'ASPNETCORE_ENVIRONMENT':'Development','DOTNET_ENVIRONMENT':'Development','Auth__Enabled':'false','Auth__AllowLocalTestProvider':'false',
                'Storage__DatabasePath':str(RUN/'synthetic.db'),'DataProtection__KeysPath':str(RUN/'keys'),'Api__BaseUrl':api+'/',
                'Notifications__Mode':'disabled','WebPush__Enabled':'false','Media__Provider':'disabled','Stripe__CheckoutEnabled':'false',
                'Stripe__InvoicesEnabled':'false','ServiceBilling__CheckoutEnabled':'false','ServiceBilling__LiveEnabled':'false',
                'MerchantPayments__CheckoutEnabled':'false','MerchantPayments__OnboardingEnabled':'false','DOTNET_PROCESSOR_COUNT':'1'})
    for project, address in [('TideCasa.Api',api),('TideCasa.Blazor',web)]:
        folder=RELEASE/project
        process=start(project,[SDK,folder/(project+'.dll')],{**env,'ASPNETCORE_URLS':address},folder)
        healthy(process,address+'/health'); check(project+' isolated Release healthy',True)
    config=RUN/'Caddyfile'
    config.write_text(f'''{{
    admin off
    auto_https disable_redirects
    skip_install_trust
}}
http://127.0.0.1:{http_port} {{
    bind 127.0.0.1
    reverse_proxy 127.0.0.1:{web_port}
}}
https://localhost:{tls_port} {{
    bind 127.0.0.1
    tls internal
    encode gzip
    reverse_proxy 127.0.0.1:{web_port}
}}
https://localhost:{api_tls_port} {{
    bind 127.0.0.1
    tls internal
    reverse_proxy 127.0.0.1:{api_port}
}}
''',encoding='utf-8')
    caddy_env={**env,'XDG_DATA_HOME':str(RUN/'caddy-data'),'XDG_CONFIG_HOME':str(RUN/'caddy-config'),'APPDATA':str(RUN/'appdata')}
    command=[CADDY,'run','--config',config,'--adapter','caddyfile']
    proxy=start('caddy',command,caddy_env,RUN);healthy(proxy,proxy_http+'/health')
    check('Real Caddy forwards Web health',True)
    roots=list(RUN.rglob('root.crt'))
    check('Test CA stored only in isolated fixture',len(roots)==1)
    context=ssl.create_default_context(cafile=str(roots[0]))
    tls=request(proxy_tls+'/enhanced-demo',context)
    check('Verified TLS serves current sample through proxy',tls[0]==200 and b'Six tools. One connected business.' in tls[1])
    check('Verified TLS serves API health through proxy',request(api_tls+'/health',context)[0]==200)
    demo = request(proxy_tls+'/book-a-demo', context)
    choice = DemoTextChoice(); choice.feed(demo[1].decode('utf-8'))
    check('Verified TLS serves optional demo text choice initially unchecked',
          demo[0] == 200 and b'Can we text you?' in demo[1]
          and b'For demo scheduling only.' in demo[1]
          and len(choice.checkboxes) == 1 and 'checked' not in choice.checkboxes[0])
    for route in ('/', '/restaurant', '/careers', '/service-terms'):
        page = request(proxy_tls+route, context)
        check('Marketing route avoids retained server circuits through proxy: '+route,
              page[0] == 200 and b'<!--Blazor:' not in page[1])
    private=request(proxy_tls+'/owner/sales',context)
    result['privateRouteResponse']={'status':private[0],'location':private[2].get('Location'),'cacheControl':private[2].get('Cache-Control')}
    print('Private route observation: '+json.dumps(result['privateRouteResponse']),flush=True)
    check('Private route keeps HTTPS sign-in redirect and no-store',private[0]==302 and urljoin(proxy_tls,private[2].get('Location','')).startswith(proxy_tls+'/signin') and 'no-store' in private[2].get('Cache-Control',''))
    signin=request(proxy_tls+'/signin',context)
    check('HTTPS forwarding produces secure antiforgery cookie','secure' in signin[2].get('Set-Cookie','').lower())
    check('API sales remains unauthorized through TLS proxy',request(api_tls+'/api/v1/owner/sales',context)[0]==401)
    negotiated=request(proxy_tls+'/_blazor/negotiate?negotiateVersion=1',context,b'{}')
    transports=json.loads(negotiated[1]).get('availableTransports',[]) if negotiated[0]==200 else []
    check('Blazor negotiation through TLS proxy offers WebSockets',any(t['transport']=='WebSockets' for t in transports))
    # Check the actual WebSocket upgrade, without claiming this creates a Blazor circuit.
    token=json.loads(negotiated[1])['connectionToken']
    with socket.create_connection(('127.0.0.1',tls_port),timeout=5) as raw:
        with context.wrap_socket(raw,server_hostname='localhost') as ws:
            from urllib.parse import quote
            ws.sendall((f'GET /_blazor?id={quote(token,safe="")} HTTP/1.1\r\nHost: localhost:{tls_port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\nOrigin: {proxy_tls}\r\n\r\n').encode())
            headers=ws.recv(4096)
            check('Caddy upgrades the actual Blazor WebSocket over TLS',headers.startswith(b'HTTP/1.1 101'))
    result['fixture']={'http':proxy_http,'https':proxy_tls,'apiHttps':api_tls,'run':str(RUN)}
    if args.hold:
        (RUN/'browser-fixture.json').write_text(json.dumps(result['fixture'],indent=2))
        print('BROWSER FIXTURE '+proxy_http+'/enhanced-demo; restart-proxy and stop-fixture files in '+str(RUN),flush=True)
        until=time.monotonic()+600
        while time.monotonic()<until and not (RUN/'stop-fixture').exists():
            if (RUN/'restart-proxy').exists() and not result.get('proxyRestarted'):
                stop(proxy);proxy=start('caddy-after-restart',command,caddy_env,RUN);healthy(proxy,proxy_http+'/health')
                check('Caddy restart restores proxy health with same test CA',request(proxy_tls+'/health',context)[0]==200)
                result['proxyRestarted']=True
                (RUN/'proxy-restarted.json').write_text(json.dumps({'completed':True}))
            time.sleep(.25)
    check('Selected Release assemblies stay unchanged', all(
        hashlib.sha256((RELEASE / name / (name + '.dll')).read_bytes()).hexdigest() == value for name, value in hashes.items()))
    result['completed']=True
finally:
    for process in reversed(PROCESS):stop(process)
    for log in LOGS:log.close()
    result['processesStopped']=all(p.poll() is not None for p in PROCESS)
    (RUN/'results.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
    print('Proxy evidence: '+str(RUN),flush=True)
