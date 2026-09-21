(() => {
  'use strict';
  const explain = (panel, text) => { panel.querySelector('[data-push-status]').textContent = text; };
  const keyBytes = text => Uint8Array.from(atob(text.replace(/-/g, '+').replace(/_/g, '/').padEnd(Math.ceil(text.length / 4) * 4, '=')), c => c.charCodeAt(0));
  async function endpointHash(endpoint) {
    const canonical = new URL(endpoint).href.replace(/%[0-9a-f]{2}/gi, escape => {
      const character = String.fromCharCode(parseInt(escape.slice(1), 16));
      return /^[A-Za-z0-9._~-]$/.test(character) ? character : escape.toUpperCase();
    });
    const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(canonical));
    return Array.from(new Uint8Array(digest), b => b.toString(16).padStart(2, '0')).join('');
  }
  async function initialize(panel) {
    if (panel.dataset.initialized) return;
    panel.dataset.initialized = 'true';
    const button = panel.querySelector('[data-push-enable]');
    const appleMobile = /iPad|iPhone|iPod/.test(navigator.userAgent) || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);
    if (appleMobile && !(navigator.standalone || matchMedia('(display-mode: standalone)').matches)) {
      explain(panel, 'Add this app to your Home Screen, then open it from there to turn on alerts.'); return;
    }
    if (!window.isSecureContext || !('serviceWorker' in navigator) || !('PushManager' in window) || !('Notification' in window)) {
      explain(panel, 'This browser cannot receive phone notifications here. Try a supported browser on the secure app, or read the updates below.'); return;
    }
    if (Notification.permission === 'denied') {
      explain(panel, 'Notifications are blocked for this app. Allow them in your browser or device settings, then reload.'); return;
    }
    try {
      const registration = await navigator.serviceWorker.getRegistration('/');
      const subscription = registration ? await registration.pushManager.getSubscription() : null;
      if (subscription) {
        const hash = await endpointHash(subscription.endpoint);
        const item = Array.from(panel.querySelectorAll('[data-push-hash]')).find(x => x.dataset.pushHash === hash);
        if (item) {
          button.textContent = 'Updates are on for this device';
          explain(panel, 'You’ll get new posts from this business. Use “Manage your alerts” below to turn them off.');
          const form = Array.from(document.querySelectorAll('[data-device-form]')).find(x => x.dataset.deviceForm === item.dataset.pushId);
          if (form) form.querySelector('[data-current-device]').hidden = false;
          return;
        }
      }
      button.disabled = false;
      explain(panel, 'Only new posts from this business. You can turn them off below.');
    } catch {
      explain(panel, 'We couldn’t check this device. Reload the page and try again.'); return;
    }
    button.addEventListener('click', async () => {
      button.disabled = true;
      try {
        // Permission must originate in this click, before asynchronous setup.
        const permission = await Notification.requestPermission();
        if (permission !== 'granted') {
          explain(panel, permission === 'denied' ? 'Notifications are blocked. You can allow them in your device settings.' : 'You haven’t allowed notifications. You can choose again when you’re ready.');
          button.disabled = permission === 'denied'; return;
        }
        const registration = await navigator.serviceWorker.register('/push-sw.js', { scope: '/' });
        // Registration activation is bounded, rather than waiting indefinitely on ready.
        if (!registration.active) await new Promise((resolve, reject) => {
          const timer = setTimeout(() => reject(new Error('activation')), 15000);
          const worker = registration.installing || registration.waiting;
          if (!worker) { clearTimeout(timer); reject(new Error('activation')); return; }
          const done = () => {
            if (worker.state === 'activated') { clearTimeout(timer); resolve(); }
            else if (worker.state === 'redundant') { clearTimeout(timer); reject(new Error('activation')); }
          };
          worker.addEventListener('statechange', done); done();
        });
        let subscription = await registration.pushManager.getSubscription();
        const applicationServerKey = keyBytes(panel.dataset.vapid);
        if (subscription?.options.applicationServerKey && !Array.from(new Uint8Array(subscription.options.applicationServerKey)).every((b, i) => b === applicationServerKey[i])) {
          explain(panel, 'This device has an older notification setup. Reset this app’s notification permission in your browser settings, then reload.'); return;
        }
        subscription ??= await registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey });
        const details = subscription.toJSON();
        const fields = new URLSearchParams({
          __RequestVerificationToken: panel.querySelector('input[name="__RequestVerificationToken"]').value,
          endpoint: details.endpoint, p256dh: details.keys.p256dh, auth: details.keys.auth, consent: 'true'
        });
        const response = await fetch(panel.dataset.subscribe, { method: 'POST', credentials: 'same-origin', redirect: 'error',
          headers: { 'Content-Type': 'application/x-www-form-urlencoded;charset=UTF-8' }, body: fields, signal: AbortSignal.timeout(20000) });
        if (!response.ok) {
          let code;
          try { code = (await response.json()).code; } catch { /* Generic failure is safe. */ }
          explain(panel, response.status === 401 ? 'Please sign in again, then return to this page.' : code === 'device-account' ? 'This browser’s alerts belong to another account. Use that account, or reset this app’s notification permission in your browser settings before trying again.' : response.status === 503 ? 'Phone alerts are unavailable right now. Your browser permission is saved; try again later.' : response.status === 429 ? 'Too many attempts or saved devices. Stop alerts on an unused device or try again later.' : 'Your notification preference could not be saved. Reload and try again.');
          button.disabled = false; return;
        }
        // Reload from server-owned preferences, rather than claiming success from browser permission alone.
        location.reload();
      } catch {
        explain(panel, 'We couldn’t finish setting up alerts. Refresh to check your settings, then try again.'); button.disabled = false;
      }
    });
  }
  const start = () => document.querySelectorAll('[data-post-push]').forEach(panel => { void initialize(panel); });
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, { once: true }); else start();
  document.addEventListener('enhancedload', start);
})();
