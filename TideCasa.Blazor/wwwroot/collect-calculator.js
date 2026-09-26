"use strict";

// This comparison runs entirely in the browser: no storage, requests or analytics.
(() => {
    const debt = document.getElementById("fee-debt");
    const rate = document.getElementById("fee-rate");
    const program = document.getElementById("fee-program");
    const difference = document.getElementById("fee-difference");
    const validation = document.getElementById("fee-validation");
    if (!debt || !rate || !program || !difference || !validation) return;
    const currency = new Intl.NumberFormat("en-US", { style: "currency", currency: "USD", maximumFractionDigits: 2 });
    const update = () => {
        const amount = debt.valueAsNumber;
        const percent = rate.valueAsNumber;
        const validAmount = Number.isFinite(amount) && amount >= 0 && amount <= 10000000;
        const validPercent = Number.isFinite(percent) && percent >= 0 && percent <= 100;
        debt.setAttribute("aria-invalid", validAmount ? "false" : "true");
        rate.setAttribute("aria-invalid", validPercent ? "false" : "true");
        if (!validAmount || !validPercent) {
            program.textContent = "—";
            difference.textContent = "—";
            validation.textContent = "Enter debt from $0 to $10,000,000 and a fee rate from 0% to 100%.";
            return;
        }
        validation.textContent = "";
        const fee = Math.round((amount * percent / 100 + Number.EPSILON) * 100) / 100;
        program.textContent = currency.format(fee);
        difference.textContent = currency.format(fee);
    };
    debt.setAttribute("aria-describedby", "fee-validation");
    rate.setAttribute("aria-describedby", "fee-validation");
    debt.addEventListener("input", update);
    rate.addEventListener("input", update);
    update();
})();
