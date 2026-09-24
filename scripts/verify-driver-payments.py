"""Driver Stripe adapter contract checks using real .NET provider/HTTP code.

Builds an isolated harness that links the production provider and transport. The
only substitute is a loopback HTTP Stripe fixture; no external accounts, money,
credentials or deployment are used. Workflow authorization is checked separately.
"""
import copy
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time
from xml.sax.saxutils import escape
from driver_payment_test_provider import DriverStripeFake, KEY, SECRET

ROOT = Path(__file__).resolve().parents[1]
RUN = ROOT / '.tools/driver-payment-provider-verification' / datetime.now(timezone.utc).strftime('%Y%m%d-%H%M%S-%f')
RUN.mkdir(parents=True)
SDK = ROOT / '.tools/dotnet-10.0.401/dotnet.exe'
RESULTS = []
fake = DriverStripeFake()
process = None


def check(label, passed, actual=None):
    RESULTS.append({'check': label, 'passed': bool(passed)})
    print(('PASS ' if passed else 'FAIL ') + label, flush=True)
    if not passed:
        raise AssertionError(label + ': ' + str(actual))


settings = {'DriverPayments:Environment': 'sandbox', 'DriverPayments:RestrictedKey': KEY,
            'DriverPayments:WebhookSecret': SECRET, 'DriverPayments:PublicBaseUrl': 'https://bartide.example.invalid',
            'DriverPayments:ApiBaseUrl': fake.url, 'DriverPayments:AllowLocalTestProvider': 'true',
            'DriverPayments:SetupEnabled': 'true', 'DriverPayments:PaymentsEnabled': 'true'}


def call(action, config=None, environment='Development', **values):
    process.stdin.write(json.dumps({'action': action, 'settings': config if config is not None else settings,
                                   'environment': environment, **values}) + '\n')
    process.stdin.flush()
    line = process.stdout.readline()
    if not line:
        raise RuntimeError('Provider harness stopped; inspect ' + str(RUN))
    return json.loads(line)


def result(action, **values):
    response = call(action, **values)
    if 'error' in response:
        raise AssertionError(str(response))
    return response


def review(label, action='checkout', **values):
    response = call(action, **values)
    check(label, response.get('error') == 'payment_review', response)


def mutate_review(label, obj, field, value, request, session):
    original = copy.deepcopy(obj)
    try:
        if value is ...:
            obj.pop(field, None)
        else:
            obj[field] = value
        review(label, request=request, session=session)
    finally:
        obj.clear()
        obj.update(original)


