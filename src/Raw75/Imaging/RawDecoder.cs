using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Blossom;
using Blossom.Core;
using Sdcb.LibRaw;
using SkiaSharp;

namespace Raw75.Imaging;

internal readonly struct RasterBuffer
{
    public readonly byte[]? Rgba;
    public readonly float[]? Linear;
    public readonly int Width;
    public readonly int Height;

    public bool HasPixels => Width > 0 && Height > 0 && (Linear != null || Rgba != null);
    public bool HasLinear => Linear != null && Width > 0 && Linear.Length >= Width * Height * 4;

    public long ByteLength
    {
        get
        {
            long n = 0;
            if (Linear != null)
                n += (long)Linear.Length * sizeof(float);
            if (Rgba != null)
                n += Rgba.Length;
            return n;
        }
    }

    public string PixelKind =>
        !HasPixels ? "empty" : HasLinear ? (Rgba != null ? "F32+RGBA8" : "F32") : "RGBA8";

    public RasterBuffer(byte[] rgba, int width, int height)
    {
        Rgba = rgba ?? throw new ArgumentNullException(nameof(rgba));
        Linear = null;
        Width = width;
        Height = height;
    }

    public RasterBuffer(float[] linear, int width, int height, byte[]? rgba = null)
    {
        Linear = linear ?? throw new ArgumentNullException(nameof(linear));
        Rgba = rgba;
        Width = width;
        Height = height;
    }
}

internal static class RawDecoder
{
    private static readonly object LibRawGate = new();
    internal const int DisplayMaxEdge = 1600;
    internal const float ProxyScale = 0.35f;
    internal const int ProxyMinEdge = 1600;
    internal const int ProxyMaxEdge = 4096;

    /// <summary>
    /// Working-copy long edge: ~35% of the source, never larger than the source,
    /// clamped so small files stay whole and huge files stay bounded.
    /// </summary>
    internal static int ProxyLongEdge(int width, int height)
    {
        int src = Math.Max(width, height);
        if (src <= 1)
            return 1;
        int want = (int)Math.Round(src * ProxyScale);
        int target = Math.Clamp(want, ProxyMinEdge, ProxyMaxEdge);
        return Math.Min(src, target);
    }

