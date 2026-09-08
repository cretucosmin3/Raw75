using System;
using SkiaSharp;

namespace Raw75.Controls;

/// <summary>Inset orange fill + vertical pill thumb that sits proud of the track.</summary>
internal static class SliderChrome
{
    public const float TrackH = 16f;
    public const float FillH = 6f;
    public const float ThumbW = 11f;
    public const float ThumbH = 20f;

    public static void Draw(SKCanvas canvas, float width, float height, float t, float zeroT, bool bipolar, bool hover, bool active)
    {
        if (width < 4f || height < 4f)
            return;

        t = Math.Clamp(t, 0f, 1f);
        zeroT = Math.Clamp(zeroT, 0f, 1f);
        float cy = height * 0.5f;
        float trackTop = cy - TrackH * 0.5f;
        var track = new SKRect(0, trackTop, width, trackTop + TrackH);
        float radius = TrackH * 0.5f;

        SKColor trackFill = hover || active ? Theme.TrackHover : Theme.Track;
        using (var bg = new SKPaint { Color = trackFill, IsAntialias = true })
            canvas.DrawRoundRect(track, radius, radius, bg);

        using (var stroke = new SKPaint
        {
            Color = hover || active ? Theme.Lighten(Theme.Hairline, 36) : Theme.Hairline,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1
        })
            canvas.DrawRoundRect(track, radius, radius, stroke);

        float fillTop = cy - FillH * 0.5f;
        float insetX = 3f;
        var inner = new SKRect(track.Left + insetX, fillTop, track.Right - insetX, fillTop + FillH);
        if (inner.Width > 1f && inner.Height > 1f)
        {
            float fillLeft;
            float fillRight;
            if (bipolar)
            {
                fillLeft = inner.Left + inner.Width * Math.Min(t, zeroT);
                fillRight = inner.Left + inner.Width * Math.Max(t, zeroT);
            }
            else
            {
                fillLeft = inner.Left;
                fillRight = inner.Left + inner.Width * t;
            }

            if (fillRight - fillLeft > 0.5f)
            {
                float ir = inner.Height * 0.5f;
                using var fill = new SKPaint { Color = Theme.TrackFill, IsAntialias = true };
                canvas.DrawRoundRect(new SKRect(fillLeft, inner.Top, fillRight, inner.Bottom), ir, ir, fill);
            }
        }

        float hx = Math.Clamp(width * t, ThumbW * 0.5f, width - ThumbW * 0.5f);
        float thumbScale = active ? 1.08f : hover ? 1.04f : 1f;
        float tw = ThumbW * thumbScale;
        float th = ThumbH * thumbScale;
        var pill = new SKRect(hx - tw * 0.5f, cy - th * 0.5f, hx + tw * 0.5f, cy + th * 0.5f);
        float pr = tw * 0.5f;

        using (var blur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1.6f))
        using (var shadow = new SKPaint
        {
            Color = new SKColor(0, 0, 0, hover || active ? (byte)90 : (byte)50),
            IsAntialias = true,
            MaskFilter = blur
        })
            canvas.DrawRoundRect(pill, pr, pr, shadow);

        SKColor thumb = active ? Theme.HandleHover : hover ? Theme.HandleHover : Theme.Handle;
        using (var handle = new SKPaint { Color = thumb, IsAntialias = true })
            canvas.DrawRoundRect(pill, pr, pr, handle);
    }
}
