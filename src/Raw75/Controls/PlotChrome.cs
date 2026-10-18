using System;
using Blossom.Animations;
using SkiaSharp;

namespace Raw75.Controls;

/// <summary>
/// Shared chrome for graph plots (tone curve, exposure curve, histogram): a recessed well with a
/// soft top shade, quarter grid, strong hairline frame, glowing curve strokes and ring handles.
/// </summary>
internal static class PlotChrome
{
    public const float CurveWidth = 1.75f;
    public const float HandleR = 6.5f;
    public const float HandleHitR = 14f;

    /// <summary>Recessed plot background; <paramref name="hover"/> (0–1) lifts the well slightly.</summary>
    public static void DrawWell(SKCanvas c, SKRect plot, float hover = 0f)
    {
        float r = Theme.Radius;
        using (var fill = new SKPaint { Color = Anim.Lerp(Theme.Well, Theme.WellHover, 0.5f * hover), IsAntialias = true })
            c.DrawRoundRect(plot, r, r, fill);

        c.Save();
        c.ClipRoundRect(new SKRoundRect(plot, r), antialias: true);
        using (var shade = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, plot.Top), new SKPoint(0, plot.Top + Math.Min(18f, plot.Height * 0.3f)),
                new[] { new SKColor(0, 0, 0, 80), new SKColor(0, 0, 0, 0) },
                SKShaderTileMode.Clamp)
        })
        {
            c.DrawRect(plot, shade);
        }
        c.Restore();
    }

    /// <summary>1px quarter lines, pixel-snapped.</summary>
    public static void DrawGrid(SKCanvas c, SKRect plot, bool vertical = true, bool horizontal = true)
    {
        using var grid = new SKPaint { Color = Theme.HairlineSubtle, StrokeWidth = 1f };
        for (int i = 1; i <= 3; i++)
        {
            float f = i * 0.25f;
            if (vertical)
            {
                float x = MathF.Round(plot.Left + plot.Width * f) + 0.5f;
                c.DrawLine(x, plot.Top + 1f, x, plot.Bottom - 1f, grid);
            }
            if (horizontal)
            {
                float y = MathF.Round(plot.Top + plot.Height * f) + 0.5f;
                c.DrawLine(plot.Left + 1f, y, plot.Right - 1f, y, grid);
            }
        }
    }

    public static void DrawFrame(SKCanvas c, SKRect plot)
    {
        using var frame = new SKPaint { Color = Theme.HairlineStrong, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = true };
        var r = plot;
        r.Inflate(-0.5f, -0.5f);
        c.DrawRoundRect(r, Theme.Radius, Theme.Radius, frame);
    }

    /// <summary>Soft vertical wash under <paramref name="curve"/> down to the plot floor.</summary>
    public static void DrawUnderFill(SKCanvas c, SKPath curve, SKRect plot, SKColor color, byte alpha = 34)
    {
        using var fill = new SKPath(curve);
        fill.LineTo(plot.Right, plot.Bottom);
        fill.LineTo(plot.Left, plot.Bottom);
        fill.Close();
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, plot.Top), new SKPoint(0, plot.Bottom),
                new[] { color.WithAlpha(alpha), color.WithAlpha(0) },
                SKShaderTileMode.Clamp)
        };
        c.DrawPath(fill, paint);
    }

    /// <summary>Anti-aliased curve with a faint glow in its own colour.</summary>
    public static void StrokeCurve(SKCanvas c, SKPath path, SKColor color, float width = CurveWidth, float glow = 1f)
    {
        if (glow > 0.01f)
        {
            using var blur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 2.4f);
            using var halo = new SKPaint
            {
                Color = color.WithAlpha((byte)(70 * Math.Clamp(glow, 0f, 1f))),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = width + 2f,
                MaskFilter = blur,
                IsAntialias = true
            };
            c.DrawPath(path, halo);
        }

        using var stroke = new SKPaint
        {
            Color = color,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = width,
            StrokeJoin = SKStrokeJoin.Round,
            StrokeCap = SKStrokeCap.Round,
            IsAntialias = true
        };
        c.DrawPath(path, stroke);
    }

    /// <summary>
    /// Ring handle: <see cref="Theme.Handle"/> disc with a dark outline; <paramref name="lit"/> (0–1)
    /// grows it and blends in an accent ring, <paramref name="active"/> adds the drag halo.
    /// </summary>
    public static void DrawHandle(SKCanvas c, float x, float y, float lit, bool active)
    {
        lit = active ? 1f : Math.Clamp(lit, 0f, 1f);
        float r = HandleR + 1.25f * lit;

        if (active)
        {
            using var haloBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3f);
            using var halo = new SKPaint { Color = Theme.Accent.WithAlpha(90), MaskFilter = haloBlur, IsAntialias = true };
            c.DrawCircle(x, y, r + 3f, halo);
        }

        using (var blur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1.2f))
        using (var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 120), MaskFilter = blur, IsAntialias = true })
            c.DrawCircle(x, y + 0.8f, r + 0.5f, shadow);

        using (var fill = new SKPaint { Color = Anim.Lerp(Theme.Handle, Theme.HandleHover, lit), IsAntialias = true })
            c.DrawCircle(x, y, r, fill);

        using (var rim = new SKPaint { Color = new SKColor(0, 0, 0, 150), Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = true })
            c.DrawCircle(x, y, r, rim);

        if (lit > 0.01f)
        {
            using var ring = new SKPaint
            {
                Color = Theme.Accent.WithAlpha((byte)(255 * lit)),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.5f,
                IsAntialias = true
            };
            c.DrawCircle(x, y, r + 2f, ring);
        }
    }

    /// <summary>Axis / zone label paint: 9.5px dim text.</summary>
    public static TextPaint AxisText(SKTextAlign align = SKTextAlign.Left, bool mono = false) => new()
    {
        TextSize = 9.5f,
        Typeface = mono ? Theme.MonospaceTypeface : Theme.GetTypeface(500),
        Color = Theme.TextDim,
        TextAlign = align
    };

    /// <summary>Small recessed reset button used in graph headers and footers (vector ↺, no glyph dependency).</summary>
    public static void DrawResetButton(SKCanvas c, SKRect r, float hover)
    {
        float rr = Theme.RadiusSm;
        using (var bg = new SKPaint { Color = Anim.Lerp(Theme.Well, Theme.WellHover, hover), IsAntialias = true })
            c.DrawRoundRect(r, rr, rr, bg);
        using (var edge = new SKPaint
        {
            Color = Anim.Lerp(Theme.Hairline, Theme.Accent, hover),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            IsAntialias = true
        })
        {
            var e = r;
            e.Inflate(-0.5f, -0.5f);
            c.DrawRoundRect(e, rr, rr, edge);
        }

        SKColor ink = Anim.Lerp(Theme.TextDim, Theme.Accent, hover);
        float cx = r.MidX, cy = r.MidY, rad = Math.Min(r.Width, r.Height) * 0.24f;
        using (var arc = new SKPaint { Color = ink, Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f, StrokeCap = SKStrokeCap.Round, IsAntialias = true })
            c.DrawArc(SKRect.Create(cx - rad, cy - rad, rad * 2f, rad * 2f), -60f, 290f, false, arc);

        // Arrow head at the arc's start, pointing counter-clockwise.
        float a = -60f * MathF.PI / 180f;
        float hx = cx + MathF.Cos(a) * rad, hy = cy + MathF.Sin(a) * rad;
        using var head = new SKPath();
        head.MoveTo(hx + 2.6f, hy - 1.4f);
        head.LineTo(hx - 0.6f, hy - 2.4f);
        head.LineTo(hx + 0.4f, hy + 1.4f);
        head.Close();
        using var fill = new SKPaint { Color = ink, IsAntialias = true };
        c.DrawPath(head, fill);
    }
}
