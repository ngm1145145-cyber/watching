// 在真实浏览器里验证手机网页客户端的触摸手势 → 发给服务端的输入消息。
// 用法: node gesture-verify.mjs [pageUrl]
// 需要服务端已在 8899 监听（只要在推帧即可，服务端是否允许远程控制不影响本测试：
// 本测试只校验客户端的「手势 → 控制消息」映射，不校验服务端是否真的注入）。
//
// ⚠️ 这个脚本会真的操作服务端那台电脑（移动光标、点左/右键、滚轮），
//    而且要先开启控制模式。请务必在没人用电脑的时候跑；确认后设置环境变量：
//      PowerShell:  $env:WATCHING_ALLOW_INPUT_TESTS = "1"
import { spawn } from 'node:child_process';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

if (process.env.WATCHING_ALLOW_INPUT_TESTS !== '1') {
  console.log('已阻止运行：本脚本会真的操作本机鼠标（移动光标、点击、滚轮）。');
  console.log('确认现在没人在用这台电脑后，设置 WATCHING_ALLOW_INPUT_TESTS=1 再跑。');
  console.log('  PowerShell:  $env:WATCHING_ALLOW_INPUT_TESTS = "1"; node tools/gesture-verify.mjs');
  process.exit(9);
}

const pageUrl = process.argv[2] || 'http://127.0.0.1:8899/mobile?ip=127.0.0.1&port=8899';
const port = 9700 + Math.floor(Math.random() * 200);
const userDir = mkdtempSync(join(tmpdir(), 'watching-gesture-'));

