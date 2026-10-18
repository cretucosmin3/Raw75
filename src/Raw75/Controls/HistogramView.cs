using System;
using Blossom.Core;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Blossom.Primitives;
using Blossom.Reactive;
using SkiaSharp;

namespace Raw75.Controls;

/// <summary>
/// RGB histogram card in the Lightroom manner: each channel is a Gaussian-smoothed shape with a
/// gradient body and a faint glowing crest; where channels overlap they add up to
/// cyan / magenta / yellow and grey. Heights use a square-root scale against the tallest interior
/// bin, so a clipping spike at either end can't flatten the rest. Corner triangles light when
/// shadows / highlights clip; hovering names the tonal zone under the pointer.
/// Bins are supplied (see <c>HistogramSampler</c>); this type does not sample pixels.
/// </summary>
public class HistogramView : Control
{
    private const int BinCount = 256;
    private const float Inset = 5f;
    private const float ClipShare = 0.01f;
    private const float SmoothSigma = 2.4f;

    private static readonly (float End, string Name)[] Zones =
    {
        (0.10f, "Blacks"),
        (0.35f, "Shadows"),
        (0.65f, "Exposure"),
        (0.90f, "Highlights"),
        (1.00f, "Whites")
    };

    private static readonly float[] Kernel = BuildKernel(SmoothSigma);

    // Raw end bins drive the clipping markers; the smoothed copies drive the shapes.
    private readonly float[] _r = new float[BinCount];
    private readonly float[] _g = new float[BinCount];
    private readonly float[] _b = new float[BinCount];
    private readonly float[] _y = new float[BinCount];
    private readonly float[] _sr = new float[BinCount];
    private readonly float[] _sg = new float[BinCount];
    private readonly float[] _sb = new float[BinCount];
    private bool _hasData;

    private ChannelPaths? _pathR, _pathG, _pathB;
    private SKRect _pathArea;
    private bool _pathsDirty = true;
    private int _hoverZone = -1;
    private SKSurface? _cache;
    private int _cacheW, _cacheH;
    private bool _cacheDirty = true;

    public HistogramView()
    {
        Name = "HistogramView";
        Transform.Height = 72;
        Overflow = OverflowMode.Clip;
        Style = new ElementStyle
        {
            BackColor = Theme.Section,
            Border = new BorderStyle { Width = 1 },
            Shadow = new ShadowStyle(0, 2.5f, 3, 3, SKColors.Transparent)
        };

        Events.OnMouseMove += (_, e) => SetHoverZone(ZoneAt(e.Relative.X));

        this.Bind(_ => ApplyTheme());
    }

    public void SetBins(float[] r, float[] g, float[] b, float[] y)
    {
        _hasData = Copy(r, _r) | Copy(g, _g) | Copy(b, _b) | Copy(y, _y);
        Smooth(_r, _sr);
        Smooth(_g, _sg);
        Smooth(_b, _sb);
        _pathsDirty = true;
        Redraw();
    }

    public override SKSize GetPreferredSize(float maxWidth, float maxHeight)
    {
        float w = maxWidth > 0 ? maxWidth : (Transform.Width > 0 ? Transform.Width : Theme.RightW);
        float h = 72f;
        if (maxHeight > 0) h = Math.Min(h, maxHeight);
        return new SKSize(w, h);
    }

    protected override void OnHoverChanged()
    {
        if (!IsHovered)
            SetHoverZone(-1);
    }

    private void ApplyTheme()
    {
        Style.BackColor = Theme.Section;
        Style.Border.Color = Theme.Hairline;
        Style.Border.Roundness = Theme.Radius;
        Theme.ApplyCardShadow(Style);
        Redraw();
    }

    private SKRect Plot => new(Inset, Inset, W - Inset, H - Inset);

    private void Redraw()
    {
        _cacheDirty = true;
        InvalidatePaint();
    }

    private int ZoneAt(float localX)
    {
        var plot = Plot;
        if (plot.Width <= 0f) return -1;
        float t = (localX - plot.Left) / plot.Width;
        if (t < 0f || t > 1f) return -1;
        for (int i = 0; i < Zones.Length; i++)
            if (t <= Zones[i].End) return i;
        return Zones.Length - 1;
    }

