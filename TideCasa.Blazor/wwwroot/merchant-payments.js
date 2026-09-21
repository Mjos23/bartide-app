(() => {
  const key = slug => 'tide-restaurant-checkout:' + slug;
  window.tideMerchantCheckout = {
    save(slug, request, orderId) {
      try {
        if (!/^[a-zA-Z0-9_-]{1,128}$/.test(slug) || !request || !/^[a-f0-9]{64}$/.test(request.trackingKey)) return false;
        sessionStorage.setItem(key(slug), JSON.stringify({ version: 1, slug, request, orderId: orderId || null }));
        return true;
      } catch { return false; }
    },
    read(slug) {
      try {
        const raw = sessionStorage.getItem(key(slug));
        if (!raw || raw.length > 32000) return null;
        const value = JSON.parse(raw);
        return value.version === 1 && value.slug === slug ? value : null;
      } catch { return null; }
    },
    clear(slug) { try { sessionStorage.removeItem(key(slug)); } catch { } }
  };
})();
