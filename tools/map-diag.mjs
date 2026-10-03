// 在真实浏览器里跑坐标映射诊断
import { spawn } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const port = 9721 + Math.floor(Math.random() * 100);
const userDir = mkdtempSync(join(tmpdir(), 'watching-map-'));
const script = readFileSync(process.argv[2], 'utf8');

const child = spawn('C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe', [
  '--headless=new', '--disable-gpu', '--no-first-run',
  `--remote-debugging-port=${port}`, `--user-data-dir=${userDir}`,
  '--window-size=390,844', 'about:blank'
], { stdio: 'ignore', windowsHide: true });

const sleep = (ms) => new Promise(r => setTimeout(r, ms));

async function getWsUrl() {
  for (let i = 0; i < 60; i++) {
    try {
      const r = await fetch(`http://127.0.0.1:${port}/json/list`);
      const list = await r.json();
      const page = list.find(t => t.type === 'page');
      if (page?.webSocketDebuggerUrl) return page.webSocketDebuggerUrl;
    } catch { }
    await sleep(250);
  }
  throw new Error('CDP 未就绪');
}

const ws = new WebSocket(await getWsUrl());
await new Promise((res, rej) => { ws.addEventListener('open', res); ws.addEventListener('error', rej); });
let id = 0; const waiters = new Map();
ws.addEventListener('message', (ev) => {
  const m = JSON.parse(ev.data);
  if (m.id && waiters.has(m.id)) { waiters.get(m.id)(m.result); waiters.delete(m.id); }
});
const send = (method, params = {}) => new Promise(res => {
  const i = ++id; waiters.set(i, res);
  ws.send(JSON.stringify({ id: i, method, params }));
});

await send('Page.enable');
await send('Runtime.enable');
await send('Page.navigate', { url: 'http://127.0.0.1:8899/?ip=127.0.0.1&port=8899' });
await sleep(6000);

// 注入诊断脚本并取回 console 输出
const wrapped = `(() => { const out = []; const log = console.log; console.log = (...a) => out.push(a.join(' '));
${script}
console.log = log; return out.join('\\n'); })()`;

const r = await send('Runtime.evaluate', { expression: wrapped, returnByValue: true, awaitPromise: true });
if (r.exceptionDetails) {
  console.log('脚本异常: ' + (r.exceptionDetails.exception?.description || r.exceptionDetails.text));
}
console.log(r.result?.value || '(无输出)');

// 顺带看页面真实几何
const geo = await send('Runtime.evaluate', {
  expression: `JSON.stringify((() => {
    const cv = document.getElementById('screen');
    if (!cv) return { error: '没找到 #screen' };
    const r = cv.getBoundingClientRect();
    return { rect: [Math.round(r.left), Math.round(r.top), Math.round(r.width), Math.round(r.height)],
             canvas: [cv.width, cv.height], dpr: window.devicePixelRatio, inner: [innerWidth, innerHeight],
             hasViewer: typeof window.Watching !== 'undefined' };
  })())`, returnByValue: true
});
console.log('\n页面真实几何: ' + (geo.result?.value || JSON.stringify(geo.exceptionDetails)));

ws.close(); child.kill();
try { rmSync(userDir, { recursive: true, force: true }); } catch { }
process.exit(0);
