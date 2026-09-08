using System;
using Raw75.Develop;
using Raw75.Imaging;

namespace Raw75.Pipeline;

/// <summary>
/// Slider preview without SKSL. Runtime effects on the window GRContext
/// abort the process; this reads the owned RGBA buffer on the UI thread.
/// </summary>
internal static class DevelopCpu
{
    public static RasterBuffer Apply(RasterBuffer src, DevelopSettings s, bool fast = false)
    {
        s ??= new DevelopSettings();
        int sw = src.Width;
        int sh = src.Height;
        int rot = s.Rotate90 & 3;
        int dw = (rot & 1) != 0 ? sh : sw;
        int dh = (rot & 1) != 0 ? sw : sh;
        byte[] dst = new byte[dw * dh * 4];

        float temp = s.Temperature / 100f;
        float tint = s.Tint / 100f;
        float ev = MathF.Pow(2f, s.Exposure);
        float contrast = s.Contrast / 100f;
        float hi = s.Highlights / 100f;
        float shd = s.Shadows / 100f;
        float whites = s.Whites / 100f;
        float blacks = s.Blacks / 100f;
        float vib = s.Vibrance / 100f;
        float sat = s.Saturation / 100f;
        float sharp = fast ? 0f : s.Sharpen / 150f;
        float denoise = fast ? 0f : Math.Max(s.DenoiseLuma, s.DenoiseChroma) / 100f;
        float straight = s.Straighten;
        bool flipH = s.FlipH;
        bool flipV = s.FlipV;

        bool linearSrc = src.HasLinear;
        float[]? linSrc = src.Linear;
        byte[]? rgba = src.Rgba;
        if (!linearSrc && rgba == null)
            return new RasterBuffer(new byte[dw * dh * 4], dw, dh);

        bool neighbor = !fast && (sharp > 0.001f || denoise > 0.001f);
        bool remap = rot != 0 || flipH || flipV || Math.Abs(straight) > 0.05f;
        float[]? look = neighbor ? new float[dw * dh * 3] : null;
        for (int y = 0; y < dh; y++)
        {
            for (int x = 0; x < dw; x++)
            {
                int sx, sy;
                if (!remap)
                {
                    sx = x;
                    sy = y;
                }
                else
                    MapSrc(x, y, dw, dh, rot, flipH, flipV, straight, sw, sh, out sx, out sy);
                int si = (sy * sw + sx) * 4;
                float r, g, b;
                if (linearSrc)
                {
                    r = linSrc![si];
                    g = linSrc[si + 1];
                    b = linSrc[si + 2];
                }
                else
                {
                    r = ToLin1(rgba![si] / 255f);
                    g = ToLin1(rgba[si + 1] / 255f);
                    b = ToLin1(rgba[si + 2] / 255f);
                }

                ApplyLook(ref r, ref g, ref b, temp, tint, ev, contrast, hi, shd, whites, blacks, vib, sat, s);

                if (look != null)
                {
                    int li = (y * dw + x) * 3;
                    look[li] = r;
                    look[li + 1] = g;
                    look[li + 2] = b;
                }
                else
                {
                    int di = (y * dw + x) * 4;
                    FilmicSrgb(r, g, b, out float or, out float og, out float ob);
                    dst[di] = ToByte(or);
                    dst[di + 1] = ToByte(og);
                    dst[di + 2] = ToByte(ob);
                    dst[di + 3] = 255;
                }
            }
        }

        if (look == null)
            return new RasterBuffer(dst, dw, dh);
        for (int y = 0; y < dh; y++)
        {
            for (int x = 0; x < dw; x++)
            {
                int li = (y * dw + x) * 3;
                float r = look[li];
                float g = look[li + 1];
                float b = look[li + 2];
                if (neighbor)
                {
                    float ar = 0, ag = 0, ab = 0;
                    Acc(look, dw, dh, x - 1, y, ref ar, ref ag, ref ab);
                    Acc(look, dw, dh, x + 1, y, ref ar, ref ag, ref ab);
                    Acc(look, dw, dh, x, y - 1, ref ar, ref ag, ref ab);
                    Acc(look, dw, dh, x, y + 1, ref ar, ref ag, ref ab);
                    ar *= 0.25f;
                    ag *= 0.25f;
                    ab *= 0.25f;
                    if (denoise > 0.001f)
                    {
                        r = r + (ar - r) * denoise;
                        g = g + (ag - g) * denoise;
                        b = b + (ab - b) * denoise;
                    }
                    if (sharp > 0.001f)
                    {
                        r += (r - ar) * sharp * 1.35f;
                        g += (g - ag) * sharp * 1.35f;
                        b += (b - ab) * sharp * 1.35f;
                    }
                }

                int di = (y * dw + x) * 4;
                FilmicSrgb(r, g, b, out float or, out float og, out float ob);
                dst[di] = ToByte(or);
                dst[di + 1] = ToByte(og);
                dst[di + 2] = ToByte(ob);
                dst[di + 3] = 255;
            }
        }

        return new RasterBuffer(dst, dw, dh);
    }

