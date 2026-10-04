// 端到端验证「控制消息 → 服务端 SendInput → 真实程序」：
//   A. 右键：真实 Chrome 页面里监听 contextmenu，确认注入的右键被页面收到
//   B. 滚轮：同一个 Chrome 窗口显示超长页面，发滚轮消息，用 CDP 读 window.scrollY
// 用法: node inject-verify.mjs [port]
// 注意：会弹出一个可见的 Chrome 窗口，跑完自动关掉。
//
// ⚠️ 这个脚本会真的操作本机鼠标（移动光标、点右键、发 Esc、滚动窗口），
//    请务必在没人用电脑的时候跑；确认后设置环境变量：
//      PowerShell:  $env:WATCHING_ALLOW_INPUT_TESTS = "1"
import net from 'node:net';
import { spawn, execFileSync } from 'node:child_process';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

if (process.env.WATCHING_ALLOW_INPUT_TESTS !== '1') {
  console.log('已阻止运行：本脚本会真的操作本机鼠标（移动光标、点右键、发 Esc、滚轮）。');
  console.log('确认现在没人在用这台电脑后，设置 WATCHING_ALLOW_INPUT_TESTS=1 再跑。');
  console.log('  PowerShell:  $env:WATCHING_ALLOW_INPUT_TESTS = "1"; node tools/inject-verify.mjs');
  process.exit(9);
}

const port = Number(process.argv[2] || 8899);
const sleep = (ms) => new Promise(r => setTimeout(r, ms));
const results = [];
const check = (name, ok, detail, hard = true) => {
  results.push({ name, ok, hard });
  console.log(`${ok ? '✅' : '❌'} ${name}${detail ? ' — ' + detail : ''}`);
};

function ps(command) {
  return execFileSync('powershell', ['-NoProfile', '-Command', command], { encoding: 'utf8' }).trim();
}

const vsOut = ps('Add-Type -AssemblyName System.Windows.Forms; $b=[System.Windows.Forms.SystemInformation]::VirtualScreen; "$($b.X),$($b.Y),$($b.Width),$($b.Height)"');
const [vx, vy, vw, vh] = vsOut.split(',').map(Number);
console.log(`虚拟屏幕: ${vw}x${vh} @ (${vx},${vy})`);

// ---------------- 连上 Watching 服务端 ----------------
const key = Buffer.from('dGhlIHNhbXBsZSBub25jZQ==').toString('base64');
const sock = net.connect(port, '127.0.0.1');
let buf = Buffer.alloc(0);
let handshake = false;
let welcome = null;

function maskFrame(opcode, payload) {
  const mask = Buffer.from([2, 4, 6, 8]);
  const len = payload.length;
  let header;
  if (len < 126) header = Buffer.from([0x80 | opcode, 0x80 | len]);
  else { header = Buffer.alloc(4); header[0] = 0x80 | opcode; header[1] = 0x80 | 126; header.writeUInt16BE(len, 2); }
  const masked = Buffer.alloc(len);
  for (let i = 0; i < len; i++) masked[i] = payload[i] ^ mask[i % 4];
  return Buffer.concat([header, mask, masked]);
}
const send = (obj) => sock.write(maskFrame(0x1, Buffer.from(JSON.stringify(obj))));

