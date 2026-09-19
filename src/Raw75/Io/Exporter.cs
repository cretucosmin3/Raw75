using System;
using System.IO;
using System.Runtime.InteropServices;
using Raw75.Develop;
using Raw75.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Tiff;
using SixLabors.ImageSharp.Formats.Tiff.Constants;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SkiaSharp;

namespace Raw75.Io;

/// <summary>
/// Encode a display-referred image to JPEG/PNG/WebP/TIFF.
/// Safe to call from a background thread. Preserves original EXIF tags (date, location, camera, exposure).
/// </summary>
public static class Exporter
{
    public static (int Width, int Height) Export(
        SKBitmap bitmap, string path, string format, int jpegQuality, int longEdge,
        string? sourcePath = null, PhotoMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        using SKImage image = SKImage.FromBitmap(bitmap)
            ?? throw new InvalidOperationException("Could not wrap bitmap for export.");
        return Export(image, path, format, jpegQuality, longEdge, sourcePath, metadata);
    }

    public static (int Width, int Height) Export(
        SKImage image, string path, string format, int jpegQuality, int longEdge,
        string? sourcePath = null, PhotoMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        using Image<Rgba32> img = ToImageSharp(image);
        return Write(img, path, format, jpegQuality, longEdge, sourcePath, metadata);
    }

    internal static (int Width, int Height) Export(
        RasterBuffer buf, string path, string format, int jpegQuality, int longEdge,
        string? sourcePath = null, PhotoMetadata? metadata = null)
    {
        if (!buf.HasPixels || buf.Rgba == null || buf.Width < 1 || buf.Height < 1)
            throw new InvalidOperationException("Export image has no pixels.");
        using Image<Rgba32> img = Image.LoadPixelData<Rgba32>(buf.Rgba, buf.Width, buf.Height);
        return Write(img, path, format, jpegQuality, longEdge, sourcePath, metadata);
    }

    private static (int Width, int Height) Write(
        Image<Rgba32> img, string path, string format, int jpegQuality, int longEdge,
        string? sourcePath = null, PhotoMetadata? metadata = null)
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

        ApplyExifMetadata(img, sourcePath, metadata);

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

        if (metadata?.CaptureTime.HasValue == true && File.Exists(path))
        {
            try
            {
                File.SetLastWriteTime(path, metadata.CaptureTime.Value);
            }
            catch { }
        }