    private static void ApplyLook(
        ref float r, ref float g, ref float b,
        float temp, float tint, float ev, float contrast,
        float hi, float shd, float whites, float blacks,
        float vib, float sat, DevelopSettings s)
    {
        r += 0.15f * temp;
        b -= 0.15f * temp;
        g -= 0.15f * tint;
        if (r < 0) r = 0;
        if (g < 0) g = 0;
        if (b < 0) b = 0;

        r *= ev;
        g *= ev;
        b *= ev;

        if (s.MatchGray)
        {
            float lum0 = Luma(r, g, b);
            if (lum0 < 1e-4f) lum0 = 1e-4f;
            float mg = 1f + (0.18f / lum0 - 1f) * 0.35f;
            r *= mg;
            g *= mg;
            b *= mg;
        }

        float er = MathF.Log2(1f + r);
        float eg = MathF.Log2(1f + g);
        float eb = MathF.Log2(1f + b);
        float k = 1f + contrast * 0.85f;
        er = (er - 0.5f) * k + 0.5f;
        eg = (eg - 0.5f) * k + 0.5f;
        eb = (eb - 0.5f) * k + 0.5f;
        r = MathF.Max(MathF.Pow(2f, er) - 1f, 0f);
        g = MathF.Max(MathF.Pow(2f, eg) - 1f, 0f);
        b = MathF.Max(MathF.Pow(2f, eb) - 1f, 0f);

        float lum = Luma(r, g, b);
        float shw = 1f - Smooth(lum, 0.02f, 0.22f);
        float hiw = Smooth(lum, 0.35f, 2.5f);
        float shGain = MathF.Pow(2f, shd * 1.15f);
        float hiGain = MathF.Pow(2f, hi * 1.0f);
        float mixS = shw;
        float mixH = hiw;
        r = r * (1f + (shGain - 1f) * mixS) * (1f + (hiGain - 1f) * mixH);
        g = g * (1f + (shGain - 1f) * mixS) * (1f + (hiGain - 1f) * mixH);
        b = b * (1f + (shGain - 1f) * mixS) * (1f + (hiGain - 1f) * mixH);

        lum = Luma(r, g, b);
        float ww = Smooth(lum, 0.8f, 4f);
        float bw = 1f - Smooth(lum, 0.0f, 0.12f);
        float wGain = MathF.Pow(2f, whites * 0.85f);
        float bGain = MathF.Pow(2f, blacks * 0.85f);
        r = r * (1f + (wGain - 1f) * ww) * (1f + (bGain - 1f) * bw);
        g = g * (1f + (wGain - 1f) * ww) * (1f + (bGain - 1f) * bw);
        b = b * (1f + (wGain - 1f) * ww) * (1f + (bGain - 1f) * bw);
        if (r < 0) r = 0;
        if (g < 0) g = 0;
        if (b < 0) b = 0;

        lum = Luma(r, g, b);
        float mx = r > g ? r : g;
        if (b > mx) mx = b;
        float mn = r < g ? r : g;
        if (b < mn) mn = b;
        float chroma = mx > 1e-5f ? (mx - mn) / mx : 0f;
        float vBoost = vib * (1f - chroma);
        r = lum + (r - lum) * (1f + vBoost);
        g = lum + (g - lum) * (1f + vBoost);
        b = lum + (b - lum) * (1f + vBoost);
        lum = Luma(r, g, b);
        r = lum + (r - lum) * (1f + sat);
        g = lum + (g - lum) * (1f + sat);
        b = lum + (b - lum) * (1f + sat);
    }