sock.on('connect', () => {
  sock.write(`GET /ws HTTP/1.1\r\nHost: 127.0.0.1\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n` +
    `Sec-WebSocket-Key: ${key}\r\nSec-WebSocket-Version: 13\r\n\r\n`);
});
sock.on('data', (d) => {
  buf = Buffer.concat([buf, d]);
  if (!handshake) {
    const i = buf.indexOf('\r\n\r\n');
    if (i < 0) return;
    handshake = true;
    buf = buf.subarray(i + 4);
    send({ t: 'hello', kind: 'pc', name: 'inject-verify', version: '1.0' });
  }
  while (buf.length >= 2) {
    const op = buf[0] & 0x0f;
    let len = buf[1] & 0x7f, off = 2;
    if (len === 126) { if (buf.length < 4) return; len = buf.readUInt16BE(2); off = 4; }
    else if (len === 127) { if (buf.length < 10) return; len = Number(buf.readBigUInt64BE(2)); off = 10; }
    if (buf.length < off + len) return;
    const payload = buf.subarray(off, off + len);
    buf = buf.subarray(off + len);
    if (op === 0x9) { sock.write(maskFrame(0xa, payload)); continue; }
    if (op !== 0x1) continue;
    try {
      const m = JSON.parse(payload.toString('utf8'));
      if (m.t === 'welcome' || m.t === 'state') welcome = m;
      if (m.t === 'error') console.log(`服务端错误: ${m.msg}`);
    } catch { }
  }
});

for (let i = 0; i < 40 && !welcome; i++) await sleep(200);
if (!welcome) { console.log('❌ 连不上服务端'); cleanup(2); }
console.log(`服务端: ${welcome.name || '?'} ${welcome.sw}x${welcome.sh} remote=${welcome.remote}`);
if (!welcome.remote) {
  console.log('⚠️ 服务端没开远程控制（托盘右键 → 设置 → 允许客户端远程控制），无法验证注入');
  cleanup(3);
}

const norm = (px, py) => ({ x: Number(((px - vx) / vw).toFixed(4)), y: Number(((py - vy) / vh).toFixed(4)) });
const moveTo = (px, py) => send({ t: 'input', kind: 'move', ...norm(px, py) });

// ---------------- A/B. 用真实 Chrome 窗口当靶子 ----------------
console.log('\n=== 起一个真实 Chrome 窗口当靶子 ===');
const page = join(tmpdir(), 'watching-scroll-page.html');
writeFileSync(page, `<!doctype html><meta charset="utf-8"><title>scroll probe</title>
<style>html,body{margin:0}body{height:9000px;background:linear-gradient(#123,#567,#abc,#def)}#m{position:absolute;top:0;left:0}</style>
<div id="m">scroll probe</div>`, 'utf8');

const cdpPort = 9900 + Math.floor(Math.random() * 90);
const userDir = mkdtempSync(join(tmpdir(), 'watching-scroll-'));
const winX = vx + 60, winY = vy + 60, winW = 900, winH = 700;
const chrome = spawn('C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe', [
  '--no-first-run', '--no-default-browser-check', '--disable-session-crashed-bubble',
  `--remote-debugging-port=${cdpPort}`, `--user-data-dir=${userDir}`,
  `--window-position=${winX},${winY}`, `--window-size=${winW},${winH}`,
  'file:///' + page.replace(/\\/g, '/')
], { stdio: 'ignore', windowsHide: false });

async function cdpUrl() {
  for (let i = 0; i < 60; i++) {
    try {
      const r = await fetch(`http://127.0.0.1:${cdpPort}/json/list`);
      const list = await r.json();
      const t = list.find(x => x.type === 'page' && x.url.startsWith('file:'));
      if (t?.webSocketDebuggerUrl) return t.webSocketDebuggerUrl;
    } catch { }
    await sleep(250);
  }
  return null;
}

