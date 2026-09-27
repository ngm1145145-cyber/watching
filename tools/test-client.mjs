// Watching 端到端测试客户端：连接服务端、收帧、写文件、校验协议
// 用法: node test-client.mjs <host> <port> [frames]
import { createHash } from 'node:crypto';
import { writeFileSync } from 'node:fs';
import net from 'node:net';

const host = process.argv[2] || '127.0.0.1';
const port = Number(process.argv[3] || 8899);
const want = Number(process.argv[4] || 6);
const outPrefix = process.argv[5] || 'H:\\ds-harness\\.tmp\\watching-test';

const key = Buffer.from('dGhlIHNhbXBsZSBub25jZQ==').toString('base64');
const sock = net.connect(port, host);

let handshakeDone = false;
let buf = Buffer.alloc(0);
const frames = [];
let meta = null;

sock.setNoDelay(true);

sock.on('connect', () => {
  const req =
    `GET /ws HTTP/1.1\r\n` +
    `Host: ${host}:${port}\r\n` +
    `Upgrade: websocket\r\n` +
    `Connection: Upgrade\r\n` +
    `Sec-WebSocket-Key: ${key}\r\n` +
    `Sec-WebSocket-Version: 13\r\n\r\n`;
  sock.write(req);
});

function maskFrame(opcode, payload) {
  const mask = Buffer.from([1, 2, 3, 4]);
  const len = payload.length;
  let header;
  if (len < 126) {
    header = Buffer.from([0x80 | opcode, 0x80 | len]);
  } else if (len <= 0xffff) {
    header = Buffer.alloc(4);
    header[0] = 0x80 | opcode; header[1] = 0x80 | 126;
    header.writeUInt16BE(len, 2);
  } else {
    header = Buffer.alloc(10);
    header[0] = 0x80 | opcode; header[1] = 0x80 | 127;
    header.writeBigUInt64BE(BigInt(len), 2);
  }
  const masked = Buffer.alloc(len);
  for (let i = 0; i < len; i++) masked[i] = payload[i] ^ mask[i % 4];
  return Buffer.concat([header, mask, masked]);
}

function parseFrames() {
  while (buf.length >= 2) {
    const b0 = buf[0], b1 = buf[1];
    const op = b0 & 0x0f;
    const masked = (b1 & 0x80) !== 0;
    let len = b1 & 0x7f;
    let off = 2;
    if (len === 126) {
      if (buf.length < 4) return;
      len = buf.readUInt16BE(2); off = 4;
    } else if (len === 127) {
      if (buf.length < 10) return;
      len = Number(buf.readBigUInt64BE(2)); off = 10;
    }
    let mask = null;
    if (masked) {
      if (buf.length < off + 4) return;
      mask = buf.subarray(off, off + 4); off += 4;
    }
    if (buf.length < off + len) return;
    let payload = buf.subarray(off, off + len);
    if (masked) {
      const copy = Buffer.from(payload);
      for (let i = 0; i < copy.length; i++) copy[i] ^= mask[i % 4];
      payload = copy;
    }
    buf = buf.subarray(off + len);
    handleFrame(op, payload);
  }
}

function handleFrame(op, payload) {
  if (op === 0x9) { // ping
    sock.write(maskFrame(0xa, payload));
    return;
  }
  if (op === 0x1) {
    const text = payload.toString('utf8');
    try {
      const msg = JSON.parse(text);
      if (msg.t === 'welcome') {
        console.log('[welcome]', text);
        sock.write(maskFrame(0x1, Buffer.from(JSON.stringify({
          t: 'quality', quality: 65, fps: 15, maxWidth: 1280
        }))));
      } else {
        console.log('[text]', text.slice(0, 200));
      }
    } catch { console.log('[text-raw]', text.slice(0, 200)); }
    return;
  }
  if (op !== 0x2) return;

  if (payload.subarray(0, 4).toString('ascii') !== 'WF01') {
    console.log('!! 帧魔数错误:', payload.subarray(0, 4).toString('hex'));
    return;
  }

  const seq = payload.readBigInt64LE(4);
  let end = 12;
  while (end < 12 + 256 && payload[end] !== 0) end++;
  const m = JSON.parse(payload.subarray(12, end).toString('utf8'));
  const jpeg = payload.subarray(12 + 256);
  const isJpeg = jpeg[0] === 0xff && jpeg[1] === 0xd8 && jpeg[jpeg.length - 2] === 0xff && jpeg[jpeg.length - 1] === 0xd9;

  frames.push({ seq: Number(seq), meta: m, jpeg, isJpeg });
  if (!meta) meta = m;

  if (frames.length <= want) {
    console.log(`[frame ${frames.length}] seq=${seq} ${m.w}x${m.h} q=${m.q} crop=${m.crop} sw=${m.sw}x${m.sh} jpeg=${jpeg.length}B valid=${isJpeg} cap=${m.capMs}ms`);
    writeFileSync(`${outPrefix}-${frames.length}.jpg`, jpeg);
  }

  if (frames.length >= want) {
    finish();
  }
}

function finish() {
  const t0 = Date.now();
  const bytes = frames.reduce((a, f) => a + f.jpeg.length, 0);
  const span = frames.length > 1 ? (frames[frames.length - 1].meta.ts - frames[0].meta.ts) : 0;
  console.log('--- 汇总 ---');
  console.log(`帧数=${frames.length} 总字节=${bytes} 时间跨度=${span}ms 实测fps=${span > 0 ? ((frames.length - 1) * 1000 / span).toFixed(1) : '?'}`);
  console.log('全部 JPEG 头尾正确 =', frames.every((f) => f.isJpeg));
  console.log('sha256(第1帧) =', createHash('sha256').update(frames[0].jpeg).digest('hex').slice(0, 16));
  sock.end();
  setTimeout(() => process.exit(0), 100);
}

sock.on('data', (d) => {
  buf = Buffer.concat([buf, d]);
  if (!handshakeDone) {
    const idx = buf.indexOf('\r\n\r\n');
    if (idx < 0) return;
    const head = buf.subarray(0, idx).toString('latin1');
    console.log('--- 握手响应 ---');
    console.log(head);
    if (!/101/.test(head)) { process.exit(2); }
    handshakeDone = true;
    buf = buf.subarray(idx + 4);
    sock.write(maskFrame(0x1, Buffer.from(JSON.stringify({
      t: 'hello', kind: 'pc', name: 'node-test', version: '1.0'
    }))));
  }
  parseFrames();
});

sock.on('error', (e) => { console.log('socket error:', e.message); process.exit(1); });
setTimeout(() => { console.log('超时：只收到', frames.length, '帧'); process.exit(3); }, 20000);
