using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using Watching.Common;

namespace Watching.Server;

/// <summary>
/// 抓屏循环：持续把最新一帧放进 Latest 槽位，客户端各自取用（天然丢帧，永不积压）。
/// 只在这条流有客户端时才会抓屏 —— 没人看的时候服务端不做任何事。
/// </summary>
public sealed class CaptureEngine : IDisposable
{
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _signal = new(false);
    private readonly ScreenEncoder _encoder;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private Thread _thread;
    private volatile bool _running;
    private volatile int _subscribers;

    private int _fps = 20;
    private int _maxWidth = 1600;
    private Rectangle _crop = Rectangle.Empty;

    private byte[] _latest;
    private long _seq;
    private int _frameW, _frameH, _screenW, _screenH;
    private double _fpsMeasured;
    private long _lastFpsTick;
    private int _framesSinceTick;
    private double _lastCaptureMs;

    public CaptureEngine(int quality, int fps, int maxWidth)
    {
        _fps = fps;
        _maxWidth = maxWidth;
        _encoder = new ScreenEncoder(quality);
    }

    public long Sequence => Interlocked.Read(ref _seq);
    public int FrameWidth => _frameW;
    public int FrameHeight => _frameH;
    public int ScreenWidth => _screenW;
    public int ScreenHeight => _screenH;
    public double MeasuredFps => _fpsMeasured;
    public double LastCaptureMs => _lastCaptureMs;
    public Rectangle Crop => _crop;
    public bool Cropped => !_crop.IsEmpty;
    public int Fps => _fps;
    public int MaxWidth => _maxWidth;
    public bool Active => _subscribers > 0;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "watching-capture" };
        try { _thread.Priority = ThreadPriority.AboveNormal; } catch { }
        _thread.Start();
    }

    public void Subscribe()
    {
        Interlocked.Increment(ref _subscribers);
        _signal.Set();
    }

    public void Unsubscribe()
    {
        if (Interlocked.Decrement(ref _subscribers) < 0) Interlocked.Exchange(ref _subscribers, 0);
    }

    public void SetQuality(int q)
    {
        lock (_gate) _encoder.SetQuality(q);
    }

    public void SetFps(int f)
    {
        _fps = Math.Clamp(f, 1, 60);
        _signal.Set();
    }

    public void SetMaxWidth(int w)
    {
        _maxWidth = w <= 0 ? 0 : Math.Clamp(w, 200, 7680);
        _signal.Set();
    }

    public void SetCrop(Rectangle crop)
    {
        _crop = crop;
        _signal.Set();
    }

    public bool TryGetLatest(out byte[] jpeg, out long seq, out int w, out int h, out bool cropped)
    {
        jpeg = _latest;
        seq = Interlocked.Read(ref _seq);
        w = _frameW;
        h = _frameH;
        cropped = !_crop.IsEmpty;
        return jpeg != null;
    }

    /// <summary>等待比 sinceSeq 更新的一帧，最多等 timeoutMs 毫秒。</summary>
    public bool WaitForFrame(long sinceSeq, int timeoutMs)
    {
        if (!Active) return false;
        if (Interlocked.Read(ref _seq) > sinceSeq) return true;
        _signal.Reset();
        if (Interlocked.Read(ref _seq) > sinceSeq) return true;
        return _signal.Wait(timeoutMs);
    }

    private void Loop()
    {
        Log.Write($"抓屏线程启动 [{_fps}fps 宽度上限={(_maxWidth <= 0 ? "原始" : _maxWidth.ToString())}]");
        while (_running)
        {
            if (_subscribers <= 0)
            {
                _signal.Wait(1000);
                _signal.Reset();
                _framesSinceTick = 0;
                continue;
            }

            long start = _clock.ElapsedMilliseconds;
            try
            {
                Publish();
            }
            catch (Exception ex)
            {
                Log.Error("抓屏失败", ex);
                _signal.Wait(500);
                _signal.Reset();
                continue;
            }
            long elapsed = _clock.ElapsedMilliseconds - start;
            int target = (int)(1000.0 / Math.Max(1, _fps));
            int wait = target - (int)elapsed;

            _framesSinceTick++;
            long now = _clock.ElapsedMilliseconds;
            if (now - _lastFpsTick >= 1000)
            {
                _fpsMeasured = _framesSinceTick * 1000.0 / Math.Max(1, now - _lastFpsTick);
                _framesSinceTick = 0;
                _lastFpsTick = now;
            }

            if (wait > 1) _signal.Wait(wait);
            else _signal.Reset();
        }
        Log.Write("抓屏线程退出");
    }

    private void Publish()
    {
        Rectangle crop;
        int maxWidth;
        lock (_gate)
        {
            crop = _crop;
            maxWidth = _maxWidth;
        }

        var sw = Stopwatch.StartNew();
        int w, h;
        byte[] data;
        try
        {
            data = _encoder.Capture(crop, maxWidth, out w, out h);
        }
        catch (Exception ex)
        {
            Log.Error("编码失败", ex);
            throw;
        }
        sw.Stop();

        var vs = ScreenEncoder.VirtualScreenBounds();

        lock (_gate)
        {
            _latest = data;
            _frameW = w;
            _frameH = h;
            _screenW = vs.Width;
            _screenH = vs.Height;
            _lastCaptureMs = sw.Elapsed.TotalMilliseconds;
            Interlocked.Increment(ref _seq);
        }
        _signal.Set();
    }

    public void Dispose()
    {
        _running = false;
        _signal.Set();
        try { _thread?.Join(1500); } catch { }
        try { _encoder.Dispose(); } catch { }
        try { _signal.Dispose(); } catch { }
    }
}
