// 验证网页/手机客户端的远程控制：在真实浏览器里开启控制 → 模拟鼠标移动 → 检查服务端光标
// 用法: node web-control-verify.mjs <pageUrl> [port]
import { spawn, execFileSync } from 'node:child_process';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const pageUrl = process.argv[2] || 'http://127.0.0.1:8899/pc?ip=127.0.0.1&port=8899';
const mode = process.argv[3] || 'mouse';           // mouse | touch
const vw = Number(process.argv[4] || 1280);
const vh = Number(process.argv[5] || 800);
const port = 9521 + Math.floor(Math.random() * 100);
const userDir = mkdtempSync(join(tmpdir(), 'watching-ctrl-'));

const child = spawn('C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe', [
  '--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check',
  `--remote-debugging-port=${port}`, `--user-data-dir=${userDir}`,
  `--window-size=${vw},${vh}`, 'about:blank'
], { stdio: 'ignore', windowsHide: true });

const sleep = (ms) => new Promise(r => setTimeout(r, ms));

function cursorPos() {
  const out = execFileSync('powershell', ['-NoProfile', '-Command',
    'Add-Type -AssemblyName System.Windows.Forms; $p=[System.Windows.Forms.Cursor]::Position; "$($p.X),$($p.Y)"'
  ], { encoding: 'utf8' }).trim();
  const [x, y] = out.split(',').map(Number);
  return { x, y };
}

function setCursor(x, y) {
  execFileSync('powershell', ['-NoProfile', '-Command',
    `Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point(${x},${y})`
  ], { encoding: 'utf8' });
}

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

const wsUrl = await getWsUrl();
const ws = new WebSocket(wsUrl);
await new Promise((res, rej) => { ws.addEventListener('open', res); ws.addEventListener('error', rej); });

