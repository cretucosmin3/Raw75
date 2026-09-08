using System;
using SkiaSharp;

namespace Raw75.Views;

internal enum CropHandle
{
    None,
    Move,
    N,
    S,
    E,
    W,
    NW,
    NE,
    SW,
    SE
}

/// <summary>Normalized crop rect (0–1 in image space) and handle interaction.</summary>
internal sealed class CropSession
{
    public const float HandlePx = 8f;
    public const float MinNorm = 0.02f;

    public float X;
    public float Y;
    public float W = 1f;
    public float H = 1f;

    public CropHandle Active { get; private set; }

    private float _downX;
    private float _downY;
    private float _origX, _origY, _origW, _origH;

    public void ResetFull()
    {
        X = 0f;
        Y = 0f;
        W = 1f;
        H = 1f;
        Active = CropHandle.None;
    }

    public void Clamp()
    {
        W = Math.Clamp(W, MinNorm, 1f);
        H = Math.Clamp(H, MinNorm, 1f);
        X = Math.Clamp(X, 0f, 1f - W);
        Y = Math.Clamp(Y, 0f, 1f - H);
    }

    public SKRect DestOnImage(SKRect imageDest)
    {
        Clamp();
        return new SKRect(
            imageDest.Left + X * imageDest.Width,
            imageDest.Top + Y * imageDest.Height,
            imageDest.Left + (X + W) * imageDest.Width,
            imageDest.Top + (Y + H) * imageDest.Height);
    }

    public CropHandle Hit(SKRect imageDest, float lx, float ly)
    {
        var r = DestOnImage(imageDest);
        float s = HandlePx + 2f;

        if (InHandle(lx, ly, r.Left, r.Top, s)) return CropHandle.NW;
        if (InHandle(lx, ly, r.Right, r.Top, s)) return CropHandle.NE;
        if (InHandle(lx, ly, r.Left, r.Bottom, s)) return CropHandle.SW;
        if (InHandle(lx, ly, r.Right, r.Bottom, s)) return CropHandle.SE;
        if (InHandle(lx, ly, r.MidX, r.Top, s)) return CropHandle.N;
        if (InHandle(lx, ly, r.MidX, r.Bottom, s)) return CropHandle.S;
        if (InHandle(lx, ly, r.Left, r.MidY, s)) return CropHandle.W;
        if (InHandle(lx, ly, r.Right, r.MidY, s)) return CropHandle.E;
        if (r.Contains(lx, ly)) return CropHandle.Move;
        return CropHandle.None;
    }

    public void BeginDrag(CropHandle handle, float lx, float ly)
    {
        Active = handle;
        _downX = lx;
        _downY = ly;
        _origX = X;
        _origY = Y;
        _origW = W;
        _origH = H;
    }

    public bool UpdateDrag(float lx, float ly, SKRect imageDest)
    {
        if (Active == CropHandle.None || imageDest.Width < 1f || imageDest.Height < 1f)
            return false;

        float dx = (lx - _downX) / imageDest.Width;
        float dy = (ly - _downY) / imageDest.Height;
        float x = _origX, y = _origY, w = _origW, h = _origH;
        float x2 = _origX + _origW;
        float y2 = _origY + _origH;

        switch (Active)
        {
            case CropHandle.Move:
                x = _origX + dx;
                y = _origY + dy;
                break;
            case CropHandle.N:
                y = _origY + dy;
                break;
            case CropHandle.S:
                y2 = _origY + _origH + dy;
                break;
            case CropHandle.W:
                x = _origX + dx;
                break;
            case CropHandle.E:
                x2 = _origX + _origW + dx;
                break;
            case CropHandle.NW:
                x = _origX + dx;
                y = _origY + dy;
                break;
            case CropHandle.NE:
                x2 = _origX + _origW + dx;
                y = _origY + dy;
                break;
            case CropHandle.SW:
                x = _origX + dx;
                y2 = _origY + _origH + dy;
                break;
            case CropHandle.SE:
                x2 = _origX + _origW + dx;
                y2 = _origY + _origH + dy;
                break;
        }

        if (Active != CropHandle.Move)
        {
            if (x > x2 - MinNorm) x = x2 - MinNorm;
            if (y > y2 - MinNorm) y = y2 - MinNorm;
            w = x2 - x;
            h = y2 - y;
        }

        x = Math.Clamp(x, 0f, 1f);
        y = Math.Clamp(y, 0f, 1f);
        w = Math.Clamp(w, MinNorm, 1f - x);
        h = Math.Clamp(h, MinNorm, 1f - y);
        x = Math.Clamp(x, 0f, 1f - w);
        y = Math.Clamp(y, 0f, 1f - h);

        if (x == X && y == Y && w == W && h == H)
            return false;

        X = x;
        Y = y;
        W = w;
        H = h;
        return true;
    }

