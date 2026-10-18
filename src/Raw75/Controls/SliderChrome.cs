using System;
using Blossom.Animations;
using SkiaSharp;

namespace Raw75.Controls;

/// <summary>
/// Raw75's slider identity: a recessed pill groove, an inset accent fill (unipolar or centre-out),
/// and a proud vertical pill thumb with an ambient shadow.
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
        => Draw(canvas, width, height, t, zeroT, bipolar, hover ? 1f : 0f, active, trackGradient);

    /// <param name="hover">0–1 hover amount (0 = idle, 1 = hovered).</param>
    public static void Draw(
        SKCanvas canvas,
        float width,
        float height,
        float t,
        float zeroT,
        bool bipolar,
        float hover,
        bool active,
        SKShader? trackGradient = null)
    {
        if (width < 4f || height < 4f)
            return;

        t = Math.Clamp(t, 0f, 1f);
        zeroT = Math.Clamp(zeroT, 0f, 1f);
        float lit = active ? 1f : Math.Clamp(hover, 0f, 1f);
        float cy = height * 0.5f;
        float trackTop = cy - TrackH * 0.5f;
        var track = new SKRect(0, trackTop, width, trackTop + TrackH);
        float radius = TrackH * 0.5f;

        // 1. Recessed groove (optionally a dimmed colour ramp for Kelvin / Tint).
        using (var bg = new SKPaint { Color = Anim.Lerp(Theme.Track, Theme.TrackHover, lit), IsAntialias = true })
            canvas.DrawRoundRect(track, radius, radius, bg);
        if (trackGradient != null)
        {
            using var ramp = new SKPaint { Shader = trackGradient, IsAntialias = true, Color = SKColors.White.WithAlpha((byte)(205 + 50 * lit)) };
            canvas.DrawRoundRect(track, radius, radius, ramp);
        }

        // Inner shade along the top edge sells the recess.
        canvas.Save();
        canvas.ClipRoundRect(new SKRoundRect(track, radius), antialias: true);
        using (var shade = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, trackTop), new SKPoint(0, trackTop + TrackH * 0.6f),
                new[] { new SKColor(0, 0, 0, 90), new SKColor(0, 0, 0, 0) },
                SKShaderTileMode.Clamp)
        })
        {
            canvas.DrawRect(track, shade);
        }
        canvas.Restore();

        using (var stroke = new SKPaint
        {
            Color = Anim.Lerp(Theme.Hairline, Theme.HairlineStrong, lit),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f
        })
        {
            canvas.DrawRoundRect(track, radius, radius, stroke);
        }

        float travel = Math.Max(1f, width - ThumbW);
        float hx = (ThumbW * 0.5f) + t * travel;
        float zeroX = (ThumbW * 0.5f) + zeroT * travel;

        // 2. Zero tick for centre-out sliders.
        if (bipolar && trackGradient == null)
        {
            using var tick = new SKPaint { Color = Theme.HairlineStrong, StrokeWidth = 1f, IsAntialias = false };
            float x = MathF.Round(zeroX) + 0.5f;
            canvas.DrawLine(x, trackTop + 2f, x, trackTop + TrackH - 2f, tick);
        }

        // 3. Inset fill bar.
        if (trackGradient == null)
        {
            float fillTop = cy - FillH * 0.5f;
            float fillLeft = bipolar ? Math.Min(hx, zeroX) : ThumbW * 0.5f;
            float fillRight = bipolar ? Math.Max(hx, zeroX) : hx;

            if (fillRight - fillLeft > 0.5f)
            {
                float ir = FillH * 0.5f;
                var fillRect = new SKRect(fillLeft, fillTop, fillRight, fillTop + FillH);
                if (lit > 0.01f)
                {
                    using var glowBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 2.2f);
                    using var glow = new SKPaint { Color = Theme.TrackFill.WithAlpha((byte)(70 * lit)), MaskFilter = glowBlur, IsAntialias = true };
                    canvas.DrawRoundRect(fillRect, ir, ir, glow);
                }
                using var fill = new SKPaint { Color = Anim.Lerp(Theme.TrackFill, Theme.AccentHover, lit * 0.6f), IsAntialias = true };
                canvas.DrawRoundRect(fillRect, ir, ir, fill);
            }
        }

        // 4. Proud vertical pill thumb.
        float thumbScale = active ? 1.10f : 1f + 0.05f * lit;
        float tw = ThumbW * thumbScale;
        float th = ThumbH * thumbScale;
        var pill = new SKRect(hx - tw * 0.5f, cy - th * 0.5f, hx + tw * 0.5f, cy + th * 0.5f);
        float pr = tw * 0.5f;

        if (active)
        {
            using var ringBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3f);
            using var ring = new SKPaint { Color = Theme.Accent.WithAlpha(90), MaskFilter = ringBlur, IsAntialias = true };
            var halo = pill;
            halo.Inflate(2f, 2f);
            canvas.DrawRoundRect(halo, pr + 2f, pr + 2f, ring);
        }

        using (var blur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1.2f))
        using (var shadow = new SKPaint
        {
            Color = new SKColor(0, 0, 0, (byte)(active ? 110 : 55 + 30 * lit)),
            IsAntialias = true,
            MaskFilter = blur
        })
        {
            var drop = pill;
            drop.Offset(0, 0.8f);
            canvas.DrawRoundRect(drop, pr, pr, shadow);
        }

        SKColor thumb = active ? Theme.HandleActive : Anim.Lerp(Theme.Handle, Theme.HandleHover, lit);
        using (var handle = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, pill.Top), new SKPoint(0, pill.Bottom),
                new[] { thumb, Theme.Darken(thumb, 26) },
                SKShaderTileMode.Clamp)
        })
        {
            canvas.DrawRoundRect(pill, pr, pr, handle);
        }

        using (var rim = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 70),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 0.75f
        })
        {
            canvas.DrawRoundRect(pill, pr, pr, rim);
        }

        // Grip line: a hint of the physical fader.
        using var grip = new SKPaint { Color = new SKColor(0, 0, 0, 55), StrokeWidth = 1f, IsAntialias = true };
        canvas.DrawLine(hx, cy - th * 0.18f, hx, cy + th * 0.18f, grip);
    }
}
