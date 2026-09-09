using System;
using Raw75.Develop;
using SkiaSharp;

namespace Raw75.Pipeline;

/// <summary>Matches the SKSL apply_geom + crop mapping (dest UV → source UV).</summary>
internal static class DevelopGeom
{
    public static SKRect VisibleSourceAabb(SKRect dest, SKRect vis, DevelopSettings? s, int fw = 0, int fh = 0)
    {
        if (dest.Width < 1f || dest.Height < 1f)
            return SKRect.Empty;

        var clip = vis;
        clip.Intersect(dest);
        if (clip.Width < 0.5f || clip.Height < 0.5f)
            return SKRect.Empty;

        float u0 = (clip.Left - dest.Left) / dest.Width;
        float v0 = (clip.Top - dest.Top) / dest.Height;
        float u1 = (clip.Right - dest.Left) / dest.Width;
        float v1 = (clip.Bottom - dest.Top) / dest.Height;

        float cx = 0f, cy = 0f, cw = 1f, ch = 1f;
        if (s != null && s.HasCrop)
        {
            cx = s.CropX;
            cy = s.CropY;
            cw = s.CropW;
            ch = s.CropH;
        }

        float minX = 1f, minY = 1f, maxX = 0f, maxY = 0f;
        Span<(float u, float v)> corners =
        [
            (u0, v0), (u1, v0), (u0, v1), (u1, v1)
        ];
        for (int i = 0; i < 4; i++)
        {
            float cu = cx + corners[i].u * cw;
            float cv = cy + corners[i].v * ch;
            MapSource(cu, cv, s, fw, fh, out float sx, out float sy);
            if (sx < minX) minX = sx;
            if (sy < minY) minY = sy;
            if (sx > maxX) maxX = sx;
            if (sy > maxY) maxY = sy;
        }

        minX = Math.Clamp(minX, 0f, 1f);
        minY = Math.Clamp(minY, 0f, 1f);
        maxX = Math.Clamp(maxX, 0f, 1f);
        maxY = Math.Clamp(maxY, 0f, 1f);
        if (maxX <= minX || maxY <= minY)
            return SKRect.Empty;
        return new SKRect(minX, minY, maxX, maxY);
    }

    public static void MapSource(float u, float v, DevelopSettings? s, out float sx, out float sy) =>
        MapSource(u, v, s, 0, 0, out sx, out sy);

    public static void MapSource(float u, float v, DevelopSettings? s, int fw, int fh, out float sx, out float sy)
    {
        float x = u;
        float y = v;

        if (s != null && Math.Abs(s.Straighten) > 0.001f && fw > 0 && fh > 0)
        {
            float a = s.Straighten * 0.01745329251f;
            float c = MathF.Cos(a);
            float sn = MathF.Sin(a);
            float qx = x - 0.5f;
            float qy = y - 0.5f;
            float invAspect = fh / (float)fw;
            float aspect = fw / (float)fh;
            x = qx * c - qy * invAspect * sn + 0.5f;
            y = qx * aspect * sn + qy * c + 0.5f;
        }

        int rot = s != null ? s.Rotate90 & 3 : 0;
        if (rot == 1)
        {
            float t = x;
            x = y;
            y = 1f - t;
        }
        else if (rot == 2)
        {
            x = 1f - x;
            y = 1f - y;
        }
        else if (rot == 3)
        {
            float t = x;
            x = 1f - y;
            y = t;
        }

        if (s != null && s.FlipH)
            x = 1f - x;
        if (s != null && s.FlipV)
            y = 1f - y;

        sx = x;
        sy = y;
    }

    public static void InverseMap(float sx, float sy, DevelopSettings? s, out float u, out float v) =>
        InverseMap(sx, sy, s, 0, 0, out u, out v);

    public static void InverseMap(float sx, float sy, DevelopSettings? s, int fw, int fh, out float u, out float v)
    {
        float x = sx;
        float y = sy;

        if (s != null && s.FlipV)
            y = 1f - y;
        if (s != null && s.FlipH)
            x = 1f - x;

        int rot = s != null ? s.Rotate90 & 3 : 0;
        if (rot == 1)
        {
            float t = x;
            x = 1f - y;
            y = t;
        }
        else if (rot == 2)
        {
            x = 1f - x;
            y = 1f - y;
        }
        else if (rot == 3)
        {
            float t = x;
            x = y;
            y = 1f - t;
        }

        if (s != null && Math.Abs(s.Straighten) > 0.001f && fw > 0 && fh > 0)
        {
            float a = s.Straighten * 0.01745329251f;
            float c = MathF.Cos(a);
            float sn = MathF.Sin(a);
            float qx = x - 0.5f;
            float qy = y - 0.5f;
            float invAspect = fh / (float)fw;
            float aspect = fw / (float)fh;
            x = qx * c + qy * invAspect * sn + 0.5f;
            y = -qx * aspect * sn + qy * c + 0.5f;
        }

        u = x;
        v = y;
    }

    public static SKRect SourceAabbToDest(SKRect source, SKRect dest, DevelopSettings? s, int fw = 0, int fh = 0)
    {
        if (dest.Width < 1f || dest.Height < 1f || source.Width < 1e-6f || source.Height < 1e-6f)
            return SKRect.Empty;

        float cx = 0f, cy = 0f, cw = 1f, ch = 1f;
        if (s != null && s.HasCrop)
        {
            cx = s.CropX;
            cy = s.CropY;
            cw = Math.Max(1e-6f, s.CropW);
            ch = Math.Max(1e-6f, s.CropH);
        }

        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        Span<(float x, float y)> corners =
        [
            (source.Left, source.Top), (source.Right, source.Top),
            (source.Left, source.Bottom), (source.Right, source.Bottom)
        ];
        for (int i = 0; i < 4; i++)
        {
            InverseMap(corners[i].x, corners[i].y, s, fw, fh, out float u, out float v);
            float dx = dest.Left + (u - cx) / cw * dest.Width;
            float dy = dest.Top + (v - cy) / ch * dest.Height;
            if (dx < minX) minX = dx;
            if (dy < minY) minY = dy;
            if (dx > maxX) maxX = dx;
            if (dy > maxY) maxY = dy;
        }

        if (maxX <= minX || maxY <= minY)
            return SKRect.Empty;
        return new SKRect(minX, minY, maxX, maxY);
    }
}
