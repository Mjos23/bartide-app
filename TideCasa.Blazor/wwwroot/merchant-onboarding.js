import { loadConnectAndInitialize } from '/vendor/stripe-connect/pure.esm.js';

let loader;
const sessions = new WeakMap();

function loadStripe() {
  if (typeof window.StripeConnect?.init === 'function') return Promise.resolve();
  if (loader) return loader;
  loader = new Promise((resolve, reject) => {
    const script = document.createElement('script');
    const timer = setTimeout(() => { script.remove(); reject(new Error('loader_unavailable')); }, 15000);
    script.onload = () => { clearTimeout(timer); typeof window.StripeConnect?.init === 'function' ? resolve() : reject(new Error('loader_unavailable')); };
    script.src = 'https://connect-js.stripe.com/v1.0/connect.js';
    script.async = true;
    script.onerror = () => { clearTimeout(timer); script.remove(); reject(new Error('loader_unavailable')); };
    document.head.appendChild(script);
  }).catch(error => { loader = undefined; throw error; });
  return loader;
}

export async function mount(formId, container, callback) {
  const form = document.getElementById(formId);
  if (!form || !form.reportValidity()) return 'confirm';
  const endpoint = new URL(form.action, location.href);
  if (endpoint.origin !== location.origin || !/^\/merchant-payments\/[^/]+\/session$/.test(endpoint.pathname)) return 'unavailable';
  await dispose(container);
  const state = { closed: false, instance: null, abort: new AbortController() };
  sessions.set(container, state);
  const report = code => { if (!state.closed) callback.invokeMethodAsync('Report', code).catch(() => {}); };
  async function requestSession() {
    if (state.closed) throw new Error('session_closed');
    // Fresh authorization and CSRF validation also run whenever ConnectJS refreshes its session.
    const response = await fetch(endpoint.pathname, {
      method: 'POST', body: new URLSearchParams(new FormData(form)), credentials: 'same-origin',
      cache: 'no-store', redirect: 'error', signal: state.abort.signal
    });
    if (!response.ok) throw new Error('session_unavailable');
    const value = await response.json();
    if (state.closed || !/^pk_test_[A-Za-z0-9_]{12,240}$/.test(value.publishableKey) || typeof value.clientSecret !== 'string'
      || value.clientSecret.length < 16 || value.clientSecret.length > 1024 || !Number.isSafeInteger(value.expiresAt) || value.expiresAt * 1000 <= Date.now()) throw new Error('session_unavailable');
    return value;
  }
  try {
    let first = await requestSession();
    const publishableKey = first.publishableKey;
    await loadStripe();
    if (state.closed) return 'unavailable';
    state.instance = loadConnectAndInitialize({
      publishableKey,
      fetchClientSecret: async () => {
        try {
          const value = first || await requestSession(); first = null;
          if (state.closed || value.publishableKey !== publishableKey) throw new Error('session_unavailable');
          return value.clientSecret;
        } catch { report('failed'); throw new Error('session_unavailable'); }
      },
      appearance: { variables: { colorPrimary: '#255c47', borderRadius: '12px' } }
    });
    const banner = state.instance.create('notification-banner');
    const onboarding = state.instance.create('account-onboarding');
    onboarding.setCollectionOptions({ fields: 'eventually_due' });
    onboarding.setOnExit(() => report('exit'));
    banner.setOnLoadError(() => report('failed'));
    onboarding.setOnLoadError(() => report('failed'));
    container.replaceChildren(banner, onboarding);
    state.onPageHide = () => { dispose(container); };
    window.addEventListener('pagehide', state.onPageHide, { once: true });
    return 'mounted';
  } catch { await dispose(container); return 'unavailable'; }
}

export async function dispose(container) {
  const state = sessions.get(container);
  if (state) {
    state.closed = true;
    state.abort.abort();
    if (state.onPageHide) window.removeEventListener('pagehide', state.onPageHide);
    sessions.delete(container);
    try { await state.instance?.logout(); } catch { }
  }
  container.replaceChildren();
}
