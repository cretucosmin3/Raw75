using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Raw75.Develop;
using Raw75.Imaging;
using SkiaSharp;

namespace Raw75.Pipeline;

/// <summary>
/// Coverage of the enabled masks, baked at source aspect: one Gray8 tile per mask, stacked vertically.
/// Tile <c>i</c> is the <c>i</c>-th enabled mask (the order <see cref="MaskRaster.Slots"/> returns).
/// Immutable once built, so the GPU look and CPU develop threads can share it.
/// </summary>
internal sealed class MaskAtlas
{
    public required byte[] Pixels { get; init; }
    public required int Width { get; init; }
    public required int TileHeight { get; init; }
    public required int Count { get; init; }
    public required int Signature { get; init; }

    /// <summary>Bilinear coverage (0–1) of tile <paramref name="tile"/> at source UV (u, v).</summary>
    public float Sample(int tile, float u, float v)
    {
        float fx = Math.Clamp(u * Width - 0.5f, 0f, Width - 1f);
        float fy = Math.Clamp(v * TileHeight - 0.5f, 0f, TileHeight - 1f);
        int x0 = (int)fx;
        int y0 = (int)fy;
        int x1 = Math.Min(x0 + 1, Width - 1);
        int y1 = Math.Min(y0 + 1, TileHeight - 1);
        float tx = fx - x0;
        float ty = fy - y0;
        int row0 = (tile * TileHeight + y0) * Width;
        int row1 = (tile * TileHeight + y1) * Width;
        float a = Pixels[row0 + x0] + (Pixels[row0 + x1] - Pixels[row0 + x0]) * tx;
        float b = Pixels[row1 + x0] + (Pixels[row1 + x1] - Pixels[row1 + x0]) * tx;
        return (a + (b - a) * ty) * (1f / 255f);
    }
}

/// <summary>
/// Turns <see cref="MaskLayer"/> shapes into coverage (<see cref="MaskAtlas"/>) and binds them to the
/// develop shaders. Radial, linear and rectangle masks are evaluated analytically; splines are filled
/// with Skia and blurred by their feather.
/// </summary>
internal static class MaskRaster
{
    /// <summary>Long side of the atlas used by the settled GPU look.</summary>
    public const int GpuLongSide = 1280;

    /// <summary>Long side while a slider or handle is being dragged.</summary>
    public const int FastLongSide = 400;

    private const int CacheSize = 4;
    private static readonly object Gate = new();
    private static readonly List<(int Key, MaskAtlas Atlas)> Cache = new();
    private static readonly MaskAtlas?[] ImageAtlas = new MaskAtlas?[2];
    private static readonly SKImage?[] ImageCache = new SKImage?[2];
    private static int _imageNext;

    public static bool HasMasks(DevelopSettings? s)
    {
        if (s == null || !s.EnableMasks || s.Masks == null)
            return false;
        for (int i = 0; i < s.Masks.Count; i++)
        {
            if (IsActive(s.Masks[i]))
                return true;
        }
        return false;
    }

    private static bool IsActive(MaskLayer m) =>
        m != null && m.Enabled && m.Amount > 0.01f && (m.Shape != MaskShape.Spline || (m.Points?.Count ?? 0) >= 3);

    /// <summary>Indexes into <see cref="DevelopSettings.Masks"/> of the masks that render, in tile order.</summary>
    public static List<int> Slots(DevelopSettings? s)
    {
        var slots = new List<int>(MaskLayer.MaxMasks);
        if (s == null || !s.EnableMasks || s.Masks == null)
            return slots;
        for (int i = 0; i < s.Masks.Count && slots.Count < MaskLayer.MaxMasks; i++)
        {
            if (IsActive(s.Masks[i]))
                slots.Add(i);
        }
        return slots;
    }

    /// <summary>Atlas dimensions for a source of <paramref name="aspect"/> (width / height).</summary>
    public static void TileSize(float aspect, int longSide, out int w, out int h)
    {
        aspect = aspect > 0.01f ? aspect : 1f;
        if (aspect >= 1f)
        {
            w = longSide;
            h = Math.Max(1, (int)MathF.Round(longSide / aspect));
        }
        else
        {
            h = longSide;
            w = Math.Max(1, (int)MathF.Round(longSide * aspect));
        }
    }