    public void EndDrag() => Active = CropHandle.None;

    public void Draw(SKCanvas canvas, SKRect imageDest, SKRect pane, float straightenDeg)
    {
        var crop = DestOnImage(imageDest);
        using var dim = new SKPaint { Color = new SKColor(0, 0, 0, 150), IsAntialias = false };
        canvas.DrawRect(new SKRect(pane.Left, pane.Top, pane.Right, crop.Top), dim);
        canvas.DrawRect(new SKRect(pane.Left, crop.Bottom, pane.Right, pane.Bottom), dim);
        canvas.DrawRect(new SKRect(pane.Left, crop.Top, crop.Left, crop.Bottom), dim);
        canvas.DrawRect(new SKRect(crop.Right, crop.Top, pane.Right, crop.Bottom), dim);

        using var border = new SKPaint
        {
            Color = new SKColor(240, 240, 240, 220),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1
        };
        canvas.DrawRect(crop, border);

        DrawGrid(canvas, crop, straightenDeg);

        using var handle = new SKPaint { Color = SKColors.White, IsAntialias = false };
        DrawHandle(canvas, crop.Left, crop.Top, handle);
        DrawHandle(canvas, crop.Right, crop.Top, handle);
        DrawHandle(canvas, crop.Left, crop.Bottom, handle);
        DrawHandle(canvas, crop.Right, crop.Bottom, handle);
        DrawHandle(canvas, crop.MidX, crop.Top, handle);
        DrawHandle(canvas, crop.MidX, crop.Bottom, handle);
        DrawHandle(canvas, crop.Left, crop.MidY, handle);
        DrawHandle(canvas, crop.Right, crop.MidY, handle);
    }

    private static void DrawGrid(SKCanvas canvas, SKRect crop, float straightenDeg)
    {
        using var line = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 70),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1
        };

        for (int i = 1; i <= 2; i++)
        {
            float t = i / 3f;
            float x = crop.Left + crop.Width * t;
            float y = crop.Top + crop.Height * t;
            canvas.DrawLine(x, crop.Top, x, crop.Bottom, line);
            canvas.DrawLine(crop.Left, y, crop.Right, y, line);
        }

        if (Math.Abs(straightenDeg) < 0.05f)
            return;

        canvas.Save();
        canvas.RotateDegrees(straightenDeg, crop.MidX, crop.MidY);
        line.Color = new SKColor(255, 255, 255, 40);
        float span = Math.Max(crop.Width, crop.Height) * 1.6f;
        int n = 12;
        for (int i = 0; i <= n; i++)
        {
            float t = i / (float)n;
            float x = crop.MidX - span * 0.5f + span * t;
            float y = crop.MidY - span * 0.5f + span * t;
            canvas.DrawLine(x, crop.MidY - span, x, crop.MidY + span, line);
            canvas.DrawLine(crop.MidX - span, y, crop.MidX + span, y, line);
        }
        canvas.Restore();
    }

    private static void DrawHandle(SKCanvas canvas, float cx, float cy, SKPaint paint)
    {
        float h = HandlePx * 0.5f;
        canvas.DrawRect(new SKRect(cx - h, cy - h, cx + h, cy + h), paint);
    }

    private static bool InHandle(float lx, float ly, float cx, float cy, float s) =>
        Math.Abs(lx - cx) <= s && Math.Abs(ly - cy) <= s;
}
