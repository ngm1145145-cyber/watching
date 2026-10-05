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
    private int _quality = 70;
    private Rectangle _crop = Rectangle.Empty;

    private byte[] _latest;
    private DeltaResult _latestDelta;
    private long _seq;
    private volatile bool _forceKeyFrame = true;
    private readonly DeltaEncoder _delta = new();
    private int _frameW, _frameH, _screenW, _screenH;
    private double _fpsMeasured;
    private long _lastFpsTick;
    private int _framesSinceTick;
    private double _lastCaptureMs;

    public CaptureEngine(int quality, int fps, int maxWidth)
    {
        _fps = fps;
        _maxWidth = maxWidth;
        _quality = quality;
        _encoder = new ScreenEncoder(quality);
        DeltaEncoder.ApplyEnvOverrides(_delta);
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
        _quality = Math.Clamp(q, 20, 95);
        lock (_gate) _encoder.SetQuality(_quality);
        _forceKeyFrame = true;   // 画质变了必须重发整帧
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

    /// <summary>是否把鼠标光标画进画面。</summary>
    public void SetDrawCursor(bool enabled)
    {
        lock (_gate) _encoder.DrawCursorEnabled = enabled;
        _forceKeyFrame = true;
    }

    public bool TryGetLatest(out byte[] jpeg, out long seq, out int w, out int h, out bool cropped)
    {
        // 必须和 Publish 用同一把锁读：否则可能拿到「第 N 帧的图 + 第 N+1 帧的宽高」，
        // 改分辨率 / 改画质那一瞬间客户端就会按错误的尺寸去贴画面
        lock (_gate)
        {
            jpeg = _latest;
            seq = _seq;
            w = _frameW;
            h = _frameH;
            cropped = !_crop.IsEmpty;
            return jpeg != null;
        }
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
        // 先取再清！以前是「用 _forceKeyFrame 处理完这一帧、最后再置 false」，
        // 于是在抓屏 + 编码这段时间里别人请求的关键帧会被这一句覆盖掉，
        // 新接入的客户端就永远等不到基准整帧（桌面不动时干脆一直黑屏）。
        bool forceKeyFrame = _forceKeyFrame;
        _forceKeyFrame = false;

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

        // 分块增量：标出哪些块变了（真正发什么由每个客户端决定）
        DeltaResult delta = null;
        var deltaWatch = Stopwatch.StartNew();
        try
        {
            delta = _delta.Process(_encoder.LastFrameBitmap, _quality, forceKeyFrame,
                _encoder.JpegCodec, _encoder.JpegParams);
        }
        catch (Exception ex)
        {
            Log.Error("增量编码失败（本帧将退化为整帧）", ex);
        }
        deltaWatch.Stop();
        _lastDeltaMs = deltaWatch.Elapsed.TotalMilliseconds;

        var vs = ScreenEncoder.VirtualScreenBounds();

        // 画面完全没变化（增量帧且零个块变了）时既不递增序号也不通知客户端，
        // 客户端因此不会收到空帧、也不会误以为有新内容 —— 这就是静态桌面几乎零流量的关键。
        if (delta != null && !delta.IsFullFrame && delta.Tiles.Count == 0)
        {
            lock (_gate)
            {
                _frameW = w;
                _frameH = h;
                _screenW = vs.Width;
                _screenH = vs.Height;
                _lastCaptureMs = sw.Elapsed.TotalMilliseconds;
            }
            return;
        }

        lock (_gate)
        {
            _latest = data;
            _latestDelta = delta;
            _frameW = w;
            _frameH = h;
            _screenW = vs.Width;
            _screenH = vs.Height;
            _lastCaptureMs = sw.Elapsed.TotalMilliseconds;
            Interlocked.Increment(ref _seq);
        }
        _signal.Set();
    }

    /// <summary>请求下一次抓屏输出整帧（新客户端接入、改过画质、布局变化等）。</summary>
    public void RequestKeyFrame() => _forceKeyFrame = true;

    /// <summary>增量编码的累计统计（用于对比省了多少流量）。</summary>
    public DeltaEncoder DeltaStats => _delta;

    /// <summary>最近一次分块比对耗时（毫秒）。</summary>
    public double LastDeltaMs => _lastDeltaMs;

    private double _lastDeltaMs;

    /// <summary>取最新一帧的「可发送内容」（整帧或若干变化分块）。</summary>
    public bool TryGetLatestDelta(out DeltaResult delta, out long seq, out int w, out int h)
    {
        // 同样必须在锁里读全，保证 delta / seq / 宽高 是同一帧的
        lock (_gate)
        {
            delta = _latestDelta;
            seq = _seq;
            w = _frameW;
            h = _frameH;
            return delta != null;
        }
    }

    /// <summary>当前订阅数（客户端数）。自适应画质只在只有一个客户端时才敢动，
    /// 否则一个慢客户端会把共享同一条流的其它客户端一起降画质。</summary>
    public int SubscriberCount => _subscribers;

    public void Dispose()
    {
        _running = false;
        _signal.Set();

        bool stopped = false;
        try { stopped = _thread == null || _thread.Join(1500); } catch { }

        // 抓屏线程还活着的时候绝不能销毁 _signal / 编码器：
        // 那个线程下一句就是 _signal.Wait/Set，会抛 ObjectDisposedException 把整个进程带走
        try { _delta.Dispose(); } catch { }
        if (stopped)
        {
            try { _encoder.Dispose(); } catch { }
            try { _signal.Dispose(); } catch { }
        }
        else
        {
            Log.Write("抓屏线程 1.5 秒内没退出来，保留资源等它自己结束（避免释放后仍在使用）");
        }
    }
}
