// 输入风暴压测：模拟客户端疯狂发鼠标移动（老版本会阻塞 UI + 拖慢服务端）
// 用法: node input-stress.mjs <port> <seconds> <每秒消息数>
import net from 'node:net';

const port = Number(process.argv[2] || 8899);
const seconds = Number(process.argv[3] || 15);
const perSecond = Number(process.argv[4] || 500);

const key = Buffer.from('dGhlIHNhbXBsZSBub25jZQ==').toString('base64');
const sock = net.connect(port, '127.0.0.1');
let buf = Buffer.alloc(0);
let handshake = false;

let frames = 0, deltaFrames = 0, fullFrames = 0, bytes = 0;
let firstTs = 0, lastTs = 0;
let sent = 0;

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
    if (op !== 0x2) continue;

    frames++;
    bytes += payload.length;
    let end = 12;
    while (end < 12 + 256 && payload[end] !== 0) end++;
    try {
      const meta = JSON.parse(payload.subarray(12, end).toString('utf8'));
      if (firstTs === 0) firstTs = meta.ts;
      lastTs = meta.ts;
      if (meta.mode === 'delta') deltaFrames++; else fullFrames++;
    } catch { }
  }
}

sock.on('data', (d) => {
  buf = Buffer.concat([buf, d]);
  if (!handshake) {
    const i = buf.indexOf('\r\n\r\n');
    if (i < 0) return;
    handshake = true;
    buf = buf.subarray(i + 4);
    send({ t: 'hello', kind: 'pc', name: 'input-stress', version: '1.0' });
  }
  parse();
});

const t0 = Date.now();
let angle = 0;

// 以指定频率狂发鼠标移动（每条消息内容都不同，模拟真实拖动）
const timer = setInterval(() => {
  const batch = Math.max(1, Math.round(perSecond / 100));
  for (let i = 0; i < batch; i++) {
    angle += 0.15;
    const x = 0.5 + 0.35 * Math.cos(angle);
    const y = 0.5 + 0.35 * Math.sin(angle);
    send({ t: 'input', kind: 'move', x: Number(x.toFixed(4)), y: Number(y.toFixed(4)) });
    sent++;
  }
}, 10);

setTimeout(() => {
  clearInterval(timer);
  const secs = (Date.now() - t0) / 1000;
  const streamSecs = firstTs && lastTs > firstTs ? (lastTs - firstTs) / 1000 : secs;
  const fps = streamSecs > 0 ? (frames - 1) / streamSecs : 0;

  console.log(`\n=== 输入压测 ${secs.toFixed(0)} 秒 ===`);
  console.log(`  发出输入消息 : ${sent} 条（目标 ${perSecond}/秒）`);
  console.log(`  收到画面帧   : ${frames} 帧（整帧 ${fullFrames} / 增量 ${deltaFrames}）`);
  console.log(`  画面帧率     : ${fps.toFixed(1)} fps`);
  console.log(`  画面流量     : ${(bytes / 1024 / secs).toFixed(0)} KB/s`);
  console.log('');
  console.log(fps >= 8 ? '✅ 输入风暴下画面依然流畅（≥8fps）' : `⚠️ 帧率偏低：${fps.toFixed(1)} fps`);
  process.exit(0);
}, seconds * 1000);

sock.on('error', (e) => { console.log('socket 错误:', e.message); process.exit(1); });
