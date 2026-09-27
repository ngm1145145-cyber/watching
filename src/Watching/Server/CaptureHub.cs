using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Drawing;
using System.Linq;
using System.Threading;
using Watching.Common;
using Timer = System.Threading.Timer;

namespace Watching.Server;

/// <summary>
/// 一条“抓屏流”。多个请求相同画面（同分辨率/画质/裁剪）的客户端共享一条流。
/// </summary>
public sealed class CaptureStream : IDisposable
{
    private readonly CaptureEngine _engine;
    private int _refs;

    public string Key { get; }
    public int Quality { get; private set; }
    public int Fps { get; private set; }
    public int MaxWidth { get; private set; }
    public Rectangle Crop { get; private set; }
    public CaptureEngine Engine => _engine;
    public int Clients => _refs;
    public bool IsIdle => _refs <= 0;

    public CaptureStream(string key, int quality, int fps, int maxWidth, Rectangle crop)
    {
        Key = key;
        Quality = quality;
        Fps = fps;
        MaxWidth = maxWidth;
        Crop = crop;
        _engine = new CaptureEngine(quality, fps, maxWidth);
        _engine.SetCrop(crop);
        _engine.Start();
    }

    public void Acquire()
    {
        Interlocked.Increment(ref _refs);
        _engine.Subscribe();
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref _refs) <= 0) Interlocked.Exchange(ref _refs, 0);
        _engine.Unsubscribe();
    }

    /// <summary>同一个客户端改变画质时更新这条流（键已包含这些参数，所以通常只有最后一个客户端会改）。</summary>
    public void Update(int quality, int fps, int maxWidth)
    {
        Quality = quality;
        Fps = fps;
        MaxWidth = maxWidth;
        _engine.SetQuality(quality);
        _engine.SetFps(fps);
        _engine.SetMaxWidth(maxWidth);
    }

    public void Dispose()
    {
        try { _engine.Dispose(); } catch { }
    }
}

/// <summary>按需创建 / 回收抓屏流。没客户端时所有流都会被销毁，服务端进入完全空闲状态。</summary>
public sealed class CaptureHub : IDisposable
{
    private readonly ConcurrentDictionary<string, CaptureStream> _streams = new();
    private readonly Timer _reaper;

    public CaptureHub()
    {
        _reaper = new Timer(_ => Reap(), null, 3000, 3000);
    }

    public static string MakeKey(int quality, int fps, int maxWidth, Rectangle crop)
    {
        if (crop.IsEmpty || crop.Width <= 0 || crop.Height <= 0)
            return $"{quality}|{fps}|{maxWidth}|full";
        return $"{quality}|{fps}|{maxWidth}|{crop.X},{crop.Y},{crop.Width},{crop.Height}";
    }

    public CaptureStream Acquire(int quality, int fps, int maxWidth, Rectangle crop)
    {
        var key = MakeKey(quality, fps, maxWidth, crop);
        var stream = _streams.GetOrAdd(key, _ => new CaptureStream(key, quality, fps, maxWidth, crop));
        stream.Acquire();
        return stream;
    }

    public void Release(CaptureStream stream)
    {
        if (stream == null) return;
        stream.Release();
    }

    public bool IsEmpty => _streams.IsEmpty;
    public int StreamCount => _streams.Count;

    public IEnumerable<CaptureStream> Streams => _streams.Values;

    private void Reap()
    {
        foreach (var kv in _streams)
        {
            if (kv.Value.IsIdle && kv.Value.Clients == 0)
            {
                if (_streams.TryRemove(kv.Key, out var removed))
                {
                    removed.Dispose();
                    Log.Write($"回收空闲抓屏流 {kv.Key}");
                }
            }
        }
    }

    public void Dispose()
    {
        try { _reaper.Dispose(); } catch { }
        foreach (var s in _streams.Values) s.Dispose();
        _streams.Clear();
    }
}
