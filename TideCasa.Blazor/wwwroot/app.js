window.tideCasa = {
  openDialog(dialog) {
    const opener = document.activeElement;
    if (!dialog.open) dialog.showModal();
    const restore = () => {
      dialog.removeEventListener('click', outside);
      opener?.focus();
    };
    const outside = event => {
      if (event.target !== dialog) return;
      const rect = dialog.getBoundingClientRect();
      if (event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom) dialog.close();
    };
    dialog.addEventListener('click', outside);
    dialog.addEventListener('close', restore, {once:true});
  }
};

// Marketing dialogs need no retained server circuit, including after navigation.
document.addEventListener('click', event => {
  if (!event.target.closest('[data-purchase-open]')) return;
  const dialog = document.getElementById('purchase-dialog');
  if (dialog) window.tideCasa.openDialog(dialog);
});

// Keep a mistyped confirmation on the current form. The server repeats this
// check before using the recovery code, including when JavaScript is disabled.
function validatePasswordConfirmation(form) {
  const password = form.elements.namedItem('password');
  const confirmation = form.elements.namedItem('confirm_password');
  confirmation.setCustomValidity(confirmation.value && confirmation.value !== password.value
    ? 'Passwords don’t match. Enter the same new password in both fields.' : '');
  return confirmation;
}

document.addEventListener('input', event => {
  const form = event.target.form;
  if (form?.hasAttribute('data-confirm-password')) validatePasswordConfirmation(form);
});

document.addEventListener('submit', event => {
  const form = event.target;
  if (!form.hasAttribute('data-confirm-password')) return;
  const confirmation = validatePasswordConfirmation(form);
  if (!confirmation.checkValidity()) {
    event.preventDefault();
    confirmation.reportValidity();
  }
});

// Install prompts are browser-owned, single-use, and only invoked after a click.
(() => {
  let pendingPrompt = null;
  const installed = () => window.matchMedia('(display-mode: standalone)').matches || navigator.standalone === true;
  const sync = () => {
    document.querySelectorAll('[data-install-control]').forEach(el => { el.hidden = installed(); });
    const button = document.querySelector('[data-install-prompt]');
    if (button) button.hidden = !pendingPrompt || installed();
  };
  window.addEventListener('beforeinstallprompt', event => { event.preventDefault(); pendingPrompt = event; sync(); });
  window.addEventListener('appinstalled', () => { pendingPrompt = null; document.getElementById('home-screen-dialog')?.close(); sync(); });
  window.matchMedia('(display-mode: standalone)').addEventListener('change', sync);
  document.addEventListener('click', async event => {
    if (event.target.closest('[data-install-open]')) window.tideCasa.openDialog(document.getElementById('home-screen-dialog'));
    if (event.target.closest('[data-install-close]')) document.getElementById('home-screen-dialog')?.close();
    const button = event.target.closest('[data-install-prompt]');
    if (!button || !pendingPrompt) return;
    const prompt = pendingPrompt; pendingPrompt = null; sync(); button.disabled = true;
    const message = document.querySelector('[data-install-message]');
    try {
      await prompt.prompt(); const choice = await prompt.userChoice;
      if (choice.outcome === 'accepted') document.getElementById('home-screen-dialog')?.close();
      else if (message) message.textContent = 'You can keep browsing, or add the app later from your browser menu.';
    } catch { if (message) message.textContent = 'Use the browser instructions above to add the app.'; }
    finally { button.disabled = false; }
  });
  sync();
})();
// Repeatable checkout jump, even when the URL already contains this section hash.
window.tideOrderingCheckout = {
  open() {
    const checkout = document.getElementById('ordering-checkout');
    if (!checkout) return;
    checkout.scrollTop = 0;
    checkout.scrollIntoView({ block: 'start', behavior: 'instant' });
    checkout.focus({ preventScroll: true });
  }
};
