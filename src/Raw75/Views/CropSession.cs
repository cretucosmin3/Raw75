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
    public const float HandlePx = 14f;
    public const float HandleHit = 16f;
    public const float RotateOffset = 26f;
    public const float MinNorm = 0.02f;

    public float X;
    public float Y;
    public float W = 1f;
    public float H = 1f;

    /// <summary>Locked pixel aspect (width/height). 0 = free.</summary>
    public float Aspect { get; set; }

    /// <summary>Crop-bar id: free, orig, 1:1, 4:3, 3:2, 16:9.</summary>
    public string RatioId { get; set; } = "free";

    public CropHandle Active { get; private set; }
    public CropHandle Hover { get; set; }

    private float _downX;
    private float _downY;
    private float _origX, _origY, _origW, _origH;

    public bool HasLockedRatio => Aspect > 1e-4f && RatioId != "free";

    public void ResetFull()
    {
        X = 0f;
        Y = 0f;
        W = 1f;
        H = 1f;
        Aspect = 0f;
        RatioId = "free";
        Active = CropHandle.None;
        Hover = CropHandle.None;
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
        float s = HandleHit;

        if (InHandle(lx, ly, r.MidX, r.Top - RotateOffset, s + 4f)) return CropHandle.Rotate;
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

    public static float PixelAspectFor(string ratioId, int fw, int fh)
    {
        if (fw < 1) fw = 1;
        if (fh < 1) fh = 1;
        return ratioId switch
        {
            "orig" => fw / (float)fh,
            "1:1" => 1f,
            "4:3" => 4f / 3f,
            "3:2" => 3f / 2f,
            "16:9" => 16f / 9f,
            _ => 0f
        };
    }

    public void ApplyRatio(string ratioId, int fw, int fh)
    {
        RatioId = string.IsNullOrEmpty(ratioId) ? "free" : ratioId;
        ApplyAspect(PixelAspectFor(RatioId, fw, fh), fw, fh);
    }

    public void ApplyAspect(float pixelAspect, int fw, int fh)
    {
        Aspect = pixelAspect;
        if (pixelAspect < 1e-4f || fw < 1 || fh < 1)
        {
            Aspect = 0f;
            return;
        }

        float cx = X + W * 0.5f;
        float cy = Y + H * 0.5f;
        float normWH = NormWH(pixelAspect, fw, fh);
        FitCentered(cx, cy, Math.Max(W, MinNorm), Math.Max(H, MinNorm), normWH);
        Clamp();
    }

    public bool UpdateDrag(
        float lx, float ly, SKRect imageDest, int fw, int fh,
        int rot = 0, float straightenDeg = 0f, bool flipH = false, bool flipV = false,
        bool fromCenter = false)
    {
        if (Active == CropHandle.None || imageDest.Width < 1f || imageDest.Height < 1f)
            return false;

        float dx = (lx - _downX) / imageDest.Width;
        float dy = (ly - _downY) / imageDest.Height;

        if (Active == CropHandle.Move)
            return UpdateMove(dx, dy, rot, straightenDeg, flipH, flipV, fw, fh);

        float x = _origX;
        float y = _origY;
        float w = _origW;
        float h = _origH;
        float normWH = Aspect > 1e-4f && fw > 0 && fh > 0 ? NormWH(Aspect, fw, fh) : 0f;
        CropHandle anchor = fromCenter ? CropHandle.Move : Active;

        if (fromCenter)
            SizeFromCenter(dx, dy, normWH, out x, out y, out w, out h);
        else if (normWH > 1e-6f)
            SizeWithAspect(dx, dy, normWH, out x, out y, out w, out h);
        else
            SizeFree(dx, dy, out x, out y, out w, out h);

        x = Math.Clamp(x, 0f, 1f - MinNorm);
        y = Math.Clamp(y, 0f, 1f - MinNorm);
        w = Math.Clamp(w, MinNorm, 1f - x);
        h = Math.Clamp(h, MinNorm, 1f - y);

        if (normWH > 1e-6f)
            RefitAspect(ref x, ref y, ref w, ref h, normWH, anchor);

        if (!RectFits(x, y, w, h, rot, straightenDeg, flipH, flipV, fw, fh))
        {
            float rLow = 0f;
            float rHigh = 1f;
            for (int i = 0; i < 12; i++)
            {
                float mid = (rLow + rHigh) * 0.5f;
                float testX = _origX + (x - _origX) * mid;
                float testY = _origY + (y - _origY) * mid;
                float testW = _origW + (w - _origW) * mid;
                float testH = _origH + (h - _origH) * mid;
                if (normWH > 1e-6f)
                    RefitAspect(ref testX, ref testY, ref testW, ref testH, normWH, anchor);
                if (RectFits(testX, testY, testW, testH, rot, straightenDeg, flipH, flipV, fw, fh))
                    rLow = mid;
                else
                    rHigh = mid;
            }

            x = _origX + (x - _origX) * rLow;
            y = _origY + (y - _origY) * rLow;
            w = _origW + (w - _origW) * rLow;
            h = _origH + (h - _origH) * rLow;
            if (normWH > 1e-6f)
                RefitAspect(ref x, ref y, ref w, ref h, normWH, anchor);
        }

        if (x == X && y == Y && w == W && h == H)
            return false;

        X = x;
        Y = y;
        W = w;
        H = h;
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
            Color = Theme.Text,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.25f
        };
        canvas.DrawRect(crop, border);

        DrawGrid(canvas, crop, straightenDeg);

        DrawCornerBracket(canvas, crop.Left, crop.Top, 1f, 1f, Hot(CropHandle.NW));
        DrawCornerBracket(canvas, crop.Right, crop.Top, -1f, 1f, Hot(CropHandle.NE));
        DrawCornerBracket(canvas, crop.Left, crop.Bottom, 1f, -1f, Hot(CropHandle.SW));
        DrawCornerBracket(canvas, crop.Right, crop.Bottom, -1f, -1f, Hot(CropHandle.SE));

        DrawEdgeHandle(canvas, crop.MidX, crop.Top, horizontal: true, Hot(CropHandle.N));
        DrawEdgeHandle(canvas, crop.MidX, crop.Bottom, horizontal: true, Hot(CropHandle.S));
        DrawEdgeHandle(canvas, crop.Left, crop.MidY, horizontal: false, Hot(CropHandle.W));
        DrawEdgeHandle(canvas, crop.Right, crop.MidY, horizontal: false, Hot(CropHandle.E));

        float hx = crop.MidX;
        float hy = crop.Top - RotateOffset;
        using var rotLine = new SKPaint
        {
            Color = Theme.Accent,
            IsAntialias = true,
            StrokeWidth = 1.5f,
            Style = SKPaintStyle.Stroke
        };
        canvas.DrawLine(crop.MidX, crop.Top, hx, hy, rotLine);
        float rotR = Hot(CropHandle.Rotate) ? 8f : 7f;
        using var rotDot = new SKPaint { Color = Theme.Accent, IsAntialias = true };
        canvas.DrawCircle(hx, hy, rotR, rotDot);
        using var rotRing = new SKPaint
        {
            Color = Theme.Handle,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f
        };
        canvas.DrawCircle(hx, hy, rotR, rotRing);
    }

    public bool ClampInside(int rot, float straightenDeg, bool flipH, bool flipV, int fw, int fh)
    {
        float normWH = Aspect > 1e-4f && fw > 0 && fh > 0 ? NormWH(Aspect, fw, fh) : 0f;
        if (normWH > 1e-6f)
            RefitAspect(ref X, ref Y, ref W, ref H, normWH, CropHandle.Move);

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
            if (normWH > 1e-6f)
            {
                testH = testW / normWH;
                if (testH < MinNorm)
                {
                    testH = MinNorm;
                    testW = testH * normWH;
                }
            }
            float testX = cx - testW * 0.5f;
            float testY = cy - testH * 0.5f;
            if (RectFits(testX, testY, testW, testH, rot, straightenDeg, flipH, flipV, fw, fh))
                low = mid;
            else
                high = mid;
        }

        W = Math.Max(MinNorm, origW * low);
        H = Math.Max(MinNorm, origH * low);
        if (normWH > 1e-6f)
        {
            H = W / normWH;
            if (H < MinNorm)
            {
                H = MinNorm;
                W = H * normWH;
            }
        }
        X = cx - W * 0.5f;
        Y = cy - H * 0.5f;
        if (normWH > 1e-6f)
            RefitAspect(ref X, ref Y, ref W, ref H, normWH, CropHandle.Move);
        else
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

    private bool UpdateMove(
        float dx, float dy,
        int rot, float straightenDeg, bool flipH, bool flipV, int fw, int fh)
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

    private void SizeFree(float dx, float dy, out float x, out float y, out float w, out float h)
    {
        float x1 = _origX;
        float y1 = _origY;
        float x2 = _origX + _origW;
        float y2 = _origY + _origH;

        switch (Active)
        {
            case CropHandle.N: y1 = _origY + dy; break;
            case CropHandle.S: y2 = _origY + _origH + dy; break;
            case CropHandle.W: x1 = _origX + dx; break;
            case CropHandle.E: x2 = _origX + _origW + dx; break;
            case CropHandle.NW: x1 = _origX + dx; y1 = _origY + dy; break;
            case CropHandle.NE: x2 = _origX + _origW + dx; y1 = _origY + dy; break;
            case CropHandle.SW: x1 = _origX + dx; y2 = _origY + _origH + dy; break;
            case CropHandle.SE: x2 = _origX + _origW + dx; y2 = _origY + _origH + dy; break;
        }

        if (x2 < x1 + MinNorm) { if (Active is CropHandle.W or CropHandle.NW or CropHandle.SW) x1 = x2 - MinNorm; else x2 = x1 + MinNorm; }
        if (y2 < y1 + MinNorm) { if (Active is CropHandle.N or CropHandle.NW or CropHandle.NE) y1 = y2 - MinNorm; else y2 = y1 + MinNorm; }

        x = x1;
        y = y1;
        w = x2 - x1;
        h = y2 - y1;
    }

    private bool Hot(CropHandle handle) => Hover == handle || Active == handle;

    private void SizeFromCenter(float dx, float dy, float normWH, out float x, out float y, out float w, out float h)
    {
        float cx = _origX + _origW * 0.5f;
        float cy = _origY + _origH * 0.5f;
        float dw = 0f;
        float dh = 0f;
        switch (Active)
        {
            case CropHandle.E: dw = dx; break;
            case CropHandle.W: dw = -dx; break;
            case CropHandle.S: dh = dy; break;
            case CropHandle.N: dh = -dy; break;
            case CropHandle.SE: dw = dx; dh = dy; break;
            case CropHandle.NE: dw = dx; dh = -dy; break;
            case CropHandle.SW: dw = -dx; dh = dy; break;
            case CropHandle.NW: dw = -dx; dh = -dy; break;
        }

        w = Math.Max(MinNorm, _origW + 2f * dw);
        h = Math.Max(MinNorm, _origH + 2f * dh);

        if (normWH > 1e-6f)
        {
            if (Active is CropHandle.N or CropHandle.S)
                w = h * normWH;
            else if (Active is CropHandle.E or CropHandle.W)
                h = w / normWH;
            else
            {
                float hFromW = w / normWH;
                if (hFromW >= h)
                    h = hFromW;
                else
                    w = h * normWH;
            }
        }

        float maxW = Math.Max(MinNorm, 2f * Math.Min(cx, 1f - cx));
        float maxH = Math.Max(MinNorm, 2f * Math.Min(cy, 1f - cy));
        if (normWH > 1e-6f)
        {
            float maxHFromW = maxW / normWH;
            if (maxHFromW < maxH)
                maxH = maxHFromW;
            else
                maxW = maxH * normWH;
        }
        if (w > maxW)
        {
            w = maxW;
            if (normWH > 1e-6f)
                h = w / normWH;
        }
        if (h > maxH)
        {
            h = maxH;
            if (normWH > 1e-6f)
                w = h * normWH;
        }

        x = cx - w * 0.5f;
        y = cy - h * 0.5f;
    }

    private void SizeWithAspect(float dx, float dy, float normWH, out float x, out float y, out float w, out float h)
    {
        float mouseX = _origX + (Active is CropHandle.W or CropHandle.NW or CropHandle.SW ? dx : _origW + dx);
        float mouseY = _origY + (Active is CropHandle.N or CropHandle.NW or CropHandle.NE ? dy : _origH + dy);

        switch (Active)
        {
            case CropHandle.SE:
                SizeFromCorner(_origX, _origY, mouseX, mouseY, normWH, flipX: false, flipY: false, out x, out y, out w, out h);
                return;
            case CropHandle.SW:
                SizeFromCorner(_origX + _origW, _origY, mouseX, mouseY, normWH, flipX: true, flipY: false, out x, out y, out w, out h);
                return;
            case CropHandle.NE:
                SizeFromCorner(_origX, _origY + _origH, mouseX, mouseY, normWH, flipX: false, flipY: true, out x, out y, out w, out h);
                return;
            case CropHandle.NW:
                SizeFromCorner(_origX + _origW, _origY + _origH, mouseX, mouseY, normWH, flipX: true, flipY: true, out x, out y, out w, out h);
                return;
            case CropHandle.E:
            case CropHandle.W:
            {
                float left = Active == CropHandle.W ? mouseX : _origX;
                float right = Active == CropHandle.E ? mouseX : _origX + _origW;
                w = Math.Max(MinNorm, right - left);
                h = w / normWH;
                float cy = _origY + _origH * 0.5f;
                x = Active == CropHandle.W ? right - w : left;
                y = cy - h * 0.5f;
                RefitAspect(ref x, ref y, ref w, ref h, normWH, Active);
                return;
            }
            default:
            {
                float top = Active == CropHandle.N ? mouseY : _origY;
                float bottom = Active == CropHandle.S ? mouseY : _origY + _origH;
                h = Math.Max(MinNorm, bottom - top);
                w = h * normWH;
                float cx = _origX + _origW * 0.5f;
                y = Active == CropHandle.N ? bottom - h : top;
                x = cx - w * 0.5f;
                RefitAspect(ref x, ref y, ref w, ref h, normWH, Active);
                return;
            }
        }
    }

    private static void SizeFromCorner(
        float fixedX, float fixedY, float mouseX, float mouseY, float normWH,
        bool flipX, bool flipY, out float x, out float y, out float w, out float h)
    {
        float dx = flipX ? fixedX - mouseX : mouseX - fixedX;
        float dy = flipY ? fixedY - mouseY : mouseY - fixedY;
        dx = Math.Max(MinNorm, dx);
        dy = Math.Max(MinNorm, dy);

        float hFromW = dx / normWH;
        if (hFromW >= dy)
        {
            w = dx;
            h = hFromW;
        }
        else
        {
            h = dy;
            w = dy * normWH;
        }

        float maxW = flipX ? fixedX : 1f - fixedX;
        float maxH = flipY ? fixedY : 1f - fixedY;
        if (w > maxW) { w = maxW; h = w / normWH; }
        if (h > maxH) { h = maxH; w = h * normWH; }
        if (w > maxW) { w = maxW; h = w / normWH; }
        w = Math.Max(MinNorm, w);
        h = Math.Max(MinNorm, w / normWH);

        x = flipX ? fixedX - w : fixedX;
        y = flipY ? fixedY - h : fixedY;
    }

    private void RefitAspect(ref float x, ref float y, ref float w, ref float h, float normWH, CropHandle handle)
    {
        w = Math.Clamp(w, MinNorm, 1f);
        h = Math.Max(MinNorm, w / normWH);
        if (h > 1f)
        {
            h = 1f;
            w = Math.Max(MinNorm, h * normWH);
        }
        if (w > 1f)
        {
            w = 1f;
            h = Math.Max(MinNorm, w / normWH);
        }

        bool lockLeft = handle is CropHandle.E or CropHandle.NE or CropHandle.SE;
        bool lockRight = handle is CropHandle.W or CropHandle.NW or CropHandle.SW;
        bool lockTop = handle is CropHandle.S or CropHandle.SW or CropHandle.SE;
        bool lockBottom = handle is CropHandle.N or CropHandle.NW or CropHandle.NE;

        if (lockLeft) x = Math.Clamp(x, 0f, 1f - w);
        else if (lockRight) x = Math.Clamp(x + w, w, 1f) - w;
        else x = Math.Clamp(x + w * 0.5f, w * 0.5f, 1f - w * 0.5f) - w * 0.5f;

        if (lockTop) y = Math.Clamp(y, 0f, 1f - h);
        else if (lockBottom) y = Math.Clamp(y + h, h, 1f) - h;
        else y = Math.Clamp(y + h * 0.5f, h * 0.5f, 1f - h * 0.5f) - h * 0.5f;

        if (x < 0f) { w += x; x = 0f; h = w / normWH; }
        if (y < 0f) { h += y; y = 0f; w = h * normWH; }
        if (x + w > 1f) { w = 1f - x; h = w / normWH; }
        if (y + h > 1f) { h = 1f - y; w = h * normWH; }
        if (x < 0f) { w += x; x = 0f; h = w / normWH; }
        if (y < 0f) { h += y; y = 0f; w = h * normWH; }

        w = Math.Clamp(w, MinNorm, 1f);
        h = Math.Max(MinNorm, w / normWH);
        x = Math.Clamp(x, 0f, 1f - w);
        y = Math.Clamp(y, 0f, 1f - h);
    }

    private void FitCentered(float cx, float cy, float w, float h, float normWH)
    {
        if (w / Math.Max(h, 1e-6f) > normWH)
            w = h * normWH;
        else
            h = w / normWH;

        if (w > 1f) { w = 1f; h = w / normWH; }
        if (h > 1f) { h = 1f; w = h * normWH; }
        if (w > 1f) { w = 1f; h = w / normWH; }

        w = Math.Max(MinNorm, w);
        h = Math.Max(MinNorm, w / normWH);
        X = Math.Clamp(cx - w * 0.5f, 0f, 1f - w);
        Y = Math.Clamp(cy - h * 0.5f, 0f, 1f - h);
        W = w;
        H = h;
    }

    private static float NormWH(float pixelAspect, int fw, int fh) =>
        pixelAspect * fh / (float)Math.Max(fw, 1);

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

    private static void DrawCornerBracket(SKCanvas canvas, float cx, float cy, float dirX, float dirY, bool hover)
    {
        float arm = hover ? 20f : 16f;
        using var p = new SKPaint
        {
            Color = hover ? Theme.Accent : Theme.Handle,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = hover ? 3.5f : 3f,
            StrokeCap = SKStrokeCap.Square
        };
        canvas.DrawLine(cx, cy, cx + dirX * arm, cy, p);
        canvas.DrawLine(cx, cy, cx, cy + dirY * arm, p);
    }

    private static void DrawEdgeHandle(SKCanvas canvas, float cx, float cy, bool horizontal, bool hover)
    {
        float longA = hover ? 16f : 12f;
        float shortA = hover ? 5f : 4f;
        using var fill = new SKPaint
        {
            Color = hover ? Theme.Accent : Theme.Handle,
            IsAntialias = true
        };
        SKRect r = horizontal
            ? new SKRect(cx - longA, cy - shortA, cx + longA, cy + shortA)
            : new SKRect(cx - shortA, cy - longA, cx + shortA, cy + longA);
        canvas.DrawRoundRect(r, 2f, 2f, fill);
    }

    private static bool NearEdge(float lx, float ly, SKRect r, float s) =>
        Math.Abs(lx - r.Left) <= s || Math.Abs(lx - r.Right) <= s
        || Math.Abs(ly - r.Top) <= s || Math.Abs(ly - r.Bottom) <= s;

    private static bool InHandle(float lx, float ly, float cx, float cy, float s) =>
        Math.Abs(lx - cx) <= s && Math.Abs(ly - cy) <= s;
}
