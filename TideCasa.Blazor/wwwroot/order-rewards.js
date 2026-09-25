(() => {
  "use strict";
  window.tideOrderRewards = {
    async link(slug, orderId, trackingKey) {
      try {
        const path = "/rewards/orders/" + encodeURIComponent(slug);
        const session = await fetch(path + "/session", { credentials: "same-origin", cache: "no-store", redirect: "error" });
        if (!session.ok) return false;
        const account = await session.json();
        const form = new URLSearchParams({ __RequestVerificationToken: account.token, user: account.user, order_id: orderId, tracking_key: trackingKey });
        const response = await fetch(path + "/link", { method: "POST", credentials: "same-origin", cache: "no-store", redirect: "error", body: form });
        return response.ok && (await response.json()).linked === true;
      } catch { return false; }
    }
  };
})();
