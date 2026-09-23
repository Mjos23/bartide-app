(() => {
  const validSlug = value => /^[A-Za-z0-9_-]{1,128}$/.test(value || '');
  const options = { credentials: 'same-origin', cache: 'no-store', redirect: 'error' };
  async function save(slug, orderId, trackingKey, expectedUser) {
    if (!validSlug(slug) || !/^[a-f0-9-]{36}$/i.test(orderId || '') || !/^[a-f0-9]{64}$/i.test(trackingKey || '')) return false;
    try {
      const root = '/customer-orders/' + encodeURIComponent(slug);
      const sessionResponse = await fetch(root + '/session', options);
      if (!sessionResponse.ok) return false;
      const session = await sessionResponse.json();
      if (!session.token || !session.user || (expectedUser && session.user !== expectedUser)) return false;
      const body = new URLSearchParams({ __RequestVerificationToken: session.token, user: session.user, order_id: orderId, tracking_key: trackingKey });
      const response = await fetch(root + '/save', { ...options, method: 'POST', body });
      return response.ok && (await response.json()).saved === true;
    } catch { return false; }
  }
  window.tideCustomerOrders = { save };
  // These account pages use full navigations; no background work on the ordering page.
  const section = document.querySelector('[data-customer-orders]');
  if (!section || !validSlug(section.dataset.slug)) return;
  const slug = section.dataset.slug, user = section.dataset.user;
  const receipt = window.tideMerchantCheckout?.read(slug);
  const panel = document.querySelector('[data-customer-save-panel]');
  const cards = [...section.querySelectorAll('[data-order]')];
  if (panel && receipt?.orderId && receipt?.request?.trackingKey && !cards.some(c => c.dataset.order === receipt.orderId)) {
    panel.hidden = false;
    panel.querySelector('[data-customer-save]').addEventListener('click', async event => {
      event.currentTarget.disabled = true;
      const saved = await save(slug, receipt.orderId, receipt.request.trackingKey, user);
      if (saved) { location.reload(); return; }
      panel.querySelector('[data-customer-save-message]').textContent = 'This receipt could not be saved. It may belong to another account, or your session changed. Refresh and try again; do not place the order again.';
      panel.querySelector('[data-customer-save]').disabled = false;
    });
  }
  let timer, busy = false, stopped = false;
  const status = section.querySelector('[data-customer-status]');
  const active = () => cards.some(c => c.dataset.active === 'true');
  async function refresh() {
    clearTimeout(timer);
    if (document.hidden || stopped || busy || !active()) return;
    busy = true;
    try {
      const response = await fetch('/customer-orders/' + encodeURIComponent(slug) + '/status', options);
      if (!response.ok) throw new Error();
      const data = await response.json();
      if (data.user !== user) {
        stopped = true;
        section.querySelectorAll('[data-order]').forEach(c => c.hidden = true);
        if (status) status.textContent = 'Your signed-in account changed. Refresh to see that account’s orders.';
        return;
      }
      if (!Array.isArray(data.orders)) throw new Error();
      for (const card of cards) {
        const item = data.orders.find(o => o.id === card.dataset.order);
        if (!item || typeof item.label !== 'string' || typeof item.active !== 'boolean') throw new Error();
        card.querySelector('[data-order-state]').textContent = item.label;
        card.dataset.active = String(item.active);
      }
      if (status) status.textContent = 'Updated ' + new Date().toLocaleTimeString();
    } catch { if (status) status.textContent = 'Updates are interrupted. The last checked status is shown. Use Refresh to try again.'; }
    finally { busy = false; if (!document.hidden && !stopped && active()) timer = setTimeout(refresh, 30000); }
  }
  document.addEventListener('visibilitychange', () => { clearTimeout(timer); if (!document.hidden) refresh(); });
  window.addEventListener('pagehide', () => { stopped = true; clearTimeout(timer); });
  if (!document.hidden && active()) timer = setTimeout(refresh, 30000);
})();
