// Native dialogs retain keyboard focus and restore it to the item that opened them.
window.tideOrderingDetails = {
    open(id) {
        const dialog = document.getElementById(id);
        if (dialog && !dialog.open) dialog.showModal();
    },
    close(id) {
        const dialog = document.getElementById(id);
        if (dialog?.open) dialog.close();
    },
    jump(id) {
        const heading = document.getElementById(id);
        if (!heading) return;
        heading.scrollIntoView({ block: 'start', behavior: 'instant' });
        heading.focus({ preventScroll: true });
    }
};