    private void SetHoverZone(int zone)
    {
        if (_hoverZone == zone) return;
        _hoverZone = zone;
        Redraw();
    }

    protected override void Paint(SKCanvas canvas)
    {
        if (W <= Inset * 2f + 2f || H <= Inset * 2f + 2f) return;

        // The card repaints on every frame while the photo is edited; redraw the graph only when
        // its content changed and blit the cached texture otherwise.
        float scale = Math.Max(1f, canvas.TotalMatrix.ScaleX);
        int pw = (int)MathF.Ceiling(W * scale), ph = (int)MathF.Ceiling(H * scale);
        if (!Gpu.IsReady)
        {
            DrawContent(canvas);
            return;
        }
        if (_cache == null || _cacheW != pw || _cacheH != ph)
        {
            _cache?.Dispose();
            _cache = Gpu.CreateSurface(pw, ph, SKColorType.Rgba8888, SKAlphaType.Premul);
            _cacheW = pw;
            _cacheH = ph;
            _cacheDirty = true;
            if (_cache == null)
            {
                DrawContent(canvas);
                return;
            }
        }
        if (_cacheDirty)
        {
            var cc = _cache.Canvas;
            cc.Clear(SKColors.Transparent);
            cc.Save();
            cc.Scale(scale);
            DrawContent(cc);
            cc.Restore();
            _cacheDirty = false;
        }

        canvas.Save();
        canvas.Scale(1f / scale);
        canvas.DrawSurface(_cache, 0, 0);
        canvas.Restore();
    }

    private void DrawContent(SKCanvas canvas)
    {
        var plot = Plot;
        PlotChrome.DrawWell(canvas, plot);

        canvas.Save();
        // Plain rect clip (the well's rounded corners sit outside the curves' reach anyway).
        canvas.ClipRect(plot);

        DrawZones(canvas, plot);

        if (_hasData)
        {
            // Keep the crest clear of the frame.
            var area = new SKRect(plot.Left, plot.Top + 6f, plot.Right, plot.Bottom);
            EnsurePaths(area);

            // Additive overlap mixes to secondaries and grey (multiply on light chrome).
            var blend = Theme.IsLight ? SKBlendMode.Multiply : SKBlendMode.Plus;
            DrawChannel(canvas, _pathR!, area, Theme.RedChannel, blend);
            DrawChannel(canvas, _pathG!, area, Theme.GreenChannel, blend);
            DrawChannel(canvas, _pathB!, area, Theme.BlueChannel, blend);
        }

        DrawZoneLabel(canvas, plot);
        canvas.Restore();

        PlotChrome.DrawFrame(canvas, plot);

        if (_hasData)
        {
            float peak = 0f;
            for (int i = 0; i < BinCount; i++)
                peak = Math.Max(peak, Math.Max(Math.Max(_r[i], _g[i]), Math.Max(_b[i], _y[i])));
            float clipAt = peak * ClipShare;
            DrawClipMark(canvas, plot, left: true, peak > 0f ? ClipTint(0, clipAt) : null);
            DrawClipMark(canvas, plot, left: false, peak > 0f ? ClipTint(BinCount - 1, clipAt) : null);
        }
        else
        {
            DrawClipMark(canvas, plot, left: true, null);
            DrawClipMark(canvas, plot, left: false, null);
        }
    }

    /// <summary>Faint zone dividers; the hovered zone gets a soft wash.</summary>
    private void DrawZones(SKCanvas canvas, SKRect plot)
    {
        using var line = new SKPaint { Color = Theme.HairlineSubtle, StrokeWidth = 1f };
        float start = 0f;
        for (int i = 0; i < Zones.Length; i++)
        {
            float end = Zones[i].End;
            if (i == _hoverZone)
            {
                using var wash = new SKPaint { Color = Theme.Text.WithAlpha(12) };
                canvas.DrawRect(new SKRect(plot.Left + plot.Width * start, plot.Top, plot.Left + plot.Width * end, plot.Bottom), wash);
            }
            if (i < Zones.Length - 1)
            {
                float x = MathF.Round(plot.Left + plot.Width * end) + 0.5f;
                canvas.DrawLine(x, plot.Top, x, plot.Bottom, line);
            }
            start = end;
        }
    }

