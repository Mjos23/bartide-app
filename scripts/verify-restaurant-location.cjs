const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../TideCasa.Blazor/wwwroot/restaurant-discovery.js'), 'utf8');
(async () => {
    const handlers = {}, status = {}, link = { removeAttribute(name) { delete this[name]; } };
    const form = { elements: { latitude: { value: '' }, longitude: { value: '' } },
        querySelector: selector => selector.includes('status') ? status : link };
    const button = { disabled: false, closest: () => form };
    const event = { target: { closest: () => button } };
    let calls = 0, resolve;
    const navigator = { geolocation: { getCurrentPosition(success, error, options) {
        calls++; assert.equal(options.timeout, 15000); resolve = { success, error };
    } } };
    const context = { window: {}, navigator, document: { addEventListener: (name, handler) => handlers[name] = handler, querySelectorAll: () => [form] } };
    vm.runInNewContext(source, context);
    assert.equal(calls, 0); assert.equal(link.hidden, true);
    const result = context.window.restaurantDiscovery.locate();
    resolve.success({ coords: { latitude: 25.77, longitude: -80.19, accuracy: 10 } });
    assert.equal((await result).latitude, 25.77);
    let pending = handlers.click(event);
    assert.equal(button.disabled, true);
    resolve.error({code:1}); await pending;
    assert.match(status.textContent, /blocked/); assert.equal(button.disabled, false);
    assert.equal(form.elements.latitude.value, '');
    pending = handlers.click(event);
    resolve.success({ coords: { latitude: 0, longitude: 0, accuracy: 5 } }); await pending;
    assert.equal(form.elements.latitude.value, '0.000000'); assert.equal(link.hidden, false);
    assert.match(link.href, /openstreetmap\.org/); assert.match(status.textContent, /Check the map/);
    navigator.geolocation.getCurrentPosition = () => { throw new Error('unavailable'); };
    await handlers.click(event); assert.match(status.textContent, /unavailable/); assert.equal(button.disabled, false);
    delete navigator.geolocation;
    assert.equal((await context.window.restaurantDiscovery.locate()).error, 'unsupported');
    assert.equal(calls, 3);
    console.log('PASS location permission, explicit requests, denial, unsupported browser, zero coordinates, map preview and failure recovery');
})().catch(error => { console.error(error); process.exitCode = 1; });