    /// <summary>The coverage atlas for <paramref name="s"/>, or null when no mask renders. Thread-safe, cached.</summary>
    public static MaskAtlas? Get(DevelopSettings? s, float aspect, int longSide)
    {
        var slots = Slots(s);
        if (slots.Count == 0)
            return null;

        var hash = new HashCode();
        hash.Add(MathF.Round(aspect * 1000f));
        hash.Add(longSide);
        foreach (int i in slots)
            hash.Add(s!.Masks[i].CoverageHash());
        int sig = hash.ToHashCode();

        lock (Gate)
        {
            for (int i = 0; i < Cache.Count; i++)
            {
                if (Cache[i].Key == sig)
                    return Cache[i].Atlas;
            }
        }

        TileSize(aspect, longSide, out int w, out int h);
        var pixels = new byte[w * h * slots.Count];
        for (int t = 0; t < slots.Count; t++)
            Rasterize(s!.Masks[slots[t]], aspect, w, h, pixels, t * w * h);

        var atlas = new MaskAtlas { Pixels = pixels, Width = w, TileHeight = h, Count = slots.Count, Signature = sig };
        lock (Gate)
        {
            Cache.Insert(0, (sig, atlas));
            if (Cache.Count > CacheSize)
                Cache.RemoveAt(Cache.Count - 1);
        }
        return atlas;
    }

    private static (int Key, MaskAtlas Atlas)? _single;

    /// <summary>Coverage of one mask on its own (any state, for the selection overlay). Cached, thread-safe.</summary>
    public static MaskAtlas Single(MaskLayer m, float aspect, int longSide)
    {
        int sig = HashCode.Combine(m.CoverageHash(), MathF.Round(aspect * 1000f), longSide);
        lock (Gate)
        {
            if (_single is { } hit && hit.Key == sig)
                return hit.Atlas;
        }
        TileSize(aspect, longSide, out int w, out int h);
        var pixels = new byte[w * h];
        Rasterize(m, aspect, w, h, pixels, 0);
        var atlas = new MaskAtlas { Pixels = pixels, Width = w, TileHeight = h, Count = 1, Signature = sig };
        lock (Gate)
            _single = (sig, atlas);
        return atlas;
    }

    /// <summary>Coverage of one mask into <paramref name="dst"/>[offset..offset + w*h].</summary>
    public static void Rasterize(MaskLayer m, float aspect, int w, int h, byte[] dst, int offset)
    {
        switch (m.Shape)
        {
            case MaskShape.Spline:
                RasterizeSpline(m, w, h, dst, offset);
                return;
            case MaskShape.Linear:
                RasterizeLinear(m, aspect, w, h, dst, offset);
                return;
            default:
                RasterizeBox(m, aspect, w, h, dst, offset);
                return;
        }
    }

    /// <summary>Radial (ellipse) and rectangle: the feather fades inward from the edge.</summary>
    private static void RasterizeBox(MaskLayer m, float aspect, int w, int h, byte[] dst, int offset)
    {
        float cx = m.CenterX * aspect;
        float cy = m.CenterY;
        float rx = Math.Max(m.RadiusX, MaskLayer.MinRadius);
        float ry = Math.Max(m.RadiusY, MaskLayer.MinRadius);
        float a = m.Angle * (MathF.PI / 180f);
        float cos = MathF.Cos(a);
        float sin = MathF.Sin(a);
        float feather = Math.Clamp(m.Feather / 100f, 0f, 1f);
        // One isotropic unit spans h pixels; keep at least ~1.5 px of antialiasing.
        float aa = 1.5f / (h * Math.Min(rx, ry));
        bool radial = m.Shape == MaskShape.Radial;
        float inner = Math.Min(1f - feather, 1f - aa);
        float fade = Math.Max(feather * Math.Min(rx, ry), 1.5f / h);
        float invW = aspect / w;
        float invH = 1f / h;

        Parallel.For(0, h, y =>
        {
            float py = (y + 0.5f) * invH - cy;
            int row = offset + y * w;
            for (int x = 0; x < w; x++)
            {
                float px = (x + 0.5f) * invW - cx;
                float lx = px * cos + py * sin;
                float ly = -px * sin + py * cos;
                float cover;
                if (radial)
                {
                    float nx = lx / rx;
                    float ny = ly / ry;
                    float d = MathF.Sqrt(nx * nx + ny * ny);
                    cover = 1f - Smooth(inner, 1f, d);
                }
                else
                {
                    float qx = MathF.Abs(lx) - rx;
                    float qy = MathF.Abs(ly) - ry;
                    float ox = MathF.Max(qx, 0f);
                    float oy = MathF.Max(qy, 0f);
                    float sd = MathF.Sqrt(ox * ox + oy * oy) + MathF.Min(MathF.Max(qx, qy), 0f);
                    cover = 1f - Smooth(-fade, 0f, sd);
                }
                dst[row + x] = (byte)(cover * 255f + 0.5f);
            }
        });
    }

