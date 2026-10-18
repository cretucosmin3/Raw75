using System;
using Blossom.Core;
using Raw75.Develop;
using SkiaSharp;

namespace Raw75.Pipeline;

/// <summary>
/// Histogram of exactly what the photo pane shows: renders the GPU develop look (same shaders,
/// same crop, no overlays) into a small half-float surface, reads it back and bins the display
/// values. Each value is split between its two nearest bins, so an 8-bit source stretched by the
/// tone curve doesn't comb into alternating empty bins from a second rounding.
/// <para>
/// Rendering and reading are separate steps in different paint passes: <see cref="Render"/>
/// submits the sample and returns, <see cref="TryRead"/> copies it back on a later frame, by which
/// time the GPU has finished it. Reading in the same frame would stall the paint until the GPU
/// drained all queued work (the full-resolution develop included). Binning runs off the UI thread
/// (<see cref="Bin"/>), so the paint only pays for the copy. Render / TryRead: GPU paint pass only.
/// </para>
/// </summary>
internal sealed class HistogramSampler : IDisposable
{
    public const int Bins = 256;

    private SKSurface? _pending;
    private int _pendingW, _pendingH;

    /// <summary>True while a rendered sample waits for <see cref="TryRead"/>.</summary>
    public bool HasPending => _pending != null;

    /// <summary>
    /// Draws <paramref name="look"/> for the whole frame at <paramref name="longEdge"/> pixels on the
    /// long side and submits it, replacing any unread sample. False when the GPU look can't run.
    /// </summary>
    public bool Render(
        DevelopLook look,
        SKImage source,
        DevelopSettings settings,
        float frameAspect,
        bool fast,
        bool applyCrop,
        int frameW,
        int frameH,
        int longEdge)
    {
        if (!Gpu.IsReady || look.Failed || source.Handle == IntPtr.Zero || frameAspect <= 0f)
            return false;

        int w = frameAspect >= 1f ? longEdge : Math.Max(1, (int)MathF.Round(longEdge * frameAspect));
        int h = frameAspect >= 1f ? Math.Max(1, (int)MathF.Round(longEdge / frameAspect)) : longEdge;

        SKSurface? surface = Gpu.CreateSurface(w, h, SKColorType.RgbaF16, SKAlphaType.Premul);
        if (surface == null)
            return false;

        var rect = new SKRect(0, 0, w, h);
        surface.Canvas.Clear(SKColors.Transparent);
        if (!look.Draw(surface.Canvas, rect, rect, source, settings, fast, applyCrop,
                0f, 0f, 1f, 1f, float.NaN, float.NaN, float.NaN, float.NaN, frameW, frameH, showClipping: false))
        {
            surface.Dispose();
            return false;
        }

        // Submit now so the GPU works on it alongside this frame; the read happens next frame.
        surface.Flush();
        DropPending();
        _pending = surface;
        _pendingW = w;
        _pendingH = h;
        return true;
    }

    /// <summary>
    /// Copies the pending sample back as raw half floats (RGBA, premultiplied: the look is opaque,
    /// so no conversion pass). Null when there is none or the read failed. Bin it with <see cref="Bin"/>.
    /// </summary>
    public unsafe Half[]? TryRead()
    {
        var surface = _pending;
        if (surface == null || !Gpu.IsReady)
            return null;
        _pending = null;

        try
        {
            int w = _pendingW, h = _pendingH;
            var pixels = new Half[w * h * 4];
            var info = new SKImageInfo(w, h, SKColorType.RgbaF16, SKAlphaType.Premul);
            fixed (Half* p = pixels)
            {
                if (!Gpu.ReadPixels(surface, info, (IntPtr)p, w * 8))
                    return null;
            }
            return pixels;
        }
        finally
        {
            surface.Dispose();
        }
    }

    /// <summary>R, G, B and luma bins from <see cref="TryRead"/> pixels. Thread-safe (pure).</summary>
    public static (float[] R, float[] G, float[] B, float[] Y) Bin(Half[] pixels)
    {
        float[] r = new float[Bins], g = new float[Bins], b = new float[Bins], y = new float[Bins];
        for (int o = 0; o + 3 < pixels.Length; o += 4)
        {
            if ((float)pixels[o + 3] < 0.5f)
                continue;
            float pr = (float)pixels[o], pg = (float)pixels[o + 1], pb = (float)pixels[o + 2];
            Splat(r, pr);
            Splat(g, pg);
            Splat(b, pb);
            Splat(y, 0.2126f * pr + 0.7152f * pg + 0.0722f * pb);
        }
        return (r, g, b, y);
    }

    /// <summary>Discards an unread sample (e.g. a different photo was bound).</summary>
    public void DropPending()
    {
        _pending?.Dispose();
        _pending = null;
    }

    public void Dispose() => DropPending();

    /// <summary>
    /// Adds one sample at display value <paramref name="v"/> (0–1) across its two nearest bins.
    /// The end bins only take truly clipped values (they drive the clipping markers).
    /// </summary>
    private static void Splat(float[] bins, float v)
    {
        if (!(v > 0f)) { bins[0] += 1f; return; }
        if (v >= 1f) { bins[Bins - 1] += 1f; return; }
        float x = Math.Clamp(v * (Bins - 1), 1f, Bins - 2);
        int i = (int)x;
        float f = x - i;
        bins[i] += 1f - f;
        if (f > 0f)
            bins[i + 1] += f;
    }
}
