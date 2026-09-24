// Browser boundary tests use the real module with synthetic DOM/Stripe interfaces; no provider is contacted.
import fs from 'node:fs';
import vm from 'node:vm';
import assert from 'node:assert/strict';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const results = [];
function check(label, condition) { assert.ok(condition, label); results.push({ check: label, passed: true }); console.log('PASS ' + label); }
const secret = 'accs_secret_synthetic_not_persisted_000000';
const publishableKey = 'pk_test_synthetic_000000000';
async function fixture({ valid = true, origin = 'https://bar.example.invalid', responseStatus = 200, expires = 3600, loadFails = false, initFails = false } = {}) {
  const requests = [], reports = [], components = [], listeners = new Map(); let initialization, logouts = 0;
  const form = { action: origin + '/merchant-payments/bistro/session', reportValidity: () => valid };
  const container = { children: [], replaceChildren(...children) { this.children = children; } };
  const stripe = { init(options) {
    if (initFails) throw new Error(secret);
    initialization = options;
    return { create(name) { const component = { name, setCollectionOptions(options) { this.collection = options; }, setOnExit(fn) { this.exit = fn; }, setOnLoadError(fn) { this.error = fn; } }; components.push(component); return component; }, async logout() { logouts++; } };
  } };
  const window = { StripeConnect: loadFails ? undefined : stripe, addEventListener: (name, fn) => listeners.set(name, fn), removeEventListener: name => listeners.delete(name) };
  const document = { getElementById: () => form, createElement: () => ({ remove() {} }), head: { appendChild(script) { check('Loader uses the fixed Stripe origin', script.src === 'https://connect-js.stripe.com/v1.0/connect.js'); script.onerror(); } } };
  const context = vm.createContext({ window, document, location: { href: 'https://bar.example.invalid/workspace/bistro/payments', origin: 'https://bar.example.invalid' }, URL, URLSearchParams, AbortController, setTimeout, clearTimeout,
    FormData: class { constructor() { return new Map([['__RequestVerificationToken', 'synthetic-csrf'], ['request_key', 'synthetic-key'], ['confirm_us', 'true']]); } },
    fetch: async (url, options) => { requests.push({ url, options }); return { ok: responseStatus === 200, async json() { return { publishableKey, clientSecret: secret, expiresAt: Math.floor(Date.now() / 1000) + expires }; } }; }
  });
  const source = new vm.SourceTextModule(fs.readFileSync(path.join(root, 'TideCasa.Blazor/wwwroot/merchant-onboarding.js'), 'utf8'), { context });
  await source.link(specifier => {
    assert.equal(specifier, '/vendor/stripe-connect/pure.esm.js');
    return new vm.SyntheticModule(['loadConnectAndInitialize'], function () { this.setExport('loadConnectAndInitialize', options => window.StripeConnect.init(options)); }, { context });
  }); await source.evaluate();
  const callback = { invokeMethodAsync(name, value) { reports.push({ name, value }); return Promise.resolve(); } };
  return { module: source.namespace, container, requests, reports, components, listeners, get initialization() { return initialization; }, get logouts() { return logouts; }, async mount() { return source.namespace.mount('form', container, callback); } };
}
try {
  const a = await fixture(); check('Embedded setup mounts after confirmed details', await a.mount() === 'mounted');
  check('Only onboarding and notification components are rendered', a.container.children.map(c => c.name).join(',') === 'notification-banner,account-onboarding');
  check('Session request uses cookie, no-store and CSRF', a.requests[0].options.credentials === 'same-origin' && a.requests[0].options.cache === 'no-store' && a.requests[0].options.redirect === 'error' && a.requests[0].options.body.get('__RequestVerificationToken') === 'synthetic-csrf');
  check('Session endpoint has no browser account selector', a.requests[0].url === '/merchant-payments/bistro/session' && !a.requests[0].options.body.has('account'));
  check('Client secret goes directly to Stripe loader callback', await a.initialization.fetchClientSecret() === secret && a.requests.length === 1);
  check('Session renewal reauthorizes through the server', await a.initialization.fetchClientSecret() === secret && a.requests.length === 2);
  a.components[1].exit(); a.components[0].error();
  check('Exit and load failures report safe states without secrets', JSON.stringify(a.reports) === '[{"name":"Report","value":"exit"},{"name":"Report","value":"failed"}]');
  await a.module.dispose(a.container);
  check('Closing setup logs out and removes components', a.logouts === 1 && a.container.children.length === 0 && a.listeners.size === 0);
  let closed = false; try { await a.initialization.fetchClientSecret(); } catch { closed = true; }
  check('Closed session cannot refresh credentials', closed);
  for (const [label, options, expected] of [
    ['unconfirmed business', { valid: false }, 'confirm'], ['foreign endpoint', { origin: 'https://foreign.invalid' }, 'unavailable'],
    ['expired session', { expires: -10 }, 'unavailable'], ['denied session', { responseStatus: 403 }, 'unavailable'],
    ['unavailable provider', { responseStatus: 503 }, 'unavailable'], ['failed loader', { loadFails: true }, 'unavailable'], ['failed component initialization', { initFails: true }, 'unavailable']
  ]) {
    const f = await fixture(options); check('Safe fallback for ' + label, await f.mount() === expected && f.container.children.length === 0 && !JSON.stringify(f.reports).includes(secret));
    if (options.valid === false || options.origin) check('No session request for ' + label, f.requests.length === 0);
  }
  const b = await fixture(); await b.mount(); b.listeners.get('pagehide')(); await new Promise(resolve => setTimeout(resolve, 0));
  check('Leaving the page disposes the Stripe session', b.logouts === 1 && b.container.children.length === 0);
} finally {
  fs.writeFileSync(path.join(root, 'output/stripe-api/embedded-js-checks.json'), JSON.stringify(results, null, 2));
}
console.log('Completed ' + results.length + ' embedded browser boundary checks.');