    private static readonly HashSet<string> RasterExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tif", ".tiff"
    };

    public static bool IsRawPath(string path)
    {
        string ext = Path.GetExtension(path);
        return !RasterExt.Contains(ext);
    }

    public static RasterBuffer? TryDecodeRaster(string path)
    {
        if (IsRawPath(path))
            return null;

        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            using var bmp = SKBitmap.Decode(bytes);
            if (bmp == null || bmp.Width <= 0)
                return null;
            RasterBuffer full = FromBitmap(bmp);
            return Limit(full, ProxyLongEdge(full.Width, full.Height));
        }
        catch
        {
            return null;
        }
    }

    public static RasterBuffer? DecodeRasterFull(string path)
    {
        if (IsRawPath(path))
            return null;

        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            using var bmp = SKBitmap.Decode(bytes);
            if (bmp == null || bmp.Width <= 0)
                return null;
            return FromBitmap(bmp);
        }
        catch
        {
            return null;
        }
    }

    public static RasterBuffer DecodeThumbnail(string path)
    {
        LibRawNative.EnsureLoaded();
        lock (LibRawGate)
        {
            using var raw = RawContext.OpenFile(path);
            using ProcessedImage image = raw.ExportThumbnail(0);
            var buf = ToBuffer(image, maxEdge: 0);
            Log.Info($"Thumb {buf.Width}x{buf.Height} type={image.ImageType} bits={image.Bits} ch={image.Channels}");
            return buf;
        }
    }

    public static RasterBuffer DecodePreview(string path)
    {
        return DecodePreview(path, out _, out _);
    }

    public static RasterBuffer DecodePreview(string path, out int nativeW, out int nativeH)
    {
        return DecodePreview(path, out nativeW, out nativeH, out _);
    }

    public static RasterBuffer DecodePreview(string path, out int nativeW, out int nativeH, out Develop.PhotoMetadata? metadata)
    {
        LibRawNative.EnsureLoaded();
        lock (LibRawGate)
        {
            using var raw = RawContext.OpenFile(path);
            bool camWb = false;
            try
            {
                camWb = raw.CameraMultipler[0] > 0.05f && raw.CameraMultipler[1] > 0.05f;
            }
            catch { }

            nativeW = raw.Width > 0 ? raw.Width : raw.RawWidth;
            nativeH = raw.Height > 0 ? raw.Height : raw.RawHeight;
            metadata = ExtractMetadata(raw, path, nativeW, nativeH);
            int edge = ProxyLongEdge(nativeW, nativeH);
            bool half = Math.Max(nativeW, nativeH) > ProxyMaxEdge;
            using ProcessedImage image = raw.ExportRawImage(c => ConfigureLinear(c, camWb, half));
            var buf = ToBuffer(image, edge);
            Log.Info($"Preview {nativeW}x{nativeH} → proxy {buf.Width}x{buf.Height} edge={edge} half={half} bits={image.Bits} ch={image.Channels} camWb={camWb} {Megabytes(buf):0.0}MB");
            return buf;
        }
    }

    public static Develop.PhotoMetadata ExtractMetadata(RawContext raw, string path, int w, int h)
    {
        var meta = new Develop.PhotoMetadata
        {
            Width = w,
            Height = h
        };
        try
        {
            var p = raw.ImageParams;
            meta.CameraMake = p.Make ?? p.NormalizedMake ?? "";
            meta.CameraModel = p.Model ?? p.NormalizedModel ?? "";
        }
        catch { }

        try
        {
            var op = raw.ImageOtherParams;
            meta.Iso = op.IsoSpeed;
            meta.ShutterSpeed = op.Shutter;
            meta.Aperture = op.Aperture;
            meta.FocalLength = op.FocalLength;
            if (op.Timestamp > 0)
            {
                meta.CaptureTime = DateTimeOffset.FromUnixTimeSeconds(op.Timestamp).LocalDateTime;
            }
        }
        catch { }

        try
        {
            var l = raw.LensInfo;
            meta.LensModel = l.Lens ?? l.LensMake ?? "";
            meta.FocalLengthIn35mm = l.FocalLengthIn35mmFormat;
        }
        catch { }

        try
        {
            var fi = new FileInfo(path);
            if (fi.Exists)
            {
                meta.FileSizeBytes = fi.Length;
                if (!meta.CaptureTime.HasValue)
                    meta.CaptureTime = fi.LastWriteTime;
            }
        }
        catch { }

        return meta;
    }

    public static Develop.PhotoMetadata ReadMetadata(string path)
    {
        var meta = new Develop.PhotoMetadata();
        try
        {
            var fi = new FileInfo(path);
            if (fi.Exists)
            {
                meta.FileSizeBytes = fi.Length;
                meta.CaptureTime = fi.LastWriteTime;
            }
        }
        catch { }

        if (IsRawPath(path))
        {
            LibRawNative.EnsureLoaded();
            lock (LibRawGate)
            {
                try
                {
                    using var raw = RawContext.OpenFile(path);
                    int nw = raw.Width > 0 ? raw.Width : raw.RawWidth;
                    int nh = raw.Height > 0 ? raw.Height : raw.RawHeight;
                    return ExtractMetadata(raw, path, nw, nh);
                }
                catch { }
            }
            return meta;
        }

        try
        {
            var info = SixLabors.ImageSharp.Image.Identify(path);
            if (info != null)
            {
                meta.Width = info.Width;
                meta.Height = info.Height;
                var exif = info.Metadata.ExifProfile;
                if (exif != null)
                {
                    meta.CameraMake = exif.GetValue(SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.Make)?.Value?.ToString() ?? "";
                    meta.CameraModel = exif.GetValue(SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.Model)?.Value?.ToString() ?? "";
                    meta.LensModel = exif.GetValue(SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.LensModel)?.Value?.ToString() ?? "";

                    var fnum = exif.GetValue(SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.FNumber)?.Value;
                    if (fnum is SixLabors.ImageSharp.Rational rF && rF.Denominator > 0)
                        meta.Aperture = (float)rF.Numerator / rF.Denominator;

                    var expTime = exif.GetValue(SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.ExposureTime)?.Value;
                    if (expTime is SixLabors.ImageSharp.Rational rE && rE.Denominator > 0)
                        meta.ShutterSpeed = (float)rE.Numerator / rE.Denominator;

                    var isoVal = exif.GetValue(SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.ISOSpeedRatings)?.Value;
                    if (isoVal != null && isoVal.Length > 0) meta.Iso = isoVal[0];

                    var focalVal = exif.GetValue(SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.FocalLength)?.Value;
                    if (focalVal is SixLabors.ImageSharp.Rational rFocal && rFocal.Denominator > 0)
                        meta.FocalLength = (float)rFocal.Numerator / rFocal.Denominator;

                    var focal35 = exif.GetValue(SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.FocalLengthIn35mmFilm)?.Value;
                    if (focal35 is ushort f35) meta.FocalLengthIn35mm = f35;

                    var dateVal = exif.GetValue(SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.DateTimeOriginal)?.Value?.ToString();
                    if (!string.IsNullOrEmpty(dateVal) && DateTime.TryParseExact(dateVal, "yyyy:MM:dd HH:mm:ss", null, System.Globalization.DateTimeStyles.None, out var dt))
                    {
                        meta.CaptureTime = dt;
                    }
                }
            }
        }
        catch { }

        return meta;
    }

    public static RasterBuffer DecodeFull(string path)
    {
        LibRawNative.EnsureLoaded();
        lock (LibRawGate)
        {
            using var raw = RawContext.OpenFile(path);
            bool camWb = false;
            try
            {
                camWb = raw.CameraMultipler[0] > 0.05f && raw.CameraMultipler[1] > 0.05f;
            }
            catch { }

            using ProcessedImage image = raw.ExportRawImage(c => ConfigureLinear(c, camWb, half: false));
            var buf = ToBuffer(image, maxEdge: 0);
            Log.Info($"Full {buf.Width}x{buf.Height} bits={image.Bits} ch={image.Channels} {Megabytes(buf):0.0}MB");
            return buf;
        }
    }

    /// <summary>
    /// Copy a source rectangle into a new buffer no larger than <paramref name="maxEdge"/>.
    /// One allocation (the output). Never copies the full native frame first.
    /// </summary>
    public static RasterBuffer SampleRegion(RasterBuffer src, int x, int y, int w, int h, int maxEdge)
    {
        if (!src.HasPixels)
            return default;
        if (x < 0) x = 0;
        if (y < 0) y = 0;
        if (x >= src.Width || y >= src.Height)
            return default;
        w = Math.Clamp(w, 1, src.Width - x);
        h = Math.Clamp(h, 1, src.Height - y);
        if (maxEdge < 1)
            maxEdge = 1;

        int m = Math.Max(w, h);
        int dw = w;
        int dh = h;
        if (m > maxEdge)
        {
            float scale = maxEdge / (float)m;
            dw = Math.Max(1, (int)Math.Round(w * scale));
            dh = Math.Max(1, (int)Math.Round(h * scale));
        }

        if (src.HasLinear)
        {
            float[] lin = src.Linear!;
            float[] dst = new float[dw * dh * 4];
            for (int row = 0; row < dh; row++)
            {
                int sy = y + Math.Min(h - 1, row * h / dh);
                int srcRow = sy * src.Width * 4;
                int dstRow = row * dw * 4;
                for (int col = 0; col < dw; col++)
                {
                    int sx = x + Math.Min(w - 1, col * w / dw);
                    int si = srcRow + sx * 4;
                    int di = dstRow + col * 4;
                    dst[di] = lin[si];
                    dst[di + 1] = lin[si + 1];
                    dst[di + 2] = lin[si + 2];
                    dst[di + 3] = lin[si + 3];
                }
            }

            return new RasterBuffer(dst, dw, dh);
        }

        byte[] rgba = src.Rgba!;
        byte[] dst8 = new byte[dw * dh * 4];
        for (int row = 0; row < dh; row++)
        {
            int sy = y + Math.Min(h - 1, row * h / dh);
            int srcRow = sy * src.Width * 4;
            int dstRow = row * dw * 4;
            for (int col = 0; col < dw; col++)
            {
                int sx = x + Math.Min(w - 1, col * w / dw);
                int si = srcRow + sx * 4;
                int di = dstRow + col * 4;
                dst8[di] = rgba[si];
                dst8[di + 1] = rgba[si + 1];
                dst8[di + 2] = rgba[si + 2];
                dst8[di + 3] = rgba[si + 3];
            }
        }

        return new RasterBuffer(dst8, dw, dh);
    }

    public static RasterBuffer Crop(RasterBuffer src, int x, int y, int w, int h)
    {
        if (!src.HasPixels)
            return default;
        if (x < 0) x = 0;
        if (y < 0) y = 0;
        if (x >= src.Width || y >= src.Height)
            return default;
        w = Math.Clamp(w, 1, src.Width - x);
        h = Math.Clamp(h, 1, src.Height - y);
        if (x == 0 && y == 0 && w == src.Width && h == src.Height)
            return src;

        if (src.HasLinear)
        {
            float[] lin = src.Linear!;
            float[] dst = new float[w * h * 4];
            for (int row = 0; row < h; row++)
            {
                int si = ((y + row) * src.Width + x) * 4;
                int di = row * w * 4;
                Array.Copy(lin, si, dst, di, w * 4);
            }

            return new RasterBuffer(dst, w, h);
        }

        byte[] rgba = src.Rgba!;
        byte[] dst8 = new byte[w * h * 4];
        for (int row = 0; row < h; row++)
        {
            int si = ((y + row) * src.Width + x) * 4;
            int di = row * w * 4;
            Array.Copy(rgba, si, dst8, di, w * 4);
        }

        return new RasterBuffer(dst8, w, h);
    }

    public static RasterBuffer Limit(RasterBuffer src, int maxEdge)
    {
        int m = Math.Max(src.Width, src.Height);
        if (m <= maxEdge)
            return src;

        float scale = maxEdge / (float)m;
        int nw = Math.Max(1, (int)Math.Round(src.Width * scale));
        int nh = Math.Max(1, (int)Math.Round(src.Height * scale));

        if (src.HasLinear)
        {
            float[] dst = new float[nw * nh * 4];
            float[] lin = src.Linear!;
            for (int y = 0; y < nh; y++)
            {
                int sy = Math.Min(src.Height - 1, y * src.Height / nh);
                int srcRow = sy * src.Width * 4;
                int dstRow = y * nw * 4;
                for (int x = 0; x < nw; x++)
                {
                    int sx = Math.Min(src.Width - 1, x * src.Width / nw);
                    int si = srcRow + sx * 4;
                    int di = dstRow + x * 4;
                    dst[di] = lin[si];
                    dst[di + 1] = lin[si + 1];
                    dst[di + 2] = lin[si + 2];
                    dst[di + 3] = lin[si + 3];
                }
            }

            Log.Info($"Limited linear to {nw}x{nh}");
            return new RasterBuffer(dst, nw, nh);
        }

        byte[] rgba = src.Rgba ?? Array.Empty<byte>();
        byte[] dst8 = new byte[nw * nh * 4];
        for (int y = 0; y < nh; y++)
        {
            int sy = Math.Min(src.Height - 1, y * src.Height / nh);
            int srcRow = sy * src.Width * 4;
            int dstRow = y * nw * 4;
            for (int x = 0; x < nw; x++)
            {
                int sx = Math.Min(src.Width - 1, x * src.Width / nw);
                int si = srcRow + sx * 4;
                int di = dstRow + x * 4;
                dst8[di] = rgba[si];
                dst8[di + 1] = rgba[si + 1];
                dst8[di + 2] = rgba[si + 2];
                dst8[di + 3] = rgba[si + 3];
            }
        }

        Log.Info($"Limited preview to {nw}x{nh}");
        return new RasterBuffer(dst8, nw, nh);
    }

    public static SKImage Upload(RasterBuffer buf, out SKBitmap? keep)
    {
        if (buf.Width < 1 || buf.Height < 1)
            throw new InvalidDataException("Empty raster.");
        if (buf.HasLinear)
            return UploadLinear(buf, out keep);
        if (buf.Rgba == null)
            throw new InvalidDataException("Empty raster.");

        var info = new SKImageInfo(buf.Width, buf.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        keep = new SKBitmap(info);
        IntPtr pixels = keep.GetPixels();
        if (pixels == IntPtr.Zero)
        {
            keep.Dispose();
            throw new InvalidOperationException("SKBitmap pixel alloc failed.");
        }

        int stride = keep.RowBytes;
        int srcStride = buf.Width * 4;
        if (stride == srcStride)
        {
            Marshal.Copy(buf.Rgba, 0, pixels, srcStride * buf.Height);
        }
        else
        {
            for (int y = 0; y < buf.Height; y++)
                Marshal.Copy(buf.Rgba, y * srcStride, pixels + y * stride, srcStride);
        }

        return Freeze(keep, out keep);
    }

    private static SKImage UploadLinear(RasterBuffer buf, out SKBitmap? keep)
    {
        float[] lin = buf.Linear!;
        int w = buf.Width;
        int h = buf.Height;
        var info = new SKImageInfo(w, h, SKColorType.RgbaF16, SKAlphaType.Unpremul);
        var cpu = new SKBitmap(info);
        IntPtr pixels = cpu.GetPixels();
        if (pixels == IntPtr.Zero)
        {
            cpu.Dispose();
            throw new InvalidOperationException("F16 pixel alloc failed.");
        }

        int stride = cpu.RowBytes;
        for (int y = 0; y < h; y++)
        {
            int srcRow = y * w * 4;
            IntPtr row = pixels + y * stride;
            for (int x = 0; x < w; x++)
            {
                int si = srcRow + x * 4;
                int di = x * 8;
                WriteHalf(row, di, lin[si]);
                WriteHalf(row, di + 2, lin[si + 1]);
                WriteHalf(row, di + 4, lin[si + 2]);
                WriteHalf(row, di + 6, lin[si + 3]);
            }
        }

        if (Gpu.IsReady)
        {
            using SKSurface? surface = Gpu.CreateSurface(w, h, SKColorType.RgbaF16, SKAlphaType.Unpremul);
            if (surface != null)
            {
                surface.Canvas.Clear(SKColors.Transparent);
                surface.Canvas.DrawBitmap(cpu, 0, 0);
                SKImage? snap = Gpu.Snapshot(surface);
                cpu.Dispose();
                if (snap != null)
                {
                    keep = null;
                    return snap;
                }
            }
        }

        return Freeze(cpu, out keep);
    }

    private static void WriteHalf(IntPtr row, int offset, float v)
    {
        if (v < -65504f) v = -65504f;
        else if (v > 65504f) v = 65504f;
        ushort bits = BitConverter.HalfToUInt16Bits((Half)v);
        Marshal.WriteInt16(row, offset, (short)bits);
    }

    private static SKImage Freeze(SKBitmap keep, out SKBitmap? live)
    {
        live = keep;
        keep.SetImmutable();
        SKImage? image = SKImage.FromBitmap(keep);
        if (image == null)
        {
            keep.Dispose();
            throw new InvalidOperationException("SKImage.FromBitmap returned null.");
        }

        GpuRetain.Attach(image, keep);
        return image;
    }

    private static float Megabytes(RasterBuffer buf)
    {
        long bytes = buf.HasLinear
            ? (long)buf.Width * buf.Height * 16
            : (long)buf.Width * buf.Height * 4;
        return bytes / (1024f * 1024f);
    }

    private static void ConfigureLinear(OutputParams c, bool camWb, bool half)
    {
        c.HalfSize = half;
        c.UseCameraWb = camWb;
        c.UseAutoWb = !camWb;
        c.Interpolation = !half;
        c.OutputBps = 16;
        c.OutputColor = (LibRawColorSpace)1;
        c.Brightness = 1f;
        c.NoAutoBright = true;
        c.AdjustMaximumThr = 0f;
        c.HighlightMode = 2;
        try
        {
            c.Gamma[0] = 1f;
            c.Gamma[1] = 1f;
        }
        catch (Exception ex)
        {
            Log.Warning("LibRaw gamma: " + ex.Message);
        }
    }

    public static SKImage Upload(SKBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        return Upload(FromBitmap(bitmap), out _);
    }

    private static RasterBuffer FromBitmap(SKBitmap bmp)
    {
        int w = bmp.Width;
        int h = bmp.Height;
        byte[] rgba = new byte[w * h * 4];
        using var pixmap = bmp.PeekPixels();
        if (pixmap == null)
            throw new InvalidDataException("Bitmap has no pixels.");

        unsafe
        {
            byte* src = (byte*)pixmap.GetPixels();
            int stride = pixmap.RowBytes;
            bool bgra = pixmap.ColorType == SKColorType.Bgra8888;
            for (int y = 0; y < h; y++)
            {
                byte* row = src + y * stride;
                int di = y * w * 4;
                for (int x = 0; x < w; x++)
                {
                    byte* p = row + x * 4;
                    if (bgra)
                    {
                        rgba[di] = p[2];
                        rgba[di + 1] = p[1];
                        rgba[di + 2] = p[0];
                    }
                    else
                    {
                        rgba[di] = p[0];
                        rgba[di + 1] = p[1];
                        rgba[di + 2] = p[2];
                    }
                    rgba[di + 3] = 255;
                    di += 4;
                }
            }
        }

        return new RasterBuffer(rgba, w, h);
    }

    private static RasterBuffer ToBuffer(ProcessedImage image, int maxEdge)
    {
        int dataLen = image.DataSize;
        if (dataLen >= 3)
        {
            var head = image.AsSpan<byte>();
            if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8)
            {
                using var decoded = SKBitmap.Decode(head.ToArray());
                if (decoded != null && decoded.Width > 0)
                    return FromBitmap(decoded);
            }
        }

        if (image.ImageType == ProcessedImageType.Jpeg)
        {
            using var decoded = SKBitmap.Decode(image.AsSpan<byte>().ToArray());
            if (decoded == null)
                throw new InvalidDataException("LibRaw JPEG thumbnail could not be decoded.");
            return FromBitmap(decoded);
        }

        int w = image.Width;
        int h = image.Height;
        int channels = Math.Max(1, image.Channels);
        int bits = image.Bits;
        if (w <= 0 || h <= 0)
            throw new InvalidDataException("LibRaw produced an empty image.");

        if (bits <= 8)
        {
            byte[] rgba = new byte[w * h * 4];
            Copy8(image.AsSpan<byte>(), rgba, w, h, channels);
            return new RasterBuffer(rgba, w, h);
        }

        return Copy16Linear(image.AsSpan<ushort>(), w, h, channels, maxEdge);
    }

    private static void Copy8(ReadOnlySpan<byte> src, byte[] dst, int w, int h, int channels)
    {
        int need = w * h * channels;
        if (src.Length < need)
            throw new InvalidDataException($"LibRaw 8-bit buffer short: {src.Length} < {need}");

        int si = 0;
        int di = 0;
        for (int i = 0; i < w * h; i++)
        {
            if (channels >= 3)
            {
                dst[di] = src[si];
                dst[di + 1] = src[si + 1];
                dst[di + 2] = src[si + 2];
            }
            else
            {
                dst[di] = dst[di + 1] = dst[di + 2] = src[si];
            }
            dst[di + 3] = 255;
            di += 4;
            si += channels;
        }
    }

    private static RasterBuffer Copy16Linear(ReadOnlySpan<ushort> src, int w, int h, int channels, int maxEdge)
    {
        int need = w * h * channels;
        if (src.Length < need)
            throw new InvalidDataException($"LibRaw 16-bit buffer short: {src.Length} < {need}");

        int nw = w;
        int nh = h;
        if (maxEdge > 0)
        {
            int m = Math.Max(w, h);
            if (m > maxEdge)
            {
                float s = maxEdge / (float)m;
                nw = Math.Max(1, (int)Math.Round(w * s));
                nh = Math.Max(1, (int)Math.Round(h * s));
            }
        }

        float[] lin = new float[nw * nh * 4];
        const float scale = 1f / 65535f;
        for (int y = 0; y < nh; y++)
        {
            int sy = nh == h ? y : Math.Min(h - 1, y * h / nh);
            int srcRow = sy * w * channels;
            int dstRow = y * nw * 4;
            for (int x = 0; x < nw; x++)
            {
                int sx = nw == w ? x : Math.Min(w - 1, x * w / nw);
                int si = srcRow + sx * channels;
                int di = dstRow + x * 4;
                if (channels >= 3)
                {
                    lin[di] = src[si] * scale;
                    lin[di + 1] = src[si + 1] * scale;
                    lin[di + 2] = src[si + 2] * scale;
                }
                else
                {
                    float v = src[si] * scale;
                    lin[di] = lin[di + 1] = lin[di + 2] = v;
                }
                lin[di + 3] = 1f;
            }
        }

        return new RasterBuffer(lin, nw, nh);
    }
}
