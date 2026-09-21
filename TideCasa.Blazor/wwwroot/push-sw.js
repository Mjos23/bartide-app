// Notifications only. No fetch handler: private pages, sessions and orders are never cached.
self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', event => event.waitUntil(self.clients.claim()));
self.addEventListener('push', event => {
  let data = {};
  try { data = event.data?.json() || {}; } catch { /* Always show a visible fallback. */ }
  const safeText = (value, fallback, max) => typeof value === 'string' && value.trim() ? value.slice(0, max) : fallback;
  let url = '/';
  try {
    const candidate = new URL(data.url, self.location.origin);
    if (candidate.origin === self.location.origin && /^\/updates\/[A-Za-z0-9_-]{1,128}$/.test(candidate.pathname) && !candidate.search) {
      url = candidate.pathname + (/^#post-[A-Fa-f0-9-]{36}$/.test(candidate.hash) ? candidate.hash : '');
    }
  } catch { /* Never follow a supplied external notification URL. */ }
  event.waitUntil(self.registration.showNotification(safeText(data.title, 'A new update from your local spot', 120), {
    body: safeText(data.body, 'Open the app to see what’s happening.', 240),
    icon: '/app-icons/icon-192.png', badge: '/app-icons/icon-192.png',
    tag: safeText(data.tag, 'business-update', 90), renotify: false, data: { url }
  }));
});
self.addEventListener('notificationclick', event => {
  event.notification.close();
  const candidate = new URL(event.notification.data?.url || '/', self.location.origin);
  const url = candidate.origin === self.location.origin && (candidate.pathname === '/' || /^\/updates\/[A-Za-z0-9_-]{1,128}$/.test(candidate.pathname)) ? candidate.href : self.location.origin + '/';
  event.waitUntil((async () => {
    const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
    const existing = windows.find(client => client.url === url);
    if (existing) return existing.focus();
    return self.clients.openWindow(url);
  })());
});
