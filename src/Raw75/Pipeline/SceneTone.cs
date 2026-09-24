using System;
using Raw75.Develop;

namespace Raw75.Pipeline;

/// <summary>
/// Luma-only forward model of the develop tone stack (exposure, whites, shadows, highlights, contrast).
/// Used to measure a look and to invert sliders toward that look.
/// </summary>
internal static class SceneTone
{
    public const float MinLuma = 0.0001f;
    public const float MidGray = 0.18f;
    public const float Gamma = 0.4545f;
    public const float ClipSoft = 0.92f;
    public const float ClipOpen = 1.0f;

    public static float LumaEv(float luma) =>
        MathF.Log2(MathF.Max(luma, MinLuma) / MidGray);

    public static float EvToLuma(float ev) =>
        MidGray * MathF.Pow(2f, ev);

    public static float Smooth(float x, float a, float b)
    {
        float span = b - a;
        if (MathF.Abs(span) < 1e-6f)
            return x >= b ? 1f : 0f;
        float t = Math.Clamp((x - a) / span, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    public static float Apply(float luma, DevelopSettings s, float[]? curveLut)
    {
        luma = MathF.Max(luma, MinLuma);
        luma *= MathF.Pow(2f, GainEv(luma, s, curveLut));

        float whites = s.EnableHdr ? s.Whites / 100f : 0f;
        float shadows = s.EnableHdr ? s.Shadows / 100f : 0f;
        float blacks = s.EnableHdr ? s.Blacks / 100f : 0f;
        float highlights = s.EnableHdr ? s.Highlights / 100f : 0f;
        float contrast = s.EnableExposure ? s.Contrast / 100f : 0f;

        luma = ApplyWhites(luma, whites);
        luma = ApplyShadows(luma, shadows, blacks);
        luma = ApplyHighlights(luma, highlights);
        luma = ApplyContrast(luma, contrast);
        return MathF.Max(luma, MinLuma);
    }

    public static float GainEv(float luma, DevelopSettings s, float[]? curveLut)
    {
        if (s == null || !s.EnableExposure)
            return 0f;
        if (s.EnableExposureCurve && curveLut != null && curveLut.Length > 1)
        {
            float t = Math.Clamp(MathF.Pow(MathF.Max(luma, MinLuma), Gamma), 0f, 1f);
            float x = t * (curveLut.Length - 1);
            int i = (int)x;
            if (i >= curveLut.Length - 1)
                return curveLut[^1];
            float f = x - i;
            return curveLut[i] + (curveLut[i + 1] - curveLut[i]) * f;
        }
        return s.Exposure;
    }

    public static float ApplyWhites(float luma, float whites)
    {
        if (MathF.Abs(whites) < 0.0001f)
            return luma;
        float evW = LumaEv(luma);
        float w = Smooth(evW, 0.3f, 2f);
        if (w < 0.0001f)
            return luma;
        float targetEv;
        if (whites > 0f)
            targetEv = evW + whites * 2.4f;
        else
        {
            float k = -whites * 1.15f;
            targetEv = evW / (1f + k * MathF.Max(evW - 0.3f, 0f));
        }
        float newEv = evW + (targetEv - evW) * w;
        return EvToLuma(newEv);
    }

    public static float ApplyHighlights(float luma, float hi)
    {
        float hl = hi * 0.833333f;
        if (MathF.Abs(hl) < 0.001f || luma <= MidGray)
            return luma;
        float evH = LumaEv(luma);
        float targetEv;
        if (hl < 0f)
        {
            float k = -hl;
            targetEv = evH / (1f + k * evH * 0.35f);
        }
        else
            targetEv = evH * (1f + hl * 0.35f);
        float wHl = Smooth(evH, 0f, 1.5f);
        float newEv = evH + (targetEv - evH) * wHl;
        return EvToLuma(newEv);
    }

    public static float ApplyShadows(float luma, float shadows, float blacks)
    {
        float sh = shadows * 0.833333f;
        float bl = blacks * 2.5f;
        if (MathF.Abs(sh) < 0.001f && MathF.Abs(bl) < 0.001f)
            return luma;
        float tPixel = MathF.Pow(MathF.Max(luma, MinLuma), Gamma);
        float shadowLift = sh * tPixel * MathF.Pow(MathF.Max(1f - tPixel, 0f), 4.5f);
        float blackLift = bl * tPixel * MathF.Pow(MathF.Max(1f - tPixel, 0f), 12f);
        float liftAmount = MathF.Max(shadowLift + blackLift, 0f);
        float tPixelCurved = MathF.Max(tPixel + shadowLift + blackLift, 0f);
        const float shadowPivot = 0.2f;
        float stretchFactor = 1f + liftAmount * 1.3f;
        float contrastedT = shadowPivot + (tPixelCurved - shadowPivot) * stretchFactor;
        float finalT = MathF.Max(tPixelCurved * 0.15f + contrastedT * 0.85f, 0f);
        return MathF.Pow(finalT, 2.2f);
    }

    public static float ApplyContrast(float luma, float contrast)
    {
        if (MathF.Abs(contrast) < 0.0005f)
            return luma;
        float c = MathF.Max(luma, 0f);
        float p = MathF.Pow(c, Gamma);
        float cp = Math.Clamp(p, 0f, 1f);
        float strength = MathF.Pow(2f, contrast * 1.25f);
        float curved = cp < 0.5f
            ? 0.5f * MathF.Pow(2f * cp, strength)
            : 1f - 0.5f * MathF.Pow(2f * (1f - cp), strength);
        float adj = MathF.Pow(curved, 2.2f);
        float mix = Smooth(c, 1f, 1.01f);
        return adj * (1f - mix) + c * mix;
    }

    /// <summary>Highlights slider (−100..100) that maps destEv toward lookEv at the highlight shoulder.</summary>
    public static float InvertHighlights(float destEv, float lookEv)
    {
        if (destEv <= 0.05f && lookEv <= 0.05f)
            return 0f;
        float evH = MathF.Max(destEv, 0.05f);
        float wHl = Smooth(evH, 0f, 1.5f);
        if (wHl < 0.02f)
            return 0f;
        float targetEv = evH + (lookEv - evH) / wHl;
        targetEv = MathF.Max(targetEv, 0.02f);
        if (lookEv < destEv - 0.02f)
        {
            float k = (evH / targetEv - 1f) / (evH * 0.35f);
            k = Math.Clamp(k, 0f, 6f);
            return Math.Clamp((-k) / 0.833333f * 100f, -100f, 0f);
        }
        if (lookEv > destEv + 0.02f)
        {
            float hl = (targetEv / evH - 1f) / 0.35f;
            return Math.Clamp(hl / 0.833333f * 100f, 0f, 100f);
        }
        return 0f;
    }

    /// <summary>Whites slider (−100..100) for the bright-end white point.</summary>
    public static float InvertWhites(float destEv, float lookEv)
    {
        float w = Smooth(destEv, 0.3f, 2f);
        if (w < 0.02f)
            return 0f;
        float targetEv = destEv + (lookEv - destEv) / w;
        if (lookEv < destEv - 0.02f)
        {
            float span = MathF.Max(destEv - 0.3f, 0.05f);
            if (targetEv < 0.05f)
                targetEv = 0.05f;
            float k = (destEv / targetEv - 1f) / span;
            k = Math.Clamp(k, 0f, 8f);
            return Math.Clamp(-k / 1.15f * 100f, -100f, 0f);
        }
        if (lookEv > destEv + 0.02f)
        {
            float whites = (targetEv - destEv) / 2.4f;
            return Math.Clamp(whites * 100f, 0f, 100f);
        }
        return 0f;
    }
}
