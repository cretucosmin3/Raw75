using System;
using System.IO;
using System.Runtime.InteropServices;
using Raw75.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Tiff;
using SixLabors.ImageSharp.Formats.Tiff.Constants;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SkiaSharp;

namespace Raw75.Io;

/// <summary>
/// Encode a display-referred image to JPEG/PNG/WebP/TIFF.
/// Safe to call from a background thread. Does not reimplement the look.
/// </summary>
public static class Exporter
{
    public static (int Width, int Height) Export(SKBitmap bitmap, string path, string format, int jpegQuality, int longEdge)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        using SKImage image = SKImage.FromBitmap(bitmap)
            ?? throw new InvalidOperationException("Could not wrap bitmap for export.");
        return Export(image, path, format, jpegQuality, longEdge);
    }

    public static (int Width, int Height) Export(SKImage image, string path, string format, int jpegQuality, int longEdge)
    {
        ArgumentNullException.ThrowIfNull(image);
        using Image<Rgba32> img = ToImageSharp(image);
        return Write(img, path, format, jpegQuality, longEdge);
    }

    internal static (int Width, int Height) Export(RasterBuffer buf, string path, string format, int jpegQuality, int longEdge)
    {
        if (!buf.HasPixels || buf.Rgba == null || buf.Width < 1 || buf.Height < 1)
            throw new InvalidOperationException("Export image has no pixels.");
        using Image<Rgba32> img = Image.LoadPixelData<Rgba32>(buf.Rgba, buf.Width, buf.Height);
        return Write(img, path, format, jpegQuality, longEdge);
    }

    private static (int Width, int Height) Write(
        Image<Rgba32> img, string path, string format, int jpegQuality, int longEdge)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Export path is empty.", nameof(path));

        // Never upscale a proxy or a smaller native. Long-edge is a downscale cap only.
        if (longEdge > 0)
        {
            int w = img.Width;
            int h = img.Height;
            int max = Math.Max(w, h);
            if (max > longEdge)
            {
                float scale = longEdge / (float)max;
                int nw = Math.Max(1, (int)Math.Round(w * scale));
                int nh = Math.Max(1, (int)Math.Round(h * scale));
                img.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(nw, nh),
                    Sampler = KnownResamplers.Lanczos3,
                    Mode = ResizeMode.Stretch,
                    PremultiplyAlpha = false
                }));
            }
        }

        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        int q = Math.Clamp(jpegQuality, 1, 100);
        string kind = NormalizeFormat(format, path);

        switch (kind)
        {
            case "png":
                img.Save(path, new PngEncoder { CompressionLevel = PngCompressionLevel.DefaultCompression });
                break;
            case "webp":
                img.Save(path, new WebpEncoder { Quality = q, FileFormat = WebpFileFormatType.Lossy });
                break;
            case "tiff":
            case "tif":
                img.Save(path, new TiffEncoder { Compression = TiffCompression.Lzw });
                break;
            default:
                img.Save(path, new JpegEncoder
                {
                    Quality = q,
                    ColorType = q >= 90 ? JpegColorType.YCbCrRatio444 : JpegColorType.YCbCrRatio422
                });
                break;
        }

        return (img.Width, img.Height);
    }

    private static string NormalizeFormat(string format, string path)
    {
        string f = (format ?? "").Trim().TrimStart('.').ToLowerInvariant();
        if (f is "jpg" or "jpeg" or "png" or "webp" or "tif" or "tiff")
            return f == "jpg" ? "jpeg" : f;

        string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "png" => "png",
            "webp" => "webp",
            "tif" or "tiff" => "tiff",
            _ => "jpeg"
        };
    }

    private static Image<Rgba32> ToImageSharp(SKImage image)
    {
        int w = image.Width;
        int h = image.Height;
        if (w <= 0 || h <= 0)
            throw new InvalidOperationException("Image has no pixels.");

        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bmp = new SKBitmap(info);
        if (!image.ReadPixels(info, bmp.GetPixels(), bmp.RowBytes))
        {
            using SKImage? raster = image.ToRasterImage();
            if (raster == null || !raster.ReadPixels(info, bmp.GetPixels(), bmp.RowBytes))
                throw new InvalidOperationException("Could not read image pixels for export.");
        }

        byte[] packed = PackRgba(bmp);
        return Image.LoadPixelData<Rgba32>(packed, w, h);
    }

    private static byte[] PackRgba(SKBitmap bmp)
    {
        int w = bmp.Width;
        int h = bmp.Height;
        int stride = bmp.RowBytes;
        int rowBytes = w * 4;
        byte[] packed = new byte[rowBytes * h];
        IntPtr src = bmp.GetPixels();
        if (src == IntPtr.Zero)
            throw new InvalidOperationException("Bitmap has no pixel buffer.");

        if (stride == rowBytes)
        {
            Marshal.Copy(src, packed, 0, packed.Length);
            return packed;
        }

        for (int y = 0; y < h; y++)
            Marshal.Copy(src + y * stride, packed, y * rowBytes, rowBytes);
        return packed;
    }
}
