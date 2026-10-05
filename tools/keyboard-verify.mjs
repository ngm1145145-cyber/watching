// 验证键盘注入（重点：空格 / 退格 / 回车，这三个以前是坏的）。
//
// ⚠️⚠️ 会真的操作本机键盘：脚本会打开一个记事本、在里面打字，最后 Ctrl+A/Ctrl+C
//      用剪贴板读回来核对。为了不碰你自己的文档，它有两道硬性保护：
//        1) 如果本来就有记事本进程在跑，直接拒绝运行（绝不往你打开着的窗口里打字）；
//        2) 只关掉它自己启动的那个记事本进程，不碰别的记事本。
//      仍然请在没有别人用电脑的时候跑。
//
// 用法: $env:WATCHING_ALLOW_INPUT_TESTS = "1"; node keyboard-verify.mjs [port]
import net from 'node:net';
import { spawn, execFileSync } from 'node:child_process';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

if (process.env.WATCHING_ALLOW_INPUT_TESTS !== '1') {
  console.log('已阻止运行：本脚本会真的操作本机键盘（打开记事本并在里面打字）。');
  console.log('确认现在没人在用这台电脑后，设置 WATCHING_ALLOW_INPUT_TESTS=1 再跑。');
  process.exit(9);
}

const port = Number(process.argv[2] || 8899);
const sleep = (ms) => new Promise(r => setTimeout(r, ms));

function ps(command) {
  return execFileSync('powershell', ['-NoProfile', '-Command', command], { encoding: 'utf8' }).trim();
}

// 已经有记事本在跑就拒绝：绝不能在用户自己的文档里打字
if (ps('(Get-Process notepad -ErrorAction SilentlyContinue | Measure-Object).Count') !== '0') {
  console.log('❌ 检测到已有记事本在运行（可能是你自己的文档），为避免误伤，本脚本拒绝运行。');
  console.log('   请先关闭记事本再试。');
  process.exit(5);
}

// 前台窗口查询需要 P/Invoke：C# 里全是单引号，直接塞进 -Command 会被 PowerShell 引号规则吃掉，
// 所以先编译成临时 dll，后面每次调用只加载一下（快得多）
const helperDir = mkdtempSync(join(tmpdir(), 'watching-kbd-'));
const csPath = join(helperDir, 'Win.cs');
const dllPath = join(helperDir, 'Win.dll');
writeFileSync(csPath, `using System;
using System.Runtime.InteropServices;
using System.Text;
namespace KbdProbe {
  public static class Win {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    public static string Title() {
      var sb = new StringBuilder(512);
      GetWindowText(GetForegroundWindow(), sb, 512);
      return sb.ToString();
    }
  }
}`, 'utf8');
ps(`Add-Type -TypeDefinition (Get-Content -Raw '${csPath}') -OutputAssembly '${dllPath}'`);
const foregroundTitle = () => ps(`Add-Type -Path '${dllPath}'; [KbdProbe.Win]::Title()`);

// ---------- 1. 打开记事本并确认它是前台窗口 ----------
const np = spawn('C:\\WINDOWS\\system32\\notepad.exe', [], { stdio: 'ignore', windowsHide: false });
await sleep(2500);

const fgTitle = foregroundTitle();
console.log(`前台窗口：${fgTitle}`);
if (!/记事本|Notepad/i.test(fgTitle)) {
  console.log('❌ 记事本不是前台窗口，为避免把字打到别的地方，已中止');
  try { np.kill(); } catch { }
  process.exit(3);
}

// ---------- 2. 连上服务端 ----------
const key = Buffer.from('dGhlIHNhbXBsZSBub25jZQ==').toString('base64');
const sock = net.connect(port, '127.0.0.1');
let buf = Buffer.alloc(0), handshake = false;

const maskFrame = (opcode, payload) => {
  const mask = Buffer.from([2, 4, 6, 8]); const len = payload.length;
  let header;
  if (len < 126) header = Buffer.from([0x80 | opcode, 0x80 | len]);
  else { header = Buffer.alloc(4); header[0] = 0x80 | opcode; header[1] = 0x80 | 126; header.writeUInt16BE(len, 2); }
  const m = Buffer.alloc(len); for (let i = 0; i < len; i++) m[i] = payload[i] ^ mask[i % 4];
  return Buffer.concat([header, mask, m]);
};
const send = (o) => sock.write(maskFrame(0x1, Buffer.from(JSON.stringify(o))));
const key_ = (k, mods) => send({ t: 'input', kind: 'key', key: k, ...(mods || {}) });

sock.on('connect', () => sock.write(`GET /ws HTTP/1.1\r\nHost: 127.0.0.1\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: ${key}\r\nSec-WebSocket-Version: 13\r\n\r\n`));
sock.on('data', (d) => {
  buf = Buffer.concat([buf, d]);
  if (!handshake) {
    const i = buf.indexOf('\r\n\r\n'); if (i < 0) return;
    handshake = true; buf = buf.subarray(i + 4);
    send({ t: 'hello', kind: 'pc', name: 'keyboard-verify', version: 'probe' });
  }
});
await sleep(1200);
if (!handshake) { console.log('❌ 连不上服务端'); cleanup(2); }

async function typeText(text) {
  for (const ch of text) {
    if (ch === ' ') key_('space');
    else if (ch === '\n') key_('enter');
    else key_(ch);
    await sleep(60);
  }
}

async function readAll() {
  key_('a', { ctrl: true });
  await sleep(250);
  key_('c', { ctrl: true });
  await sleep(400);
  return ps('Get-Clipboard -Raw');
}

// ---------- 3. 清空文档 → 逐项验证 ----------
key_('a', { ctrl: true });
await sleep(200);
key_('delete');
await sleep(400);

const results = [];
const check = (name, ok, got) => { results.push(ok); console.log(`${ok ? '✅' : '❌'} ${name}${got !== undefined ? ' — 实际=' + JSON.stringify(got) : ''}`); };

// 3.1 空格（这次修的核心）
await typeText('a b');
let text = await readAll();
check('字母 + 空格', text === 'a b', text);

// 3.2 退格
key_('backspace');
await sleep(400);
text = await readAll();
check('退格', text === 'a ', text);

// 3.3 回车 + 继续输入
await typeText('\nx');
await sleep(150);
text = await readAll();
check('回车换行', text === 'a \r\nx', text);

// 3.4 方向键（带扩展位；不带的话 NumLock 打开时会变成小键盘 4/8/6/2）
key_('up');
await sleep(200);
await typeText('Z');
await sleep(150);
text = await readAll();
check('方向键上（扩展键）', text === 'a Z\r\nx', text);

console.log('');
const failed = results.filter(r => !r).length;
console.log(failed ? `❌ ${failed}/${results.length} 项失败` : `🎉 全部 ${results.length} 项键盘验证通过`);
cleanup(failed ? 4 : 0);

function cleanup(code) {
  try { sock.destroy(); } catch { }
  // 只关自己启动的那个记事本进程；绝不用 Get-Process notepad | Stop-Process（那会连用户的窗口一起杀掉）
  try { if (np && np.pid) ps(`Stop-Process -Id ${np.pid} -Force -ErrorAction SilentlyContinue`); } catch { }
  try { np.kill(); } catch { }
  try { rmSync(helperDir, { recursive: true, force: true }); } catch { }
  setTimeout(() => process.exit(code), 300);
}