    private void DrawZoneLabel(SKCanvas canvas, SKRect plot)
    {
        if (_hoverZone < 0) return;
        float start = _hoverZone == 0 ? 0f : Zones[_hoverZone - 1].End;
        float end = Zones[_hoverZone].End;
        float cx = plot.Left + plot.Width * (start + end) * 0.5f;

        using var text = new TextPaint
        {
            TextSize = 10f,
            Typeface = Theme.GetTypeface(600),
            Color = Theme.TextSecondary,
            TextAlign = SKTextAlign.Center
        };
        string name = Zones[_hoverZone].Name.ToUpperInvariant();
        float tw = text.MeasureText(name);
        float x = Math.Clamp(cx, plot.Left + tw * 0.5f + 6f, plot.Right - tw * 0.5f - 6f);
        var pill = new SKRect(x - tw * 0.5f - 6f, plot.Top + 5f, x + tw * 0.5f + 6f, plot.Top + 21f);
        using (var bg = new SKPaint { Color = Theme.Window.WithAlpha(200), IsAntialias = true })
            canvas.DrawRoundRect(pill, 8f, 8f, bg);
        canvas.DrawText(name, x, pill.Bottom - 4.5f, text);
    }

    private void EnsurePaths(SKRect area)
    {
        if (!_pathsDirty && area == _pathArea && _pathR != null)
            return;

        float peak = 0f;
        for (int i = 1; i < BinCount - 1; i++)
            peak = Math.Max(peak, Math.Max(_sr[i], Math.Max(_sg[i], _sb[i])));
        if (peak <= 0f)
            peak = 1f;

        _pathR?.Dispose();
        _pathG?.Dispose();
        _pathB?.Dispose();
        _pathR = BuildPaths(_sr, area, peak);
        _pathG = BuildPaths(_sg, area, peak);
        _pathB = BuildPaths(_sb, area, peak);
        _pathArea = area;
        _pathsDirty = false;
    }