const child = spawn('C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe', [
  '--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check',
  `--remote-debugging-port=${port}`, `--user-data-dir=${userDir}`,
  '--window-size=420,860', 'about:blank'
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

const wsUrl = await getWsUrl();
const ws = new WebSocket(wsUrl);
await new Promise((res, rej) => { ws.addEventListener('open', res); ws.addEventListener('error', rej); });

let id = 0;
const waiters = new Map();
ws.addEventListener('message', (ev) => {
  const m = JSON.parse(ev.data);
  if (m.id && waiters.has(m.id)) { waiters.get(m.id)(m.result); waiters.delete(m.id); }
});
const send = (method, params = {}) => new Promise(res => {
  const i = ++id; waiters.set(i, res);
  ws.send(JSON.stringify({ id: i, method, params }));
});
const evaluate = async (expr) => {
  const r = await send('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true });
  if (r.exceptionDetails) throw new Error('页面异常: ' + (r.exceptionDetails.exception?.description || r.exceptionDetails.text));
  return r.result?.value;
};

await send('Page.enable');
await send('Runtime.enable');

// 在页面脚本之前装好 WebSocket.send 录音机，捕获客户端真正发出去的控制消息
await send('Page.addScriptToEvaluateOnNewDocument', {
  source: `
    window.__sent = [];
    (function () {
      var orig = WebSocket.prototype.send;
      WebSocket.prototype.send = function (d) {
        try { window.__sent.push(String(d)); } catch (e) { }
        return orig.call(this, d);
      };
    })();
  `
});

await send('Page.navigate', { url: pageUrl });
await sleep(8000);

const boot = await evaluate(`(() => {
  const btn = document.getElementById('btnCtrl');
  return {
    title: document.title,
    hasCanvas: !!document.getElementById('screen'),
    ctrlText: btn ? btn.textContent : null,
    ctrlDisabled: btn ? btn.disabled : null,
    sent: window.__sent.length
  };
})()`);
console.log('=== 页面状态 ===');
console.log(JSON.stringify(boot, null, 1));

if (!boot.hasCanvas) { console.log('❌ 找不到 #screen 画布'); shutdown(2); }
if (boot.sent < 1) { console.log('❌ 页面没有连上服务端（没有任何 WS 消息）'); shutdown(2); }

// 开启控制模式（服务端没开远程控制时按钮会点不动，这种情况直接失败退出）
const ctrl = await evaluate(`(() => {
  const btn = document.getElementById('btnCtrl');
  if (btn.textContent.indexOf('不可用') >= 0) return { ok: false, reason: btn.textContent };
  if (btn.textContent.indexOf('开') < 0 || btn.textContent.indexOf('关') >= 0) btn.click();
  return { ok: true, text: btn.textContent };
})()`);
if (!ctrl.ok) {
  console.log(`⚠️ 服务端未开启远程控制（按钮 = ${ctrl.reason}），无法测试手势`);
  shutdown(3);
}
console.log(`控制模式: ${ctrl.text}`);

// ---------- 手势工具（页面内） ----------
const helpers = `
  window.__mkTouch = function (id, x, y) {
    var cv = document.getElementById('screen');
    return new Touch({ identifier: id, target: cv, clientX: x, clientY: y, pageX: x, pageY: y });
  };
  window.__fire = function (type, touches, changed) {
    var cv = document.getElementById('screen');
    var ev = new TouchEvent(type, {
      touches: touches, targetTouches: touches, changedTouches: changed || touches,
      bubbles: true, cancelable: true
    });
    cv.dispatchEvent(ev);
  };
  window.__pt = function (fx, fy) {
    var r = document.getElementById('screen').getBoundingClientRect();
    return { x: r.left + r.width * fx, y: r.top + r.height * fy };
  };
  window.__msgs = function () { return window.__sent.map(function (s) { try { return JSON.parse(s); } catch (e) { return null; } }).filter(Boolean); };
  window.__inputs = function () { return window.__msgs().filter(function (m) { return m.t === 'input'; }); };
  window.__clear = function () { var n = window.__sent.length; window.__sent.length = 0; return n; };
  'ready'
`;
await evaluate(helpers);

const results = [];
function check(name, ok, detail) {
  results.push({ name, ok, detail });
  console.log(`${ok ? '✅' : '❌'} ${name}${detail ? ' — ' + detail : ''}`);
}

// ---------- 1. 单击 = 左键 down + up ----------
await evaluate('__clear()');
await evaluate(`(() => {
  var p = __pt(0.5, 0.5);
  var t = __mkTouch(1, p.x, p.y);
  __fire('touchstart', [t], [t]);
  __fire('touchend', [], [t]);
  return true;
})()`);
await sleep(300);
let ins = await evaluate('__inputs()');
let kinds = ins.map(m => m.kind + (m.button ? ':' + m.button : '') + (m.delta !== undefined ? ':' + m.delta : ''));
check('单击 → move + down:left + up:left',
  kinds.includes('down:left') && kinds.includes('up:left') && kinds.includes('move'),
  kinds.join(' | '));

// ---------- 2. 长按 700ms = 右键 down + up（不能带左键） ----------
await evaluate('__clear()');
await evaluate(`(() => {
  var p = __pt(0.5, 0.5);
  var t = __mkTouch(1, p.x, p.y);
  __fire('touchstart', [t], [t]);
  return true;
})()`);
await sleep(750);
await evaluate(`(() => { var p = __pt(0.5, 0.5); var t = __mkTouch(1, p.x, p.y); __fire('touchend', [], [t]); return true; })()`);
await sleep(300);
ins = await evaluate('__inputs()');
kinds = ins.map(m => m.kind + (m.button ? ':' + m.button : ''));
check('长按 → move + down:right + up:right',
  kinds.includes('down:right') && kinds.includes('up:right'),
  kinds.join(' | '));
check('长按不残留左键', !kinds.some(k => k.indexOf('left') >= 0), kinds.filter(k => k.indexOf('left') >= 0).join(' | ') || '无左键消息');

// ---------- 3. 拖动 60px = 按住左键拖动 ----------
await evaluate('__clear()');
await evaluate(`(() => {
  var a = __pt(0.35, 0.5);
  window.__dragA = a;
  var t1 = __mkTouch(1, a.x, a.y);
  __fire('touchstart', [t1], [t1]);
  return true;
})()`);
for (let i = 1; i <= 5; i++) {                 // 分多次派发，模拟真实手指（每 40ms 一次）
  await evaluate(`(() => {
    var a = window.__dragA;
    var t = __mkTouch(1, a.x + ${i * 12}, a.y + ${i * 6});
    __fire('touchmove', [t], [t]);
    return true;
  })()`);
  await sleep(40);
}
await evaluate(`(() => {
  var a = window.__dragA;
  var e = __mkTouch(1, a.x + 60, a.y + 30);
  __fire('touchend', [], [e]);
  return true;
})()`);
await sleep(300);
ins = await evaluate('__inputs()');
kinds = ins.map(m => m.kind + (m.button ? ':' + m.button : ''));
const nDown = kinds.filter(k => k === 'down:left').length;
const nUp = kinds.filter(k => k === 'up:left').length;
check('拖动 → 按住左键拖动（down:left … 多个 move … up:left）',
  nDown === 1 && nUp === 1 &&
  kinds.indexOf('down:left') === 0 && kinds.lastIndexOf('up:left') === kinds.length - 1 &&
  kinds.filter(k => k === 'move').length >= 3,
  kinds.join(' | '));

// ---------- 4. 双指上下滑 = 滚轮 ----------
await evaluate('__clear()');
await evaluate(`(() => {
  var a = __pt(0.42, 0.55), b = __pt(0.58, 0.55);
  var t1 = __mkTouch(1, a.x, a.y), t2 = __mkTouch(2, b.x, b.y);
  __fire('touchstart', [t1, t2], [t1, t2]);
  for (var i = 1; i <= 10; i++) {          // 两指一起上滑 20px/次，距离不变
    var d = i * 20;
    var u1 = __mkTouch(1, a.x, a.y - d), u2 = __mkTouch(2, b.x, b.y - d);
    __fire('touchmove', [u1, u2], [u1, u2]);
  }
  var e1 = __mkTouch(1, a.x, a.y - 200), e2 = __mkTouch(2, b.x, b.y - 200);
  __fire('touchend', [], [e1, e2]);
  return true;
})()`);
await sleep(300);
ins = await evaluate('__inputs()');
let wheels = ins.filter(m => m.kind === 'wheel');
kinds = ins.map(m => m.kind + (m.delta !== undefined ? ':' + m.delta : ''));
check('双指上滑 200px → 滚轮向下（delta -120 × ≥3）',
  wheels.length >= 3 && wheels.every(w => w.delta === -120),
  kinds.join(' | ') || '无消息');

// 反向：下滑 = 滚轮向上
await evaluate('__clear()');
await evaluate(`(() => {
  var a = __pt(0.42, 0.48), b = __pt(0.58, 0.48);
  var t1 = __mkTouch(1, a.x, a.y), t2 = __mkTouch(2, b.x, b.y);
  __fire('touchstart', [t1, t2], [t1, t2]);
  for (var i = 1; i <= 6; i++) {
    var d = i * 20;
    var u1 = __mkTouch(1, a.x, a.y + d), u2 = __mkTouch(2, b.x, b.y + d);
    __fire('touchmove', [u1, u2], [u1, u2]);
  }
  var e1 = __mkTouch(1, a.x, a.y + 120), e2 = __mkTouch(2, b.x, b.y + 120);
  __fire('touchend', [], [e1, e2]);
  return { }
})()`);
await sleep(300);
ins = await evaluate('__inputs()');
wheels = ins.filter(m => m.kind === 'wheel');
check('双指下滑 → 滚轮向上（delta +120）',
  wheels.length >= 1 && wheels.every(w => w.delta === 120),
  ins.map(m => m.kind + (m.delta !== undefined ? ':' + m.delta : '')).join(' | ') || '无消息');

// ---------- 5. 双指张开 = 缩放，不发滚轮 ----------
await evaluate('__clear()');
await evaluate(`(() => {
  var a = __pt(0.45, 0.5), b = __pt(0.55, 0.5);
  var t1 = __mkTouch(1, a.x, a.y), t2 = __mkTouch(2, b.x, b.y);
  __fire('touchstart', [t1, t2], [t1, t2]);
  for (var i = 1; i <= 8; i++) {            // 距离翻倍
    var w = i * 8;
    var u1 = __mkTouch(1, a.x - w, a.y), u2 = __mkTouch(2, b.x + w, b.y);
    __fire('touchmove', [u1, u2], [u1, u2]);
  }
  var e1 = __mkTouch(1, a.x - 64, a.y), e2 = __mkTouch(2, b.x + 64, b.y);
  __fire('touchend', [], [e1, e2]);
  return true;
})()`);
await sleep(300);
ins = await evaluate('__inputs()');
check('双指张开被识别为缩放（无滚轮消息）',
  ins.filter(m => m.kind === 'wheel').length === 0,
  ins.map(m => m.kind).join(' | ') || '无消息');

console.log('');
const failed = results.filter(r => !r.ok);
if (!failed.length) console.log(`🎉 全部 ${results.length} 项手势验证通过`);
else console.log(`❌ ${failed.length}/${results.length} 项失败: ` + failed.map(f => f.name).join('; '));

shutdown(failed.length ? 4 : 0);

function shutdown(code) {
  try { ws.close(); } catch { }
  try { child.kill(); } catch { }
  setTimeout(() => { try { rmSync(userDir, { recursive: true, force: true }); } catch { } process.exit(code); }, 200);
}
