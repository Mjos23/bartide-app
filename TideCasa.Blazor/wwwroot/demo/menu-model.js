(function (root) {
  'use strict';
  function priceLabel(item) {
    if (item.price_cents === null) {
      if (typeof item.price_label !== 'string' || !item.price_label.trim()) throw new Error('Missing price explanation');
      return item.price_label;
    }
    if (!Number.isSafeInteger(item.price_cents) || item.price_cents < 0 || item.price_cents > 1000000) throw new Error('Invalid price');
    return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD', minimumFractionDigits: item.price_cents % 100 ? 2 : 0 }).format(item.price_cents / 100);
  }
  function selectItems(menu, category) {
    if (category !== 'all' && !menu.categories.some(c => c.id === category)) throw new Error('Unknown category');
    return menu.items.filter(item => category === 'all' || item.category === category);
  }
  const api = { priceLabel, selectItems };
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.BartideMenuModel = api;
})(typeof window !== 'undefined' ? window : globalThis);
