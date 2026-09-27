// 访问密码 + 远程控制开关的验证脚本
// 用法: node test-auth.mjs <port> <pwd>
import net from 'node:net';

const port = Number(process.argv[2] || 8899);
const pwd = process.argv[3] || '';

const b64 = (s) => Buffer.from(s, 'utf8').toString('base64');

// 1) HTTP 未带密码
const httpStatus = (headers = {}) => new Promise((resolve, reject) => {
  const s = net.connect(port, '127.0.0.1');
  let data = '';
  s.on('connect', () => {
    s.write(`GET /api/info HTTP/1.1\r\nHost: 127.0.0.1\r\n` +
      Object.entries(headers).map(([k, v]) => `${k}: ${v}\r\n`).join('') + `\r\n`);
  });
  s.on('data', (d) => { data += d.toString('latin1'); });
  s.on('end', () => resolve(data.split('\r\n')[0]));
  s.on('error', reject);
  setTimeout(() => { s.destroy(); resolve(data.split('\r\n')[0] || 'timeout'); }, 4000);
});

// 2) WebSocket 带 query 密码 / 不带
const wsTest = () => new Promise((resolve) => {
  const key = Buffer.from('dGhlIHNhbXBsZSBub25jZQ==').toString('base64');
  const s = net.connect(port, '127.0.0.1');
  let buf = '';
  let done = false;
  const finish = (r) => { if (!done) { done = true; resolve(r); try { s.destroy(); } catch { } } };
  s.on('connect', () => {
    const url = pwd ? `/ws?pwd=${encodeURIComponent(pwd)}` : '/ws';
    s.write(`GET ${url} HTTP/1.1\r\nHost: 127.0.0.1\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n` +
      `Sec-WebSocket-Key: ${key}\r\nSec-WebSocket-Version: 13\r\n\r\n`);
  });
  s.on('data', (d) => {
    buf += d.toString('latin1');
    if (buf.includes('\r\n\r\n')) {
      const line = buf.split('\r\n')[0];
      if (/101/.test(line)) finish('WS 101 允许');
      else {
        // 带 query 密码失败时，再试 Authorization 头
        if (buf.includes('401')) finish('WS 401 拒绝');
      }
    }
  });
  s.on('error', () => finish('连接错误'));
  setTimeout(() => finish('超时'), 4000);
});

const noAuth = await httpStatus();
const withAuth = await httpStatus({ Authorization: 'Basic ' + b64('watching:' + pwd) });
const withBadAuth = await httpStatus({ Authorization: 'Basic ' + b64('watching:wrong') });
const wsOk = await wsTest();

console.log(`HTTP 无密码       : ${noAuth}`);
console.log(`HTTP 正确密码     : ${withAuth}`);
console.log(`HTTP 错误密码     : ${withBadAuth}`);
console.log(`WebSocket 连接    : ${wsOk}`);
