using System;
using Raw75.Develop;
using SkiaSharp;

namespace Raw75.Imaging;

/// <summary>
/// Simple HDR-ish merge: average GPU display buffers of aligned same-size shots.
/// Real align/de-ghost is still to come; this produces a usable extra document.
/// </summary>
public static class HdrMerge
{
    public static SKBitmap? Average(PhotoDocument[] docs)
    {
        if (docs == null || docs.Length < 2)
            return null;

        SKImage? first = docs[0].Display ?? docs[0].Proxy;
        if (first == null)
            return null;

        int w = first.Width;
        int h = first.Height;
        var acc = new float[w * h * 4];
        int n = 0;

        foreach (var d in docs)
        {
            var img = d.Display ?? d.Proxy;
            if (img == null || img.Width != w || img.Height != h)
                continue;
            using var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            if (!img.ReadPixels(bmp.Info, bmp.GetPixels(), bmp.RowBytes, 0, 0))
                continue;
            n++;
            unsafe
            {
                byte* p = (byte*)bmp.GetPixels();
                int i = 0;
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    acc[i] += p[0];
                    acc[i + 1] += p[1];
                    acc[i + 2] += p[2];
                    p += 4;
                    i += 4;
                }
            }
        }

        if (n < 2)
            return null;

        var outBmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        unsafe
        {
            byte* p = (byte*)outBmp.GetPixels();
            int i = 0;
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                p[0] = (byte)Math.Clamp(acc[i] / n, 0, 255);
                p[1] = (byte)Math.Clamp(acc[i + 1] / n, 0, 255);
                p[2] = (byte)Math.Clamp(acc[i + 2] / n, 0, 255);
                p[3] = 255;
                p += 4;
                i += 4;
            }
        }
        return outBmp;
    }
}