let id = 0;
const waiters = new Map();
const events = [];
ws.addEventListener('message', (ev) => {
  const m = JSON.parse(ev.data);
  if (m.id && waiters.has(m.id)) { waiters.get(m.id)(m.result); waiters.delete(m.id); }
  else if (m.method) events.push(m);
});
const send = (method, params = {}) => new Promise(res => {
  const i = ++id; waiters.set(i, res);
  ws.send(JSON.stringify({ id: i, method, params }));
});
const evaluate = async (expr) => {
  const r = await send('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true });
  return r.result?.value;
};

await send('Page.enable');
await send('Runtime.enable');
await send('Log.enable');
await send('Page.navigate', { url: pageUrl });
await sleep(7000);   // 等连接 + 收帧

// 1. 页面状态
const state = await evaluate(`(() => {
  const btn = document.getElementById('btnCtrl');
  return {
    title: document.title,
    hasCtrlBtn: !!btn,
    ctrlText: btn ? btn.textContent : null,
    ctrlTitle: btn ? btn.title : null,
    detail: document.getElementById('detailPill')?.textContent || '',
    viewerReady: typeof window.Watching !== 'undefined'
  };
})()`);
console.log('=== 页面状态 ===');
console.log(JSON.stringify(state, null, 1));

if (!state.hasCtrlBtn) {
  console.log('❌ 页面上没有控制按钮');
  process.exit(2);
}

// 2. 移动服务端光标到一个基准位置
setCursor(200, 150);
await sleep(500);
const before = cursorPos();
console.log(`\n基准光标 = (${before.x}, ${before.y})`);

// 3. 点「控制」按钮开启控制
const toggled = await evaluate(`(() => {
  const btn = document.getElementById('btnCtrl');
  btn.click();
  return { text: btn.textContent, controlMode: document.body.classList.contains('control-mode') };
})()`);
console.log(`点击控制按钮后: text="${toggled.text}"  control-mode=${toggled.controlMode}`);

// 4. 派发输入事件（鼠标或触摸），走真实事件链路
const eventScript = mode === 'touch'
  ? `(() => {
      const cv = document.getElementById('screen');
      const r = cv.getBoundingClientRect();
      const x = r.left + r.width * 0.62, y = r.top + r.height * 0.38;
      const mk = (type) => {
        const t = new Touch({ identifier: 1, target: cv, clientX: x, clientY: y });
        return new TouchEvent(type, {
          touches: type === 'touchend' ? [] : [t],
          changedTouches: [t], bubbles: true, cancelable: true
        });
      };
      cv.dispatchEvent(mk('touchstart'));
      for (let i = 0; i < 3; i++) cv.dispatchEvent(mk('touchmove'));
      cv.dispatchEvent(mk('touchend'));
      return { x: Math.round(x), y: Math.round(y), w: Math.round(r.width), h: Math.round(r.height) };
    })()`
  : `(() => {
      const cv = document.getElementById('screen');
      const r = cv.getBoundingClientRect();
      const x = r.left + r.width * 0.62;
      const y = r.top + r.height * 0.38;
      for (let i = 0; i < 3; i++) {
        cv.dispatchEvent(new MouseEvent('mousemove', { clientX: x, clientY: y, bubbles: true }));
      }
      return { x: Math.round(x), y: Math.round(y), w: Math.round(r.width), h: Math.round(r.height) };
    })()`;

const moved = await evaluate(eventScript);
console.log(`已派发 ${mode === 'touch' ? 'touch' : 'mousemove'} 到画布 (${moved.x}, ${moved.y})，画布 ${moved.w}x${moved.h}`);
await sleep(1200);

const after = cursorPos();
console.log(`移动后光标 = (${after.x}, ${after.y})`);

// 计算期望位置，避免只看 Δ 被时序干扰
const screen = execFileSync('powershell', ['-NoProfile', '-Command',
  'Add-Type -AssemblyName System.Windows.Forms; $b=[System.Windows.Forms.SystemInformation]::VirtualScreen; "$($b.Width),$($b.Height)"'
], { encoding: 'utf8' }).trim();
const [sw, sh] = screen.split(',').map(Number);

// 用同样的归一化算法反推期望的屏幕坐标
const norm = await evaluate(`(() => {
  const v = window.__probeViewer;
  const cv = document.getElementById('screen');
  const r = cv.getBoundingClientRect();
  const x = r.left + r.width * 0.62, y = r.top + r.height * 0.38;
  const p = v.toNormalized(x, y);
  return p ? { x: p.x, y: p.y } : null;
})()`.replace('window.__probeViewer', 'window.__probeViewer || null'));

let expectX = null, expectY = null;
if (norm) {
  expectX = Math.round(norm.x * (sw - 1));
  expectY = Math.round(norm.y * (sh - 1));
  console.log(`期望光标 ≈ (${expectX}, ${expectY})   （归一化 ${norm.x.toFixed(3)}, ${norm.y.toFixed(3)}）`);
}

const dx = expectX !== null ? Math.abs(after.x - expectX) : 0;
const dy = expectY !== null ? Math.abs(after.y - expectY) : 0;
const movedFromBase = Math.abs(after.x - before.x) > 20 || Math.abs(after.y - before.y) > 20;

console.log('');
if (expectX !== null && dx <= 12 && dy <= 12) {
  console.log(`✅ 远程控制生效且坐标准确：误差 (${dx}, ${dy}) 像素`);
  process.exit(0);
} else if (movedFromBase) {
  console.log(`✅ 远程控制生效（服务端光标被移动）· 期望 (${expectX},${expectY}) 实际 (${after.x},${after.y})`);
  process.exit(0);
} else {
  console.log('❌ 服务端光标没动 —— 控制没生效');
  process.exit(3);
}

const errors = events.filter(e => e.method === 'Log.entryAdded' && e.params.entry.level === 'error')
  .map(e => e.params.entry.text);
const exceptions = events.filter(e => e.method === 'Runtime.exceptionThrown')
  .map(e => e.params.exceptionDetails.exception?.description || e.params.exceptionDetails.text);
if (errors.length) console.log('\n控制台错误:\n  ' + errors.join('\n  '));
if (exceptions.length) console.log('\nJS 异常:\n  ' + exceptions.join('\n  '));
if (!errors.length && !exceptions.length) console.log('无控制台错误 / JS 异常');

ws.close();
child.kill();
try { rmSync(userDir, { recursive: true, force: true }); } catch { }
process.exit(dx > 20 || dy > 20 ? 0 : 3);
