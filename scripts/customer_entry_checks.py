"""Customer HTTP journeys in verify-auth's isolated real API/web fixture."""
import html
import http.cookiejar
import re
import urllib.parse
import urllib.request
import uuid


def run(ctx):
    check, call, sql = ctx['check'], ctx['call'], ctx['sql']
    ctx['clear_limits']()
    jar = http.cookiejar.CookieJar()
    client = urllib.request.build_opener(urllib.request.ProxyHandler({}), ctx['NoRedirect'](), urllib.request.HTTPCookieProcessor(jar))

    def get(path, headers=None):
        return call(path, base=ctx['WEB'], client=client, headers=headers)

    def form(path, action):
        status, document, headers = get(path)
        check('Customer form renders: ' + action, status == 200 and 'YOUR BARTIDE ACCOUNT' in document)
        match = next(m for m in re.finditer(r'<form\b([^>]*)>(.*?)</form>', document, re.S)
                     if 'action="/auth/session/' + action + '"' in m.group(1))
        fields = {html.unescape(k): html.unescape(v) for k, v in re.findall(
            r'<input(?=[^>]*type="hidden")(?=[^>]*name="([^"]+)")(?=[^>]*value="([^"]*)")[^>]*>', match.group(2))}
        check('Customer form keeps context and antiforgery: ' + action,
              fields.get('entry') == 'customer' and '__RequestVerificationToken' in fields)
        return fields

    def post(action, fields):
        return call('/auth/session/' + action, fields, base=ctx['WEB'], client=client,
                    form=True, headers={'Origin': ctx['WEB']})

    status, document, headers = get('/', {'Host': 'order.tide.casa'})
    (ctx['RUN'] / 'customer-home.html').write_text(document, encoding='utf-8')
    print('Customer host response: ' + str(status) + '; redirect: ' + headers.get('Location', ''), flush=True)
    check('Customer domain opens the ordering entrance', status == 200 and 'Closer to you.' in document
          and 'Create your free account' in document and 'customer.webmanifest' in document)
    check('Customer domain uses its own canonical address', 'https://order.tide.casa/' in document
          and 'no-store' in headers.get('Cache-Control', ''))
    check('Customer domain account shortcut stays in customer area',
          get('/account', {'Host': 'order.tide.casa'})[2].get('Location') == '/customer/account')
    check('Customer account requires the customer login',
          get('/customer/account')[2].get('Location', '').startswith('/customer/signin?'))
    status, document, _ = get('/', {'Host': 'bar.tide.casa'})
    check('Business domain retains its restaurant-owner homepage', status == 200 and 'Closer to you.' not in document
          and 'https://order.tide.casa/' in document)
    check('Customer sitemap has only customer public pages', '/restaurants' in get('/sitemap.xml', {'Host': 'order.tide.casa'})[1]
          and '/purchase/' not in get('/sitemap.xml', {'Host': 'order.tide.casa'})[1])

    table = 'a' * 64
    target = '/order/owned-active?table=' + table
    query = '?return_to=' + urllib.parse.quote(target, safe='')
    fields = form('/customer/signup' + query, 'signup')
    signup_document = html.unescape(get('/customer/signup' + query)[1])
    check('Signup header login preserves the restaurant and table',
          'href="/customer/signin' + query + '"' in signup_document)
    email, password = 'customer@example.invalid', ctx['PASSWORD']
    ident = str(uuid.UUID(int=107))
    ctx['users'][email] = {**ctx['users']['bob@example.invalid'], 'id': ident, 'email': email,
                           'user_metadata': {'full_name': 'Customer Test'}}
    status, _, headers = post('signup', {**fields, 'email': email, 'password': password})
    verification = headers.get('Location', '')
    check('Signup requires customer email verification and preserves table', status in (302, 303)
          and verification.startswith('/customer/verify-email?notice=check-email')
          and urllib.parse.parse_qs(urllib.parse.urlsplit(verification).query).get('return_to') == [target]
          and not any(c.name == 'TideCasa.Auth' for c in jar))
    fields = form(verification, 'resend')
    check('Customer can resend verification', post('resend', {**fields, 'email': email})[2].get('Location', '').startswith('/customer/verify-email?'))
    fields = form(verification, 'verify')
    status, _, headers = post('verify', {**fields, 'email': email, 'code': '123456'})
    check('Verified restaurant signup returns to the original table menu', status in (302, 303) and headers.get('Location') == target)
    status, account, headers = get('/customer/account')
    check('Customer account shows its verified identity and restaurant discovery', status == 200 and email in account
          and 'Customer Test' in account and 'Find restaurants near me' in account and '/purchase/' not in account)
    check('Customer account is private and exposes no provider tokens', 'no-store' in headers.get('Cache-Control', '')
          and all(token not in account for token in ctx['tokens']))
    check('Restaurant signup creates one global application identity',
          sql('SELECT app_user_id FROM bartide_auth_identities WHERE provider_user_id=?', (ident,)) == [('supabase:' + ident,)])
    logout = re.search(r'<form\b[^>]*action="/auth/session/signout"[^>]*>(.*?)</form>', account, re.S).group(1)
    fields = {html.unescape(k): html.unescape(v) for k, v in re.findall(
        r'<input(?=[^>]*type="hidden")(?=[^>]*name="([^"]+)")(?=[^>]*value="([^"]*)")[^>]*>', logout)}
    check('Customer logout returns to customer sign-in', post('signout', fields)[2].get('Location') == '/customer/signin?notice=signed-out')
    check('Logged-out customer cannot reopen account', get('/customer/account')[0] in (302, 303, 401))
    ctx['clear_limits']()

    fields = form('/customer/signin', 'signin')
    status, _, headers = post('signin', {**fields, 'email': email, 'password': password})
    check('Same restaurant credentials sign in to the central account', status in (302, 303)
          and headers.get('Location') == '/customer/account' and email in get('/customer/account')[1])
    check('Returning customer reuses the same global identity',
          sql('SELECT COUNT(*) FROM bartide_auth_identities WHERE provider_user_id=?', (ident,))[0][0] == 1)
    customer_token = next(token for token, subject in reversed(ctx['tokens'].items()) if subject == ident)
    check('Customer has no access to another restaurant workspace', ctx['access']('owned-active', customer_token)[0] == 404)
    for raw, expected in [('//other.invalid/steal', '/customer/account'),
                          (target + '&tracking=private&payment_return=secret', target),
                          (target + '&table=' + table, '/order/owned-active'),
                          ('/order/owned-active?table=invalid', '/order/owned-active'),
                          ('/auth/session/signout', '/customer/account')]:
        fields = form('/customer/signin?return_to=' + urllib.parse.quote(raw, safe=''), 'signin')
        check('Customer return path is restricted: ' + raw.split('?')[0], fields.get('return_to') == expected)

    fields = form('/customer/forgot-password' + query, 'forgot')
    status, _, headers = post('forgot', {**fields, 'email': email})
    reset = headers.get('Location', '')
    check('Customer recovery opens customer code entry without leaking email', status in (302, 303)
          and reset.startswith('/customer/reset-password?notice=reset-email') and email not in reset)
    fields = form(reset, 'reset')
    replacement = 'customer-replacement-password'
    status, _, headers = post('reset', {**fields, 'email': email, 'code': '123456',
                                      'password': replacement, 'confirm_password': replacement})
    check('Customer reset returns to customer sign-in and keeps restaurant context', status in (302, 303)
          and headers.get('Location', '').startswith('/customer/signin?notice=password-reset')
          and urllib.parse.parse_qs(urllib.parse.urlsplit(headers.get('Location', '')).query).get('return_to') == [target])
    fields = form('/customer/signin', 'signin')
    check('Customer can sign in with the changed password', post('signin', {**fields, 'email': email, 'password': replacement})[2].get('Location') == '/customer/account')
    ctx['clear_limits']()
