// Preserve the user's current form on an unconfirmed save. Native, CSRF-protected
// form submission remains available when JavaScript is disabled.
(() => {
    if (window.barTideOnboardingForms) return;
    window.barTideOnboardingForms = true;
    const notices = {
        stale: "This workspace changed since you opened it. Your entries are still shown here and were not saved. Open setup in another tab to compare the latest details, then reload before submitting again.",
        "not-ready": "This step needs attention. Your entries are still shown here. Review the required fields before saving again.",
        invalid: "These details were not saved. Your entries are still here. Check the required choices, field limits, hours and two-letter state, then try again.",
        expired: "Your form expired. Your entries are still here. Copy any unsaved changes before refreshing the page.",
        unconfirmed: "We couldn’t confirm whether this change saved. Your entries are still here. Check the current setup in another tab before trying again."
    };
    document.addEventListener("submit", async event => {
        const form = event.target;
        if (!(form instanceof HTMLFormElement) || !form.closest(".client-onboarding")) return;
        const action = new URL(form.action, window.location.href);
        if (action.origin !== window.location.origin || !/^\/onboarding\/[A-Za-z0-9_-]+\/(business|brand|service|team)$/.test(action.pathname)) return;
        event.preventDefault();
        if (form.dataset.saving === "true") return;
        const body = new URLSearchParams();
        for (const [name, value] of new FormData(form)) {
            if (typeof value !== "string") return;
            body.append(name, value);
        }
        form.dataset.saving = "true";
        const button = event.submitter || form.querySelector('button[type="submit"]');
        const previousLabel = button?.textContent;
        if (button) { button.disabled = true; button.textContent = "Saving…"; }
        let notice = form.querySelector("[data-save-notice]");
        if (notice) notice.remove();
        const controller = new AbortController();
        const timeout = setTimeout(() => controller.abort(), 30000);
        try {
            const response = await fetch(action.href, { method: "POST", body, credentials: "same-origin", signal: controller.signal, headers: { Accept: "text/html" } });
            const destination = new URL(response.url);
            const tenant = action.pathname.split("/")[2];
            const expectedPath = "/workspace/" + tenant + "/onboarding";
            const code = destination.searchParams.get("notice");
            if (response.ok && destination.origin === window.location.origin && destination.pathname === expectedPath && code === "saved") {
                window.location.assign(destination.pathname + destination.search + "#" + action.pathname.split("/")[3]);
                return;
            }
            showNotice(destination.pathname === expectedPath ? (notices[code] || notices.unconfirmed) : response.status === 401 || destination.pathname === "/signin" ? "Sign in again before saving. Your entries are still here; copy any unsaved changes first." : notices.unconfirmed);
        } catch { showNotice(notices.unconfirmed); }
        finally {
            clearTimeout(timeout);
            form.dataset.saving = "false";
            if (button) { button.disabled = false; button.textContent = previousLabel; }
        }
        function showNotice(message) {
            notice = document.createElement("div");
            notice.dataset.saveNotice = "true";
            notice.className = "onboarding-notice";
            notice.setAttribute("role", "alert");
            notice.tabIndex = -1;
            const text = document.createElement("p"); text.textContent = message; notice.append(text);
            const link = document.createElement("a");
            link.href = window.location.pathname;
            link.target = "_blank";
            link.rel = "noopener";
            link.textContent = "Open current saved setup in a new tab";
            notice.append(link);
            form.prepend(notice);
            notice.focus();
        }
    });
})();
