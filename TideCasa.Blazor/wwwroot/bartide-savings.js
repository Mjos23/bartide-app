(() => {
    "use strict";
    // A delegated handler survives Blazor enhanced navigation without duplicate listeners.
    if (window.barTideSavingsReady) return;
    window.barTideSavingsReady = true;
    const money = new Intl.NumberFormat("en-US", { style: "currency", currency: "USD", maximumFractionDigits: 0 });
    function update(root) {
        const salesInput = root.querySelector("[data-savings-sales]");
        const rateInput = root.querySelector("[data-savings-rate]");
        const sales = Number(salesInput.value);
        const rate = Number(rateInput.value);
        const valid = salesInput.value.trim() !== "" && Number.isFinite(sales) && sales >= 0 && sales <= 1000000 && [15, 25, 30].includes(rate);
        const error = root.querySelector("[data-savings-error]");
        error.hidden = valid;
        salesInput.setAttribute("aria-invalid", String(!valid));
        const commission = root.querySelector("[data-savings-commission]");
        const difference = root.querySelector("[data-savings-difference]");
        const panel = root.querySelector("[data-savings-panel]");
        if (!valid) {
            commission.textContent = "—";
            difference.textContent = "—";
            panel.removeAttribute("data-negative");
            root.querySelector("[data-savings-label]").textContent = "Potential monthly savings*";
            root.querySelector("[data-savings-caption]").textContent = "enter your sales to compare";
            return;
        }
        // Compare the same whole-dollar amounts that are shown in the three figures.
        const fees = Math.round(sales * rate / 100);
        const saving = fees - 149;
        commission.textContent = money.format(fees);
        difference.textContent = money.format(saving);
        panel.toggleAttribute("data-negative", saving < 0);
        root.querySelector("[data-savings-label]").textContent = saving <= 0 ? "Monthly cost difference*" : "Potential monthly savings*";
        root.querySelector("[data-savings-caption]").textContent = saving < 0 ? "BarTide costs more at this volume" : saving === 0 ? "about the same monthly cost" : "you could keep, before other costs";
    }
    function onInput(event) {
        if (!(event.target instanceof Element) || !event.target.matches("[data-savings-sales], [data-savings-rate]")) return;
        const root = event.target.closest("[data-bartide-savings]");
        if (root) update(root);
    }
    document.addEventListener("input", onInput);
    document.addEventListener("change", onInput);
})();