        return (img.Width, img.Height);
    }

    private static void ApplyExifMetadata(Image<Rgba32> img, string? sourcePath, PhotoMetadata? metadata)
    {
        ExifProfile? profile = null;

        if (!string.IsNullOrEmpty(sourcePath) && File.Exists(sourcePath))
        {
            try
            {
                var srcInfo = Image.Identify(sourcePath);
                if (srcInfo?.Metadata?.ExifProfile != null)
                {
                    profile = srcInfo.Metadata.ExifProfile.DeepClone();
                }
            }
            catch { }
        }

        profile ??= new ExifProfile();

        if (metadata?.CaptureTime.HasValue == true)
        {
            string dtStr = metadata.CaptureTime.Value.ToString("yyyy:MM:dd HH:mm:ss");
            if (profile.GetValue(ExifTag.DateTimeOriginal) == null)
                profile.SetValue(ExifTag.DateTimeOriginal, dtStr);
            if (profile.GetValue(ExifTag.DateTimeDigitized) == null)
                profile.SetValue(ExifTag.DateTimeDigitized, dtStr);
            if (profile.GetValue(ExifTag.DateTime) == null)
                profile.SetValue(ExifTag.DateTime, dtStr);
        }

        if (metadata?.HasGps == true)
        {
            double lat = metadata.GpsLatitude!.Value;
            double lon = metadata.GpsLongitude!.Value;
            string latRef = lat >= 0 ? "N" : "S";
            string lonRef = lon >= 0 ? "E" : "W";

            profile.SetValue(ExifTag.GPSLatitude, DegreesToGpsRationals(lat));
            profile.SetValue(ExifTag.GPSLatitudeRef, latRef);
            profile.SetValue(ExifTag.GPSLongitude, DegreesToGpsRationals(lon));
            profile.SetValue(ExifTag.GPSLongitudeRef, lonRef);

            if (metadata.GpsAltitude.HasValue)
            {
                double alt = metadata.GpsAltitude.Value;
                profile.SetValue(ExifTag.GPSAltitude, new Rational((uint)Math.Clamp(Math.Round(Math.Abs(alt) * 10.0), 0, uint.MaxValue), 10));
                profile.SetValue(ExifTag.GPSAltitudeRef, (byte)(alt < 0 ? 1 : 0));
            }

            if (metadata.CaptureTime.HasValue)
            {
                profile.SetValue(ExifTag.GPSDateStamp, metadata.CaptureTime.Value.ToString("yyyy:MM:dd"));
                profile.SetValue(ExifTag.GPSTimestamp, new Rational[]
                {
                    new((uint)metadata.CaptureTime.Value.Hour, 1),
                    new((uint)metadata.CaptureTime.Value.Minute, 1),
                    new((uint)metadata.CaptureTime.Value.Second, 1)
                });
            }
        }

        if (metadata != null)
        {
            if (!string.IsNullOrEmpty(metadata.CameraMake) && profile.GetValue(ExifTag.Make) == null)
                profile.SetValue(ExifTag.Make, metadata.CameraMake);

            if (!string.IsNullOrEmpty(metadata.CameraModel) && profile.GetValue(ExifTag.Model) == null)
                profile.SetValue(ExifTag.Model, metadata.CameraModel);

            if (!string.IsNullOrEmpty(metadata.LensModel) && profile.GetValue(ExifTag.LensModel) == null)
                profile.SetValue(ExifTag.LensModel, metadata.LensModel);

            if (metadata.Iso > 0.1f && profile.GetValue(ExifTag.ISOSpeedRatings) == null)
                profile.SetValue(ExifTag.ISOSpeedRatings, new ushort[] { (ushort)Math.Clamp(Math.Round(metadata.Iso), 1, ushort.MaxValue) });

            if (metadata.ShutterSpeed > 0.000001f && profile.GetValue(ExifTag.ExposureTime) == null)
                profile.SetValue(ExifTag.ExposureTime, DoubleToRational(metadata.ShutterSpeed));

            if (metadata.Aperture > 0.01f && profile.GetValue(ExifTag.FNumber) == null)
                profile.SetValue(ExifTag.FNumber, DoubleToRational(metadata.Aperture));

            if (metadata.FocalLength > 0.1f && profile.GetValue(ExifTag.FocalLength) == null)
                profile.SetValue(ExifTag.FocalLength, DoubleToRational(metadata.FocalLength));

            if (metadata.FocalLengthIn35mm > 0.1f && profile.GetValue(ExifTag.FocalLengthIn35mmFilm) == null)
                profile.SetValue(ExifTag.FocalLengthIn35mmFilm, (ushort)Math.Clamp(Math.Round(metadata.FocalLengthIn35mm), 1, ushort.MaxValue));

            if (!string.IsNullOrEmpty(metadata.Artist) && profile.GetValue(ExifTag.Artist) == null)
                profile.SetValue(ExifTag.Artist, metadata.Artist);

            if (!string.IsNullOrEmpty(metadata.Copyright) && profile.GetValue(ExifTag.Copyright) == null)
                profile.SetValue(ExifTag.Copyright, metadata.Copyright);

            if (!string.IsNullOrEmpty(metadata.Description) && profile.GetValue(ExifTag.ImageDescription) == null)
                profile.SetValue(ExifTag.ImageDescription, metadata.Description);
        }

        profile.SetValue(ExifTag.Orientation, (ushort)1);
        profile.SetValue(ExifTag.PixelXDimension, (uint)img.Width);
        profile.SetValue(ExifTag.PixelYDimension, (uint)img.Height);
        profile.SetValue(ExifTag.Software, "Raw75");

        img.Metadata.ExifProfile = profile;
    }

    private static Rational DoubleToRational(double value)
    {
        if (value <= 0 || double.IsNaN(value) || double.IsInfinity(value))
            return new Rational(0, 1);

        if (value < 1.0)
        {
            uint denom = (uint)Math.Clamp(Math.Round(1.0 / value), 1, uint.MaxValue);
            return new Rational(1, denom);
        }

        uint den = 100;
        uint num = (uint)Math.Clamp(Math.Round(value * den), 1, uint.MaxValue);
        return new Rational(num, den);
    }

    private static Rational[] DegreesToGpsRationals(double decimalDegrees)
    {
        double abs = Math.Abs(decimalDegrees);
        uint d = (uint)Math.Floor(abs);
        double minRem = (abs - d) * 60.0;
        uint m = (uint)Math.Floor(minRem);
        double s = (minRem - m) * 60.0;
        uint sNum = (uint)Math.Clamp(Math.Round(s * 1000.0), 0, uint.MaxValue);
        uint sDen = 1000;
        return new Rational[]
        {
            new(d, 1),
            new(m, 1),
            new(sNum, sDen)
        };
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
