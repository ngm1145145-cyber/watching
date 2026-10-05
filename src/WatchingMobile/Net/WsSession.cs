using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Watching.Mobile;

/// <summary>WebSocket 握手被拒绝（携带 HTTP 状态码，便于区分密码错误与网络问题）。</summary>
public sealed class WsHandshakeException : Exception
{
    public int StatusCode { get; }
    public string StatusLine { get; }

    public WsHandshakeException(int statusCode, string statusLine)
        : base($"WebSocket 握手失败：{statusLine}")
    {
        StatusCode = statusCode;
        StatusLine = statusLine;
    }
}

/// <summary>
/// 极简 WebSocket 客户端（RFC6455）。
/// 自己实现的原因：不依赖任何第三方库，也不受不同 .NET 版本 ClientWebSocket 行为差异影响。
/// </summary>
public sealed class WsSession : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;

    public string HttpStatusLine { get; private set; }
    public int HttpStatusCode { get; private set; }

    private WsSession(TcpClient tcp, NetworkStream stream)
    {
        _tcp = tcp;
        _stream = stream;
    }

    public static async Task<WsSession> ConnectAsync(string host, int port, string pathAndQuery,
        string authHeader, CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            // 必须把 ct 传进来：不传的话，对方 IP 不可达（SYN 被丢弃、不回 RST）时
            // 内核会自己重试两分钟，8 秒超时和「断开」都拦不住，界面一直卡在「连接中…」
            await tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }

        var stream = tcp.GetStream();
        var key = Convert.ToBase64String(Guid.NewGuid().ToByteArray());

        var sb = new StringBuilder();
        sb.Append("GET ").Append(pathAndQuery).Append(" HTTP/1.1\r\n");
        sb.Append("Host: ").Append(host).Append(':').Append(port).Append("\r\n");
        sb.Append("Upgrade: websocket\r\n");
        sb.Append("Connection: Upgrade\r\n");
        sb.Append("Sec-WebSocket-Key: ").Append(key).Append("\r\n");
        sb.Append("Sec-WebSocket-Version: 13\r\n");
        sb.Append("User-Agent: WatchingMobile/1.0\r\n");
        if (!string.IsNullOrEmpty(authHeader))
            sb.Append("Authorization: ").Append(authHeader).Append("\r\n");
        sb.Append("\r\n");

        var req = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(req, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        var session = new WsSession(tcp, stream);
        if (!await session.ReadHandshakeAsync(ct).ConfigureAwait(false))
        {
            int code = session.HttpStatusCode;
            string line = session.HttpStatusLine;
            session.Dispose();
            throw new WsHandshakeException(code, line ?? "无响应");
        }
        return session;
    }

    private async Task<bool> ReadHandshakeAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];
        int total = 0;
        int end = -1;

        while (total < buffer.Length)
        {
            int n = await _stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct).ConfigureAwait(false);
            if (n <= 0) return false;
            total += n;

            for (int i = 0; i + 3 < total; i++)
            {
                if (buffer[i] == 13 && buffer[i + 1] == 10 && buffer[i + 2] == 13 && buffer[i + 3] == 10)
                {
                    end = i;
                    break;
                }
            }
            if (end >= 0) break;
        }

        if (end < 0) return false;

        // 101 之后服务端可能紧接着就把第一帧（welcome / state）发过来了，
        // 同一次 read 里多出来的这些字节必须留着给 ReadFrameAsync，
        // 以前的实现直接把它们丢掉，帧流会错位（表现为「已连接但没有画面」）
        int surplus = total - (end + 4);
        if (surplus > 0)
        {
            _pending = new byte[surplus];
            Buffer.BlockCopy(buffer, end + 4, _pending, 0, surplus);
            _pendingOffset = 0;
        }

        var text = Encoding.UTF8.GetString(buffer, 0, end);
        var lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);
        HttpStatusLine = lines.Length > 0 ? lines[0] : null;

        var parts = (HttpStatusLine ?? "").Split(' ');
        if (parts.Length >= 2 && int.TryParse(parts[1], out int code)) HttpStatusCode = code;

        return HttpStatusCode == 101;
    }

    public enum Op : int { Continuation = 0, Text = 1, Binary = 2, Close = 8, Ping = 9, Pong = 10 }

    /// <summary>读一帧。返回 false 表示连接已结束。</summary>
    public async Task<(Op op, byte[] payload)> ReadFrameAsync(CancellationToken ct)
    {
        var head = await ReadExactAsync(2, ct).ConfigureAwait(false);
        int op = head[0] & 0x0F;
        bool masked = (head[1] & 0x80) != 0;
        long len = head[1] & 0x7F;

        if (len == 126)
        {
            var ext = await ReadExactAsync(2, ct).ConfigureAwait(false);
            len = BinaryPrimitives.ReadUInt16BigEndian(ext);
        }
        else if (len == 127)
        {
            var ext = await ReadExactAsync(8, ct).ConfigureAwait(false);
            len = (long)BinaryPrimitives.ReadUInt64BigEndian(ext);
        }

        if (len < 0 || len > 64L * 1024 * 1024) throw new IOException("帧过大：" + len);

        byte[] mask = null;
        if (masked) mask = await ReadExactAsync(4, ct).ConfigureAwait(false);

        var payload = len == 0 ? Array.Empty<byte>() : await ReadExactAsync((int)len, ct).ConfigureAwait(false);
        if (masked)
        {
            for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i % 4];
        }

        return ((Op)op, payload);
    }

    private async Task<byte[]> ReadExactAsync(int count, CancellationToken ct)
    {
        var buf = new byte[count];
        int off = 0;

        // 先吃握手时多读出来的字节
        if (_pendingOffset < _pending.Length)
        {
            int take = Math.Min(count, _pending.Length - _pendingOffset);
            Buffer.BlockCopy(_pending, _pendingOffset, buf, 0, take);
            _pendingOffset += take;
            off = take;
            if (_pendingOffset >= _pending.Length)
            {
                _pending = Array.Empty<byte>();
                _pendingOffset = 0;
            }
        }

        while (off < count)
        {
            int n = await _stream.ReadAsync(buf.AsMemory(off, count - off), ct).ConfigureAwait(false);
            if (n <= 0) throw new IOException("连接已关闭");
            off += n;
        }
        return buf;
    }

    private byte[] _pending = Array.Empty<byte>();
    private int _pendingOffset;

    public Task SendTextAsync(string text, CancellationToken ct)
        => SendAsync(Op.Text, Encoding.UTF8.GetBytes(text ?? ""), ct);

    public Task SendPongAsync(byte[] payload, CancellationToken ct)
        => SendAsync(Op.Pong, payload ?? Array.Empty<byte>(), ct);

    public async Task SendAsync(Op op, byte[] payload, CancellationToken ct)
    {
        int len = payload.Length;
        int headerLen = len < 126 ? 2 : (len <= ushort.MaxValue ? 4 : 10);
        var frame = new byte[headerLen + 4 + len];

        frame[0] = (byte)(0x80 | ((int)op & 0x0F));
        if (len < 126)
        {
            frame[1] = (byte)(0x80 | len);
        }
        else if (len <= ushort.MaxValue)
        {
            frame[1] = 0x80 | 126;
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2, 2), (ushort)len);
        }
        else
        {
            frame[1] = 0x80 | 127;
            BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(2, 8), (ulong)len);
        }

        var mask = new byte[4];
        Random.Shared.NextBytes(mask);
        int p = headerLen;
        frame[p] = mask[0]; frame[p + 1] = mask[1]; frame[p + 2] = mask[2]; frame[p + 3] = mask[3];
        p += 4;

        for (int i = 0; i < len; i++) frame[p + i] = (byte)(payload[i] ^ mask[i % 4]);

        await _stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
    }
}
