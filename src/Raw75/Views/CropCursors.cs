using System;
using Blossom;
using Silk.NET.Core;
using Silk.NET.Input;
using SkiaSharp;

namespace Raw75.Views;

/// <summary>Crop-handle mouse cursors, including diagonal resize images Silk.NET does not ship.</summary>
internal static class CropCursors
{
    private static RawImage _nwse;
    private static RawImage _nesw;
    private static RawImage _rotate;
    private static bool _built;

    public static void Apply(CropHandle handle)
    {
        Ensure();
        switch (handle)
        {
            case CropHandle.N:
            case CropHandle.S:
                Browser.SetCursor(StandardCursor.VResize);
                break;
            case CropHandle.E:
            case CropHandle.W:
                Browser.SetCursor(StandardCursor.HResize);
                break;
            case CropHandle.NW:
            case CropHandle.SE:
                Browser.SetCustomCursor(_nwse, 16, 16);
                break;
            case CropHandle.NE:
            case CropHandle.SW:
                Browser.SetCustomCursor(_nesw, 16, 16);
                break;
            case CropHandle.Move:
                Browser.SetCursor(StandardCursor.Hand);
                break;
            case CropHandle.Rotate:
                Browser.SetCustomCursor(_rotate, 16, 16);
                break;
            default:
                Browser.SetCursor(StandardCursor.Default);
                break;
        }
    }

    public static void Reset() => Browser.SetCursor(StandardCursor.Default);

    private static void Ensure()
    {
        if (_built)
            return;
        _nwse = DrawDiagonal(nwse: true);
        _nesw = DrawDiagonal(nwse: false);
        _rotate = DrawRotate();
        _built = true;
    }

    private static RawImage DrawDiagonal(bool nwse)
    {
        const int s = 32;
        using var bmp = new SKBitmap(s, s, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);

        float x0, y0, x1, y1;
        if (nwse)
        {
            x0 = 7f; y0 = 7f; x1 = 25f; y1 = 25f;
        }
        else
        {
            x0 = 25f; y0 = 7f; x1 = 7f; y1 = 25f;
        }

        using var outline = new SKPaint
        {
            Color = SKColors.Black,
            IsAntialias = true,
            StrokeWidth = 5f,
            StrokeCap = SKStrokeCap.Round,
            Style = SKPaintStyle.Stroke
        };
        using var fill = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true,
            StrokeWidth = 2.4f,
            StrokeCap = SKStrokeCap.Round,
            Style = SKPaintStyle.Stroke
        };

        canvas.DrawLine(x0, y0, x1, y1, outline);
        DrawHead(canvas, x0, y0, x1, y1, outline, 7f);
        DrawHead(canvas, x1, y1, x0, y0, outline, 7f);
        canvas.DrawLine(x0, y0, x1, y1, fill);
        DrawHead(canvas, x0, y0, x1, y1, fill, 6f);
        DrawHead(canvas, x1, y1, x0, y0, fill, 6f);

        return ToRaw(bmp);
    }

    private static RawImage DrawRotate()
    {
        const int s = 32;
        using var bmp = new SKBitmap(s, s, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);

        var arc = new SKRect(7, 7, 25, 25);
        using var outline = new SKPaint
        {
            Color = SKColors.Black,
            IsAntialias = true,
            StrokeWidth = 5f,
            StrokeCap = SKStrokeCap.Round,
            Style = SKPaintStyle.Stroke
        };
        using var fill = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true,
            StrokeWidth = 2.4f,
            StrokeCap = SKStrokeCap.Round,
            Style = SKPaintStyle.Stroke
        };
        canvas.DrawArc(arc, 40, 260, false, outline);
        canvas.DrawArc(arc, 40, 260, false, fill);

        float a = 40f * 0.01745329251f;
        float cx = 16f + 9f * MathF.Cos(a);
        float cy = 16f + 9f * MathF.Sin(a);
        float tx = -MathF.Sin(a);
        float ty = MathF.Cos(a);
        DrawHead(canvas, cx, cy, cx - tx * 8f, cy - ty * 8f, outline, 7f);
        DrawHead(canvas, cx, cy, cx - tx * 8f, cy - ty * 8f, fill, 6f);

        return ToRaw(bmp);
    }

    private static void DrawHead(SKCanvas canvas, float tipX, float tipY, float fromX, float fromY, SKPaint paint, float size)
    {
        float dx = fromX - tipX;
        float dy = fromY - tipY;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1e-3f)
            return;
        dx /= len;
        dy /= len;
        float px = -dy;
        float py = dx;
        canvas.DrawLine(tipX, tipY, tipX + dx * size + px * size * 0.55f, tipY + dy * size + py * size * 0.55f, paint);
        canvas.DrawLine(tipX, tipY, tipX + dx * size - px * size * 0.55f, tipY + dy * size - py * size * 0.55f, paint);
    }

    private static RawImage ToRaw(SKBitmap bmp)
    {
        int n = bmp.Width * bmp.Height * 4;
        var pixels = new byte[n];
        int i = 0;
        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                SKColor c = bmp.GetPixel(x, y);
                pixels[i++] = c.Red;
                pixels[i++] = c.Green;
                pixels[i++] = c.Blue;
                pixels[i++] = c.Alpha;
            }
        }
        return new RawImage(bmp.Width, bmp.Height, pixels);
    }
}