    /// <summary>Gradient body plus a crisp crest with a soft glow, all additive.</summary>
    private static void DrawChannel(SKCanvas canvas, ChannelPaths paths, SKRect area, SKColor color, SKBlendMode blend)
    {
        var shape = paths.Shape;
        var crest = paths.Crest;
        using (var body = new SKPaint
        {
            IsAntialias = true,
            BlendMode = blend,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, area.Top), new SKPoint(0, area.Bottom),
                new[] { color.WithAlpha(150), color.WithAlpha(46) },
                SKShaderTileMode.Clamp)
        })
        {
            canvas.DrawPath(shape, body);
        }

        // Halo as a wide faint stroke: a blur mask filter here costs several ms per repaint.
        using (var glow = new SKPaint
        {
            Color = color.WithAlpha(40),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 3.5f,
            StrokeJoin = SKStrokeJoin.Round,
            BlendMode = blend
        })
        {
            canvas.DrawPath(crest, glow);
        }

        using var edge = new SKPaint
        {
            Color = Lift(color).WithAlpha(220),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.1f,
            StrokeJoin = SKStrokeJoin.Round,
            BlendMode = blend
        };
        canvas.DrawPath(crest, edge);
    }

    private static float BinHeight(float[] bins, int i, float peak) => MathF.Sqrt(Math.Clamp(bins[i] / peak, 0f, 1f));

    /// <summary>
    /// Crest through every bin and the filled shape under it. The bins are already Gaussian-smoothed,
    /// so ~1px line segments read as a smooth curve; cubic splines looked the same but cost several
    /// times more for Skia to fill and stroke on every repaint.
    /// </summary>
    private static ChannelPaths BuildPaths(float[] bins, SKRect area, float peak)
    {
        var crest = new SKPath();
        for (int i = 0; i < BinCount; i++)
        {
            float x = area.Left + area.Width * (i / (float)(BinCount - 1));
            float y = area.Bottom - area.Height * BinHeight(bins, i, peak);
            if (i == 0) crest.MoveTo(x, y);
            else crest.LineTo(x, y);
        }

        var shape = new SKPath(crest);
        shape.LineTo(area.Right, area.Bottom);
        shape.LineTo(area.Left, area.Bottom);
        shape.Close();
        return new ChannelPaths(crest, shape);
    }

    private sealed class ChannelPaths(SKPath crest, SKPath shape) : IDisposable
    {
        public SKPath Crest { get; } = crest;
        public SKPath Shape { get; } = shape;

        public void Dispose()
        {
            Crest.Dispose();
            Shape.Dispose();
        }
    }

    private static SKColor Lift(SKColor c) => new(
        (byte)Math.Min(255, c.Red + 40), (byte)Math.Min(255, c.Green + 40), (byte)Math.Min(255, c.Blue + 40), c.Alpha);

    /// <summary>Lit colour for an end bin: the sum of the clipping channels, white when all (or only luma) clip.</summary>
    private SKColor? ClipTint(int bin, float threshold)
    {
        bool r = _r[bin] > threshold, g = _g[bin] > threshold, b = _b[bin] > threshold;
        if (r && g && b) return Theme.Text;
        if (!r && !g && !b) return _y[bin] > threshold ? Theme.Text : null;

        int cr = 0, cg = 0, cb = 0;
        void Add(SKColor c) { cr += c.Red; cg += c.Green; cb += c.Blue; }
        if (r) Add(Theme.RedChannel);
        if (g) Add(Theme.GreenChannel);
        if (b) Add(Theme.BlueChannel);
        return new SKColor((byte)Math.Min(255, cr), (byte)Math.Min(255, cg), (byte)Math.Min(255, cb));
    }

    private static void DrawClipMark(SKCanvas canvas, SKRect plot, bool left, SKColor? lit)
    {
        const float S = 7f;
        const float Pad = 4f;
        float x = left ? plot.Left + Pad : plot.Right - Pad;
        float dx = left ? S : -S;
        float y = plot.Top + Pad;

        using var tri = new SKPath();
        tri.MoveTo(x, y);
        tri.LineTo(x + dx, y);
        tri.LineTo(x, y + S);
        tri.Close();

        if (lit is { } color)
        {
            using var blur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 2f);
            using var glow = new SKPaint { Color = color.WithAlpha(120), MaskFilter = blur, IsAntialias = true };
            canvas.DrawPath(tri, glow);
            using var fill = new SKPaint { Color = color, IsAntialias = true };
            canvas.DrawPath(tri, fill);
        }
        else
        {
            using var idle = new SKPaint { Color = Theme.HairlineStrong, IsAntialias = true };
            canvas.DrawPath(tri, idle);
        }
    }

    /// <summary>
    /// Gaussian blur over the interior bins (the end bins are clipping counts and stay out of it).
    /// Removes sampling noise and quantisation combing so the shape reads as a curve.
    /// </summary>
    private static void Smooth(float[] src, float[] dst)
    {
        int radius = Kernel.Length / 2;
        dst[0] = 0f;
        dst[BinCount - 1] = 0f;
        for (int i = 1; i < BinCount - 1; i++)
        {
            float sum = 0f, weight = 0f;
            for (int k = -radius; k <= radius; k++)
            {
                int j = i + k;
                if (j < 1 || j > BinCount - 2) continue;
                sum += src[j] * Kernel[k + radius];
                weight += Kernel[k + radius];
            }
            dst[i] = weight > 0f ? sum / weight : 0f;
        }
        // Let the shape meet the baseline at the ends instead of a cliff.
        dst[0] = dst[1] * 0.5f;
        dst[BinCount - 1] = dst[BinCount - 2] * 0.5f;
    }

    private static float[] BuildKernel(float sigma)
    {
        int radius = (int)MathF.Ceiling(sigma * 3f);
        var k = new float[radius * 2 + 1];
        for (int i = -radius; i <= radius; i++)
            k[i + radius] = MathF.Exp(-(i * i) / (2f * sigma * sigma));
        return k;
    }

    private static bool Copy(float[]? src, float[] dest)
    {
        Array.Clear(dest);
        if (src == null || src.Length == 0) return false;
        int n = Math.Min(BinCount, src.Length);
        Array.Copy(src, dest, n);
        return true;
    }

    public override void Dispose()
    {
        _cache?.Dispose();
        _pathR?.Dispose();
        _pathG?.Dispose();
        _pathB?.Dispose();
        base.Dispose();
    }
}
