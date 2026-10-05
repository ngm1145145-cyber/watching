// 验证「新客户端接入时一定能拿到整帧」——这是「手机连上却黑屏」的直接原因：
// 增量帧是相对上一帧的差量，客户端没有基准画面就什么都画不出来。
// 服务端以前会在「抓屏+编码」期间把别人请求的关键帧标志覆盖掉，于是新接入的
// 客户端可能只收到增量帧，一直黑屏到下一个整帧（默认 150 帧 ≈ 7 秒，桌面不动时更久）。
//
// 用法: node keyframe-verify.mjs [port] [轮数]
// 只做网络连接，不操作鼠标键盘。
import net from 'node:net';

const port = Number(process.argv[2] || 8899);
const rounds = Number(process.argv[3] || 6);
const sleep = (ms) => new Promise(r => setTimeout(r, ms));

function connect(name) {
  return new Promise((resolve, reject) => {
    const key = Buffer.from('dGhlIHNhbXBsZSBub25jZQ==').toString('base64');
    const sock = net.connect(port, '127.0.0.1');
    let buf = Buffer.alloc(0);
    let handshake = false;
    const state = { name, firstMode: null, frames: 0, sock };

    const maskFrame = (opcode, payload) => {
      const mask = Buffer.from([2, 4, 6, 8]);
      const len = payload.length;
      let header;
      if (len < 126) header = Buffer.from([0x80 | opcode, 0x80 | len]);
      else { header = Buffer.alloc(4); header[0] = 0x80 | opcode; header[1] = 0x80 | 126; header.writeUInt16BE(len, 2); }
      const masked = Buffer.alloc(len);
      for (let i = 0; i < len; i++) masked[i] = payload[i] ^ mask[i % 4];
      return Buffer.concat([header, mask, masked]);
    };
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
        send({ t: 'hello', kind: 'pc', name, version: 'probe' });
        resolve(state);
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

        state.frames++;
        if (state.firstMode === null) {
          let end = 12;
          while (end < 12 + 256 && payload[end] !== 0) end++;
          try {
            const meta = JSON.parse(payload.subarray(12, end).toString('utf8'));
            state.firstMode = meta.mode || 'full';
          } catch { state.firstMode = 'unparsed'; }
        }
      }
    });

    sock.on('error', reject);
    setTimeout(() => reject(new Error(name + ' 连接超时')), 8000);
  });
}

let bad = 0;
for (let i = 1; i <= rounds; i++) {
  // 第一路把抓屏流「焐热」，第二路再接进来 —— 复现「别人已经在这条流上看，我连进来」
  const a = await connect('warmup-' + i);
  await sleep(1200);                       // 桌面不动，增量可能是空的；关键是让流处于活跃状态
  const b = await connect('join-' + i);
  await sleep(1500);

  const ok = b.firstMode === 'full';
  if (!ok) bad++;
  console.log(`第 ${i} 轮：老客户端=${a.firstMode}(${a.frames}帧)  新接入客户端首帧=${b.firstMode}(${b.frames}帧)  ${ok ? '✅' : '❌ 不是整帧'}`);
  try { a.sock.destroy(); } catch { }
  try { b.sock.destroy(); } catch { }
  await sleep(400);
}

console.log('');
if (bad === 0) console.log(`🎉 ${rounds} 轮全部通过：新接入的客户端首帧都是整帧（不会黑屏）`);
else console.log(`❌ ${bad}/${rounds} 轮的首帧不是整帧 —— 新客户端会没有基准画面`);
process.exit(bad ? 4 : 0);
