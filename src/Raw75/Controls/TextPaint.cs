using System;
using SkiaSharp;

namespace Raw75.Controls;

/// <summary>
/// Paint plus font for canvas text. SkiaSharp 3+ moved size, typeface, alignment and
/// hinting from <see cref="SKPaint"/> to <see cref="SKFont"/>; this keeps the two together
/// so custom-drawn controls can build one object per label.
/// </summary>
public sealed class TextPaint : IDisposable
{
    public SKPaint Paint { get; } = new() { IsAntialias = true };
    public SKFont Font { get; } = new() { Subpixel = true, Edging = SKFontEdging.Antialias, Hinting = SKFontHinting.Slight };

    public SKColor Color { get => Paint.Color; set => Paint.Color = value; }
    public bool IsAntialias { get => Paint.IsAntialias; set => Paint.IsAntialias = value; }
    public SKPaintStyle Style { get => Paint.Style; set => Paint.Style = value; }
    public float StrokeWidth { get => Paint.StrokeWidth; set => Paint.StrokeWidth = value; }
    public SKShader? Shader { get => Paint.Shader; set => Paint.Shader = value; }
    public SKMaskFilter? MaskFilter { get => Paint.MaskFilter; set => Paint.MaskFilter = value; }
    public SKImageFilter? ImageFilter { get => Paint.ImageFilter; set => Paint.ImageFilter = value; }

    public float TextSize { get => Font.Size; set => Font.Size = value; }
    public SKTypeface? Typeface { get => Font.Typeface; set => Font.Typeface = value; }
    public SKTextAlign TextAlign { get; set; } = SKTextAlign.Left;
    public bool SubpixelText { get => Font.Subpixel; set => Font.Subpixel = value; }
    public bool FakeBoldText { get => Font.Embolden; set => Font.Embolden = value; }
    public SKFontHinting HintingLevel { get => Font.Hinting; set => Font.Hinting = value; }

    /// <summary>LCD subpixel AA is wrong on translucent GPU layers; grayscale AA is kept either way.</summary>
    public bool LcdRenderText { get; set; }

    public SKFontMetrics FontMetrics => Font.Metrics;

    public float MeasureText(string? text) => string.IsNullOrEmpty(text) ? 0f : Font.MeasureText(text, Paint);

    public float MeasureText(string? text, ref SKRect bounds)
    {
        if (string.IsNullOrEmpty(text))
        {
            bounds = SKRect.Empty;
            return 0f;
        }
        return Font.MeasureText(text, out bounds, Paint);
    }

    public void Dispose()
    {
        Paint.Dispose();
        Font.Dispose();
    }
}

public static class TextPaintCanvasExtensions
{
    public static void DrawText(this SKCanvas canvas, string? text, float x, float y, TextPaint paint)
    {
        if (string.IsNullOrEmpty(text)) return;
        canvas.DrawText(text, x, y, paint.TextAlign, paint.Font, paint.Paint);
    }

    public static void DrawText(this SKCanvas canvas, string? text, SKPoint p, TextPaint paint)
        => canvas.DrawText(text, p.X, p.Y, paint);
}
