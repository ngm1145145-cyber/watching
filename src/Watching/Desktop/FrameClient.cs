using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Watching.Common;
using Watching.Server;

namespace Watching.Desktop;

public enum ClientState { Idle, Connecting, Connected, Closed, Failed }

public sealed class FrameEventArgs : EventArgs
{
    public System.Windows.Media.Imaging.BitmapSource Image { get; init; }
    public FramePacket.Meta Meta { get; init; }
    public long Sequence { get; init; }
    public int ByteLength { get; init; }
    /// <summary>本帧是否是增量帧（只含变化分块）。</summary>
    public bool IsDelta { get; init; }
}

public sealed class ClientStats
{
    public double Fps { get; set; }
    public double Kbps { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public long Frames { get; set; }
    public long Dropped { get; set; }
    public int Quality { get; set; }
    public bool Remote { get; set; }
    public string ServerName { get; set; }
    public int ScreenWidth { get; set; }
    public int ScreenHeight { get; set; }
    /// <summary>单帧解码+渲染消耗（毫秒），用于判断是网络还是本机解码跟不上。</summary>
    public double SendMs { get; set; }
    /// <summary>增量帧占比（0~1），越高说明省流量效果越好。</summary>
    public double DeltaRatio { get; set; }
}

/// <summary>WebSocket 画面接收端（自动重连、自动丢帧、可发送远程输入）。</summary>
public sealed class FrameClient : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private ClientWebSocket _ws;
    private CancellationTokenSource _cts;
    private Task _task;

    private int _bytesWindow;
    private long _framesWindow;
    private long _lastStatTick;
    private double _sendMsTotal;
    private int _sendMsCount;
    private long _dropped;
    private long _deltaFrames;
    private long _fullFrames;
    private long _frames;
    private int _pendingFrames;
    private long _lastSeq = -1;

    private string _host;
    private int _port;
    private string _password;

    public string DisplayName { get; set; } = Environment.MachineName;
    public string Kind { get; set; } = "pc";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public event EventHandler<ClientState> StateChanged;
    public event EventHandler<FrameEventArgs> FrameReceived;
    public event EventHandler<ServerMessage> MessageReceived;
    public event EventHandler<ClientStats> StatsUpdated;

    public ClientState State { get; private set; } = ClientState.Idle;
    public ClientStats Stats { get; } = new();