try:
    sources = ['TideCasa.Api/Features/DriverPayments/DriverPaymentOptions.cs',
               'TideCasa.Api/Features/DriverPayments/DriverStripeProvider.cs',
               'TideCasa.Api/Infrastructure/Payments/StripeHttpTransport.cs',
               'TideCasa.Api/Infrastructure/Payments/StripeJson.cs']
    includes = ''.join('<Compile Include="' + escape(str(ROOT / path), {'"': '&quot;'}) + '" />' for path in sources)
    (RUN / 'DriverProviderChecks.csproj').write_text('''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><FrameworkReference Include="Microsoft.AspNetCore.App" />''' + includes + '''
<Using Include="Microsoft.Extensions.Configuration" /><Using Include="Microsoft.Extensions.Hosting" /></ItemGroup></Project>''')
    (RUN / 'Program.cs').write_text('''using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using TideCasa.Api.Features.DriverPayments;
using TideCasa.Api.Features.RestaurantOrdering;
using TideCasa.Api.Infrastructure.Payments;
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
for (string? line; (line = Console.ReadLine()) is not null;)
{
    try
    {
        using var doc = JsonDocument.Parse(line); var r = doc.RootElement;
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(r.GetProperty("settings").Deserialize<Dictionary<string,string?>>(json)!).Build();
        var options = new DriverPaymentOptions(cfg, new FixtureEnvironment { EnvironmentName = r.GetProperty("environment").GetString()! });
        using var provider = new DriverStripeProvider(options); var ct = CancellationToken.None;
        string Text(string key) => r.GetProperty(key).GetString()!;
        object value = Text("action") switch
        {
            "options" => new { options.Configured, options.SetupEnabled, options.PaymentsEnabled, options.LocalTest, options.Sandbox, options.Environment },
            "createRecipient" => await provider.CreateRecipientAsync(r.GetProperty("recipient").Deserialize<DriverRecipientCreate>(json)!, Text("key"), ct),
            "recipient" => await provider.RecipientAsync(Text("account"), Text("user"), ct),
            "link" => new { url = await provider.OnboardingLinkAsync(Text("account"), Text("user"), Text("key"), ct) },
            "create" => await provider.CreateCheckoutAsync(r.GetProperty("request").Deserialize<DriverChargeRequest>(json)!, ct),
            "checkout" => await provider.CheckoutAsync(r.GetProperty("request").Deserialize<DriverChargeRequest>(json)!, Text("session"), ct),
            _ => throw new Exception("Unknown fixture command")
        };
        Console.WriteLine(JsonSerializer.Serialize(value, json));
    }
    catch (OrderingException e) { Console.WriteLine(JsonSerializer.Serialize(new { error = e.Code, status = e.Status }, json)); }
    catch (StripeTransportException e) { Console.WriteLine(JsonSerializer.Serialize(new { error = "transport", kind = e.Kind }, json)); }
    catch (Exception e) { Console.WriteLine(JsonSerializer.Serialize(new { error = e.GetType().Name }, json)); }
}
sealed class FixtureEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Development";
    public string ApplicationName { get; set; } = "DriverProviderChecks";
    public string ContentRootPath { get; set; } = ".";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
namespace TideCasa.Api.Features.RestaurantOrdering
{
    public sealed class OrderingException(string message, int status = 400, string code = "invalid_order") : Exception(message)
    { public int Status { get; } = status; public string Code { get; } = code; }
}
''')
    build = subprocess.run([str(SDK), 'build', str(RUN / 'DriverProviderChecks.csproj'), '--nologo'],
                           cwd=ROOT, capture_output=True, text=True)
    (RUN / 'build.log').write_text(build.stdout + build.stderr)
    check('Production provider/options/transport compile independently', build.returncode == 0, build.stdout + build.stderr)
    process = subprocess.Popen([str(SDK), str(RUN / 'bin/Debug/net10.0/DriverProviderChecks.dll')],
                               cwd=ROOT, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                               text=True, creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)

    check('Driver payments default disabled', call('options', config={}) == {
        'configured': False, 'setupEnabled': False, 'paymentsEnabled': False, 'localTest': False, 'sandbox': True, 'environment': 'sandbox'})
    check('Synthetic sandbox explicit configuration enabled', call('options')['paymentsEnabled'])
    for label, changed, environment in [
        ('production cannot use fixture host', {}, 'Production'),
        ('local fixture requires explicit flag', {'DriverPayments:AllowLocalTestProvider': 'false'}, 'Development'),
        ('fixture cannot select a remote host', {'DriverPayments:ApiBaseUrl': 'http://example.invalid'}, 'Development'),
        ('restricted sandbox key required', {'DriverPayments:RestrictedKey': 'sk_test_synthetic_000000000'}, 'Development'),
        ('environment/key mismatch fails closed', {'DriverPayments:Environment': 'live'}, 'Development'),
        ('unknown environment fails closed', {'DriverPayments:Environment': 'test'}, 'Development'),
        ('origin rejects credentials', {'DriverPayments:PublicBaseUrl': 'https://user@bartide.example.invalid'}, 'Development'),
        ('origin rejects path', {'DriverPayments:PublicBaseUrl': 'https://bartide.example.invalid/sub'}, 'Development'),
        ('origin rejects query', {'DriverPayments:PublicBaseUrl': 'https://bartide.example.invalid/?x=y'}, 'Development'),
        ('origin rejects public HTTP', {'DriverPayments:PublicBaseUrl': 'http://bartide.example.invalid'}, 'Development'),
    ]:
        response = call('options', config={**settings, **changed}, environment=environment)
        check(label, not response['configured'] and not response['paymentsEnabled'], response)
    live = {k: v for k, v in settings.items() if k not in ('DriverPayments:ApiBaseUrl', 'DriverPayments:AllowLocalTestProvider')}
    live.update({'DriverPayments:Environment': 'live', 'DriverPayments:RestrictedKey': 'rk_live_synthetic_unused_000000000'})
    response = call('options', config=live, environment='Production')
    check('Valid live settings remain disabled until verified', response['configured'] and not response['setupEnabled'] and not response['paymentsEnabled'])
    response = call('options', config={**live, 'DriverPayments:LivePaymentsVerified': 'true'}, environment='Production')
    check('Explicit live verification enables only matching live configuration', response['paymentsEnabled'] and not response['sandbox'])
    check('Webhook secret required for payment acceptance', not call('options', config={**settings, 'DriverPayments:WebhookSecret': ''})['paymentsEnabled'])
    before = len(fake.calls)
    response = call('createRecipient', config={}, recipient={'userId': 'u', 'name': 'Driver', 'email': 'driver@example.invalid'}, key='disabled')
    check('Disabled creation makes no provider calls', response.get('error') == 'driver_payments_disabled' and len(fake.calls) == before)

    recipient = {'userId': 'supabase:synthetic_driver', 'name': 'Synthetic Driver', 'email': 'driver@example.invalid'}
    created = result('createRecipient', recipient=recipient, key='recipient-saved-1')
    account = created['accountId']
    check('Recipient account is bound and ready', created['ready'])
    request_body = next(x['body'] for x in fake.calls if x['path'] == '/v2/core/accounts')
    check('Recipient setup requests Express US individual transfers only', request_body['dashboard'] == 'express'
          and request_body['identity'] == {'country': 'us', 'entity_type': 'individual'}
          and request_body['configuration'] == {'recipient': {'capabilities': {'stripe_balance': {'stripe_transfers': {'requested': True}}}}}
          and request_body['defaults']['responsibilities'] == {'fees_collector': 'application', 'losses_collector': 'application'})
    check('Recipient create exact retry retains one account', result('createRecipient', recipient=recipient, key='recipient-saved-1')['accountId'] == account and len(fake.accounts) == 1)
    review('Cross-driver recipient binding rejected', action='recipient', account=account, user='different-driver')
    account_obj = fake.accounts[account]
    original = copy.deepcopy(account_obj)
    for label, updater in [
        ('Unknown requirements block readiness', lambda a: a.pop('requirements')),
        ('Pending transfers block readiness', lambda a: a['configuration']['recipient']['capabilities']['stripe_balance']['stripe_transfers'].update(status='pending')),
        ('Pending payouts block readiness', lambda a: a['configuration']['recipient']['capabilities']['stripe_balance']['payouts'].update(status='pending')),
        ('Missing payouts block readiness', lambda a: a['configuration']['recipient']['capabilities']['stripe_balance'].pop('payouts')),
        ('Unknown applied schema blocks readiness', lambda a: a['configuration']['recipient'].update(applied='2026-09-24T00:00:00Z')),
        ('Company recipient blocks readiness', lambda a: a['identity'].update(entity_type='company')),
        ('Closed recipient blocks readiness', lambda a: a.update(closed=True)),
        ('Due requirements block readiness', lambda a: a['requirements']['summary'].update(minimum_deadline={'status': 'currently_due'})),
    ]:
        updater(account_obj)
        check(label, not result('recipient', account=account, user=recipient['userId'])['ready'])
        account_obj.clear(); account_obj.update(copy.deepcopy(original))
    link = result('link', account=account, user=recipient['userId'], key='link-saved-1')['url']
    check('Onboarding uses Stripe host', link.startswith('https://connect.stripe.com/'))
    link_body = next(x['body']['use_case']['account_onboarding'] for x in fake.calls if x['path'] == '/v2/core/account_links')
    check('Onboarding uses recipient configuration and driver earnings return', link_body['configurations'] == ['recipient']
          and link_body['return_url'] == 'https://bartide.example.invalid/driver/earnings?payment_setup=returned')
    fake.link_url = 'https://connect.stripe.com.evil.invalid/setup'
    review('Untrusted onboarding URL rejected', action='link', account=account, user=recipient['userId'], key='link-bad-2')
    fake.link_url = link

    request = {'paymentId': 'delivery-1', 'attemptId': 'attempt-1', 'tenantId': 'bistro', 'driverUserId': recipient['userId'],
               'accountId': account, 'driverPayCents': 1000, 'feeCents': 50, 'totalCents': 1050,
               'returnPath': '/workspace/bistro/driver-payments', 'expiresAt': int(time.time()) + 3600}
    opened = result('create', request=request)
    session = opened['sessionId']
    check('Approved network payment opens hosted Checkout', opened['state'] == 'open' and opened['url'].startswith('https://checkout.stripe.com/'))
    body = next(x['body'] for x in fake.calls if x['path'] == '/v1/checkout/sessions')
    check('Client1050 driver1000 fee50 wire amounts', body['line_items[0][price_data][unit_amount]'] == '1000'
          and body['line_items[1][price_data][unit_amount]'] == '50' and body['payment_intent_data[application_fee_amount]'] == '50')
    check('Payment destinations and card restriction are explicit', body['payment_intent_data[transfer_data][destination]'] == account
          and body['allowed_payment_method_types[0]'] == 'card' and 'payment_intent_data[on_behalf_of]' not in body)
    check('Session and intent identity metadata match', all(body.get('payment_intent_data[metadata][' + k[9:]) == v for k, v in body.items() if k.startswith('metadata[')))
    check('Retry retains one charge session and same request', result('create', request=request)['sessionId'] == session and len(fake.sessions) == 1)
    check('All provider operations use platform context and pinned API version', all(x['account'] is None and x['version'] == '2026-08-26.dahlia' for x in fake.calls))
    check('Open session is never paid on a redirect', result('checkout', request=request, session=session)['state'] == 'open')
    for field, value in [('totalCents', 999), ('feeCents', 51), ('driverPayCents', 49), ('returnPath', '//evil.invalid'), ('accountId', 'acct/unsafe'),
                         ('returnPage', -1), ('returnPage', 100001)]:
        before = len(fake.calls)
        review('Invalid saved request rejected before provider ' + field, request={**request, field: value}, session=session)
        check('No calls for invalid saved ' + field, len(fake.calls) == before)
    for field, value in [('amount_total', 1049), ('livemode', True), ('currency', 'eur'), ('expires_at', request['expiresAt'] + 1),
                         ('client_reference_id', 'other'), ('success_url', 'https://evil.invalid'), ('payment_method_types', ['card', 'us_bank_account']),
                         ('metadata', {}), ('url', 'https://checkout.stripe.com.evil.invalid/c/pay')]:
        mutate_review('Session mismatch rejected ' + field, fake.sessions[session], field, value, request, session)
    intent = fake.pay(session)
    check('Paid requires capture, transfer and application fee evidence', result('checkout', request=request, session=session)['state'] == 'paid')
    check('Replayed creation retrieves latest paid state', result('create', request=request)['state'] == 'paid')
    for field, value in [('livemode', True), ('amount_received', 1000), ('amount', 999), ('currency', 'eur'), ('status', 'processing'),
                         ('metadata', {}), ('application_fee_amount', 49), ('on_behalf_of', account), ('transfer_data', {'destination': 'acct_wrongdriver'}),
                         ('payment_method_types', ['link'])]:
        mutate_review('Intent mismatch rejected ' + field, intent, field, value, request, session)
    charge = intent['latest_charge']
    for field, value in [('paid', False), ('captured', False), ('amount_captured', 1000), ('livemode', True), ('currency', 'eur'),
                         ('application_fee_amount', 100), ('transfer_data', {'destination': 'acct_wrongdriver'}),
                         ('transfer', None), ('application_fee', None), ('disputed', ...), ('refunded', ...), ('amount_refunded', -1),
                         ('payment_method_details', {'type': 'us_bank_account'})]:
        mutate_review('Charge mismatch rejected ' + field, charge, field, value, request, session)
    transfer = fake.transfers[charge['transfer']]
    for field, value in [('destination', 'acct_wrongdriver'), ('amount', 1000), ('currency', 'eur'), ('source_transaction', 'ch_other'),
                         ('livemode', True), ('amount_reversed', 1), ('reversed', True), ('destination_payment', None)]:
        mutate_review('Transfer mismatch rejected ' + field, transfer, field, value, request, session)
    fee = fake.fees[charge['application_fee']]
    for field, value in [('amount', 51), ('account', 'acct_wrongdriver'), ('charge', 'py_other'), ('originating_transaction', 'ch_other'),
                         ('currency', 'eur'), ('livemode', True), ('amount_refunded', 1), ('refunded', True)]:
        mutate_review('Fee mismatch rejected ' + field, fee, field, value, request, session)
    charge['disputed'] = True
    check('Disputed charge is never marked paid', result('checkout', request=request, session=session)['state'] == 'disputed')
    charge['disputed'] = False
    charge['amount_refunded'] = 100
    check('Partial refund is reported for review', result('checkout', request=request, session=session)['state'] == 'refunded')
    charge['amount_refunded'] = 1050; charge['refunded'] = True
    check('Full refund is reported', result('checkout', request=request, session=session)['state'] == 'refunded')
    charge['amount_refunded'] = 0; charge['refunded'] = False

    own = {**request, 'paymentId': 'delivery-own-2', 'attemptId': 'attempt-own-2', 'feeCents': 0, 'totalCents': 1000}
    own_session = result('create', request=own)['sessionId']
    own_body = [x['body'] for x in fake.calls if x['path'] == '/v1/checkout/sessions'][-1]
    check('Own driver payment has no fee parameter or fee line', 'payment_intent_data[application_fee_amount]' not in own_body and 'line_items[1][quantity]' not in own_body)
    own_intent = fake.pay(own_session)
    check('Own driver receives entire client payment', result('checkout', request=own, session=own_session)['state'] == 'paid')
    mutate_review('Unexpected platform fee on own driver fails closed', own_intent['latest_charge'], 'application_fee', 'fee_unexpected', own, own_session)

    uncertain = {**request, 'paymentId': 'delivery-uncertain-3', 'attemptId': 'attempt-uncertain-3'}
    fake.fail_once.add('/v1/checkout/sessions')
    count = len(fake.sessions)
    response = call('create', request=uncertain)
    check('Uncertain create is surfaced without immediate duplicate write', response.get('error') == 'transport' and len(fake.sessions) == count + 1)
    response = result('create', request=uncertain)
    check('Durable retry recovers the same uncertain session', response['state'] == 'open' and len(fake.sessions) == count + 1)
    fake.sessions[response['sessionId']].update(status='expired', url=None)
    check('Expired unpaid session is identified', result('checkout', request=uncertain, session=response['sessionId'])['state'] == 'expired')

    paged = {**request, 'paymentId': 'delivery-paged-4', 'attemptId': 'attempt-paged-4', 'returnPage': 7}
    paged_session = result('create', request=paged)['sessionId']
    check('Checkout preserves later payment-list page on success and cancel',
          fake.sessions[paged_session]['success_url'] == 'https://bartide.example.invalid/workspace/bistro/driver-payments?payment_returned=true&page=7'
          and fake.sessions[paged_session]['cancel_url'] == 'https://bartide.example.invalid/workspace/bistro/driver-payments?payment_canceled=true&page=7')
    check('Saved requests without ReturnPage preserve original return URLs',
          fake.sessions[session]['success_url'] == 'https://bartide.example.invalid/workspace/bistro/driver-payments?payment_returned=true'
          and fake.sessions[session]['cancel_url'] == 'https://bartide.example.invalid/workspace/bistro/driver-payments?payment_canceled=true')
    mutate_review('Altered return page is rejected when reconciling', fake.sessions[paged_session], 'success_url',
                  'https://bartide.example.invalid/workspace/bistro/driver-payments?payment_returned=true&page=8', paged, paged_session)
    fake.pay(paged_session)
    check('Paged checkout verifies completed transfer and fee', result('checkout', request=paged, session=paged_session)['state'] == 'paid')

    print('Provider contract checks complete: ' + str(len(RESULTS)), flush=True)
finally:
    if process is not None:
        process.stdin.close()
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill(); process.wait(timeout=10)
        (RUN / 'harness.stderr.log').write_text(process.stderr.read())
    fake.close()
    (RUN / 'result.json').write_text(json.dumps({'results': RESULTS, 'allPassed': bool(RESULTS) and all(r['passed'] for r in RESULTS),
        'sourceSha256': {p: hashlib.sha256((ROOT / p).read_bytes()).hexdigest() for p in sources}}, indent=2))
    print('Evidence: ' + str(RUN), flush=True)
