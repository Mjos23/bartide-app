(() => {
    if (window.bartideDriverFeePreview) return;
    window.bartideDriverFeePreview = true;
    const money = cents => new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(cents / 100);
    document.addEventListener('input', event => {
        if (!(event.target instanceof HTMLInputElement) || event.target.name !== 'pay') return;
        const form = event.target.closest('form[data-driver-fee-preview]');
        const output = form?.querySelector('[data-driver-fee-output]');
        if (!output) return;
        const match = /^(\d{1,4})(?:\.(\d{1,2}))?$/.exec(event.target.value);
        const cents = match ? Number(match[1]) * 100 + Number((match[2] || '').padEnd(2, '0')) : -1;
        if (cents < 50 || cents > 100000) { output.textContent = 'Enter driver pay from $0.50 to $1,000.00, with at most two decimal places.'; return; }
        const fee = Math.floor((cents * 5 + 50) / 100);
        output.textContent = `${money(cents)} driver pay + ${money(fee)} network fee = ${money(cents + fee)} client total per delivery.`;
    });
})();
