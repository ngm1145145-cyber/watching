// 传输/CPU 基准测试：连上服务端收 N 秒，统计帧率、流量、以及服务端每帧的
// 抓屏+编码耗时（帧头的 meta.capMs）、整帧/增量占比。
// 只做网络连接，不操作鼠标键盘，可以随时跑。
//
// 用法: node stream-bench.mjs [port] [seconds] [label]
import net from 'node:net';

const port = Number(process.argv[2] || 8899);
const seconds = Number(process.argv[3] || 40);
const label = process.argv[4] || '';

const key = Buffer.from('dGhlIHNhbXBsZSBub25jZQ==').toString('base64');
const sock = net.connect(port, '127.0.0.1');
let buf = Buffer.alloc(0);
let handshake = false;

const stat = { frames: 0, bytes: 0, full: 0, delta: 0, capMs: [], fullCapMs: [], deltaCapMs: [], seqGaps: 0 };
let lastSeq = -1;

const maskFrame = (opcode, payload) => {
  const mask = Buffer.from([3, 1, 4, 1]);
  const len = payload.length;
  let header;
  if (len < 126) header = Buffer.from([0x80 | opcode, 0x80 | len]);
  else { header = Buffer.alloc(4); header[0] = 0x80 | opcode; header[1] = 0x80 | 126; header.writeUInt16BE(len, 2); }
  const m = Buffer.alloc(len);
  for (let i = 0; i < len; i++) m[i] = payload[i] ^ mask[i % 4];
  return Buffer.concat([header, mask, m]);
};
const send = (o) => sock.write(maskFrame(0x1, Buffer.from(JSON.stringify(o))));

sock.on('connect', () => sock.write(
  `GET /ws HTTP/1.1\r\nHost: 127.0.0.1\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n` +
  `Sec-WebSocket-Key: ${key}\r\nSec-WebSocket-Version: 13\r\n\r\n`));

sock.on('error', (e) => { console.log('socket 错误:', e.message); process.exit(1); });

sock.on('data', (d) => {
  buf = Buffer.concat([buf, d]);
  if (!handshake) {
    const i = buf.indexOf('\r\n\r\n');
    if (i < 0) return;
    handshake = true;
    buf = buf.subarray(i + 4);
    send({ t: 'hello', kind: 'pc', name: 'stream-bench', version: 'bench' });
    send({ t: 'quality', quality: 70, fps: 20, maxWidth: 1600 });
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
    if (op !== 0x2) continue;

    stat.frames++;
    stat.bytes += payload.length;
    if (payload.subarray(0, 4).toString('ascii') !== 'WF01') continue;

    const seq = payload.readBigInt64LE(4);
    if (lastSeq >= 0 && seq !== lastSeq + 1n) stat.seqGaps++;
    lastSeq = seq;

    let end = 12;
    while (end < 12 + 256 && payload[end] !== 0) end++;
    let meta = {};
    try { meta = JSON.parse(payload.subarray(12, end).toString('utf8')); } catch { }

    const cap = Number(meta.capMs || 0);
    if (cap > 0) {
      stat.capMs.push(cap);
      if (meta.mode === 'full') { stat.full++; stat.fullCapMs.push(cap); }
      else { stat.delta++; stat.deltaCapMs.push(cap); }
    } else if (meta.mode === 'full') stat.full++;
    else stat.delta++;
  }
});

const avg = (a) => a.length ? a.reduce((x, y) => x + y, 0) / a.length : 0;
const pct = (a, p) => { if (!a.length) return 0; const s = [...a].sort((x, y) => x - y); return s[Math.min(s.length - 1, Math.floor(s.length * p))]; };

setTimeout(() => {
  const kb = stat.bytes / 1024;
  console.log(`\n=== ${seconds} 秒传输基准 ${label ? '(' + label + ')' : ''} ===`);
  console.log(`  帧数 ${stat.frames}（整帧 ${stat.full} / 增量 ${stat.delta}）  ≈ ${(stat.frames / seconds).toFixed(1)} fps`);
  console.log(`  流量 ${kb.toFixed(0)} KB  ≈ ${(kb / seconds).toFixed(0)} KB/s   平均 ${stat.frames ? (kb / stat.frames).toFixed(1) : 0} KB/帧`);
  console.log(`  服务端每帧抓屏+编码 capMs：平均 ${avg(stat.capMs).toFixed(1)}ms  p50 ${pct(stat.capMs, 0.5).toFixed(1)}ms  p90 ${pct(stat.capMs, 0.9).toFixed(1)}ms`);
  console.log(`    其中整帧 ${avg(stat.fullCapMs).toFixed(1)}ms（${stat.fullCapMs.length} 次）  增量帧 ${avg(stat.deltaCapMs).toFixed(1)}ms（${stat.deltaCapMs.length} 次）`);
  console.log(`  序号跳变 ${stat.seqGaps} 次`);
  process.exit(0);
}, seconds * 1000);
