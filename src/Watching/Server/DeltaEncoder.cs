using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using Watching.Common;

namespace Watching.Server;

/// <summary>一帧的分块增量编码结果。</summary>
public sealed class DeltaResult
{
    /// <summary>true = 关键帧（整帧 JPEG）；false = 只含变化分块。</summary>
    public bool IsFullFrame { get; init; }

    /// <summary>整帧 JPEG（关键帧时有效）。</summary>
    public byte[] FullJpeg { get; init; }

    /// <summary>分块尺寸与网格（两种模式都填，客户端据此定位）。</summary>
    public int TileSize { get; init; }
    public int TilesX { get; init; }
    public int TilesY { get; init; }

    /// <summary>变化的分块（增量模式时有效）。</summary>
    public List<DeltaTile> Tiles { get; init; } = new();

    /// <summary>变化面积占比（0~1）。</summary>
    public double ChangedRatio { get; init; }

    /// <summary>本帧实际编码出的字节数（不含协议头）。</summary>
    public int EncodedBytes { get; init; }
}

public sealed class DeltaTile
{
    public int X { get; init; }
    public int Y { get; init; }
    public int W { get; init; }
    public int H { get; init; }
    public byte[] Jpeg { get; init; }
}

/// <summary>
/// 分块增量编码器：把画面切成 TileSize×TileSize 的块，逐块跟上一帧比对，
/// 只把变化的块编码成小 JPEG。静态桌面（看文档、挂机）流量能降一个数量级，
/// 客户端需要解码的像素也少得多，所以更流畅。
///
/// 关键帧（整帧）策略：首次、分块布局变化、变化面积超阈值、或强制刷新时。
/// </summary>
public sealed class DeltaEncoder : IDisposable
{
    private readonly object _gate = new();

    private byte[] _prevPixels;        // 上一帧紧凑像素（仅当前尺寸有效）
    private ulong[][] _prevHashes;     // 每块指纹
    private int _width, _height;
    private int _tilesX, _tilesY;
    private int _framesSinceKey;

    public int TileSize { get; }
    public int KeyFrameInterval { get; set; } = 150;

    /// <summary>变化面积超过这个比例就直接发整帧（此时分块反而更费）。</summary>
    public double FullFrameThreshold { get; set; } = 0.45;

    /// <summary>
    /// 块内平均像素差小于该值就算「没变」。
    /// 用容差而不是精确哈希：软件渲染 / 视频抖动会带来 ±1~2 的噪声，
    /// 精确比对会让每一块都判定为变化，增量就永远不会触发。
    /// </summary>
    public int NoiseTolerance { get; set; } = 3;

    public int FramesSinceKeyFrame => _framesSinceKey;
    public long FullFrames { get; private set; }
    public long DeltaFrames { get; private set; }
    public long TilesSent { get; private set; }

    /// <summary>
    /// 调优用的覆盖开关（默认不设置）：
    ///   WATCHING_DELTA_THRESHOLD=1  → 变化再大也走增量，用于量化「增量比整帧省多少」
    ///   WATCHING_DELTA_TOLERANCE=n  → 调整噪声容差
    /// </summary>
    public static void ApplyEnvOverrides(DeltaEncoder encoder)
    {
        var th = Environment.GetEnvironmentVariable("WATCHING_DELTA_THRESHOLD");
        if (double.TryParse(th, out var t)) encoder.FullFrameThreshold = Math.Clamp(t, 0.05, 1.0);

        var tol = Environment.GetEnvironmentVariable("WATCHING_DELTA_TOLERANCE");
        if (int.TryParse(tol, out var n)) encoder.NoiseTolerance = Math.Clamp(n, 0, 40);
    }

    /// <summary>实际发出的字节数（整帧 + 所有分块）。</summary>
    public long SentBytes { get; private set; }

    /// <summary>假如每帧都发整帧的估算字节数，用于对比增量到底省了多少。</summary>
    public long BaselineBytes { get; private set; }

    /// <summary>最近一次整帧的大小（估算基线用）。</summary>
    private int _lastFullFrameBytes = 70 * 1024;

