// 抓一帧并保存，用于人工检查光标是否画进画面
// 用法: node grab-frame.mjs <port> <输出jpg>
import net from 'node:net';
import { writeFileSync } from 'node:fs';

const port = Number(process.argv[2] || 8899);
const out = process.argv[3] || 'frame.jpg';

const key = Buffer.from('dGhlIHNhbXBsZSBub25jZQ==').toString('base64');
const sock = net.connect(port, '127.0.0.1');
let buf = Buffer.alloc(0);
let handshake = false;

function maskFrame(opcode, payload) {
  const mask = Buffer.from([1, 3, 5, 7]);
  const len = payload.length;
  let header = len < 126
    ? Buffer.from([0x80 | opcode, 0x80 | len])
    : (() => { const h = Buffer.alloc(4); h[0] = 0x80 | opcode; h[1] = 0x80 | 126; h.writeUInt16BE(len, 2); return h; })();
  const masked = Buffer.alloc(len);
  for (let i = 0; i < len; i++) masked[i] = payload[i] ^ mask[i % 4];
  return Buffer.concat([header, mask, masked]);
}
const send = (obj) => sock.write(maskFrame(0x1, Buffer.from(JSON.stringify(obj))));

sock.on('connect', () => {
  sock.write(`GET /ws HTTP/1.1\r\nHost: 127.0.0.1\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n` +
    `Sec-WebSocket-Key: ${key}\r\nSec-WebSocket-Version: 13\r\n\r\n`);
});

function handle(payload) {
  if (payload.subarray(0, 4).toString('ascii') !== 'WF01') return;
  let end = 12;
  while (end < 12 + 256 && payload[end] !== 0) end++;
  const meta = JSON.parse(payload.subarray(12, end).toString('utf8'));

  if (meta.mode === 'delta') return;   // 只要整帧

  const jpeg = payload.subarray(12 + 256);
  writeFileSync(out, jpeg);
  console.log(`已保存 ${out}  ${meta.w}x${meta.h}  ${jpeg.length} 字节  mode=${meta.mode}`);
  sock.end();
  process.exit(0);
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
    if (op === 0x2) handle(payload);
  }
}

sock.on('data', (d) => {
  buf = Buffer.concat([buf, d]);
  if (!handshake) {
    const i = buf.indexOf('\r\n\r\n');
    if (i < 0) return;
    handshake = true;
    buf = buf.subarray(i + 4);
    send({ t: 'hello', kind: 'pc', name: 'grab-frame', version: '1.0' });
  }
  parse();
});

setTimeout(() => { console.log('超时：没收到整帧'); process.exit(1); }, 12000);
