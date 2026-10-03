using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Watching.Mobile;

public enum ScreenClientState { Idle, Connecting, Connected, Reconnecting, AuthFailed, Failed, Closed }

public sealed class FrameEventArgs : EventArgs
{
    public byte[] Jpeg { get; init; }
    public FramePacket.Meta Meta { get; init; }
    public long Sequence { get; init; }
}

public sealed class StatsEventArgs : EventArgs
{
    public double Fps { get; init; }
    public double Kbps { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public long Frames { get; init; }
    public long Dropped { get; init; }
}

/// <summary>画面客户端：按 IP 连接服务端，自动重连，永远只保留最新一帧。</summary>
public sealed class ScreenClient : IDisposable
{
    private CancellationTokenSource _cts;
    private Task _task;
    private WsSession _session;
    private readonly object _sendGate = new();

    private int _pendingFrames;
    private long _frames;
    private long _dropped;
    private long _lastSeq = -1;
    private int _bytesWindow;
    private long _framesWindow;
    private long _statTick;

    public string Host { get; private set; }
    public int Port { get; private set; }
    public string Password { get; private set; }
    public string ClientName { get; set; } = "安卓手机";
    public ScreenClientState State { get; private set; } = ScreenClientState.Idle;
    public bool RemoteControlEnabled { get; private set; }
    public string ServerName { get; private set; }
    public int ScreenWidth { get; private set; }
    public int ScreenHeight { get; private set; }
    public int Quality { get; private set; } = 65;
    public int FpsLimit { get; private set; } = 20;

    public event EventHandler<ScreenClientState> StateChanged;
    public event EventHandler<FrameEventArgs> FrameReceived;
    public event EventHandler<StatsEventArgs> StatsUpdated;
    public event EventHandler<ServerMessage> MessageReceived;

    public ScreenClient(string host, int port, string password)
    {
        Host = host;
        Port = port;
        Password = password;
    }

    private static string BasicAuth(string password)
        => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("watching:" + password));

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        _task = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _session?.Dispose(); } catch { }
        _session = null;
        _cts = null;
        SetState(ScreenClientState.Closed);
    }

    private void SetState(ScreenClientState state)
    {
        State = state;
        try { StateChanged?.Invoke(this, state); } catch { }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        int attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            SetState(attempt == 0 ? ScreenClientState.Connecting : ScreenClientState.Reconnecting);

            try
            {
                var path = "/ws";
                var auth = string.IsNullOrEmpty(Password) ? null : BasicAuth(Password);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));

                var session = await WsSession.ConnectAsync(Host, Port, path, auth, timeout.Token)
                    .ConfigureAwait(false);
                _session = session;
                attempt = 0;
                SetState(ScreenClientState.Connected);

                await session.SendTextAsync(new ClientMessage
                {
                    Type = "hello",
                    Kind = "mobile",
                    Name = ClientName,
                    Version = "1.0.2"
                }.ToJson(), ct).ConfigureAwait(false);

                _statTick = Environment.TickCount64;
                await ReceiveLoopAsync(session, ct).ConfigureAwait(false);
            }
            catch (WsHandshakeException hex)
            {
                if (hex.StatusCode == 401)
                {
                    _lastError = "访问密码不正确";
                    SetState(ScreenClientState.AuthFailed);
                    return; // 密码错误不重连，等用户改
                }
                _lastError = $"服务端拒绝连接（HTTP {hex.StatusCode}）";
            }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested) break;
            }
            catch (SocketException)
            {
                _lastError = $"连接不上 {Host}:{Port}，请检查 IP、端口和防火墙";
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
            }
            finally
            {
                try { _session?.Dispose(); } catch { }
                _session = null;
            }

            if (ct.IsCancellationRequested) break;

            attempt++;
            SetState(ScreenClientState.Reconnecting);
            try { await Task.Delay(Math.Min(1000 * attempt, 5000), ct).ConfigureAwait(false); }
            catch { break; }
        }

        SetState(ScreenClientState.Closed);
    }

    private string _lastError;

    public string LastError => _lastError;

    private async Task ReceiveLoopAsync(WsSession session, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var (op, payload) = await session.ReadFrameAsync(ct).ConfigureAwait(false);

            switch (op)
            {
                case WsSession.Op.Close:
                    return;

                case WsSession.Op.Ping:
                    await session.SendPongAsync(payload, ct).ConfigureAwait(false);
                    break;

                case WsSession.Op.Pong:
                    break;

                case WsSession.Op.Text:
                    HandleText(Encoding.UTF8.GetString(payload));
                    break;

                case WsSession.Op.Binary:
                    HandleFrame(payload);
                    break;
            }
        }
    }

    private void HandleText(string json)
    {
        var msg = ServerMessage.Parse(json);
        if (msg == null) return;

        switch (msg.Type)
        {
            case "welcome":
                ServerName = msg.MachineName;
                ScreenWidth = msg.ScreenWidth;
                ScreenHeight = msg.ScreenHeight;
                RemoteControlEnabled = msg.RemoteControl;
                if (msg.Quality > 0) Quality = msg.Quality;
                if (msg.Fps > 0) FpsLimit = msg.Fps;
                break;
            case "state":
                RemoteControlEnabled = msg.RemoteControl;
                break;
        }

        try { MessageReceived?.Invoke(this, msg); } catch { }
    }

    private void HandleFrame(byte[] data)
    {
        if (Interlocked.Increment(ref _pendingFrames) > 2)
        {
            Interlocked.Decrement(ref _pendingFrames);
            Interlocked.Increment(ref _dropped);
            return;
        }

        try
        {
            if (!FramePacket.TryParse(data, out long seq, out var meta, out int jpegOffset)) return;
            if (seq == _lastSeq) return;
            _lastSeq = seq;

            var jpeg = new byte[data.Length - jpegOffset];
            Buffer.BlockCopy(data, jpegOffset, jpeg, 0, jpeg.Length);

            _frames++;
            _framesWindow++;
            _bytesWindow += data.Length;

            long now = Environment.TickCount64;
            if (now - _statTick >= 1000)
            {
                double seconds = Math.Max(1, now - _statTick) / 1000.0;
                var stats = new StatsEventArgs
                {
                    Fps = _framesWindow / seconds,
                    Kbps = _bytesWindow / 1024.0 / seconds,
                    Width = meta?.w ?? 0,
                    Height = meta?.h ?? 0,
                    Frames = _frames,
                    Dropped = _dropped
                };
                _framesWindow = 0;
                _bytesWindow = 0;
                _statTick = now;
                try { StatsUpdated?.Invoke(this, stats); } catch { }
            }

            try { FrameReceived?.Invoke(this, new FrameEventArgs { Jpeg = jpeg, Meta = meta, Sequence = seq }); }
            catch { }
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

    public void SendQuality(int quality, int fps, int maxWidth)
    {
        Quality = quality;
        FpsLimit = fps;
        Send(new ClientMessage { Type = "quality", Quality = quality, Fps = fps, MaxWidth = maxWidth });
    }

    public void Send(ClientMessage msg)
    {
        var session = _session;
        if (session == null) return;
        try
        {
            lock (_sendGate)
            {
                session.SendTextAsync(msg.ToJson(), CancellationToken.None).GetAwaiter().GetResult();
            }
        }
        catch { }
    }

    public void Dispose() => Stop();
}