    public FrameClient(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public void Connect(string host, int port, string password)
    {
        Close();
        _host = host;
        _port = port;
        _password = password;
        Common.Log.Write($"客户端开始连接 ws://{host}:{port}/ws（密码={(string.IsNullOrEmpty(password) ? "无" : "有")}）");
        _cts = new CancellationTokenSource();
        _task = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Close()
    {
        try { _pumpCts?.Cancel(); } catch { }
        try { _cts?.Cancel(); } catch { }
        try
        {
            if (_ws != null && _ws.State == WebSocketState.Open)
                _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).Wait(500);
        }
        catch { }
        SetState(ClientState.Closed);
    }

    private void SetState(ClientState state)
    {
        State = state;
        _dispatcher.BeginInvoke(new Action(() => StateChanged?.Invoke(this, state)));
    }

    public void SetQuality(int quality, int fps, int maxWidth)
    {
        Stats.Quality = quality;
        Send(new ClientMessage { Type = "quality", Quality = quality, Fps = fps, MaxWidth = maxWidth });
    }

    // ---------------- 输入/控制消息发送 ----------------
    //
    // 以前这里是「UI 线程上同步 SendAsync(...).Wait(1000)」：
    // 鼠标每移动一次就阻塞一次 UI 线程，开启远程控制后客户端立刻卡成幻灯片。
    // 现在改成后台发送泵 + 合并队列：
    //   - 鼠标移动只保留最新一个位置（合并），永不让积压
    //   - 按键/点击这类不能丢的按顺序排在最前面
    //   - 整个发送过程不再碰 UI 线程

    private readonly ConcurrentQueue<byte[]> _outQueue = new();
    private byte[] _pendingMove;
    private readonly object _queueGate = new();
    private Task _pumpTask;
    private CancellationTokenSource _pumpCts;

    public void Send(ClientMessage msg)
    {
        try
        {
            if (_ws == null || _ws.State != WebSocketState.Open) return;

            var bytes = JsonSerializer.SerializeToUtf8Bytes(msg, JsonOpts);

            // 鼠标移动：只保留最新的，丢中间帧
            if (string.Equals(msg.Type, "input", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(msg.Kind, "move", StringComparison.OrdinalIgnoreCase))
            {
                lock (_queueGate) _pendingMove = bytes;
                return;
            }

            _outQueue.Enqueue(bytes);
        }
        catch { }
    }

    private void StartInputPump(CancellationToken ct)
    {
        try { _pumpCts?.Cancel(); } catch { }
        _pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _pumpCts.Token;

        _pumpTask = Task.Run(async () =>
        {
            var batch = new List<byte[]>(16);
            while (!token.IsCancellationRequested)
            {
                try
                {
                    byte[] move;
                    lock (_queueGate)
                    {
                        move = _pendingMove;
                        _pendingMove = null;
                    }

                    batch.Clear();
                    while (_outQueue.TryDequeue(out var b)) batch.Add(b);

                    var ws = _ws;
                    if (ws == null || ws.State != WebSocketState.Open)
                    {
                        if (batch.Count == 0 && move == null)
                        {
                            await Task.Delay(50, token).ConfigureAwait(false);
                            continue;
                        }
                        break;
                    }

                    foreach (var b in batch)
                        await ws.SendAsync(b, WebSocketMessageType.Text, true, token).ConfigureAwait(false);

                    if (move != null)
                        await ws.SendAsync(move, WebSocketMessageType.Text, true, token).ConfigureAwait(false);

                    // 有活干就几乎不等待；空闲时降到 100Hz 轮询，避免空转
                    if (batch.Count == 0 && move == null)
                        await Task.Delay(10, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    try { await Task.Delay(50, token).ConfigureAwait(false); } catch { break; }
                }
            }
        }, token);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        int attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            SetState(ClientState.Connecting);

            try
            {
                _ws = new ClientWebSocket();
                _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

                var scheme = _ws.Options.RemoteCertificateValidationCallback != null ? "ws" : "ws";
                var uri = new Uri($"{scheme}://{_host}:{_port}/ws");
                if (!string.IsNullOrEmpty(_password))
                    _ws.Options.SetRequestHeader("Authorization",
                        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("watching:" + _password)));

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(6));
                await _ws.ConnectAsync(uri, timeout.Token).ConfigureAwait(false);

                attempt = 0;
                SetState(ClientState.Connected);
                Common.Log.Write($"客户端已连接 {_host}:{_port}");

                // 启动后台发送泵（输入与画质设置都走它，不再阻塞 UI 线程）
                StartInputPump(ct);

                Send(new ClientMessage
                {
                    Type = "hello",
                    Kind = Kind,
                    Name = DisplayName,
                    Version = "1.0.3"
                });

                _lastStatTick = Environment.TickCount64;
                await ReceiveLoopAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested) break;
            }
            catch (Exception ex)
            {
                // 连接失败，进入重试
                Common.Log.Write($"客户端连接 {_host}:{_port} 失败：{ex.GetType().Name} {ex.Message}");
            }

            try { _ws?.Dispose(); } catch { }
            _ws = null;

            if (ct.IsCancellationRequested) break;

            SetState(ClientState.Failed);
            attempt++;
            int delay = Math.Min(1000 * attempt, 5000);
            try { await Task.Delay(delay, ct).ConfigureAwait(false); } catch { break; }
        }

        SetState(ClientState.Closed);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[1024 * 1024];
        using var ms = new MemoryStream(1024 * 1024);

