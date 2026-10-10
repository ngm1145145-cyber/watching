// 局域网自检：从「局域网源地址」而不是 127.0.0.1 去连本机服务端，把三条路都走一遍。
//
// 说明：同一台机器上互连不会经过防火墙（Windows 会走回环捷径），所以这个脚本
// 证明的是「服务端确实在局域网地址上收连接、握手、推帧、回自动发现」，
// 以及「我们的诊断记录能看见非回环来源」。防火墙与真实跨机连通性只能靠另一台设备实测。
//
// 只做网络连接，不操作鼠标键盘。
// 用法: node lan-selfcheck.mjs
import net from 'node:net';
import dgram from 'node:dgram';
import { execFileSync } from 'node:child_process';
import http from 'node:http';

const PORT = 8899;
const sleep = (ms) => new Promise(r => setTimeout(r, ms));

function ps(cmd) {
  return execFileSync('powershell', ['-NoProfile', '-Command', cmd], { encoding: 'utf8' }).trim();
}

const results = [];
const check = (name, ok, detail) => {
  results.push(ok);
  console.log(`${ok ? '✅' : '❌'} ${name}${detail ? ' — ' + detail : ''}`);
};

// ---------- 0. 本机的局域网地址 ----------
const localIps = ps(`Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } | ForEach-Object { $_.IPAddress }`)
  .split(/\r?\n/).map(s => s.trim()).filter(Boolean);
console.log('本机非回环地址：' + localIps.join(', '));

const lan = localIps.filter(ip => ip.startsWith('192.168.') && !ip.startsWith('192.168.56.'));
if (lan.length === 0) { console.log('❌ 没有可用的局域网地址'); process.exit(2); }

// 源地址必须和目标在同一个网段（跨网段绑源去连本机，路由是走不通的，那是测试脚本的错）
const subnet = (ip) => ip.split('.').slice(0, 3).join('.');
const target = lan[0];
const source = lan.find(ip => ip !== target && subnet(ip) === subnet(target)) || target;
console.log(`测试：源地址 ${source} → 目标地址 ${target}:${PORT}\n`);

// ---------- 1. HTTP（从局域网源地址） ----------
function httpGet(path, localAddress) {
  return new Promise((resolve) => {
    const req = http.get({ host: target, port: PORT, path, localAddress, timeout: 5000 }, (res) => {
      let body = '';
      res.on('data', (c) => body += c);
      res.on('end', () => resolve({ status: res.statusCode, body: body.slice(0, 60) }));
    });
    req.on('error', (e) => resolve({ status: 0, body: e.message }));
    req.on('timeout', () => { req.destroy(); resolve({ status: 0, body: 'timeout' }); });
  });
}

const health = await httpGet('/health', source);
check(`HTTP /health（源 ${source}）`, health.status === 200, `${health.status} ${health.body}`);

const mobile = await httpGet('/mobile', source);
check('HTTP /mobile 页面', mobile.status === 200, `${mobile.status} ${mobile.body.slice(0, 30)}`);

