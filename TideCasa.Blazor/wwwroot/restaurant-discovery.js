(() => {
    "use strict";
    // Called only by an explicit button. Search coordinates never enter storage or URLs.
    window.restaurantDiscovery = {
        locate: () => new Promise(resolve => {
            if (!navigator.geolocation) return resolve({ error: "unsupported" });
            navigator.geolocation.getCurrentPosition(
                position => resolve({ latitude: position.coords.latitude, longitude: position.coords.longitude, accuracy: position.coords.accuracy }),
                error => resolve({ error: error.code === 1 ? "denied" : "unavailable" }),
                { enableHighAccuracy: true, timeout: 15000, maximumAge: 60000 });
        })
    };
    function updateMap(form) {
        const latitude = form.elements.latitude.value, longitude = form.elements.longitude.value;
        const link = form.querySelector("[data-restaurant-map]");
        const valid = latitude !== "" && longitude !== "" && Number.isFinite(Number(latitude)) && Number.isFinite(Number(longitude))
            && Math.abs(Number(latitude)) <= 90 && Math.abs(Number(longitude)) <= 180;
        link.hidden = !valid;
        if (valid) link.href = "https://www.openstreetmap.org/?mlat=" + encodeURIComponent(latitude) + "&mlon=" + encodeURIComponent(longitude) + "#map=18/" + encodeURIComponent(latitude) + "/" + encodeURIComponent(longitude);
        else link.removeAttribute("href");
    }
    document.addEventListener("click", async event => {
        const button = event.target.closest("[data-capture-restaurant-location]");
        if (!button || button.disabled) return;
        const form = button.closest("[data-restaurant-location]");
        const status = form.querySelector("[data-restaurant-location-status]");
        button.disabled = true; status.textContent = "Finding your restaurant location…";
        try {
            const position = await window.restaurantDiscovery.locate();
            if (position.error) {
                status.textContent = position.error === "denied" ? "Location access is blocked. Allow it in your browser or enter the restaurant’s map coordinates." : "We could not find your location. Try again or enter the restaurant’s map coordinates.";
                return;
            }
            form.elements.latitude.value = position.latitude.toFixed(6);
            form.elements.longitude.value = position.longitude.toFixed(6);
            updateMap(form);
            status.textContent = "Location found (accuracy about " + Math.round(position.accuracy) + " meters). Check the map, then save your listing.";
        } catch { status.textContent = "Location is unavailable. Try again or enter the restaurant’s map coordinates."; }
        finally { button.disabled = false; }
    });
    document.addEventListener("input", event => {
        const form = event.target.closest("[data-restaurant-location]");
        if (form && ["latitude", "longitude"].includes(event.target.name)) updateMap(form);
    });
    function refresh() { document.querySelectorAll("[data-restaurant-location]").forEach(updateMap); }
    refresh();
    document.addEventListener("DOMContentLoaded", refresh);
})();
