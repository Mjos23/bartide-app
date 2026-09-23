(() => {
    if (window.tideEmailTrackingReady) return;
    window.tideEmailTrackingReady = true;
    // One delegated listener survives Blazor enhanced and interactive navigation.
    document.addEventListener('click', event => {
        if (navigator.globalPrivacyControl || navigator.doNotTrack === '1') return;
        const anchor = event.target instanceof Element ? event.target.closest('a[href]') : null;
        if (!anchor || anchor.hasAttribute('download')) return;
        let target;
        try { target = new URL(anchor.href, location.href); } catch { return; }
        const marketingOrigins = new Set(['https://bar.tide.casa', 'https://tide.casa']);
        const sampleOrigin = target.origin === 'https://demo.tide.casa';
        if (target.origin !== location.origin && !marketingOrigins.has(target.origin) && !sampleOrigin) return;
        const route = target.pathname.replace(/\/$/, '') || '/';
        let path = null;
        if (sampleOrigin && route === '/') path = 'sample';
        else if (sampleOrigin) return;
        else if ((route === '/' || route === '/restaurant') && target.hash === '#pricing') path = 'pricing';
        else if (route === '/' || route === '/restaurant') path = 'home';
        else if (route === '/book-a-demo') path = 'demo';
        else if (route === '/contact') path = 'contact';
        else if (route === '/purchase/restaurant' || route === '/purchase/business' || route === '/start/restaurant' || route === '/start/business') path = 'purchase';
        else if (route === '/enhanced-demo' || route === '/sample-bar') path = 'sample';
        if (!path) return;
        // Never block navigation or include the destination query, hash or contact data.
        fetch('/email-events/click', { method: 'POST', credentials: 'same-origin', keepalive: true,
            headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ path }) }).catch(() => {});
    }, { capture: true });
})();
