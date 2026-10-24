// Minimal Chrome DevTools Protocol client for the HyperHarbor WebView2 (debug port 9333).
import { writeFileSync } from 'node:fs';

export async function connect(port = 9333) {
  const targets = await (await fetch(`http://127.0.0.1:${port}/json`)).json();
  const page = targets.find((t) => t.type === 'page' && t.title === 'HyperHarbor');
  if (!page) throw new Error('HyperHarbor page not found on the debug port.');
  const ws = new WebSocket(page.webSocketDebuggerUrl);
  await new Promise((ok, fail) => { ws.onopen = ok; ws.onerror = fail; });
  let next = 1;
  const pending = new Map();
  ws.onmessage = (m) => {
    const msg = JSON.parse(m.data);
    if (msg.id && pending.has(msg.id)) {
      const { ok, fail } = pending.get(msg.id);
      pending.delete(msg.id);
      msg.error ? fail(new Error(msg.error.message)) : ok(msg.result);
    }
  };
  const send = (method, params = {}) => new Promise((ok, fail) => {
    const id = next++;
    pending.set(id, { ok, fail });
    ws.send(JSON.stringify({ id, method, params }));
  });
  const evaluate = async (expression) => {
    const r = await send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
    if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description ?? r.exceptionDetails.text);
    return r.result.value;
  };
  const screenshot = async (file) => {
    const r = await send('Page.captureScreenshot', { format: 'png' });
    writeFileSync(file, Buffer.from(r.data, 'base64'));
  };
  return { send, evaluate, screenshot, close: () => ws.close() };
}

// CLI: node cdp.mjs eval "<expr>" | node cdp.mjs shot <file>
if (process.argv[1]?.endsWith('cdp.mjs')) {
  const [cmd, arg] = process.argv.slice(2);
  const c = await connect();
  if (cmd === 'shot') await c.screenshot(arg);
  else console.log(JSON.stringify(await c.evaluate(arg), null, 1));
  c.close();
}