        while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
        {
            ms.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buffer, 0, result.Count);
                if (ms.Length > 32 * 1024 * 1024) return;
            } while (!result.EndOfMessage);

            var data = ms.ToArray();

            if (result.MessageType == WebSocketMessageType.Text)
            {
                HandleText(data);
            }
            else if (result.MessageType == WebSocketMessageType.Binary)
            {
                // 顺带统计本机处理一帧的耗时，方便区分「网络慢」还是「解码慢」
                var sw = System.Diagnostics.Stopwatch.StartNew();
                HandleFrame(data);
                sw.Stop();
                _sendMsTotal += sw.Elapsed.TotalMilliseconds;
                _sendMsCount++;
            }
        }
    }

    private void HandleText(byte[] data)
    {
        ServerMessage msg;
        try { msg = JsonSerializer.Deserialize<ServerMessage>(Encoding.UTF8.GetString(data), JsonOpts); }
        catch { return; }
        if (msg == null) return;

        if (msg.RemoteControl && msg.Type == "state") Stats.Remote = msg.RemoteControl;
        if (msg.Type == "welcome")
        {
            Stats.ServerName = msg.MachineName;
            Stats.ScreenWidth = msg.ScreenWidth;
            Stats.ScreenHeight = msg.ScreenHeight;
            Stats.Remote = msg.RemoteControl;
            if (msg.Quality > 0) Stats.Quality = msg.Quality;
            if (msg.Version != null) { }
        }

        _dispatcher.BeginInvoke(new Action(() => MessageReceived?.Invoke(this, msg)));
    }

    private void HandleFrame(byte[] data)
    {
        // 解码来不及时直接丢弃旧帧，永远只显示最新的
        if (Interlocked.Increment(ref _pendingFrames) > 3)
        {
            Interlocked.Decrement(ref _pendingFrames);
            Interlocked.Increment(ref _dropped);
            return;
        }

        try
        {
            if (!FramePacket.TryParsePacket(data, out var parsed)) return;

            long seq = parsed.Sequence;
            var meta = parsed.Meta;

            _frames++;
            _framesWindow++;
            _bytesWindow += data.Length;
            _lastSeq = seq;
            if (parsed.IsDelta) _deltaFrames++; else _fullFrames++;

            long now = Environment.TickCount64;
            if (now - _lastStatTick >= 1000)
            {
                double seconds = Math.Max(1, now - _lastStatTick) / 1000.0;
                Stats.Fps = _framesWindow / seconds;
                Stats.Kbps = _bytesWindow / 1024.0 / seconds;
                Stats.Width = meta.w;
                Stats.Height = meta.h;
                Stats.Frames = _frames;
                Stats.Dropped = _dropped;
                Stats.SendMs = _sendMsCount > 0 ? Math.Round(_sendMsTotal / _sendMsCount, 1) : 0;
                Stats.DeltaRatio = _frames > 0 ? _deltaFrames / (double)_frames : 0;

                _framesWindow = 0;
                _bytesWindow = 0;
                _sendMsTotal = 0;
                _sendMsCount = 0;
                _lastStatTick = now;

                var snapshot = new ClientStats
                {
                    Fps = Stats.Fps,
                    Kbps = Stats.Kbps,
                    Width = Stats.Width,
                    Height = Stats.Height,
                    Frames = Stats.Frames,
                    Dropped = Stats.Dropped,
                    Quality = Stats.Quality,
                    Remote = Stats.Remote,
                    ServerName = Stats.ServerName,
                    ScreenWidth = Stats.ScreenWidth,
                    ScreenHeight = Stats.ScreenHeight,
                    SendMs = Stats.SendMs,
                    DeltaRatio = Stats.DeltaRatio
                };
                _dispatcher.BeginInvoke(new Action(() => StatsUpdated?.Invoke(this, snapshot)));
            }

            // 合成（WriteableBitmap 写入）必须在 UI 线程做
            _dispatcher.BeginInvoke(new Action(() => ApplyAndRaise(parsed)),
                DispatcherPriority.Render);
        }
        catch
        {
            // 忽略坏帧
        }
        finally
        {
            Interlocked.Decrement(ref _pendingFrames);
        }
    }

    /// <summary>在 UI 线程上把分块合成为完整画面并抛给界面。</summary>
    private void ApplyAndRaise(FramePacket.Parsed parsed)
    {
        try
        {
            var image = Apply(parsed);
            if (image == null) return;

            var args = new FrameEventArgs
            {
                Image = image,
                Meta = parsed.Meta,
                Sequence = parsed.Sequence,
                ByteLength = _lastPacketLength,
                IsDelta = parsed.IsDelta
            };

            FrameReceived?.Invoke(this, args);
        }
        catch
        {
            // 忽略合成失败（下一帧是整帧时会自动恢复）
        }
    }

    private System.Windows.Media.Imaging.WriteableBitmap _composite;
    private byte[] _lastPacketData;
    private int _lastPacketLength;

    /// <summary>
    /// 把收到的内容合成成完整画面：
    /// 整帧 → 直接作为新基准；增量帧 → 把变化分块贴到基准对应位置。
    /// </summary>
    private System.Windows.Media.Imaging.BitmapSource Apply(FramePacket.Parsed parsed)
    {
        // 把整包数据留着给解码用（分块 JPEG 是包内的切片）
        _lastPacketData = parsed.PacketData;
        _lastPacketLength = parsed.PacketData?.Length ?? 0;

        if (!parsed.IsDelta)
        {
            var bmp = DecodeClip(parsed.FullJpegOffset, parsed.FullJpegLength);
            if (bmp == null) return null;

            var wb = new WriteableBitmap(bmp);
            wb.Freeze();
            _composite = wb;
            return wb;
        }

        var baseBmp = _composite;
        if (baseBmp == null) return null;   // 还没有基准，等关键帧

        foreach (var tile in parsed.Tiles)
        {
            var bmp = DecodeClip(tile.JpegOffset, tile.Length);
            if (bmp == null) continue;

            try
            {
                int w = bmp.PixelWidth, h = bmp.PixelHeight;
                int stride = w * 4;
                var pixels = new byte[stride * h];
                bmp.CopyPixels(pixels, stride, 0);
                baseBmp.WritePixels(new System.Windows.Int32Rect(tile.X, tile.Y, w, h), pixels, stride, 0);
            }
            catch
            {
                // 单块失败不影响其它块
            }
        }

        return baseBmp;
    }

    private BitmapSource DecodeClip(int offset, int length)
    {
        var data = _lastPacketData;
        if (data == null || length <= 0 || offset + length > data.Length) return null;
        return Decode(data, offset, length);
    }

    private static BitmapImage Decode(byte[] buffer, int offset, int count)
    {
        try
        {
            using var ms = new MemoryStream(buffer, offset, count, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            image.StreamSource = ms;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        Close();
        try { _cts?.Dispose(); } catch { }
        try { _ws?.Dispose(); } catch { }
    }
}
