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

    public void ApplyAspect(float pixelAspect, int fw, int fh)
    {
        Aspect = pixelAspect;
        if (pixelAspect < 1e-4f || fw < 1 || fh < 1)
            return;

        float cx = X + W * 0.5f;
        float cy = Y + H * 0.5f;
        float normWH = pixelAspect * fh / (float)fw;
        float w, h;
        if (W / Math.Max(H, 1e-6f) > normWH)
        {
            w = H * normWH;
            h = H;
        }
        else
        {
            w = W;
            h = W / normWH;
        }

        if (w > 1f) { w = 1f; h = 1f / normWH; }
        if (h > 1f) { h = 1f; w = normWH; }

        X = Math.Clamp(cx - w * 0.5f, 0f, 1f - w);
        Y = Math.Clamp(cy - h * 0.5f, 0f, 1f - h);
        W = Math.Max(MinNorm, w);
        H = Math.Max(MinNorm, h);
        Clamp();
    }

    public bool UpdateDrag(
        float lx, float ly, SKRect imageDest, int fw, int fh,
        int rot = 0, float straightenDeg = 0f, bool flipH = false, bool flipV = false)
    {
        if (Active == CropHandle.None || imageDest.Width < 1f || imageDest.Height < 1f)
            return false;

        float dx = (lx - _downX) / imageDest.Width;
        float dy = (ly - _downY) / imageDest.Height;

        if (Active == CropHandle.Move)
        {
            float w = _origW;
            float h = _origH;
            float targetX = Math.Clamp(_origX + dx, 0f, 1f - w);
            float targetY = Math.Clamp(_origY + dy, 0f, 1f - h);

            if (RectFits(targetX, targetY, w, h, rot, straightenDeg, flipH, flipV, fw, fh))
            {
                if (targetX == X && targetY == Y)
                    return false;
                X = targetX;
                Y = targetY;
                return true;
            }

            bool xFits = RectFits(targetX, Y, w, h, rot, straightenDeg, flipH, flipV, fw, fh);
            bool yFits = RectFits(X, targetY, w, h, rot, straightenDeg, flipH, flipV, fw, fh);

            if (xFits && !yFits)
            {
                if (targetX == X) return false;
                X = targetX;
                return true;
            }
            if (yFits && !xFits)
            {
                if (targetY == Y) return false;
                Y = targetY;
                return true;
            }

            float low = 0f;
            float high = 1f;
            for (int i = 0; i < 8; i++)
            {
                float mid = (low + high) * 0.5f;
                float testX = Math.Clamp(_origX + dx * mid, 0f, 1f - w);
                float testY = Math.Clamp(_origY + dy * mid, 0f, 1f - h);
                if (RectFits(testX, testY, w, h, rot, straightenDeg, flipH, flipV, fw, fh))
                    low = mid;
                else
                    high = mid;
            }

            float bestX = Math.Clamp(_origX + dx * low, 0f, 1f - w);
            float bestY = Math.Clamp(_origY + dy * low, 0f, 1f - h);
            if (bestX == X && bestY == Y)
                return false;

            X = bestX;
            Y = bestY;
            return true;
        }

        // Resize handles
        float x = _origX;
        float y = _origY;
        float x2 = _origX + _origW;
        float y2 = _origY + _origH;

        switch (Active)
        {
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

        if (Active is CropHandle.W or CropHandle.NW or CropHandle.SW)
        {
            if (x > x2 - MinNorm) x = x2 - MinNorm;
        }
        else if (Active is CropHandle.E or CropHandle.NE or CropHandle.SE)
        {
            if (x2 < x + MinNorm) x2 = x + MinNorm;
        }

        if (Active is CropHandle.N or CropHandle.NW or CropHandle.NE)
        {
            if (y > y2 - MinNorm) y = y2 - MinNorm;
        }
        else if (Active is CropHandle.S or CropHandle.SW or CropHandle.SE)
        {
            if (y2 < y + MinNorm) y2 = y + MinNorm;
        }

        float rw = x2 - x;
        float rh = y2 - y;

        if (Aspect > 1e-4f && fw > 0 && fh > 0)
        {
            float normWH = Aspect * fh / (float)fw;
            switch (Active)
            {
                case CropHandle.N:
                case CropHandle.S:
                    rw = Math.Max(MinNorm, rh * normWH);
                    x = _origX + _origW * 0.5f - rw * 0.5f;
                    break;
                case CropHandle.E:
                case CropHandle.W:
                    rh = Math.Max(MinNorm, rw / normWH);
                    y = _origY + _origH * 0.5f - rh * 0.5f;
                    break;
                case CropHandle.SE:
                {
                    float targetW = Math.Max(MinNorm, _origW + dx);
                    float targetH = Math.Max(MinNorm, _origH + dy);
                    if (targetW / normWH > targetH)
                        rh = targetW / normWH;
                    else
                        rw = targetH * normWH;
                    x = _origX;
                    y = _origY;
                    break;
                }
                case CropHandle.SW:
                {
                    float targetW = Math.Max(MinNorm, _origW - dx);
                    float targetH = Math.Max(MinNorm, _origH + dy);
                    if (targetW / normWH > targetH)
                        rh = targetW / normWH;
                    else
                        rw = targetH * normWH;
                    x = (_origX + _origW) - rw;
                    y = _origY;
                    break;
                }
                case CropHandle.NE:
                {
                    float targetW = Math.Max(MinNorm, _origW + dx);
                    float targetH = Math.Max(MinNorm, _origH - dy);
                    if (targetW / normWH > targetH)
                        rh = targetW / normWH;
                    else
                        rw = targetH * normWH;
                    x = _origX;
                    y = (_origY + _origH) - rh;
                    break;
                }
                case CropHandle.NW:
                {
                    float targetW = Math.Max(MinNorm, _origW - dx);
                    float targetH = Math.Max(MinNorm, _origH - dy);
                    if (targetW / normWH > targetH)
                        rh = targetW / normWH;
                    else
                        rw = targetH * normWH;
                    x = (_origX + _origW) - rw;
                    y = (_origY + _origH) - rh;
                    break;
                }
            }
        }

        x = Math.Clamp(x, 0f, 1f - MinNorm);
        y = Math.Clamp(y, 0f, 1f - MinNorm);
        rw = Math.Clamp(rw, MinNorm, 1f - x);
        rh = Math.Clamp(rh, MinNorm, 1f - y);

        if (RectFits(x, y, rw, rh, rot, straightenDeg, flipH, flipV, fw, fh))
        {
            if (x == X && y == Y && rw == W && rh == H)
                return false;
            X = x;
            Y = y;
            W = rw;
            H = rh;
            return true;
        }

        float rLow = 0f;
        float rHigh = 1f;
        for (int i = 0; i < 10; i++)
        {
            float mid = (rLow + rHigh) * 0.5f;
            float testX = _origX + (x - _origX) * mid;
            float testY = _origY + (y - _origY) * mid;
            float testW = _origW + (rw - _origW) * mid;
            float testH = _origH + (rh - _origH) * mid;
            if (RectFits(testX, testY, testW, testH, rot, straightenDeg, flipH, flipV, fw, fh))
                rLow = mid;
            else
                rHigh = mid;
        }

        float finalX = _origX + (x - _origX) * rLow;
        float finalY = _origY + (y - _origY) * rLow;
        float finalW = _origW + (rw - _origW) * rLow;
        float finalH = _origH + (rh - _origH) * rLow;

        if (finalX == X && finalY == Y && finalW == W && finalH == H)
            return false;

        X = finalX;
        Y = finalY;
        W = finalW;
        H = finalH;
        return true;
    }

    public void EndDrag() => Active = CropHandle.None;

    public void RotateCrop(int dir)
    {
        if (dir == 1) // 90 CW
        {
            float ox = X, oy = Y, ow = W, oh = H;
            X = Math.Clamp(1f - (oy + oh), 0f, 1f);
            Y = Math.Clamp(ox, 0f, 1f);
            W = Math.Clamp(oh, MinNorm, 1f);
            H = Math.Clamp(ow, MinNorm, 1f);
        }
        else if (dir == -1) // 90 CCW
        {
            float ox = X, oy = Y, ow = W, oh = H;
            X = Math.Clamp(oy, 0f, 1f);
            Y = Math.Clamp(1f - (ox + ow), 0f, 1f);
            W = Math.Clamp(oh, MinNorm, 1f);
            H = Math.Clamp(ow, MinNorm, 1f);
        }
        if (Aspect > 1e-4f)
            Aspect = 1f / Aspect;
    }

    public void FlipCrop(bool h)
    {
        if (h)
            X = Math.Clamp(1f - (X + W), 0f, 1f);
        else
            Y = Math.Clamp(1f - (Y + H), 0f, 1f);
    }

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

        if (Math.Abs(straightenDeg) > 0.05f)
        {
            line.Color = new SKColor(255, 255, 255, 35);
            int n = 8;
            for (int i = 1; i < n; i++)
            {
                if (i % (n / 3) == 0) continue;
                float t = i / (float)n;
                float x = crop.Left + crop.Width * t;
                float y = crop.Top + crop.Height * t;
                canvas.DrawLine(x, crop.Top, x, crop.Bottom, line);
                canvas.DrawLine(crop.Left, y, crop.Right, y, line);
            }
        }
    }

    private static void DrawHandle(SKCanvas canvas, float cx, float cy, SKPaint paint)
    {
        float h = HandlePx * 0.5f;
        canvas.DrawRect(new SKRect(cx - h, cy - h, cx + h, cy + h), paint);
    }

    public bool ClampInside(int rot, float straightenDeg, bool flipH, bool flipV, int fw, int fh)
    {
        if (Fits(rot, straightenDeg, flipH, flipV, fw, fh))
            return true;

        float cx = X + W * 0.5f;
        float cy = Y + H * 0.5f;

        if (!RectFits(cx - MinNorm * 0.5f, cy - MinNorm * 0.5f, MinNorm, MinNorm, rot, straightenDeg, flipH, flipV, fw, fh))
        {
            for (int step = 0; step < 16; step++)
            {
                if (RectFits(cx - MinNorm * 0.5f, cy - MinNorm * 0.5f, MinNorm, MinNorm, rot, straightenDeg, flipH, flipV, fw, fh))
                    break;
                cx = cx * 0.85f + 0.5f * 0.15f;
                cy = cy * 0.85f + 0.5f * 0.15f;
            }
        }

        float low = 0.001f;
        float high = 1.0f;
        float origW = W;
        float origH = H;

        for (int iter = 0; iter < 16; iter++)
        {
            float mid = (low + high) * 0.5f;
            float testW = Math.Max(MinNorm, origW * mid);
            float testH = Math.Max(MinNorm, origH * mid);
            float testX = cx - testW * 0.5f;
            float testY = cy - testH * 0.5f;
            if (RectFits(testX, testY, testW, testH, rot, straightenDeg, flipH, flipV, fw, fh))
                low = mid;
            else
                high = mid;
        }

        W = Math.Max(MinNorm, origW * low);
        H = Math.Max(MinNorm, origH * low);
        X = cx - W * 0.5f;
        Y = cy - H * 0.5f;
        Clamp();
        return Fits(rot, straightenDeg, flipH, flipV, fw, fh);
    }

    public static bool RectFits(
        float x, float y, float w, float h,
        int rot, float straightenDeg, bool flipH, bool flipV, int fw, int fh)
    {
        if (x < -0.0001f || y < -0.0001f || x + w > 1.0001f || y + h > 1.0001f)
            return false;

        float[] xs = { x, x + w, x, x + w };
        float[] ys = { y, y, y + h, y + h };
        for (int i = 0; i < 4; i++)
        {
            MapDisplayToSource(xs[i], ys[i], rot, straightenDeg, flipH, flipV, fw, fh, out float sx, out float sy);
            if (sx < -0.001f || sx > 1.001f || sy < -0.001f || sy > 1.001f)
                return false;
        }
        return true;
    }

    public bool Fits(int rot, float straightenDeg, bool flipH, bool flipV, int fw, int fh) =>
        RectFits(X, Y, W, H, rot, straightenDeg, flipH, flipV, fw, fh);

    public static void MapDisplayToSource(
        float u, float v, int rot, float deg, bool flipH, bool flipV, int fw, int fh,
        out float sx, out float sy)
    {
        float p = u;
        float q = v;

        if (Math.Abs(deg) > 0.001f && fw > 0 && fh > 0)
        {
            float a = deg * 0.01745329251f;
            float c = MathF.Cos(a);
            float s = MathF.Sin(a);
            float qx = p - 0.5f;
            float qy = q - 0.5f;
            float invAspect = fh / (float)fw;
            float aspect = fw / (float)fh;
            p = qx * c - qy * invAspect * s + 0.5f;
            q = qx * aspect * s + qy * c + 0.5f;
        }

        rot &= 3;
        if (rot == 1) { float t = p; p = q; q = 1f - t; }
        else if (rot == 2) { p = 1f - p; q = 1f - q; }
        else if (rot == 3) { float t = p; p = 1f - q; q = t; }

        if (flipH) p = 1f - p;
        if (flipV) q = 1f - q;

        sx = p;
        sy = q;
    }

    private static bool NearEdge(float lx, float ly, SKRect r, float s) =>
        Math.Abs(lx - r.Left) <= s || Math.Abs(lx - r.Right) <= s
        || Math.Abs(ly - r.Top) <= s || Math.Abs(ly - r.Bottom) <= s;

    private static bool InHandle(float lx, float ly, float cx, float cy, float s) =>
        Math.Abs(lx - cx) <= s && Math.Abs(ly - cy) <= s;
}
