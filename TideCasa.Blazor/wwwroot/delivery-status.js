// Receipt status only: no GPS, customer coordinates, or background location access.
window.tideDeliveryStatus = (() => {
  let sequence = 0;
  const active = new Map();
  function stop(id) {
    const entry = active.get(id);
    if (!entry) return;
    clearTimeout(entry.timer);
    document.removeEventListener('visibilitychange', entry.visibility);
    active.delete(id);
  }
  function start(receiver) {
    const id = ++sequence;
    const entry = { timer: null, running: false, visibility: null };
    async function tick() {
      if (!active.has(id) || document.hidden || entry.running) return;
      entry.running = true;
      let delay = 60000;
      try { delay = await receiver.invokeMethodAsync('PollDeliveryAsync'); }
      catch { /* The server circuit may reconnect; keep the last receipt visible. */ }
      finally { entry.running = false; }
      if (!active.has(id)) return;
      if (delay === 0) { stop(id); return; }
      if (!document.hidden) entry.timer = setTimeout(tick, Math.max(15000, delay));
    }
    entry.visibility = () => {
      clearTimeout(entry.timer);
      if (!document.hidden && !entry.running) tick();
    };
    active.set(id, entry);
    document.addEventListener('visibilitychange', entry.visibility);
    if (!document.hidden) entry.timer = setTimeout(tick, 15000);
    return id;
  }
  return { start, stop };
})();