    /// <summary>Linear gradient: full at Start, smoothly to none at End.</summary>
    private static void RasterizeLinear(MaskLayer m, float aspect, int w, int h, byte[] dst, int offset)
    {
        float sx = m.StartX * aspect;
        float sy = m.StartY;
        float dx = m.EndX * aspect - sx;
        float dy = m.EndY - sy;
        float len2 = dx * dx + dy * dy;
        if (len2 < 1e-8f)
        {
            Array.Clear(dst, offset, w * h);
            return;
        }
        float kx = dx / len2;
        float ky = dy / len2;
        float invW = aspect / w;
        float invH = 1f / h;

        Parallel.For(0, h, y =>
        {
            float py = (y + 0.5f) * invH - sy;
            int row = offset + y * w;
            for (int x = 0; x < w; x++)
            {
                float px = (x + 0.5f) * invW - sx;
                float t = px * kx + py * ky;
                dst[row + x] = (byte)((1f - Smooth(0f, 1f, t)) * 255f + 0.5f);
            }
        });
    }

    /// <summary>Closed spline filled with Skia; the feather is a Gaussian blur centred on the outline.</summary>
    private static unsafe void RasterizeSpline(MaskLayer m, int w, int h, byte[] dst, int offset)
    {
        Array.Clear(dst, offset, w * h);
        var pts = m.Points;
        if (pts == null || pts.Count < 3)
            return;

        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var p in pts)
        {
            minX = MathF.Min(minX, p.X * w);
            maxX = MathF.Max(maxX, p.X * w);
            minY = MathF.Min(minY, p.Y * h);
            maxY = MathF.Max(maxY, p.Y * h);
        }
        float size = MathF.Max(1f, MathF.Min(maxX - minX, maxY - minY));
        float sigma = Math.Clamp(m.Feather / 100f, 0f, 1f) * 0.12f * size;

