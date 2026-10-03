// 远程控制端到端验证：连接服务端 → 发送鼠标移动 → 检查服务端光标是否真的移动
// 用法: node input-verify.mjs <port> [pwd]
import net from 'node:net';
import { execFileSync } from 'node:child_process';

const port = Number(process.argv[2] || 8899);
const pwd = process.argv[3] || '';

const key = Buffer.from('dGhlIHNhbXBsZSBub25jZQ==').toString('base64');
const sock = net.connect(port, '127.0.0.1');
let buf = Buffer.alloc(0);
let handshake = false;
const texts = [];

function maskFrame(opcode, payload) {
  const mask = Buffer.from([9, 8, 7, 6]);
  const len = payload.length;
  let header;
  if (len < 126) header = Buffer.from([0x80 | opcode, 0x80 | len]);
  else { header = Buffer.alloc(4); header[0] = 0x80 | opcode; header[1] = 0x80 | 126; header.writeUInt16BE(len, 2); }
  const masked = Buffer.alloc(len);
  for (let i = 0; i < len; i++) masked[i] = payload[i] ^ mask[i % 4];
  return Buffer.concat([header, mask, masked]);
}
const send = (obj) => sock.write(maskFrame(0x1, Buffer.from(JSON.stringify(obj))));

// 用 PowerShell 读光标位置（服务端和本脚本在同一台机器）
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

sock.on('connect', () => {
  sock.write(`GET /ws HTTP/1.1\r\nHost: 127.0.0.1\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n` +
    `Sec-WebSocket-Key: ${key}\r\nSec-WebSocket-Version: 13\r\n\r\n`);
});

function parse() {
  while (buf.length >= 2) {
    const op = buf[0] & 0x0f;
    let len = buf[1] & 0x7f, off = 2;
    if (len === 126) { if (buf.length < 4) return; len = buf.readUInt16BE(2); off = 4; }
    else if (len === 127) { if (buf.length < 10) return; len = Number(buf.readBigUInt64BE(2)); off = 10; }
    if (buf.length < off + len) return;
    const payload = buf.subarray(off, off + len);
    buf = buf.subarray(off + len);
    if (op === 0x9) { sock.write(maskFrame(0xa, payload)); continue; }
    if (op === 0x1) texts.push(payload.toString('utf8'));
  }
}

sock.on('data', (d) => {
  buf = Buffer.concat([buf, d]);
  if (!handshake) {
    const i = buf.indexOf('\r\n\r\n');
    if (i < 0) return;
    handshake = true;
    buf = buf.subarray(i + 4);
    send({ t: 'hello', kind: 'pc', name: 'input-verify', version: '1.0' });
  }
  parse();
});

const sleep = (ms) => new Promise(r => setTimeout(r, ms));

(async () => {
  await sleep(1200);

  const welcome = texts.map(t => { try { return JSON.parse(t); } catch { return null; } })
    .find(m => m && m.t === 'welcome');
  console.log('服务端 remote 状态 =', welcome ? welcome.remote : '（没收到 welcome）');

  if (!welcome || !welcome.remote) {
    console.log('❌ 服务端没开启远程控制，无法验证注入');
    process.exit(2);
  }

  // 先移动到一个完全不同的地方作为基线
  setCursor(100, 100);
  await sleep(400);
  const before = cursorPos();
  console.log(`基线光标: (${before.x}, ${before.y})`);

  // 让服务端把光标挪到屏幕 60%/40% 处
  const targetNx = 0.60, targetNy = 0.40;
  send({ t: 'input', kind: 'move', x: targetNx, y: targetNy });
  await sleep(700);
  const after = cursorPos();

  // 屏幕尺寸（服务端用的虚拟桌面）
  const screen = execFileSync('powershell', ['-NoProfile', '-Command',
    'Add-Type -AssemblyName System.Windows.Forms; $b=[System.Windows.Forms.SystemInformation]::VirtualScreen; "$($b.Width),$($b.Height)"'
  ], { encoding: 'utf8' }).trim();
  const [sw, sh] = screen.split(',').map(Number);
  const expectX = Math.round(targetNx * (sw - 1));
  const expectY = Math.round(targetNy * (sh - 1));

  console.log(`发送 move → 归一化 (${targetNx}, ${targetNy})`);
  console.log(`期望光标 ≈ (${expectX}, ${expectY})   实际光标 = (${after.x}, ${after.y})`);

  const dx = Math.abs(after.x - expectX), dy = Math.abs(after.y - expectY);
  const moved = Math.abs(after.x - before.x) > 20 || Math.abs(after.y - before.y) > 20;

  console.log('');
  if (moved && dx <= 6 && dy <= 6) {
    console.log(`✅ 远程控制可用：光标确实被服务端移动了，坐标误差 (${dx}, ${dy}) 像素`);
    process.exit(0);
  } else if (moved) {
    console.log(`⚠️ 光标移动了但坐标偏差较大：(${dx}, ${dy}) 像素`);
    process.exit(1);
  } else {
    console.log('❌ 光标没有移动 —— 远程控制注入失败（可能被 UIPI 拦截，需要以管理员运行服务端）');
    process.exit(3);
  }
})();

sock.on('error', (e) => { console.log('socket 错误:', e.message); process.exit(1); });