    public DeltaEncoder(int tileSize = 128)
    {
        // 128 是折中值：块小定位准但协议开销大，块大则单块传得多
        TileSize = Math.Clamp(tileSize, 64, 512);
    }

    /// <summary>
    /// 处理一帧：返回应当发送的内容（整帧或若干变化分块）。
    /// </summary>
    /// <param name="bmp">已按目标分辨率准备好的位图（24bpp 或 32bpp）。</param>
    /// <param name="quality">JPEG 画质。</param>
    /// <param name="forceKeyFrame">强制发整帧（新客户端接入、刚改过画质等）。</param>
    public DeltaResult Process(Bitmap bmp, int quality, bool forceKeyFrame,
        ImageCodecInfo codec, EncoderParameters encoderParams)
    {
        lock (_gate)
        {
            int w = bmp.Width, h = bmp.Height;
            int tilesX = (w + TileSize - 1) / TileSize;
            int tilesY = (h + TileSize - 1) / TileSize;

            if (w != _width || h != _height || tilesX != _tilesX || tilesY != _tilesY || _prevPixels == null)
            {
                ResetLayout(w, h, tilesX, tilesY);
                forceKeyFrame = true;
            }

            var pixels = ReadPixels(bmp);
            bool wantFull = forceKeyFrame || _framesSinceKey >= KeyFrameInterval;

            var changed = new List<(Rectangle rect, int tx, int ty)>();
            if (!wantFull)
            {
                for (int ty = 0; ty < tilesY; ty++)
                {
                    for (int tx = 0; tx < tilesX; tx++)
                    {
                        var rect = TileRect(tx, ty, w, h);
                        if (TileChanged(pixels, w, rect, _prevPixels))
                            changed.Add((rect, tx, ty));
                    }
                }

                if (changed.Count / (double)(tilesX * tilesY) > FullFrameThreshold)
                    wantFull = true;
            }

            DeltaResult result;

            if (wantFull)
            {
                using var ms = new MemoryStream(96 * 1024);
                bmp.Save(ms, codec, encoderParams);
                var bytes = ms.ToArray();

                result = new DeltaResult
                {
                    IsFullFrame = true,
                    FullJpeg = bytes,
                    TileSize = TileSize,
                    TilesX = tilesX,
                    TilesY = tilesY,
                    ChangedRatio = 1.0,
                    EncodedBytes = bytes.Length
                };

                _framesSinceKey = 0;
                FullFrames++;
                _lastFullFrameBytes = bytes.Length;
                SentBytes += bytes.Length;
                BaselineBytes += bytes.Length;
            }
            else
            {
                var tiles = new List<DeltaTile>(changed.Count);
                int total = 0;

                foreach (var (rect, _, _) in changed)
                {
                    var tileBmp = GetTileBitmap(rect);

                    using (var g = Graphics.FromImage(tileBmp))
                    {
                        g.CompositingMode = CompositingMode.SourceCopy;
                        g.DrawImage(bmp, new Rectangle(0, 0, rect.Width, rect.Height), rect, GraphicsUnit.Pixel);
                    }

                    using var ms = new MemoryStream(8 * 1024);
                    tileBmp.Save(ms, codec, encoderParams);

                    var jpeg = ms.ToArray();
                    tiles.Add(new DeltaTile
                    {
                        X = rect.X,
                        Y = rect.Y,
                        W = rect.Width,
                        H = rect.Height,
                        Jpeg = jpeg
                    });
                    total += jpeg.Length;
                }

                result = new DeltaResult
                {
                    IsFullFrame = false,
                    TileSize = TileSize,
                    TilesX = tilesX,
                    TilesY = tilesY,
                    Tiles = tiles,
                    ChangedRatio = changed.Count / (double)(tilesX * tilesY),
                    EncodedBytes = total
                };

                _framesSinceKey++;
                DeltaFrames++;
                TilesSent += tiles.Count;
                SentBytes += total;
                BaselineBytes += _lastFullFrameBytes;
            }

            // 记录本帧，供下一帧比较
            _prevPixels = pixels;
            return result;
        }
    }