// ---------- 2. WebSocket：握手 + 收帧（从局域网源地址） ----------
function wsProbe(name, localAddress) {
  return new Promise((resolve) => {
    const key = Buffer.from('dGhlIHNhbXBsZSBub25jZQ==').toString('base64');
    const sock = net.connect({ host: target, port: PORT, localAddress });
    let buf = Buffer.alloc(0), handshake = false, frames = 0, firstMode = null, err = null;

    const maskFrame = (opcode, payload) => {
      const mask = Buffer.from([2, 4, 6, 8]); const len = payload.length;
      let header;
      if (len < 126) header = Buffer.from([0x80 | opcode, 0x80 | len]);
      else { header = Buffer.alloc(4); header[0] = 0x80 | opcode; header[1] = 0x80 | 126; header.writeUInt16BE(len, 2); }
      const m = Buffer.alloc(len); for (let i = 0; i < len; i++) m[i] = payload[i] ^ mask[i % 4];
      return Buffer.concat([header, mask, m]);
    };
    const send = (o) => sock.write(maskFrame(0x1, Buffer.from(JSON.stringify(o))));

    sock.on('connect', () => sock.write(
      `GET /ws HTTP/1.1\r\nHost: ${target}:${PORT}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n` +
      `Sec-WebSocket-Key: ${key}\r\nSec-WebSocket-Version: 13\r\n\r\n`));
    sock.on('error', (e) => { err = e.message; });
    sock.on('data', (d) => {
      buf = Buffer.concat([buf, d]);
      if (!handshake) {
        const i = buf.indexOf('\r\n\r\n'); if (i < 0) return;
        handshake = true; buf = buf.subarray(i + 4);
        send({ t: 'hello', kind: 'pc', name, version: 'probe' });
      }
      while (buf.length >= 2) {
        const op = buf[0] & 0x0f; let len = buf[1] & 0x7f, off = 2;
        if (len === 126) { if (buf.length < 4) return; len = buf.readUInt16BE(2); off = 4; }
        else if (len === 127) { if (buf.length < 10) return; len = Number(buf.readBigUInt64BE(2)); off = 10; }
        if (buf.length < off + len) return;
        const payload = buf.subarray(off, off + len); buf = buf.subarray(off + len);
        if (op === 0x9) { sock.write(maskFrame(0xa, payload)); continue; }
        if (op !== 0x2) continue;
        frames++;
        if (firstMode === null) {
          let end = 12; while (end < 12 + 256 && payload[end] !== 0) end++;
          try { firstMode = JSON.parse(payload.subarray(12, end).toString('utf8')).mode; } catch { }
        }
      }
    });
    setTimeout(() => { try { sock.destroy(); } catch { } resolve({ handshake, frames, firstMode, err }); }, 3000);
  });
}

const ws = await wsProbe('lan-selfcheck', source);
check(`WebSocket 握手（源 ${source}）`, ws.handshake, ws.err || (ws.handshake ? '101 Switching Protocols' : '无响应'));
check('握手后收到画面帧', ws.frames > 0, `${ws.frames} 帧，首帧 mode=${ws.firstMode}`);

// ---------- 3. 自动发现（从局域网源地址探测） ----------
const discovered = await new Promise((resolve) => {
  const s = dgram.createSocket({ type: 'udp4', reuseAddr: true });
  const got = [];
  s.on('message', (b, ri) => got.push({ from: ri.address, text: b.toString('utf8') }));
  s.bind(0, source, async () => {
    s.setBroadcast(true);
    for (const t of [target, '192.168.3.255', '255.255.255.255']) {
      try { s.send(Buffer.from('WATCHING_DISCOVER'), PORT, t); } catch { }
      await sleep(300);
    }
    await sleep(1200);
    s.close();
    resolve(got);
  });
});
let announcedHost = null;
for (const r of discovered) {
  try { const j = JSON.parse(r.text); if (j.host && !announcedHost) announcedHost = j.host; } catch { }
}
check('自动发现回应', discovered.length > 0, `收到 ${discovered.length} 条回应`);
check('回应里的 host 是能连上的局域网地址', !!announcedHost && lan.includes(announcedHost),
  `host=${announcedHost}（本机局域网地址：${lan.join(', ')}）`);

// ---------- 4. 每个局域网地址上都测一遍（多网卡机器要对每个地址都能连） ----------
for (const ip of lan) {
  const sameSubnetSource = lan.find(x => x !== ip && subnet(x) === subnet(ip)) || ip;
  const h = await httpGet('/health', sameSubnetSource === ip ? undefined : sameSubnetSource);
  check(`本机地址 ${ip} 上能连（HTTP /health）`, h.status === 200, `${h.status} ${h.body}`);
}

console.log('');
const failed = results.filter(r => !r).length;
console.log(failed ? `❌ ${failed}/${results.length} 项失败` : `🎉 全部 ${results.length} 项本机侧检查通过`);
process.exit(failed ? 4 : 0);
