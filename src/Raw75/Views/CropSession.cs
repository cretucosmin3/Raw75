using System;
using SkiaSharp;

namespace Raw75.Views;

internal enum CropHandle
{
    None,
    Move,
    Rotate,
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

    /// <summary>Locked pixel aspect (width/height). 0 = free.</summary>
    public float Aspect { get; set; }

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

        if (InHandle(lx, ly, r.MidX, r.Top - 18f, s + 6f)) return CropHandle.Rotate;
        if (InHandle(lx, ly, r.Left, r.Top, s)) return CropHandle.NW;
        if (InHandle(lx, ly, r.Right, r.Top, s)) return CropHandle.NE;
        if (InHandle(lx, ly, r.Left, r.Bottom, s)) return CropHandle.SW;
        if (InHandle(lx, ly, r.Right, r.Bottom, s)) return CropHandle.SE;
        if (InHandle(lx, ly, r.MidX, r.Top, s)) return CropHandle.N;
        if (InHandle(lx, ly, r.MidX, r.Bottom, s)) return CropHandle.S;
        if (InHandle(lx, ly, r.Left, r.MidY, s)) return CropHandle.W;
        if (InHandle(lx, ly, r.Right, r.MidY, s)) return CropHandle.E;
        if (r.Contains(lx, ly) && !NearEdge(lx, ly, r, s + 2f))
            return CropHandle.Move;
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

    public void ApplyAspect(float pixelAspect, int imgW, int imgH)
    {
        Aspect = pixelAspect;
        if (pixelAspect < 1e-4f || imgW < 1 || imgH < 1)
            return;

        float cx = X + W * 0.5f;
        float cy = Y + H * 0.5f;
        float normWH = pixelAspect * imgH / imgW;
        float w, h;
        if (normWH >= 1f)
        {
            w = 1f;
            h = 1f / normWH;
        }
        else
        {
            h = 1f;
            w = normWH;
        }

        X = Math.Clamp(cx - w * 0.5f, 0f, 1f - w);
        Y = Math.Clamp(cy - h * 0.5f, 0f, 1f - h);
        W = w;
        H = h;
        Clamp();
    }

    public bool UpdateDrag(float lx, float ly, SKRect imageDest, int imgW, int imgH)
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
            if (Aspect > 1e-4f && imgW > 0 && imgH > 0)
                ConstrainAspect(ref x, ref y, ref w, ref h, imgW, imgH);
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

    private void ConstrainAspect(ref float x, ref float y, ref float w, ref float h, int imgW, int imgH)
    {
        float normWH = Aspect * imgH / (float)imgW;
        if (normWH < 1e-4f)
            return;

        float x2 = x + w;
        float y2 = y + h;
        switch (Active)
        {
            case CropHandle.N:
            case CropHandle.S:
                w = h * normWH;
                x = _origX + _origW * 0.5f - w * 0.5f;
                break;
            case CropHandle.E:
            case CropHandle.W:
                h = w / normWH;
                y = _origY + _origH * 0.5f - h * 0.5f;
                break;
            default:
                if (w / Math.Max(h, 1e-6f) > normWH)
                    w = h * normWH;
                else
                    h = w / normWH;
                if (Active == CropHandle.NE || Active == CropHandle.SE)
                    x = x2 - w;
                if (Active == CropHandle.NW || Active == CropHandle.NE)
                    y = y2 - h;
                break;
        }
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

        float hx = crop.MidX;
        float hy = crop.Top - 18f;
        using var rotLine = new SKPaint
        {
            Color = new SKColor(255, 153, 51, 230),
            IsAntialias = true,
            StrokeWidth = 1.5f,
            Style = SKPaintStyle.Stroke
        };
        canvas.DrawLine(crop.MidX, crop.Top, hx, hy, rotLine);
        using var rotDot = new SKPaint { Color = Theme.Accent, IsAntialias = true };
        canvas.DrawCircle(hx, hy, 6f, rotDot);
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

    public bool ClampInside(int rot, float straightenDeg, bool flipH, bool flipV)
    {
        if (Fits(rot, straightenDeg, flipH, flipV))
            return true;
        for (int i = 0; i < 24; i++)
        {
            float cx = X + W * 0.5f;
            float cy = Y + H * 0.5f;
            W = Math.Max(MinNorm, W * 0.92f);
            H = Math.Max(MinNorm, H * 0.92f);
            X = cx - W * 0.5f;
            Y = cy - H * 0.5f;
            Clamp();
            if (Fits(rot, straightenDeg, flipH, flipV))
                return true;
        }
        return Fits(rot, straightenDeg, flipH, flipV);
    }

    public bool Fits(int rot, float straightenDeg, bool flipH, bool flipV)
    {
        float[] xs = { X, X + W, X, X + W };
        float[] ys = { Y, Y, Y + H, Y + H };
        for (int i = 0; i < 4; i++)
        {
            MapDisplayToSource(xs[i], ys[i], rot, straightenDeg, flipH, flipV, out float sx, out float sy);
            if (sx < -0.001f || sx > 1.001f || sy < -0.001f || sy > 1.001f)
                return false;
        }
        return true;
    }

    public static void MapDisplayToSource(
        float u, float v, int rot, float deg, bool flipH, bool flipV,
        out float sx, out float sy)
    {
        float p = u, q = v;
        rot &= 3;
        if (rot == 1) { p = v; q = 1f - u; }
        else if (rot == 2) { p = 1f - u; q = 1f - v; }
        else if (rot == 3) { p = 1f - v; q = u; }
        if (flipH) p = 1f - p;
        if (flipV) q = 1f - q;
        if (Math.Abs(deg) > 0.05f)
        {
            float a = deg * (MathF.PI / 180f);
            float c = MathF.Cos(a);
            float s = MathF.Sin(a);
            float dx = p - 0.5f;
            float dy = q - 0.5f;
            p = dx * c - dy * s + 0.5f;
            q = dx * s + dy * c + 0.5f;
        }
        sx = p;
        sy = q;
    }

    private static bool NearEdge(float lx, float ly, SKRect r, float s) =>
        Math.Abs(lx - r.Left) <= s || Math.Abs(lx - r.Right) <= s
        || Math.Abs(ly - r.Top) <= s || Math.Abs(ly - r.Bottom) <= s;

    private static bool InHandle(float lx, float ly, float cx, float cy, float s) =>
        Math.Abs(lx - cx) <= s && Math.Abs(ly - cy) <= s;
}
