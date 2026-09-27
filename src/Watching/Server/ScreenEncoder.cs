using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

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
        if (q == _quality && _encoderParams.Param[0] != null) return;
        _quality = q;
        _encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)q);
    }

    public static Rectangle VirtualScreenBounds()
    {
        return new Rectangle(
            User32.GetSystemMetrics(User32.SM_XVIRTUALSCREEN),
            User32.GetSystemMetrics(User32.SM_YVIRTUALSCREEN),
            Math.Max(1, User32.GetSystemMetrics(User32.SM_CXVIRTUALSCREEN)),
            Math.Max(1, User32.GetSystemMetrics(User32.SM_CYVIRTUALSCREEN)));
    }

    /// <summary>抓取一帧并编码成 JPEG。</summary>
    /// <param name="crop">屏幕绝对坐标下的裁剪区域；Empty 表示整个虚拟桌面。</param>
    /// <param name="maxWidth">发送宽度上限，0 表示原始大小。</param>
    public byte[] Capture(Rectangle crop, int maxWidth, out int outW, out int outH)
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
            return Encode(_fullBmp, crop.Width, crop.Height);
        }

        if (targetW == crop.Width && targetH == crop.Height)
        {
            _outGfx.DrawImage(_fullBmp,
                new Rectangle(0, 0, targetW, targetH),
                new Rectangle(0, 0, crop.Width, crop.Height),
                GraphicsUnit.Pixel);
        }
        else
        {
            _outGfx.DrawImage(_fullBmp,
                new Rectangle(0, 0, targetW, targetH),
                new Rectangle(0, 0, crop.Width, crop.Height),
                GraphicsUnit.Pixel);
        }

        outW = targetW;
        outH = targetH;
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
