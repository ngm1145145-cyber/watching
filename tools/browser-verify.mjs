// 用 Chrome DevTools 协议做真实浏览器端到端验证（渲染 + 截图 + DOM 状态）
// 用法: node browser-verify.mjs <url> <outPng> [waitMs] [width] [height]
import { spawn } from 'node:child_process';
import { writeFileSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const url = process.argv[2] || 'http://127.0.0.1:8899/';
const outPng = process.argv[3] || 'H:\\ds-harness\\.tmp\\browser.png';
const waitMs = Number(process.argv[4] || 6000);
const vw = Number(process.argv[5] || 1440);
const vh = Number(process.argv[6] || 900);
const port = 9333 + Math.floor(Math.random() * 200);

const chromePaths = [
  'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
  'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe'
];

const userDir = mkdtempSync(join(tmpdir(), 'watching-cdp-'));

const child = spawn(chromePaths[0], [
  '--headless=new',
  '--disable-gpu',
  '--no-first-run',
  '--no-default-browser-check',
  `--remote-debugging-port=${port}`,
  `--user-data-dir=${userDir}`,
  `--window-size=${vw},${vh}`,
  'about:blank'
], { stdio: 'ignore', windowsHide: true });

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function getWsUrl() {
  for (let i = 0; i < 60; i++) {
    try {
      const res = await fetch(`http://127.0.0.1:${port}/json/list`);
      const list = await res.json();
      const page = list.find((t) => t.type === 'page');
      if (page?.webSocketDebuggerUrl) return page.webSocketDebuggerUrl;
    } catch { }
    await sleep(250);
  }
  throw new Error('CDP 未就绪');
}

class Cdp {
  constructor(ws) {
    this.ws = ws;
    this.id = 0;
    this.waiters = new Map();
    this.events = [];
    ws.addEventListener('message', (ev) => {
      const msg = JSON.parse(ev.data);
      if (msg.id && this.waiters.has(msg.id)) {
        const { resolve, reject } = this.waiters.get(msg.id);
        this.waiters.delete(msg.id);
        if (msg.error) reject(new Error(JSON.stringify(msg.error)));
        else resolve(msg.result);
      } else if (msg.method) {
        this.events.push(msg);
      }
    });
  }
  send(method, params = {}) {
    const id = ++this.id;
    return new Promise((resolve, reject) => {
      this.waiters.set(id, { resolve, reject });
      this.ws.send(JSON.stringify({ id, method, params }));
      setTimeout(() => {
        if (this.waiters.has(id)) { this.waiters.delete(id); reject(new Error('CDP 超时: ' + method)); }
      }, 20000);
    });
  }
}

function connect(url) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(url);
    ws.addEventListener('open', () => resolve(ws));
    ws.addEventListener('error', (e) => reject(new Error('WS 连接失败')));
  });
}

try {
  const wsUrl = await getWsUrl();
  const ws = await connect(wsUrl);
  const cdp = new Cdp(ws);

  await cdp.send('Page.enable');
  await cdp.send('Runtime.enable');
  await cdp.send('Log.enable');
  await cdp.send('Page.navigate', { url });

  // 等页面加载 + WebSocket 收帧
  await sleep(waitMs);

  const evaluate = async (expr) => {
    const r = await cdp.send('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: false });
    return r.result?.value;
  };

  const report = await evaluate(`(() => {
    const canvas = document.getElementById('screen');
    const login = document.getElementById('login');
    const viewer = document.getElementById('viewer');
    let nonBlack = null, size = null, sample = null;
    if (canvas) {
      size = [canvas.width, canvas.height];
      try {
        const c2 = document.createElement('canvas');
        c2.width = 80; c2.height = 45;
        const g = c2.getContext('2d');
        g.drawImage(canvas, 0, 0, 80, 45);
        const d = g.getImageData(0, 0, 80, 45).data;
        let lit = 0, r = 0, gg = 0, b = 0;
        for (let i = 0; i < d.length; i += 4) {
          const v = d[i] + d[i+1] + d[i+2];
          if (v > 40) lit++;
          r += d[i]; gg += d[i+1]; b += d[i+2];
        }
        nonBlack = lit / (d.length / 4);
        sample = [Math.round(r/(d.length/4)), Math.round(gg/(d.length/4)), Math.round(b/(d.length/4))];
      } catch (e) { sample = 'err:' + e.message; }
    }
    const links = [...document.querySelectorAll('a[href]')].map(a => a.getAttribute('href'));
    const placeholders = links.filter(h => /your-name|__REPO__/.test(h || ''));
    const okLinks = links.filter(h => /ngm1145145-cyber\\.github\\.io|github\\.com\\/ngm1145145-cyber/.test(h || ''));
    const brokenImgs = [...document.querySelectorAll('img')].filter(i => i.complete && i.naturalWidth === 0).map(i => i.getAttribute('src'));
    return {
      title: document.title,
      headings: document.querySelectorAll('h1,h2').length,
      cards: document.querySelectorAll('.card,.node,.step').length,
      images: document.querySelectorAll('img').length,
      brokenImages: brokenImgs,
      linkCount: links.length,
      repoLinks: okLinks.length,
      leftoverPlaceholders: placeholders,
      sampleRepoLink: okLinks[0] || null,
      bodyBg: getComputedStyle(document.body).backgroundColor,
      h1Size: document.querySelector('h1') ? getComputedStyle(document.querySelector('h1')).fontSize : null,
      hasWatching: typeof window.Watching === 'function' || typeof window.Watching === 'object',
      viewerHidden: viewer ? viewer.classList.contains('hidden') : null,
      loginHidden: login ? login.classList.contains('hidden') : null,
      statText: document.getElementById('statPill')?.textContent ?? null,
      detailText: document.getElementById('detailPill')?.textContent ?? null,
      reconnectShown: document.getElementById('reconnect') ? !document.getElementById('reconnect').classList.contains('hidden') : null,
      canvasSize: size,
      canvasNonBlackRatio: nonBlack,
      canvasAvgRgb: sample
    };
  })()`);

  console.log('=== 浏览器内状态 ===');
  console.log(JSON.stringify(report, null, 2));

  const errors = cdp.events
    .filter((e) => e.method === 'Log.entryAdded' && ['error', 'warning'].includes(e.params.entry.level))
    .map((e) => `${e.params.entry.level}: ${e.params.entry.text}`);
  const exceptions = cdp.events
    .filter((e) => e.method === 'Runtime.exceptionThrown')
    .map((e) => e.params.exceptionDetails.exception?.description || e.params.exceptionDetails.text);
  if (errors.length) console.log('=== 控制台 ===\n' + errors.join('\n'));
  if (exceptions.length) console.log('=== JS 异常 ===\n' + exceptions.join('\n'));
  if (!errors.length && !exceptions.length) console.log('无控制台错误 / JS 异常');

  const shot = await cdp.send('Page.captureScreenshot', { format: 'png' });
  writeFileSync(outPng, Buffer.from(shot.data, 'base64'));
  console.log('截图已保存: ' + outPng);

  ws.close();
  child.kill();
  try { rmSync(userDir, { recursive: true, force: true }); } catch { }
  process.exit(0);
} catch (e) {
  console.log('验证失败: ' + e.message);
  try { child.kill(); } catch { }
  process.exit(1);
}
