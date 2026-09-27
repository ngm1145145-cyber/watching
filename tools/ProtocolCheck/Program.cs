// 复用安卓 App 的同一份网络源码（WsSession.cs / Protocol.cs）在 Windows 上做协议验证：
// 握手 → hello → 收二进制帧 → 解析元数据 → 解码 JPEG 并校验尺寸。
// 用法: dotnet run --project tools/ProtocolCheck -- <host> <port> [frames] [pwd]
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Watching.Mobile;

string host = args.Length > 0 ? args[0] : "127.0.0.1";
int port = args.Length > 1 ? int.Parse(args[1]) : 8899;
int want = args.Length > 2 ? int.Parse(args[2]) : 5;
string pwd = args.Length > 3 ? args[3] : null;

Console.WriteLine($"== 用安卓 App 的网络层代码连接 ws://{host}:{port}/ws ==");

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var auth = string.IsNullOrEmpty(pwd)
    ? null
    : "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("watching:" + pwd));

WsSession session;
try
{
    session = await WsSession.ConnectAsync(host, port, "/ws", auth, cts.Token);
}
catch (WsHandshakeException hex)
{
    Console.WriteLine($"!! 握手被拒绝: HTTP {hex.StatusCode} ({hex.StatusLine})");
    return 2;
}
catch (Exception ex)
{
    Console.WriteLine($"!! 连接失败: {ex.GetType().Name} {ex.Message}");
    return 1;
}

Console.WriteLine($"握手成功: {session.HttpStatusLine}");

await session.SendTextAsync(new ClientMessage
{
    Type = "hello", Kind = "mobile", Name = "协议验证", Version = "1.0.0"
}.ToJson(), cts.Token);

await session.SendTextAsync(new ClientMessage
{
    Type = "quality", Quality = 65, Fps = 15, MaxWidth = 1280
}.ToJson(), cts.Token);

int got = 0;
bool aborted = false;
var sw = Stopwatch.StartNew();
long totalBytes = 0;
long firstSeq = -1, lastSeq = -1;

while (got < want && !cts.IsCancellationRequested && !aborted)
{
    var (op, payload) = await session.ReadFrameAsync(cts.Token);

    switch (op)
    {
        case WsSession.Op.Ping:
            await session.SendPongAsync(payload, cts.Token);
            continue;
        case WsSession.Op.Pong:
            continue;
        case WsSession.Op.Close:
            Console.WriteLine("!! 服务端关闭了连接");
            aborted = true;
            continue;
        case WsSession.Op.Text:
            var msg = ServerMessage.Parse(System.Text.Encoding.UTF8.GetString(payload));
            if (msg != null) Console.WriteLine($"  [文本] t={msg.Type} 服务端={msg.MachineName} 屏幕={msg.ScreenWidth}x{msg.ScreenHeight} 远程控制={msg.RemoteControl}");
            continue;
        case WsSession.Op.Binary:
            break;
        default:
            continue;
    }

    if (!FramePacket.TryParse(payload, out long seq, out var meta, out int jpegOffset))
    {
        Console.WriteLine("!! 帧解析失败");
        continue;
    }

    int jpegLen = payload.Length - jpegOffset;
    var jpeg = new byte[jpegLen];
    Buffer.BlockCopy(payload, jpegOffset, jpeg, 0, jpegLen);

    bool jpegValid = jpeg.Length > 4 && jpeg[0] == 0xFF && jpeg[1] == 0xD8 &&
                     jpeg[^2] == 0xFF && jpeg[^1] == 0xD9;

    using var ms = new MemoryStream(jpeg);
    using var bmp = new Bitmap(ms);

    got++;
    if (firstSeq < 0) firstSeq = seq;
    lastSeq = seq;
    totalBytes += payload.Length;

    Console.WriteLine($"  [第{got}帧] seq={seq} 元数据={meta?.w}x{meta?.h} JPEG={jpegLen}B " +
                      $"头尾正确={jpegValid} 解码后={bmp.Width}x{bmp.Height} 均值像素={AvgPixel(bmp)}");
}

sw.Stop();
Console.WriteLine($"== 收到 {got} 帧，共 {totalBytes / 1024.0:F0} KB，" +
                  $"序号 {firstSeq}→{lastSeq}，用时 {sw.Elapsed.TotalSeconds:F2}s ==");
session.Dispose();
return got > 0 ? 0 : 3;

static string AvgPixel(Bitmap bmp)
{
    long r = 0, g = 0, b = 0;
    int n = 0;
    for (int y = 0; y < bmp.Height; y += Math.Max(1, bmp.Height / 20))
    {
        for (int x = 0; x < bmp.Width; x += Math.Max(1, bmp.Width / 20))
        {
            var c = bmp.GetPixel(x, y);
            r += c.R; g += c.G; b += c.B; n++;
        }
    }
    return n == 0 ? "-" : $"({r / n},{g / n},{b / n})";
}
