using System;
using SkiaSharp;

namespace Raw75.Controls;

/// <summary>
/// Compacted physical-style slider chrome:
/// Sits 10px recessed track, 4px glowing fill bar, and an 8x16px vertical pill thumb.
/// </summary>
internal static class SliderChrome
{
    public const float TrackH = Theme.TrackH;
    public const float FillH = Theme.FillH;
    public const float ThumbW = Theme.ThumbW;
    public const float ThumbH = Theme.ThumbH;

    public static void Draw(
        SKCanvas canvas,
        float width,
        float height,
        float t,
        float zeroT,
        bool bipolar,
        bool hover,
        bool active,
        SKShader? trackGradient = null)
    {
        if (width < 4f || height < 4f)
            return;

        t = Math.Clamp(t, 0f, 1f);
        zeroT = Math.Clamp(zeroT, 0f, 1f);
        float cy = height * 0.5f;
        float trackTop = cy - TrackH * 0.5f;
        var track = new SKRect(0, trackTop, width, trackTop + TrackH);
        float radius = TrackH * 0.5f;

        // 1. Recessed Track Background (with optional color gradient)
        SKColor trackFill = hover || active ? Theme.TrackHover : Theme.Track;
        using (var bg = new SKPaint
        {
            Color = trackFill,
            IsAntialias = true,
            Shader = trackGradient
        })
        {
            canvas.DrawRoundRect(track, radius, radius, bg);
        }

        // 2. Crisp 1px Inner/Outer Hairline Stroke
        using (var stroke = new SKPaint
        {
            Color = hover || active ? Theme.Lighten(Theme.Hairline, 32) : Theme.Hairline,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f
        })
        {
            canvas.DrawRoundRect(track, radius, radius, stroke);
        }

        float travel = Math.Max(1f, width - ThumbW);
        float hx = (ThumbW * 0.5f) + t * travel;

        // 3. Inset Fill Bar (Unipolar or Bipolar)
        // Only draw fill bar if not using an explicit full-track gradient (like Kelvin/Tint)
        if (trackGradient == null)
        {
            float fillTop = cy - FillH * 0.5f;
            float fillLeft;
            float fillRight;
            if (bipolar)
            {
                float zeroX = (ThumbW * 0.5f) + zeroT * travel;
                fillLeft = Math.Min(hx, zeroX);
                fillRight = Math.Max(hx, zeroX);
            }
            else
            {
                float minX = ThumbW * 0.5f;
                fillLeft = minX;
                fillRight = hx;
            }

            if (fillRight - fillLeft > 0.5f)
            {
                float ir = FillH * 0.5f;
                using var fill = new SKPaint { Color = Theme.TrackFill, IsAntialias = true };
                canvas.DrawRoundRect(new SKRect(fillLeft, fillTop, fillRight, fillTop + FillH), ir, ir, fill);
            }
        }

        // 4. Proud Vertical Pill Thumb
        float thumbScale = active ? 1.10f : hover ? 1.05f : 1f;
        float tw = ThumbW * thumbScale;
        float th = ThumbH * thumbScale;
        var pill = new SKRect(hx - tw * 0.5f, cy - th * 0.5f, hx + tw * 0.5f, cy + th * 0.5f);
        float pr = tw * 0.5f;

        // Ambient occlusion drop shadow behind thumb
        using (var blur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1.2f))
        using (var shadow = new SKPaint
        {
            Color = new SKColor(0, 0, 0, (byte)(active ? 100 : hover ? 75 : 45)),
            IsAntialias = true,
            MaskFilter = blur
        })
        {
            canvas.DrawRoundRect(pill, pr, pr, shadow);
        }

        // Thumb handle body (pure white resting, warm glow on hover/active)
        SKColor thumb = active ? Theme.HandleActive : hover ? Theme.HandleHover : Theme.Handle;
        using (var handle = new SKPaint { Color = thumb, IsAntialias = true })
        {
            canvas.DrawRoundRect(pill, pr, pr, handle);
        }

        // Subtle top/side rim highlight
        using (var rim = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 120),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 0.75f
        })
        {
            canvas.DrawRoundRect(pill, pr, pr, rim);
        }
    }
}
