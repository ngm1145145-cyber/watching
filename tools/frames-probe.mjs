// 逐帧诊断：显示每帧大小、JPEG 头尾，并在测试期间轻微改变屏幕内容
// 用法: node frames-probe.mjs <port> <seconds>
import net from 'node:net';
import { createRequire } from 'node:module';

const port = Number(process.argv[2] || 8899);
const seconds = Number(process.argv[3] || 15);

const key = Buffer.from('dGhlIHNhbXBsZSBub25jZQ==').toString('base64');
const sock = net.connect(port, '127.0.0.1');
let buf = Buffer.alloc(0);
let handshake = false;
let frames = 0, badMagic = 0, badJpeg = 0, bytes = 0;
const sizes = [];

function maskFrame(opcode, payload) {
  const mask = Buffer.from([3, 1, 4, 1]);
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
    const magic = payload.subarray(0, 4).toString('ascii');
    if (magic !== 'WF01') { badMagic++; console.log(`  第${frames}帧 魔数错误: ${magic} len=${len}`); continue; }
    const jpeg = payload.subarray(12 + 256);
    const okHead = jpeg[0] === 0xff && jpeg[1] === 0xd8;
    const okTail = jpeg[jpeg.length - 2] === 0xff && jpeg[jpeg.length - 1] === 0xd9;
    if (!okHead || !okTail) {
      badJpeg++;
      if (badJpeg <= 3) {
        console.log(`  第${frames}帧 JPEG 头尾异常 head=${jpeg[0]?.toString(16)},${jpeg[1]?.toString(16)} ` +
                    `tail=${jpeg[jpeg.length-2]?.toString(16)},${jpeg[jpeg.length-1]?.toString(16)} ` +
                    `总长=${payload.length} jpeg长=${jpeg.length}`);
      }
    }
    sizes.push(jpeg.length);
  }
}

sock.on('data', (d) => {
  buf = Buffer.concat([buf, d]);
  if (!handshake) {
    const i = buf.indexOf('\r\n\r\n');
    if (i < 0) return;
    handshake = true;
    buf = buf.subarray(i + 4);
    send({ t: 'hello', kind: 'pc', name: 'frames-probe', version: '1.0' });
    send({ t: 'quality', quality: 70, fps: 20, maxWidth: 1280 });
  }
  parse();
});

sock.on('error', (e) => { console.log('socket 错误:', e.message); process.exit(1); });

setTimeout(() => {
  console.log(`\n=== ${seconds} 秒统计 ===`);
  console.log(`  收到帧数: ${frames}`);
  console.log(`  魔数错误: ${badMagic}   JPEG 头尾异常: ${badJpeg}`);
  console.log(`  总字节: ${(bytes / 1024).toFixed(0)} KB   平均 ${frames ? (bytes / frames / 1024).toFixed(1) : 0} KB/帧`);
  if (sizes.length) {
    const uniq = new Set(sizes).size;
    console.log(`  不同大小的帧: ${uniq} / ${sizes.length}（唯一说明画面在变；几乎全相同说明静止跳帧在起作用）`);
  }
  process.exit(badMagic + badJpeg > 0 ? 1 : 0);
}, seconds * 1000);
