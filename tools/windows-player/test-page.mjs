// Runs the actual adapted page's lifecycle functions with minimal visibility/canvas inputs.
// Native WebView focus and Android lifecycle still require device/UI acceptance.
import fs from 'node:fs';
import vm from 'node:vm';
import assert from 'node:assert/strict';
const html = fs.readFileSync(process.argv[2], 'utf8');
function block(marker) {
  const start = html.indexOf(marker);
  assert(start >= 0, `Missing ${marker}`);
  const body = html.indexOf('{', start);
  let end = body + 1, depth = 1;
  for (; depth && end < html.length; end++) {
    if (html[end] === '{') depth++;
    else if (html[end] === '}') depth--;
  }
  assert.equal(depth, 0);
  return html.slice(start, end);
}
let pauses = 0, resumes = 0, handler;
const classes = new Set(['active']);
const context = {
  document: { visibilityState: 'hidden', addEventListener(name, fn) { assert.equal(name, 'visibilitychange'); handler = fn; } },
  canvasElement: { classList: { contains: x => classes.has(x), add: x => classes.add(x), remove: x => classes.delete(x) } },
  pauseMenu: { hidden: true },
  GM_pause() { pauses++; }, GM_unpause() { resumes++; },
  enterFullscreenIfSupported() {}, lockOrientationIfSupported() {},
};
vm.runInNewContext(block('function pause()') + '\n' + block('function resume()') + '\n' + block('document.addEventListener("visibilitychange", (event) =>') + ');', context);
handler();
assert.equal(pauses, 1);
assert.equal(context.pauseMenu.hidden, true, 'GX menu must stay hidden while pausing');
context.document.visibilityState = 'visible'; handler();
assert.equal(resumes, 1, 'Single-player must resume automatically');
assert.equal(context.pauseMenu.hidden, true);
assert.equal(classes.has('paused'), false);
assert(html.includes('overflow:hidden!important'));
console.log('PASS: pause in background, automatic resume, no GX overlay, release viewport without scrolling.');
