// 分块增量传输探针：区分整帧/增量帧，统计分块数与字节数，算出省了多少流量
// 用法: node delta-probe.mjs <port> <seconds>
import net from 'node:net';

const port = Number(process.argv[2] || 8899);
const seconds = Number(process.argv[3] || 20);

const key = Buffer.from('dGhlIHNhbXBsZSBub25jZQ==').toString('base64');
const sock = net.connect(port, '127.0.0.1');
let buf = Buffer.alloc(0);
let handshake = false;

let frames = 0, fullFrames = 0, deltaFrames = 0, tiles = 0, bytes = 0;
let fullBytes = 0, deltaBytes = 0, tileBytes = 0;
let badMagic = 0, badJpeg = 0, parseErrors = 0;
const t0 = Date.now();
let firstTs = 0, lastTs = 0;

function maskFrame(opcode, payload) {
  const mask = Buffer.from([5, 3, 2, 7]);
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

function handleBinary(payload) {
  frames++;
  bytes += payload.length;

  if (payload.subarray(0, 4).toString('ascii') !== 'WF01') { badMagic++; return; }

  let end = 12;
  while (end < 12 + 256 && payload[end] !== 0) end++;
  let meta;
  try { meta = JSON.parse(payload.subarray(12, end).toString('utf8')); }
  catch { parseErrors++; return; }

  const body = payload.subarray(12 + 256);

  if (meta.mode === 'delta') {
    deltaFrames++;
    deltaBytes += payload.length;

    if (body.subarray(0, 4).toString('ascii') !== 'DT01') { parseErrors++; return; }
    const count = body.readInt32LE(4);
    let p = 8;
    const entries = [];
    for (let i = 0; i < count; i++) {
      const x = (body[p] << 8) | body[p + 1];
      const y = (body[p + 2] << 8) | body[p + 3];
      const len = body.readUInt32LE(p + 4);
      p += 8;
      entries.push({ x, y, len });
    }
    for (const e of entries) {
      const jpeg = body.subarray(p, p + e.len);
      p += e.len;
      tileBytes += e.len;
      tiles++;
      if (!(jpeg[0] === 0xff && jpeg[1] === 0xd8 && jpeg[jpeg.length - 2] === 0xff && jpeg[jpeg.length - 1] === 0xd9)) badJpeg++;
    }
    if (firstTs === 0) firstTs = meta.ts;
    lastTs = meta.ts;
  } else {
    fullFrames++;
    fullBytes += payload.length;
    const ok = body[0] === 0xff && body[1] === 0xd8 && body[body.length - 2] === 0xff && body[body.length - 1] === 0xd9;
    if (!ok) badJpeg++;
  }
}

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
    if (op === 0x2) handleBinary(payload);
  }
}

sock.on('data', (d) => {
  buf = Buffer.concat([buf, d]);
  if (!handshake) {
    const i = buf.indexOf('\r\n\r\n');
    if (i < 0) return;
    handshake = true;
    buf = buf.subarray(i + 4);
    send({ t: 'hello', kind: 'pc', name: 'delta-probe', version: '1.0' });
    send({ t: 'quality', quality: 70, fps: 20, maxWidth: 1600 });
  }
  parse();
});

sock.on('error', (e) => { console.log('socket 错误:', e.message); process.exit(1); });

setTimeout(() => {
  const secs = (Date.now() - t0) / 1000;
  const streamSecs = firstTs && lastTs > firstTs ? (lastTs - firstTs) / 1000 : secs;
  console.log(`\n=== ${secs.toFixed(1)} 秒统计（服务端时间跨度 ${streamSecs.toFixed(1)}s）===`);
  console.log(`  总帧数      : ${frames}   （整帧 ${fullFrames} / 增量 ${deltaFrames}）`);
  console.log(`  总流量      : ${(bytes / 1024).toFixed(0)} KB   平均 ${(bytes / 1024 / secs).toFixed(0)} KB/s`);
  console.log(`  整帧流量    : ${(fullBytes / 1024).toFixed(0)} KB`);
  console.log(`  增量流量    : ${(deltaBytes / 1024).toFixed(0)} KB  （其中分块数据 ${(tileBytes / 1024).toFixed(0)} KB）`);
  console.log(`  分块总数    : ${tiles}   平均每增量帧 ${deltaFrames ? (tiles / deltaFrames).toFixed(1) : 0} 块`);
  if (frames) console.log(`  平均每帧    : ${(bytes / frames / 1024).toFixed(1)} KB`);
  console.log(`  错误        : 魔数 ${badMagic} / JPEG ${badJpeg} / 解析 ${parseErrors}`);
  const verdict = badMagic + badJpeg + parseErrors === 0 ? '✅ 协议正常' : '⚠️ 有错误';
  console.log(`  ${verdict}`);
  process.exit(badMagic + badJpeg + parseErrors > 0 ? 1 : 0);
}, seconds * 1000);
