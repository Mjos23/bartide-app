// Execute the actual progressive enhancement against isolated input events.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const listeners = [];
const form = { output: { textContent: '' }, querySelector() { return this.output; } };
class Input {
  constructor(value, target = form) { this.name = 'pay'; this.value = value; this.target = target; }
  closest(selector) { assert.equal(selector, 'form[data-driver-fee-preview]'); return this.target; }
}
const context = vm.createContext({ window: {}, document: { addEventListener(name, callback) {
  assert.equal(name, 'input'); listeners.push(callback);
} }, HTMLInputElement: Input, Intl });
const script = fs.readFileSync(path.join(__dirname, '../TideCasa.Blazor/wwwroot/driver-network.js'), 'utf8');
vm.runInContext(script, context);
vm.runInContext(script, context);
assert.equal(listeners.length, 1, 'Repeated loading must not duplicate listeners');
for (const [value, expected] of [
  ['10', '$10.00 driver pay + $0.50 network fee = $10.50 client total per delivery.'],
  ['10.10', '$10.10 driver pay + $0.51 network fee = $10.61 client total per delivery.'],
  ['0.50', '$0.50 driver pay + $0.03 network fee = $0.53 client total per delivery.'],
  ['1000.00', '$1,000.00 driver pay + $50.00 network fee = $1,050.00 client total per delivery.'],
]) {
  listeners[0]({ target: new Input(value) });
  assert.equal(form.output.textContent, expected);
}
for (const value of ['', '0.49', '1000.01', '10.001', '-5', '1e2']) {
  listeners[0]({ target: new Input(value) });
  assert.equal(form.output.textContent, 'Enter driver pay from $0.50 to $1,000.00, with at most two decimal places.');
}
const prior = form.output.textContent;
listeners[0]({ target: new Input('9', null) });
assert.equal(form.output.textContent, prior, 'Unrelated forms must not change the preview');
const other = { output: { textContent: '' }, querySelector() { return this.output; } };
listeners[0]({ target: new Input('10', other) });
assert.equal(form.output.textContent, prior, 'Only the edited driver offer may update');
assert.match(other.output.textContent, /\$10\.50 client total/);
console.log('13 driver fee preview checks passed.');
