// 验证远程控制：通过 WebSocket 发送鼠标移动，检查服务端光标是否真的移动
// 用法: node test-input.mjs <port> [pwd]
import net from 'node:net';

const port = Number(process.argv[2] || 8899);
const pwd = process.argv[3] || '';

const key = Buffer.from('dGhlIHNhbXBsZSBub25jZQ==').toString('base64');
const sock = net.connect(port, '127.0.0.1');
let buf = Buffer.alloc(0);
let handshake = false;
let text = [];

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

sock.on('connect', () => {
  const url = pwd ? `/ws?pwd=${encodeURIComponent(pwd)}` : '/ws';
  sock.write(`GET ${url} HTTP/1.1\r\nHost: 127.0.0.1\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n` +
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
    if (op === 0x1) {
      const t = payload.toString('utf8');
      text.push(t);
      const msg = JSON.parse(t);
      if (msg.t === 'welcome') {
        console.log('服务端 remote 状态 =', msg.remote);
        // 发三个不同位置，便于观察
        send({ t: 'input', kind: 'move', x: 0.25, y: 0.25 });
        console.log('已发送 move → 归一化 (0.25, 0.25)');
      } else {
        console.log('收到文本:', t.slice(0, 160));
      }
    }
    if (op === 0x9) sock.write(maskFrame(0xa, payload));
  }
}

sock.on('data', (d) => {
  buf = Buffer.concat([buf, d]);
  if (!handshake) {
    const i = buf.indexOf('\r\n\r\n');
    if (i < 0) return;
    console.log(buf.subarray(0, i).toString('latin1').split('\r\n')[0]);
    handshake = true;
    buf = buf.subarray(i + 4);
    send({ t: 'hello', kind: 'pc', name: 'input-test', version: '1.0' });
  }
  parse();
});

sock.on('error', (e) => { console.log('错误:', e.message); process.exit(1); });
setTimeout(() => { sock.end(); process.exit(0); }, 4000);
