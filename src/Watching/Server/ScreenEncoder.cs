using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using Watching.Common;

namespace Watching.Server;

/// <summary>
/// 屏幕抓取 + JPEG 编码。复用位图与编码器，避免每帧分配大对象。
/// 每个“抓屏流”持有一个实例，因此不同客户端可以有各自的分辨率/画质/裁剪区域。
/// </summary>
public sealed class ScreenEncoder : IDisposable
{
    private Bitmap _fullBmp;
    private Graphics _fullGfx;
    private int _fullW, _fullH;

    private Bitmap _outBmp;
    private Graphics _outGfx;
    private int _outW = -1, _outH = -1;
    private bool _outIsFull;

    private readonly EncoderParameters _encoderParams = new(1);
    private readonly ImageCodecInfo _jpegCodec = FindJpegCodec();
    private int _quality;

    public ScreenEncoder(int quality) => SetQuality(quality);

    /// <summary>最近一次编码所用的 JPEG 编码器（增量编码复用）。</summary>
    public ImageCodecInfo JpegCodec => _jpegCodec;

    /// <summary>最近一次编码所用的编码参数（增量编码复用）。</summary>
    public EncoderParameters JpegParams => _encoderParams;

    /// <summary>
    /// 最近一次 Capture 之后、目标分辨率下的整帧位图。
    /// 调用方只能在下次 Capture 之前读取（内部会复用这块缓冲）。
    /// </summary>
    public Bitmap LastFrameBitmap { get; private set; }

    public static ImageCodecInfo FindJpegCodec()
    {
        try
        {
            foreach (var codec in ImageCodecInfo.GetImageEncoders())
            {
                if (codec.FormatID == ImageFormat.Jpeg.Guid) return codec;
            }
        }
        catch { }
        return null;
    }