const cwsUrl = await cdpUrl();
if (!cwsUrl) { console.log('❌ 可见 Chrome 没起来，滚轮无法验证'); check('滚轮让真实窗口滚动', false, 'Chrome 未就绪'); }
else {
  const cws = new WebSocket(cwsUrl);
  await new Promise((res, rej) => { cws.addEventListener('open', res); cws.addEventListener('error', rej); });
  let cid = 0; const waiters = new Map();
  cws.addEventListener('message', (ev) => {
    const m = JSON.parse(ev.data);
    if (m.id && waiters.has(m.id)) { waiters.get(m.id)(m.result); waiters.delete(m.id); }
  });
  const ceval = (expr) => new Promise(res => {
    const i = ++cid; waiters.set(i, res);
    cws.send(JSON.stringify({ id: i, method: 'Runtime.evaluate', params: { expression: expr, returnByValue: true } }));
  });
  const cval = async (expr) => {
    const r = await ceval(expr);
    if (r?.exceptionDetails) {
      console.log(`  (页面求值异常: ${r.exceptionDetails.exception?.description || r.exceptionDetails.text})`);
      return undefined;
    }
    return r?.result?.value;
  };

  await sleep(2500);

  const cx = winX + winW / 2, cy = winY + winH / 2;
  const nc = norm(cx, cy);

  // 先确认能读到页面（顺便等页面就绪）
  let ready = -1;
  for (let i = 0; i < 20; i++) {
    ready = await cval('1+1');
    if (ready === 2) break;
    await sleep(400);
  }
  if (ready !== 2) { console.log('❌ 连不上 Chrome 页面调试通道'); check('右键被真实页面收到（contextmenu 事件）', false, 'CDP 求值失败'); check('滚轮向下让真实窗口滚动', false, 'CDP 求值失败'); }
  else {
    // ---- A. 右键：在页面里监听 contextmenu，看注入的右键有没有被页面收到 ----
    const hooked = await cval('(() => { window.__ctx = 0; window.addEventListener("contextmenu", function(){ window.__ctx++; }); return "hooked"; })()');
    console.log(`contextmenu 监听: ${hooked}`);
    moveTo(cx, cy);
    await sleep(300);
    send({ t: 'input', kind: 'down', button: 'right', ...nc });
    send({ t: 'input', kind: 'up', button: 'right', ...nc });
    await sleep(900);
    let ctx = -1;
    for (let i = 0; i < 5; i++) {
      ctx = await cval('window.__ctx');
      if (ctx >= 1) break;
      await sleep(400);
    }
    check('右键被真实页面收到（contextmenu 事件）', ctx >= 1, `contextmenu 触发 ${ctx} 次`);
    send({ t: 'input', kind: 'key', key: 'esc' });     // 关掉 Chrome 自己的右键菜单
    await sleep(400);
    moveTo(cx, cy);
    await sleep(200);

    // ---- B. 滚轮：读 window.scrollY ----
    const y0 = (await cval('window.scrollY')) ?? -1;
    console.log(`\n=== 滚轮注入 ===\n初始 scrollY = ${y0}`);

    send({ t: 'input', kind: 'wheel', delta: -600 });     // 向下滚 5 格
    await sleep(900);
    const y1 = (await cval('window.scrollY')) ?? -1;
    console.log(`滚轮 -600 之后 scrollY = ${y1}`);

    send({ t: 'input', kind: 'wheel', delta: 600 });      // 回到顶部
    await sleep(900);
    const y2 = (await cval('window.scrollY')) ?? -1;
    console.log(`滚轮 +600 之后 scrollY = ${y2}`);

    check('滚轮向下让真实窗口滚动', y1 > y0 + 50, `${y0} → ${y1}`);
    check('滚轮向上把窗口滚回顶部', y2 < y1 - 50, `${y1} → ${y2}`);
  }

  try { cws.close(); } catch { }
}
try { chrome.kill(); } catch { }
setTimeout(() => { try { rmSync(userDir, { recursive: true, force: true }); } catch { } }, 300);

console.log('');
const hard = results.filter(r => r.hard);
const failed = hard.filter(r => !r.ok);
if (!failed.length) console.log(`🎉 注入验证通过（${hard.length} 项硬性检查）`);
else console.log(`❌ ${failed.length}/${hard.length} 项失败: ` + failed.map(f => f.name).join('; '));
cleanup(failed.length ? 4 : 0);

function cleanup(code) {
  try { sock.destroy(); } catch { }
  try { rmSync(userDir, { recursive: true, force: true }); } catch { }
  setTimeout(() => process.exit(code), 400);
}
