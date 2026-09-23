const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const source = fs.readFileSync('TideCasa.Blazor/wwwroot/email-tracking.js', 'utf8');
let listener;
let registrations = 0;
const requests = [];
class Element {
  constructor(href) { this.href = href; }
  closest() { return this; }
  hasAttribute() { return false; }
}
const context = { window: {}, document: { addEventListener: (type, fn) => { assert.equal(type, 'click'); listener = fn; registrations++; } },
  navigator: {}, Element, URL, location: { origin: 'https://bar.tide.casa', href: 'https://bar.tide.casa/?campaign=public-token' },
  fetch: (url, options) => { requests.push({ url, options }); return Promise.resolve(); } };
vm.runInNewContext(source, context); vm.runInNewContext(source, context);
assert.equal(registrations, 1, 'enhanced navigation does not duplicate the listener');
let passed = 1;
for (const [url, expected] of [
  ['/book-a-demo','demo'], ['https://tide.casa/book-a-demo?private=never-record','demo'],
  ['/purchase/restaurant','purchase'], ['/contact','contact'], ['/#pricing','pricing'],
  ['https://bar.tide.casa/enhanced-demo','sample'], ['https://demo.tide.casa/','sample'],
  ['https://demo.tide.casa/owner','none'], ['https://tide.casa.attacker.invalid/book-a-demo','none'],
  ['https://attacker.invalid/purchase/restaurant','none'], ['https://tide.casa:8080/book-a-demo','none'],
  ['mailto:hello@tide.casa','none'], ['/owner/email-results','none']]) {
  const before = requests.length;
  listener({ target: new Element(url) });
  if (expected === 'none') assert.equal(requests.length, before, url);
  else {
    assert.equal(requests.length,before+1,url);
    const request=requests.at(-1);
    assert.equal(request.url,'/email-events/click');
    assert.deepEqual(JSON.parse(request.options.body), { path: expected });
    assert.equal(request.options.keepalive,true);
  }
  passed++;
}
context.navigator.globalPrivacyControl=true;
const before=requests.length; listener({target:new Element('/book-a-demo')}); assert.equal(requests.length,before); passed++;
console.log(JSON.stringify({ passed, result: 'click paths, trusted domain boundaries, privacy and navigation lifecycle verified' }));
