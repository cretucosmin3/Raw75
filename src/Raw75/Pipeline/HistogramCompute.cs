using System;
using SkiaSharp;

namespace Raw75.Pipeline;

/// <summary>
/// RGB + luminance histograms from developed display pixels (not the viewport).
/// Bins are normalized so the peak across channels is 1.
/// </summary>
public static class HistogramCompute
{
    public const int Bins = 256;

    public static void Compute(SKBitmap bgra, float[] r, float[] g, float[] b, float[] y)
    {
        Compute(bgra, r, g, b, y, out _, out _);
    }

    public static void Compute(
        SKBitmap bgra,
        float[] r,
        float[] g,
        float[] b,
        float[] y,
        out int clippedBlack,
        out int clippedWhite)
    {
        EnsureBins(r, g, b, y);
        clippedBlack = 0;
        clippedWhite = 0;
        Clear(r, g, b, y);
        if (bgra == null || bgra.Width < 1 || bgra.Height < 1)
            return;

        using var pixmap = bgra.PeekPixels();
        if (pixmap != null && pixmap.GetPixels() != IntPtr.Zero)
        {
            CountPixmap(pixmap, r, g, b, y, out clippedBlack, out clippedWhite);
            Normalize(r, g, b, y);
            return;
        }

        CountGetPixel(bgra, r, g, b, y, out clippedBlack, out clippedWhite);
        Normalize(r, g, b, y);
    }

    public static void Compute(SKImage image, float[] r, float[] g, float[] b, float[] y)
    {
        Compute(image, r, g, b, y, out _, out _);
    }

    public static void Compute(
        SKImage image,
        float[] r,
        float[] g,
        float[] b,
        float[] y,
        out int clippedBlack,
        out int clippedWhite)
    {
        EnsureBins(r, g, b, y);
        clippedBlack = 0;
        clippedWhite = 0;
        Clear(r, g, b, y);
        if (image == null || image.Width < 1 || image.Height < 1)
            return;

        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var bmp = new SKBitmap(info);
        if (bmp.GetPixels() == IntPtr.Zero)
            return;
        if (!image.ReadPixels(info, bmp.GetPixels(), bmp.RowBytes))
            return;

        Compute(bmp, r, g, b, y, out clippedBlack, out clippedWhite);
    }

    private static void EnsureBins(float[] r, float[] g, float[] b, float[] y)
    {
        if (r == null || g == null || b == null || y == null)
            throw new ArgumentNullException();
        if (r.Length < Bins || g.Length < Bins || b.Length < Bins || y.Length < Bins)
            throw new ArgumentException("Histogram arrays must have 256 bins.");
    }

    private static void Clear(float[] r, float[] g, float[] b, float[] y)
    {
        Array.Clear(r, 0, Bins);
        Array.Clear(g, 0, Bins);
        Array.Clear(b, 0, Bins);
        Array.Clear(y, 0, Bins);
    }

    private static void CountGetPixel(
        SKBitmap bmp,
        float[] r,
        float[] g,
        float[] b,
        float[] y,
        out int clippedBlack,
        out int clippedWhite)
    {
        clippedBlack = 0;
        clippedWhite = 0;
        int w = bmp.Width;
        int h = bmp.Height;
        for (int j = 0; j < h; j++)
        {
            for (int i = 0; i < w; i++)
            {
                SKColor c = bmp.GetPixel(i, j);
                Acc(c.Red, c.Green, c.Blue, r, g, b, y, ref clippedBlack, ref clippedWhite);
            }
        }
    }

    private static void CountPixmap(
        SKPixmap pixmap,
        float[] r,
        float[] g,
        float[] b,
        float[] y,
        out int clippedBlack,
        out int clippedWhite)
    {
        clippedBlack = 0;
        clippedWhite = 0;
        SKColorType ct = pixmap.ColorType;
        if (ct == SKColorType.Bgra8888)
        {
            CountBgra(pixmap, r, g, b, y, out clippedBlack, out clippedWhite, blueFirst: true);
            return;
        }
        if (ct == SKColorType.Rgba8888)
        {
            CountBgra(pixmap, r, g, b, y, out clippedBlack, out clippedWhite, blueFirst: false);
            return;
        }

        int w = pixmap.Width;
        int h = pixmap.Height;
        for (int j = 0; j < h; j++)
        {
            for (int i = 0; i < w; i++)
            {
                SKColor c = pixmap.GetPixelColor(i, j);
                Acc(c.Red, c.Green, c.Blue, r, g, b, y, ref clippedBlack, ref clippedWhite);
            }
        }
    }

    private static unsafe void CountBgra(
        SKPixmap pixmap,
        float[] r,
        float[] g,
        float[] b,
        float[] y,
        out int clippedBlack,
        out int clippedWhite,
        bool blueFirst)
    {
        clippedBlack = 0;
        clippedWhite = 0;
        int w = pixmap.Width;
        int h = pixmap.Height;
        int stride = pixmap.RowBytes;
        byte* basePtr = (byte*)pixmap.GetPixels();
        if (basePtr == null)
            return;

        for (int j = 0; j < h; j++)
        {
            byte* row = basePtr + j * stride;
            for (int i = 0; i < w; i++)
            {
                byte* p = row + (i << 2);
                byte rb, gb, bb;
                if (blueFirst)
                {
                    bb = p[0];
                    gb = p[1];
                    rb = p[2];
                }
                else
                {
                    rb = p[0];
                    gb = p[1];
                    bb = p[2];
                }
                Acc(rb, gb, bb, r, g, b, y, ref clippedBlack, ref clippedWhite);
            }
        }
    }

    private static void Acc(
        byte rb,
        byte gb,
        byte bb,
        float[] r,
        float[] g,
        float[] b,
        float[] y,
        ref int clippedBlack,
        ref int clippedWhite)
    {
        r[rb] += 1f;
        g[gb] += 1f;
        b[bb] += 1f;
        int yi = (int)(0.2126f * rb + 0.7152f * gb + 0.0722f * bb + 0.5f);
        if (yi < 0) yi = 0;
        else if (yi > 255) yi = 255;
        y[yi] += 1f;

        int mn = rb < gb ? rb : gb;
        if (bb < mn) mn = bb;
        int mx = rb > gb ? rb : gb;
        if (bb > mx) mx = bb;
        if (mn == 0) clippedBlack++;
        if (mx == 255) clippedWhite++;
    }

    private static void Normalize(float[] r, float[] g, float[] b, float[] y)
    {
        float max = 0f;
        for (int i = 0; i < Bins; i++)
        {
            if (r[i] > max) max = r[i];
            if (g[i] > max) max = g[i];
            if (b[i] > max) max = b[i];
            if (y[i] > max) max = y[i];
        }
        if (max <= 0f)
            return;
        float inv = 1f / max;
        for (int i = 0; i < Bins; i++)
        {
            r[i] *= inv;
            g[i] *= inv;
            b[i] *= inv;
            y[i] *= inv;
        }
    }
}
