using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Watching.Common;

namespace Watching.Server;

/// <summary>一个已连接的观看端（电脑客户端 / 手机客户端 / 网页客户端）。</summary>
public sealed class ClientConnection : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly CaptureHub _hub;
    private readonly AppConfig _config;
    private readonly ServerHost _host;

    private Task _sendTask;
    private Task _recvTask;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<byte[]> _textQueue = new();

    private int _quality;
    private int _fps;
    private int _maxWidth;
    private Rectangle _crop = Rectangle.Empty;
    private CaptureStream _streamRef;
    private bool _closed;
    private bool _remoteSent;

    private int _bytesInWindow;
    private long _statTick;
    private double _kbps;
    private long _framesSent;
    private long _framesDropped;
    private long _lastSentSeq = -1;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public string Id { get; } = Guid.NewGuid().ToString("N").Substring(0, 8);
    public string RemoteAddress { get; }
    public string Kind { get; private set; } = "unknown";
    public string ClientName { get; private set; } = "";
    public DateTime ConnectedAt { get; } = DateTime.Now;
    public double Kbps => _kbps;
    public int Quality => _quality;
    public int TargetFps => _fps;
    public Rectangle Crop => _crop;

    public ClientConnection(TcpClient tcp, NetworkStream stream, CaptureHub hub, AppConfig config, ServerHost host)
    {
        _tcp = tcp;
        _stream = stream;
        _hub = hub;
        _config = config;
        _host = host;
        _quality = config.Quality;
        _fps = config.Fps;
        _maxWidth = config.MaxWidth;
        RemoteAddress = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
    }

    public void Start()
    {
        AttachStream();
        _sendTask = Task.Run(SendLoopAsync);
        _recvTask = Task.Run(ReceiveLoopAsync);
    }

    // ---------------- 抓屏流绑定 ----------------

    private void AttachStream()
    {
        var stream = _hub.Acquire(_quality, _fps, _maxWidth, _crop);
        var old = Interlocked.Exchange(ref _streamRef, stream);
        if (old != null)
        {
            _hub.Release(old);
            _lastSentSeq = -1;
        }
    }

    private void DetachStream()
    {
        var old = Interlocked.Exchange(ref _streamRef, null);
        if (old != null) _hub.Release(old);
    }

    // ---------------- 发送 ----------------

    /// <summary>把一条文本消息排进发送队列（线程安全，可从任意线程调用）。</summary>
    public void QueueText(ServerMessage msg)
    {
        try
        {
            var json = JsonSerializer.Serialize(msg, JsonOpts);
            _textQueue.Enqueue(Encoding.UTF8.GetBytes(json));
        }
        catch (Exception ex)
        {
            Log.Error("序列化服务端消息失败", ex);
        }
    }

    /// <summary>
    /// 发送循环。做了几处传输层优化：
    ///   1. 复用同一个 268 字节头缓冲 + 直接把 JPEG 写给 socket，不再每帧拼接一个大数组
    ///   2. 完全没变化的画面跳过不发（静止桌面几乎不占带宽）
    ///   3. 统计单帧发送耗时，超过预算就自适应降画质/降帧，避免越堆越卡
    /// </summary>
    private async Task SendLoopAsync()
    {
        var pingClock = Stopwatch.StartNew();
        var header = new byte[FramePacket.HeaderSize];
        bool adaptive = _config.AdaptiveQuality;
        int effectiveQuality = _quality;
        int effectiveFps = _fps;

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                // 1) 先把控制消息发完
                while (_textQueue.TryDequeue(out var text))
                    await WebSocketProtocol.WriteFrameAsync(_stream, text, WebSocketProtocol.OpText, _cts.Token)
                        .ConfigureAwait(false);

                // 2) 心跳，顺带探测死连接
                if (pingClock.ElapsedMilliseconds >= 20000)
                {
                    pingClock.Restart();
                    await WebSocketProtocol.WriteFrameAsync(_stream, Array.Empty<byte>(),
                        WebSocketProtocol.OpPing, _cts.Token).ConfigureAwait(false);
                }

                // 3) 服务端远程控制状态变化时通知客户端
                bool remote = _host.RemoteControlEnabled;
                if (remote != _remoteSent)
                {
                    _remoteSent = remote;
                    QueueText(new ServerMessage { Type = "state", RemoteControl = remote });
                    continue;
                }

                var stream = Volatile.Read(ref _streamRef);
                if (stream == null)
                {
                    await Task.Delay(50, _cts.Token).ConfigureAwait(false);
                    continue;
                }

                // 4) 取最新一帧（客户端跟不上就自动丢帧）
                long seq = stream.Engine.Sequence;
                if (seq == _lastSentSeq || !stream.Engine.Active)
                {
                    stream.Engine.WaitForFrame(seq, 400);
                    continue;
                }

                if (!stream.Engine.TryGetLatest(out var jpeg, out seq, out int w, out int h, out bool cropped)
                    || jpeg == null)
                {
                    await Task.Delay(20, _cts.Token).ConfigureAwait(false);
                    continue;
                }

                if (seq == _lastSentSeq) continue;
                if (_lastSentSeq >= 0 && seq - _lastSentSeq > Math.Max(3, _fps)) _framesDropped++;

                // 5) 静止画面跳过：和上一帧字节完全一致就不发（省流量）
                if (_config.SkipUnchangedFrames && jpeg.Length == _lastFrameLength && jpeg.Length > 0)
                {
                    ulong hash = Hash64(jpeg);
                    if (hash == _lastFrameHash)
                    {
                        _framesSkipped++;
                        _lastSentSeq = seq;
                        continue;
                    }
                    _lastFrameHash = hash;
                }
                else
                {
                    _lastFrameLength = jpeg.Length;
                    if (_config.SkipUnchangedFrames) _lastFrameHash = Hash64(jpeg);
                }

                _lastSentSeq = seq;

                var meta = new FramePacket.Meta
                {
                    w = w,
                    h = h,
                    sw = stream.Engine.ScreenWidth,
                    sh = stream.Engine.ScreenHeight,
                    q = effectiveQuality,
                    crop = cropped,
                    ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    mode = "gdi",
                    capMs = Math.Round(stream.Engine.LastCaptureMs, 1)
                };

                if (!FramePacket.WriteMetaHeader(header, seq, meta))
                {
                    await Task.Delay(20, _cts.Token).ConfigureAwait(false);
                    continue;
                }

                var sendWatch = Stopwatch.StartNew();
                await WebSocketProtocol.WritePacketAsync(_stream, jpeg, header, _cts.Token).ConfigureAwait(false);
                sendWatch.Stop();

                _framesSent++;
                _bytesInWindow += jpeg.Length + header.Length;

                long now = Environment.TickCount64;
                if (_statTick == 0) _statTick = now;
                long window = now - _statTick;
                if (window >= 1000)
                {
                    _kbps = _bytesInWindow / 1024.0 * 1000.0 / Math.Max(1, window);
                    _bytesInWindow = 0;
                    _statTick = now;

                    // 每 30 秒记一条传输统计，方便确认优化是否生效
                    long watch = now - _lastLogTick;
                    if (watch >= 30000)
                    {
                        _lastLogTick = now;
                        Log.Write($"[传输] {Id} 已发 {_framesSent} 帧 / 跳过 {_framesSkipped} 帧" +
                                  $"（静止画面省流量）· 丢帧 {_framesDropped} · 当前 {_kbps:F0} KB/s" +
                                  $" · 单帧发送 {_sendMs:F0}ms · 画质 {effectiveQuality}");
                    }
                }

                // 6) 自适应：发送明显变慢说明链路拥塞，降低画质与帧率，慢慢再恢复
                _sendMs = _sendMs <= 0 ? sendWatch.Elapsed.TotalMilliseconds
                                       : _sendMs * 0.7 + sendWatch.Elapsed.TotalMilliseconds * 0.3;

                if (adaptive && stream.Engine is CaptureEngine engine && _framesSent % 8 == 0)
                {
                    int targetFrameMs = (int)(1000.0 / Math.Max(1, _fps));
                    if (_sendMs > targetFrameMs * 0.8 && effectiveQuality > 40)
                    {
                        effectiveQuality = Math.Max(40, effectiveQuality - 8);
                        engine.SetQuality(effectiveQuality);
                        Log.Write($"链路拥塞（发送 {_sendMs:F0}ms）→ 自动降画质到 {effectiveQuality}");
                    }
                    else if (_sendMs < targetFrameMs * 0.35 && effectiveQuality < _quality)
                    {
                        effectiveQuality = Math.Min(_quality, effectiveQuality + 4);
                        engine.SetQuality(effectiveQuality);
                    }
                }
            }
        }
        catch (Exception)
        {
            // 断开 / 取消
        }
        finally
        {
            Close();
        }
    }

    private long _lastFrameLength = -1;
    private ulong _lastFrameHash;
    private long _framesSkipped;
    private double _sendMs;
    private long _lastLogTick = Environment.TickCount64;

    /// <summary>FNV-1a：比 SHA256 快得多，用来判断两帧 JPEG 是否完全相同足够。</summary>
    private static ulong Hash64(byte[] data)
    {
        ulong h = 14695981039346656037UL;
        for (int i = 0; i < data.Length; i++)
        {
            h ^= data[i];
            h *= 1099511628211UL;
        }
        return h;
    }

    // ---------------- 接收 ----------------

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var (op, payload) = await WebSocketProtocol.ReadFrameAsync(_stream, _cts.Token).ConfigureAwait(false);

                switch (op)
                {
                    case WebSocketProtocol.OpClose:
                        return;
                    case WebSocketProtocol.OpPing:
                        await WebSocketProtocol.WriteFrameAsync(_stream, payload, WebSocketProtocol.OpPong, _cts.Token)
                            .ConfigureAwait(false);
                        break;
                    case WebSocketProtocol.OpPong:
                        break;
                    case WebSocketProtocol.OpText:
                        HandleText(Encoding.UTF8.GetString(payload));
                        break;
                }
            }
        }
        catch (Exception)
        {
            // 断开
        }
        finally
        {
            Close();
        }
    }

    private void HandleText(string text)
    {
        ClientMessage msg;
        try { msg = JsonSerializer.Deserialize<ClientMessage>(text, JsonOpts); }
        catch { return; }
        if (msg == null) return;

        switch ((msg.Type ?? "").ToLowerInvariant())
        {
            case "hello":
                Kind = (msg.Kind ?? "unknown").ToLowerInvariant();
                ClientName = msg.Name ?? "";
                if (!string.IsNullOrEmpty(ClientName) && ClientName.Length > 40)
                    ClientName = ClientName.Substring(0, 40);
                Log.Write($"客户端接入 [{Kind}] {RemoteAddress} {ClientName}");
                QueueText(new ServerMessage
                {
                    Type = "welcome",
                    ScreenWidth = User32.GetSystemMetrics(User32.SM_CXVIRTUALSCREEN),
                    ScreenHeight = User32.GetSystemMetrics(User32.SM_CYVIRTUALSCREEN),
                    Quality = _quality,
                    Fps = _fps,
                    MaxWidth = _maxWidth,
                    RemoteControl = _host.RemoteControlEnabled,
                    MachineName = Environment.MachineName,
                    Version = "1.0.0"
                });
                _host.OnClientSettingsChanged(this);
                break;

            case "quality":
                if (msg.Quality.HasValue)
                {
                    _quality = Math.Clamp(msg.Quality.Value, 20, 95);
                    _config.Quality = _quality;
                }
                if (msg.Fps.HasValue) _fps = Math.Clamp(msg.Fps.Value, 1, 60);
                if (msg.MaxWidth.HasValue) _maxWidth = Math.Clamp(msg.MaxWidth.Value, 200, 7680);
                AttachStream();
                _host.OnClientSettingsChanged(this);
                break;

            case "crop":
                _crop = (msg.X0 == null && msg.X1 == null) ? Rectangle.Empty : NormalizeCrop(msg);
                AttachStream();
                QueueText(new ServerMessage { Type = "crop", Cropped = !_crop.IsEmpty });
                _host.OnClientSettingsChanged(this);
                break;

            case "input":
                HandleInput(msg);
                break;

            case "ping":
                QueueText(new ServerMessage
                {
                    Type = "pong",
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });
                break;
        }
    }

    private static Rectangle NormalizeCrop(ClientMessage msg)
    {
        double x0 = msg.X0 ?? 0, y0 = msg.Y0 ?? 0, x1 = msg.X1 ?? 0, y1 = msg.Y1 ?? 0;
        if (x1 < x0) (x0, x1) = (x1, x0);
        if (y1 < y0) (y0, y1) = (y1, y0);

        var vs = ScreenEncoder.VirtualScreenBounds();
        int px = (int)Math.Round(vs.X + x0 * vs.Width);
        int py = (int)Math.Round(vs.Y + y0 * vs.Height);
        int pw = (int)Math.Round((x1 - x0) * vs.Width);
        int ph = (int)Math.Round((y1 - y0) * vs.Height);

        pw = Math.Clamp(pw, 32, vs.Width);
        ph = Math.Clamp(ph, 32, vs.Height);

        var r = new Rectangle(px, py, pw, ph);
        r.Intersect(vs);
        if (r.Width >= vs.Width - 4 && r.Height >= vs.Height - 4) return Rectangle.Empty;
        return r;
    }

    private void HandleInput(ClientMessage msg)
    {
        if (!_host.RemoteControlEnabled)
        {
            QueueText(new ServerMessage { Type = "error", Message = "服务端未开启远程控制" });
            return;
        }

        try
        {
            switch ((msg.Kind ?? "").ToLowerInvariant())
            {
                case "move":
                    if (msg.X.HasValue && msg.Y.HasValue)
                        InputInjector.MouseMove(ScreenX(msg.X.Value), ScreenY(msg.Y.Value));
                    break;
                case "down":
                    InputInjector.MouseButton(msg.Button, true);
                    break;
                case "up":
                    InputInjector.MouseButton(msg.Button, false);
                    break;
                case "click":
                    InputInjector.MouseMove(ScreenX(msg.X ?? 0), ScreenY(msg.Y ?? 0));
                    InputInjector.MouseButton(msg.Button, true);
                    InputInjector.MouseButton(msg.Button, false);
                    break;
                case "wheel":
                    InputInjector.MouseWheel(msg.Delta ?? 0);
                    break;
                case "key":
                    InputInjector.Key(msg.Key, msg.Ctrl == true, msg.Alt == true, msg.Shift == true, msg.Win == true);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("处理远程输入失败", ex);
        }
    }

    /// <summary>归一化坐标（0..1，相对当前裁剪画面）→ 屏幕绝对像素坐标。</summary>
    private double ScreenX(double nx)
    {
        var vs = ScreenEncoder.VirtualScreenBounds();
        if (!_crop.IsEmpty) return _crop.X + Math.Clamp(nx, 0, 1) * _crop.Width;
        return vs.X + Math.Clamp(nx, 0, 1) * vs.Width;
    }

    private double ScreenY(double ny)
    {
        var vs = ScreenEncoder.VirtualScreenBounds();
        if (!_crop.IsEmpty) return _crop.Y + Math.Clamp(ny, 0, 1) * _crop.Height;
        return vs.Y + Math.Clamp(ny, 0, 1) * vs.Height;
    }

    public string Describe()
    {
        string who = string.IsNullOrEmpty(ClientName) ? RemoteAddress : $"{ClientName} ({RemoteAddress})";
        string kind = Kind switch
        {
            "pc" => "电脑客户端",
            "mobile" => "手机客户端",
            "web" => "网页客户端",
            _ => "客户端"
        };
        return $"{kind} {who} {_kbps:F0}KB/s";
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        try { _cts.Cancel(); } catch { }
        try { _stream?.Close(); } catch { }
        try { _tcp?.Close(); } catch { }
        DetachStream();
        _host.OnClientClosed(this);
    }

    public void Dispose()
    {
        Close();
        try { _cts.Dispose(); } catch { }
    }
}