        var info = new SKImageInfo(w, h, SKColorType.Alpha8, SKAlphaType.Premul);
        fixed (byte* p = &dst[offset])
        {
            using var surface = SKSurface.Create(info, (IntPtr)p, w);
            if (surface == null)
                return;
            using var path = new SKPath();
            AppendSpline(path, pts, (u, v) => new SKPoint(u * w, v * h));
            using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
            if (sigma > 0.3f)
                paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, sigma);
            surface.Canvas.DrawPath(path, paint);
            surface.Canvas.Flush();
        }
    }

    /// <summary>
    /// Closed uniform Catmull-Rom through <paramref name="pts"/> as cubic Béziers. <paramref name="map"/> must be
    /// affine (UV → pixels or screen), so mapping the control points maps the curve exactly.
    /// </summary>
    public static void AppendSpline(SKPath path, IReadOnlyList<MaskPoint> pts, Func<float, float, SKPoint> map)
    {
        int n = pts.Count;
        if (n < 2)
            return;
        path.MoveTo(map(pts[0].X, pts[0].Y));
        int segments = n < 3 ? 1 : n;
        for (int i = 0; i < segments; i++)
        {
            SplineSegment(pts, i, out float c1x, out float c1y, out float c2x, out float c2y, out float ex, out float ey);
            path.CubicTo(map(c1x, c1y), map(c2x, c2y), map(ex, ey));
        }
        if (n >= 3)
            path.Close();
    }

    /// <summary>Control points and end of the closed spline's segment from point <paramref name="i"/> to the next.</summary>
    public static void SplineSegment(IReadOnlyList<MaskPoint> pts, int i,
        out float c1x, out float c1y, out float c2x, out float c2y, out float ex, out float ey)
    {
        int n = pts.Count;
        var p0 = pts[(i - 1 + n) % n];
        var p1 = pts[i % n];
        var p2 = pts[(i + 1) % n];
        var p3 = pts[(i + 2) % n];
        c1x = p1.X + (p2.X - p0.X) / 6f;
        c1y = p1.Y + (p2.Y - p0.Y) / 6f;
        c2x = p2.X - (p3.X - p1.X) / 6f;
        c2y = p2.Y - (p3.Y - p1.Y) / 6f;
        ex = p2.X;
        ey = p2.Y;
    }

    /// <summary>
    /// GPU image of <paramref name="atlas"/>. UI thread only: a two-slot cache so the full and the drag-time
    /// atlas can alternate between frames without re-uploading.
    /// </summary>
    public static SKImage? Image(MaskAtlas atlas)
    {
        for (int i = 0; i < ImageAtlas.Length; i++)
        {
            if (ReferenceEquals(ImageAtlas[i], atlas) && ImageCache[i] != null)
                return ImageCache[i];
        }
        SKImage? image = CreateImage(atlas);
        if (image == null)
            return null;
        int slot = _imageNext;
        _imageNext = (_imageNext + 1) % ImageCache.Length;
        GpuRetain.Retire(ImageCache[slot]);
        ImageCache[slot] = image;
        ImageAtlas[slot] = atlas;
        return image;
    }

    public static SKImage? CreateImage(MaskAtlas atlas)
    {
        var info = new SKImageInfo(atlas.Width, atlas.TileHeight * atlas.Count, SKColorType.Gray8, SKAlphaType.Opaque);
        return SKImage.FromPixelCopy(info, atlas.Pixels, atlas.Width);
    }

    /// <summary>
    /// Adds the <c>u_masks</c> child and sets the mask uniforms. A null <paramref name="image"/> binds no masks
    /// (and a white placeholder child), whatever the settings say.
    /// </summary>
    public static SKShader? Bind(SKRuntimeEffectUniforms u, SKRuntimeEffectChildren children,
        DevelopSettings s, MaskAtlas? atlas, SKImage? image)
    {
        int count = atlas != null && image != null ? atlas.Count : 0;
        SKImage tex = count > 0 ? image! : DevelopRenderer.WhitePixel();
        SKShader shader = tex.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, Raw75.Controls.Sampling.Linear);
        children.Add("u_masks", shader);

        Set(u, "u_maskCount", (float)count);
        Set(u, "u_maskTile", count > 0 ? new[] { (float)atlas!.Width, (float)atlas.TileHeight } : new[] { 1f, 1f });
        var slots = count > 0 ? Slots(s) : null;
        for (int i = 0; i < MaskLayer.MaxMasks; i++)
        {
            float[] a = new float[4], b = new float[4], c = new float[4];
            if (slots != null && i < count && i < slots.Count)
                Pack(s.Masks[slots[i]], a, b, c);
            Set(u, "u_mA" + i, a);
            Set(u, "u_mB" + i, b);
            Set(u, "u_mC" + i, c);
        }
        return shader;
    }

    /// <summary>
    /// Shader / CPU parameter block: A = (EV, contrast, highlights, shadows), B = (whites, blacks, temp, tint),
    /// C = (saturation, vibrance, dehaze, amount); a negative amount inverts the mask.
    /// </summary>
    public static void Pack(MaskLayer m, float[] a, float[] b, float[] c)
    {
        if (m.EnableLight)
        {
            a[0] = m.Exposure;
            a[1] = m.Contrast / 100f;
            a[2] = m.Highlights / 100f;
            a[3] = m.Shadows / 100f;
            b[0] = m.Whites / 100f;
            b[1] = m.Blacks / 100f;
            c[2] = m.Dehaze / 100f;
        }
        if (m.EnableColor)
        {
            b[2] = m.Temperature / 100f;
            b[3] = m.Tint / 100f;
            c[0] = m.Saturation / 100f;
            c[1] = m.Vibrance / 100f;
        }
        float amount = Math.Clamp(m.Amount / 100f, 0f, 1f);
        c[3] = m.Invert ? -amount : amount;
    }

    private static void Set(SKRuntimeEffectUniforms u, string name, float value)
    {
        try { u[name] = value; }
        catch (ArgumentException) { }
    }

    private static void Set(SKRuntimeEffectUniforms u, string name, float[] value)
    {
        try { u[name] = value; }
        catch (ArgumentException) { }
    }

    private static float Smooth(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
