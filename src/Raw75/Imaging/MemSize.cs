using System;
using SkiaSharp;

namespace Raw75.Imaging;

internal static class MemSize
{
    public static string Bytes(long n)
    {
        if (n < 1024)
            return n + " B";
        double v = n;
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (v >= 1024 && i < u.Length - 1)
        {
            v /= 1024;
            i++;
        }
        return i >= 2 ? $"{v:0.00} {u[i]}" : $"{v:0.#} {u[i]}";
    }

    public static long Image(SKImage? image)
    {
        if (image == null || image.Handle == IntPtr.Zero)
            return 0;
        SKImageInfo info = image.Info;
        if (info.BytesSize > 0)
            return info.BytesSize;
        int bpp = BytesPerPixel(info.ColorType);
        return (long)image.Width * image.Height * bpp;
    }

    public static long Bitmap(SKBitmap? bitmap)
    {
        if (bitmap == null || bitmap.Handle == IntPtr.Zero)
            return 0;
        int n = bitmap.ByteCount;
        if (n > 0)
            return n;
        return (long)bitmap.Width * bitmap.Height * BytesPerPixel(bitmap.ColorType);
    }

    public static string ImageLabel(SKImage? image)
    {
        if (image == null || image.Handle == IntPtr.Zero)
            return "null";
        return $"{image.Width}x{image.Height} {image.ColorType}  {Bytes(Image(image))}  0x{image.Handle:X}";
    }

    public static int BytesPerPixel(SKColorType t) => t switch
    {
        SKColorType.RgbaF16 or SKColorType.RgbaF16Clamped => 8,
        SKColorType.RgbaF32 => 16,
        SKColorType.Gray8 or SKColorType.Alpha8 => 1,
        SKColorType.Rgb565 or SKColorType.Argb4444 => 2,
        _ => 4
    };
}