    public void SetQuality(int quality)
    {
        int q = Math.Clamp(quality, 20, 95);

        // 必须和 Capture 用同一把锁：_encoderParams 会直接交给 GDI+ 的 Save 使用，
        // 抓屏线程正在编码时从别的线程改它，会编出错误画质甚至抛 ExternalException。
        lock (_captureGate)
        {
            if (q == _quality && _encoderParams.Param[0] != null) return;
            _quality = q;

            var old = _encoderParams.Param[0];
            _encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)q);
            try { old?.Dispose(); } catch { }
        }
    }

    public static Rectangle VirtualScreenBounds()
    {
        return new Rectangle(
            User32.GetSystemMetrics(User32.SM_XVIRTUALSCREEN),
            User32.GetSystemMetrics(User32.SM_YVIRTUALSCREEN),
            Math.Max(1, User32.GetSystemMetrics(User32.SM_CXVIRTUALSCREEN)),
            Math.Max(1, User32.GetSystemMetrics(User32.SM_CYVIRTUALSCREEN)));
    }

    /// <summary>
    /// 抓取一帧并编码成 JPEG。
    /// 注意：多个抓屏流共享位图缓冲，必须串行调用（内部有锁）。
    /// </summary>
    /// <param name="crop">屏幕绝对坐标下的裁剪区域；Empty 表示整个虚拟桌面。</param>
    /// <param name="maxWidth">发送宽度上限，0 表示原始大小。</param>
    public byte[] Capture(Rectangle crop, int maxWidth, out int outW, out int outH)
    {
        lock (_captureGate)
        {
            return CaptureLocked(crop, maxWidth, out outW, out outH);
        }
    }

    private readonly object _captureGate = new();

    private byte[] CaptureLocked(Rectangle crop, int maxWidth, out int outW, out int outH)
    {
        var vs = VirtualScreenBounds();
        if (crop.IsEmpty || crop.Width <= 0 || crop.Height <= 0)
        {
            crop = vs;
        }
        else
        {
            crop.Intersect(vs);
            if (crop.Width <= 0 || crop.Height <= 0) crop = vs;
        }

        EnsureFullBuffer(vs);

        // 1) 一次 BitBlt 抓取需要的那块区域
        _fullGfx.CopyFromScreen(crop.X, crop.Y, 0, 0, new Size(crop.Width, crop.Height),
            CopyPixelOperation.SourceCopy);

        int targetW = maxWidth > 0 ? Math.Min(maxWidth, crop.Width) : crop.Width;
        if (targetW < 1) targetW = 1;
        int targetH = Math.Max(1, (int)Math.Round(crop.Height * (double)targetW / crop.Width));

        // 2) 直接从全屏缓冲缩放/拷贝到输出位图（省掉一次中间分配）
        EnsureOutputBuffer(targetW, targetH, crop.Width == targetW && crop.Height == targetH);

        if (_outIsFull)
        {
            // 输出缓冲就是全屏缓冲
            outW = _outW;
            outH = _outH;
            LastFrameBitmap = _fullBmp;
            DrawCursor(_fullBmp, crop, crop.Width, crop.Height);
            return Encode(_fullBmp, crop.Width, crop.Height);
        }

        _outGfx.DrawImage(_fullBmp,
            new Rectangle(0, 0, targetW, targetH),
            new Rectangle(0, 0, crop.Width, crop.Height),
            GraphicsUnit.Pixel);

        // 光标画在缩放后的位图上，客户端就能看到鼠标了
        DrawCursor(_outBmp, crop, targetW, targetH);

        outW = targetW;
        outH = targetH;
        LastFrameBitmap = _outBmp;
        return Encode(_outBmp, targetW, targetH);
    }

    private byte[] Encode(Bitmap bmp, int w, int h)
    {
        using var ms = new MemoryStream(Math.Max(65536, w * h / 8));
        if (_jpegCodec != null)
            bmp.Save(ms, _jpegCodec, _encoderParams);
        else
            bmp.Save(ms, ImageFormat.Jpeg);
        return ms.ToArray();
    }

    private void EnsureFullBuffer(Rectangle vs)
    {
        if (_fullBmp != null && _fullW == vs.Width && _fullH == vs.Height) return;

        _fullGfx?.Dispose();
        _fullBmp?.Dispose();

        _fullW = vs.Width;
        _fullH = vs.Height;
        // 32bpp 比 24bpp 快约 20%（GDI 对齐更好）
        _fullBmp = new Bitmap(_fullW, _fullH, PixelFormat.Format32bppRgb);
        _fullGfx = Graphics.FromImage(_fullBmp);
        _fullGfx.CompositingMode = CompositingMode.SourceCopy;
        _outW = -1;
        _outH = -1;
    }

    /// <summary>
    /// 把鼠标光标画到输出位图上。
    ///
    /// Windows 的 BitBlt 抓屏**不包含光标**，所以客户端看不到鼠标 —— 这会让远程操作
    /// 完全没法用（不知道点在哪）。这里用 GetCursorInfo + DrawIconEx 手动补上。
    /// 画在「缩放后」的位图上，并按比例换算坐标。
    /// </summary>
    private void DrawCursor(Bitmap target, Rectangle crop, int targetW, int targetH)
    {
        if (!_drawCursor) return;
        if (targetW <= 0 || targetH <= 0 || crop.Width <= 0 || crop.Height <= 0) return;

        var ci = new User32.CURSORINFO();
        ci.cbSize = System.Runtime.InteropServices.Marshal.SizeOf<User32.CURSORINFO>();
        if (!User32.GetCursorInfo(ref ci)) return;
        if ((ci.flags & User32.CURSOR_SHOWING) == 0) return;
        if (ci.hCursor == IntPtr.Zero) return;

        int cx = ci.ptScreenPosX, cy = ci.ptScreenPosY;
        if (cx < crop.Left || cx > crop.Right || cy < crop.Top || cy > crop.Bottom) return;

        // 热点偏移：让光标的「尖」对准真实位置
        int hotX = 0, hotY = 0;
        if (User32.GetIconInfo(ci.hCursor, out var ii))
        {
            hotX = ii.xHotspot;
            hotY = ii.yHotspot;
            if (ii.hbmColor != IntPtr.Zero) User32.DeleteObject(ii.hbmColor);
            if (ii.hbmMask != IntPtr.Zero) User32.DeleteObject(ii.hbmMask);
        }

        double scale = targetW / (double)crop.Width;
        int dx = (int)Math.Round((cx - crop.Left) * scale) - (int)Math.Round(hotX * scale);
        int dy = (int)Math.Round((cy - crop.Top) * scale) - (int)Math.Round(hotY * scale);

        int iconW = (int)Math.Round(32 * scale);
        int iconH = (int)Math.Round(32 * scale);
        if (iconW < 8) iconW = 8;
        if (iconH < 8) iconH = 8;

        using var g = Graphics.FromImage(target);
        IntPtr hdc = g.GetHdc();
        try
        {
            User32.DrawIconEx(hdc, dx, dy, ci.hCursor, iconW, iconH, 0, IntPtr.Zero, User32.DI_NORMAL);
        }
        finally
        {
            g.ReleaseHdc(hdc);
        }
    }

    /// <summary>是否把鼠标光标画进画面（默认开）。</summary>
    public bool DrawCursorEnabled
    {
        get => _drawCursor;
        set => _drawCursor = value;
    }

    private bool _drawCursor = true;

    private void EnsureOutputBuffer(int w, int h, bool canReuseFull)
    {
        bool fullIsUsable = canReuseFull && _fullBmp.Width == w && _fullBmp.Height == h;
        _outIsFull = fullIsUsable;
        if (fullIsUsable) { _outW = w; _outH = h; return; }

        if (_outBmp != null && _outW == w && _outH == h) return;

        _outGfx?.Dispose();
        _outBmp?.Dispose();

        _outW = w;
        _outH = h;
        _outBmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        _outGfx = Graphics.FromImage(_outBmp);
        _outGfx.CompositingMode = CompositingMode.SourceCopy;
        _outGfx.InterpolationMode = InterpolationMode.Bilinear;
        _outGfx.PixelOffsetMode = PixelOffsetMode.HighSpeed;
    }

    public void Dispose()
    {
        try { _fullGfx?.Dispose(); } catch { }
        try { _fullBmp?.Dispose(); } catch { }
        try { _outGfx?.Dispose(); } catch { }
        try { _outBmp?.Dispose(); } catch { }
        try { _encoderParams?.Dispose(); } catch { }
    }
}