    private Rectangle TileRect(int tx, int ty, int w, int h)
    {
        int x = tx * TileSize;
        int y = ty * TileSize;
        return new Rectangle(x, y, Math.Min(TileSize, w - x), Math.Min(TileSize, h - y));
    }

    // 分块位图复用池：每帧新建 Bitmap 会拖慢抓屏循环
    private readonly Dictionary<(int w, int h), Bitmap> _tilePool = new();

    private Bitmap GetTileBitmap(Rectangle rect)
    {
        var key = (rect.Width, rect.Height);
        if (!_tilePool.TryGetValue(key, out var bmp))
        {
            bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format24bppRgb);
            _tilePool[key] = bmp;
        }
        return bmp;
    }

    private void ResetLayout(int w, int h, int tilesX, int tilesY)
    {
        _width = w;
        _height = h;
        _tilesX = tilesX;
        _tilesY = tilesY;
        _prevPixels = null;
        _framesSinceKey = KeyFrameInterval;
        _prevHashes = new ulong[tilesY][];
        for (int ty = 0; ty < tilesY; ty++) _prevHashes[ty] = new ulong[tilesX];
    }

    /// <summary>把位图读成紧凑的 32bpp 像素数组（比 GetPixel 快几个数量级）。</summary>
    private static byte[] ReadPixels(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            var buffer = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                IntPtr src = IntPtr.Add(data.Scan0, y * data.Stride);
                System.Runtime.InteropServices.Marshal.Copy(src, buffer, y * w * 4, w * 4);
            }
            return buffer;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>
    /// 判断一块是否真的变了。
    ///
    /// 隔行隔像素抽样算平均绝对差，可以过滤掉渲染/JPEG 噪声；但只看平均差会漏掉
    /// 「变化很小但很实」的东西：一个文字光标、刚敲下去的一个字，在 128x128 的块里
    /// 平均差不到 1，而容差是 3，于是这些地方要等到下一个整帧（150 帧 ≈ 7 秒）才更新。
    /// 所以再加一条「有样点变化很大」的判据：噪声是每像素 ±2~3 的抖动，
    /// 而光标/文字边缘是 100 以上的跳变，用 56 当阈值两边都不会误判。
    /// </summary>
    private bool TileChanged(byte[] cur, int imageWidth, Rectangle rect, byte[] prev)
    {
        if (prev == null) return true;

        long diff = 0;
        int samples = 0;
        int maxDelta = 0;

        // 每 4 行 / 每 4 像素取样：128x128 的块约 1k 次取样，
        // 足以发现光标（32x32）级别的变化，又比逐像素快 16 倍。
        for (int y = rect.Top; y < rect.Bottom; y += 4)
        {
            int rowBase = y * imageWidth * 4;
            for (int x = rect.Left; x < rect.Right; x += 4)
            {
                int i = rowBase + x * 4;
                int db = Math.Abs(cur[i] - prev[i]);
                int dg = Math.Abs(cur[i + 1] - prev[i + 1]);
                int dr = Math.Abs(cur[i + 2] - prev[i + 2]);

                diff += db + dg + dr;
                samples += 3;
                if (db > maxDelta) maxDelta = db;
                if (dg > maxDelta) maxDelta = dg;
                if (dr > maxDelta) maxDelta = dr;
            }
        }

        if (samples == 0) return false;
        if (diff / (double)samples > NoiseTolerance) return true;

        // 平均差很小，但有明显跳变的样点：光标、小图标、单个字符
        return maxDelta >= StrongChangeThreshold;
    }

    /// <summary>单个样点算「确实变了」的阈值（0~255）。JPEG 噪声远低于它。</summary>
    private const int StrongChangeThreshold = 56;

    public void Dispose()
    {
        _prevPixels = null;
        _prevHashes = null;
        foreach (var b in _tilePool.Values)
        {
            try { b.Dispose(); } catch { }
        }
        _tilePool.Clear();
    }
}
