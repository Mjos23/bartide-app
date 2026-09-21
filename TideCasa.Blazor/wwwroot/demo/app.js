(function () {
  'use strict';
  const byId = id => document.getElementById(id);
  let installPrompt = null;
  const installArea = byId('install-menu');
  const installButton = byId('install-button');
  const standalone = window.matchMedia('(display-mode: standalone)');
  function hideInstalled() { if (standalone.matches || navigator.standalone === true) installArea.hidden = true; }
  hideInstalled();
  standalone.addEventListener('change', hideInstalled);
  window.addEventListener('beforeinstallprompt', event => {
    event.preventDefault(); installPrompt = event; installButton.hidden = false;
  });
  window.addEventListener('appinstalled', () => { installArea.hidden = true; installPrompt = null; });
  installButton.addEventListener('click', async () => {
    const prompt = installPrompt; if (!prompt) return;
    installButton.disabled = true;
    try { await prompt.prompt(); await prompt.userChoice; }
    catch { installArea.querySelector('details').open = true; }
    finally { installPrompt = null; installButton.hidden = true; installButton.disabled = false; }
  });
  function element(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  }
  try {
    const menu = window.BARTIDE_MENU;
    const { priceLabel, selectItems } = window.BartideMenuModel;
    if (menu?.schema !== 'bartide-menu/1' || !Array.isArray(menu.items)) throw new Error('Invalid menu');
    menu.items.forEach(priceLabel);
    document.title = menu.venue.name + ' | Menu';
    byId('venue-name').textContent = menu.venue.name;
    byId('venue-area').textContent = menu.venue.area;
    byId('venue-tagline').textContent = menu.venue.tagline;
    byId('service-note').textContent = menu.venue.service_note;
    byId('venue-hours').textContent = menu.venue.hours_text || '';
    if (menu.venue.website_url) {
      const website = new URL(menu.venue.website_url);
      if (!['https:', 'http:'].includes(website.protocol) || website.username || website.password) throw new Error('Invalid website URL');
      byId('current-website').href = website.href;
      byId('current-website').hidden = false;
    }
    const dishDialog = byId('dish-dialog');
    let dishOpener = null;
    function showDish(item, opener) {
      dishOpener = opener;
      byId('dish-large-photo').src = item.photo_src;
      byId('dish-large-photo').alt = item.photo_alt;
      byId('dish-full-photo').href = item.photo_src;
      byId('dish-title').textContent = item.name;
      byId('dish-category').textContent = menu.categories.find(c => c.id === item.category)?.label || '';
      byId('dish-price').textContent = priceLabel(item);
      byId('dish-description').textContent = item.details || item.description;
      dishDialog.showModal(); document.body.classList.add('dish-open');
    }
    byId('close-dish').addEventListener('click', () => dishDialog.close());
    dishDialog.addEventListener('close', () => { document.body.classList.remove('dish-open'); if (dishOpener?.isConnected) dishOpener.focus(); });
    dishDialog.addEventListener('click', event => { if (event.target === dishDialog) { const box = dishDialog.getBoundingClientRect(); if (event.clientX < box.left || event.clientX > box.right || event.clientY < box.top || event.clientY > box.bottom) dishDialog.close(); } });
    const filters = [{ id: 'all', label: 'Everything' }, ...menu.categories];
    function render(category) {
      const selected = selectItems(menu, category);
      const fragment = document.createDocumentFragment();
      for (const group of menu.categories) {
        const items = selected.filter(item => item.category === group.id);
        if (!items.length) continue;
        const section = element('section', 'menu-group');
        const heading = element('h3', 'group-title', group.label);
        heading.id = 'group-' + group.id;
        section.setAttribute('aria-labelledby', heading.id);
        section.append(heading);
        const list = element('ul', 'item-grid');
        for (const item of items) {
          const card = element('li', 'menu-item');
          if (item.photo_src) {
            if (!/^assets\/menu\/[a-z0-9-]+\.(webp|jpg|png)$/.test(item.photo_src) || typeof item.photo_alt !== 'string' || !item.photo_alt.trim()) throw new Error('Invalid menu photo');
            const photo = element('img', 'dish-photo');
            photo.src = item.photo_src; photo.alt = item.photo_alt;
            photo.width = 720; photo.height = 480; photo.loading = 'lazy'; photo.decoding = 'async';
            photo.addEventListener('error', () => { photo.hidden = true; });
            const photoButton = element('button', 'dish-photo-button');
            photoButton.type = 'button'; photoButton.setAttribute('aria-label', 'View ' + item.name + ' details');
            photoButton.setAttribute('aria-haspopup', 'dialog');
            photoButton.append(photo, element('span', 'photo-action', 'View dish ↗'));
            photoButton.addEventListener('click', () => showDish(item, photoButton));
            card.append(photoButton);
          }
          const details = element('div', 'dish-details');
          const top = element('div', 'item-top');
          top.append(element('h4', '', item.name), element('span', item.price_cents === null ? 'price market-price' : 'price', priceLabel(item)));
          details.append(top, element('p', 'description', item.description));
          if (!item.available) { card.classList.add('sold-out'); details.append(element('p', 'availability', 'Currently unavailable')); }
          card.append(details);
          list.append(card);
        }
        section.append(list); fragment.append(section);
      }
      byId('menu-sections').replaceChildren(fragment);
      byId('result-count').textContent = selected.length + (selected.length === 1 ? ' menu item' : ' menu items');
      for (const button of byId('categories').children) button.setAttribute('aria-pressed', String(button.dataset.category === category));
    }
    for (const filter of filters) {
      const button = element('button', 'category', filter.label);
      button.type = 'button'; button.dataset.category = filter.id;
      button.addEventListener('click', () => render(filter.id));
      byId('categories').append(button);
    }
    render('all');
  } catch (error) {
    byId('menu-error').hidden = false;
    byId('venue-name').textContent = 'Menu unavailable';
    byId('categories').replaceChildren();
    byId('menu-sections').replaceChildren();
    byId('result-count').textContent = '';
  }
})();