    private static void FilmicSrgb(float r, float g, float b, out float or, out float og, out float ob)
    {
        or = ToSrgb1(Filmic1(r));
        og = ToSrgb1(Filmic1(g));
        ob = ToSrgb1(Filmic1(b));
    }

    private static float Filmic1(float x)
    {
        if (x < 0f) x = 0f;
        const float a = 2.51f, b = 0.03f, c = 2.43f, d = 0.59f, e = 0.14f;
        float y = (x * (a * x + b)) / (x * (c * x + d) + e);
        return Clamp01(y);
    }

    private static float Luma(float r, float g, float b) => 0.2627f * r + 0.6780f * g + 0.0593f * b;

    private static float Smooth(float x, float a, float b)
    {
        float t = (x - a) / (b - a);
        t = Clamp01(t);
        return t * t * (3f - 2f * t);
    }

    private static float ToLin1(float s)
    {
        s = Clamp01(s);
        if (s <= 0.04045f) return s / 12.92f;
        return MathF.Pow((s + 0.055f) / 1.055f, 2.4f);
    }

    private static float ToSrgb1(float l)
    {
        l = Clamp01(l);
        if (l <= 0.0031308f) return 12.92f * l;
        return 1.055f * MathF.Pow(l, 1f / 2.4f) - 0.055f;
    }

    private static void Acc(float[] look, int dw, int dh, int x, int y, ref float r, ref float g, ref float b)
    {
        if (x < 0) x = 0;
        else if (x >= dw) x = dw - 1;
        if (y < 0) y = 0;
        else if (y >= dh) y = dh - 1;
        int i = (y * dw + x) * 3;
        r += look[i];
        g += look[i + 1];
        b += look[i + 2];
    }

    public static void FillHistogram(RasterBuffer buf, PhotoDocument doc)
    {
        Array.Clear(doc.HistogramR);
        Array.Clear(doc.HistogramG);
        Array.Clear(doc.HistogramB);
        Array.Clear(doc.HistogramY);
        byte[]? p = buf.Rgba;
        if (p == null) return;
        int n = buf.Width * buf.Height;
        for (int i = 0; i < n; i++)
        {
            int o = i * 4;
            byte r = p[o];
            byte g = p[o + 1];
            byte b = p[o + 2];
            doc.HistogramR[r] += 1f;
            doc.HistogramG[g] += 1f;
            doc.HistogramB[b] += 1f;
            int y = (int)(0.2126f * r + 0.7152f * g + 0.0722f * b + 0.5f);
            if (y < 0) y = 0;
            else if (y > 255) y = 255;
            doc.HistogramY[y] += 1f;
        }
    }

    private static void MapSrc(
        int x, int y, int dw, int dh, int rot, bool flipH, bool flipV, float straightDeg,
        int sw, int sh, out int sx, out int sy)
    {
        // dest(x,y) → src. 90° CW: dest(x,y) = src(y, H-1-x) with dest size (H,W).
        int ix = x, iy = y;
        if (rot == 1) { ix = y; iy = sh - 1 - x; }
        else if (rot == 2) { ix = sw - 1 - x; iy = sh - 1 - y; }
        else if (rot == 3) { ix = sw - 1 - y; iy = x; }

        if (flipH) ix = sw - 1 - ix;
        if (flipV) iy = sh - 1 - iy;

        if (Math.Abs(straightDeg) > 0.05f)
        {
            float a = straightDeg * (MathF.PI / 180f);
            float c = MathF.Cos(a);
            float sn = MathF.Sin(a);
            float cx = (sw - 1) * 0.5f;
            float cy = (sh - 1) * 0.5f;
            float dx = ix - cx;
            float dy = iy - cy;
            ix = (int)MathF.Round(dx * c - dy * sn + cx);
            iy = (int)MathF.Round(dx * sn + dy * c + cy);
        }

        sx = ix;
        sy = iy;
        if (sx < 0) sx = 0;
        else if (sx >= sw) sx = sw - 1;
        if (sy < 0) sy = 0;
        else if (sy >= sh) sy = sh - 1;
    }

    private static float Clamp01(float x)
    {
        if (x < 0) return 0;
        if (x > 1) return 1;
        return x;
    }

    private static byte ToByte(float x)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 255;
        return (byte)(x * 255f + 0.5f);
    }
}
