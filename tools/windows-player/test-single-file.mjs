import { spawn, execFileSync } from 'node:child_process';
import { createServer } from 'node:net';
import fs from 'node:fs';

// Launch only this test game, hidden, with a temporary loopback DevTools endpoint.
const [exe, report] = process.argv.slice(2);
const server = createServer(); await new Promise(r => server.listen(0, '127.0.0.1', r));
const port = server.address().port; await new Promise(r => server.close(r));
const game = spawn(exe, [], { windowsHide: true, stdio: 'ignore', env: {
  ...process.env, WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: `--remote-debugging-port=${port} --remote-debugging-address=127.0.0.1`,
} });
let socket;
try {
  const deadline = Date.now() + 60000; let page;
  while (Date.now() < deadline) {
    try { page = (await (await fetch(`http://127.0.0.1:${port}/json`)).json()).find(p => p.url === 'https://game.local/index.html'); } catch {}
    if (page) break;
    if (game.exitCode !== null) throw Error(`Player exited: ${game.exitCode}`);
    await new Promise(r => setTimeout(r, 500));
  }
  if (!page) throw Error('The bundled player did not load the game page.');
  socket = new WebSocket(page.webSocketDebuggerUrl);
  await new Promise((resolve, reject) => { socket.onopen = resolve; socket.onerror = reject; });
  let id = 0; const pending = new Map();
  socket.onmessage = e => { const m = JSON.parse(e.data); const p = pending.get(m.id); if (p) { pending.delete(m.id); m.error ? p.reject(Error(m.error.message)) : p.resolve(m.result); } };
  const call = (method, params = {}) => new Promise((resolve, reject) => {
    const n = ++id; pending.set(n, {resolve, reject}); socket.send(JSON.stringify({id:n, method, params}));
  });
  let state;
  while (Date.now() < deadline) {
    const result = await call('Runtime.evaluate', {expression: `JSON.stringify({title:document.title,ready:document.readyState,canvas:!!document.querySelector('canvas'),running:typeof Module!=='undefined'&&!!Module.calledRun})`, returnByValue:true});
    state = JSON.parse(result.result.value);
    if (state.ready === 'complete' && state.canvas && state.running) break;
    await new Promise(r => setTimeout(r, 500));
  }
  if (!state?.running || !state.canvas) throw Error('The game runtime did not initialize: ' + JSON.stringify(state));
  const shot = await call('Page.captureScreenshot', {format:'png', captureBeyondViewport:false});
  fs.writeFileSync(report.replace(/\.json$/, '.png'), Buffer.from(shot.data,'base64'));
  fs.writeFileSync(report, JSON.stringify({exe,pid:game.pid,url:page.url,...state},null,2)+'\n');
  console.log('PASS: lone EXE loaded the bundled game in WebView2.', JSON.stringify(state));
} finally {
  socket?.close();
  // Only the test-owned player and its WebView2 children are terminated.
  if (game.exitCode === null) try { execFileSync('taskkill', ['/PID',String(game.pid),'/T','/F'],{windowsHide:true,stdio:'ignore'}); } catch {}
}
