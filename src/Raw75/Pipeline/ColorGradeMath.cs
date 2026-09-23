using System;
using Raw75.Develop;

namespace Raw75.Pipeline;

/// <summary>
/// 3-way color grading.
/// Tint RGB is packed on the CPU so the shader only applies luma masks.
/// </summary>
internal static class ColorGradeMath
{
    public const float SatScale = 500f;
    public const float LumScale = 500f;
    public const float BalanceScale = 200f;

    public static bool IsActive(DevelopSettings s)
    {
        if (s == null || !s.EnableColorGrading)
            return false;
        return s.GradingShadowSat > 0.001f || MathF.Abs(s.GradingShadowLum) > 0.001f
            || s.GradingMidSat > 0.001f || MathF.Abs(s.GradingMidLum) > 0.001f
            || s.GradingHighlightSat > 0.001f || MathF.Abs(s.GradingHighlightLum) > 0.001f;
    }

    public static void HsvToRgb(float h, float s, float v, out float r, out float g, out float b)
    {
        h %= 360f;
        if (h < 0f) h += 360f;
        s = Math.Clamp(s, 0f, 1f);
        v = Math.Clamp(v, 0f, 1f);
        float c = v * s;
        float h60 = h / 60f;
        float x = c * (1f - MathF.Abs(h60 % 2f - 1f));
        float m = v - c;
        float rp, gp, bp;
        if (h < 60f) { rp = c; gp = x; bp = 0f; }
        else if (h < 120f) { rp = x; gp = c; bp = 0f; }
        else if (h < 180f) { rp = 0f; gp = c; bp = x; }
        else if (h < 240f) { rp = 0f; gp = x; bp = c; }
        else if (h < 300f) { rp = x; gp = 0f; bp = c; }
        else { rp = c; gp = 0f; bp = x; }
        r = rp + m;
        g = gp + m;
        b = bp + m;
    }

    public static float[] PackRange(float hueDeg, float satUi, float lumUi, float satStrength, float lumStrength)
    {
        float sat = satUi / SatScale;
        float lum = lumUi / LumScale;
        float lumTerm = lum * lumStrength;
        if (sat <= 0.001f)
            return new[] { 0f, 0f, 0f, lumTerm };

        HsvToRgb(hueDeg, 1f, 1f, out float r, out float g, out float b);
        float k = sat * satStrength;
        return new[] { (r - 0.5f) * k, (g - 0.5f) * k, (b - 0.5f) * k, lumTerm };
    }

    public static void BindUniforms(Action<string, float[]> setVec, DevelopSettings s)
    {
        if (s == null || !s.EnableColorGrading)
        {
            setVec("u_gradeShadow", new[] { 0f, 0f, 0f, 0f });
            setVec("u_gradeMid", new[] { 0f, 0f, 0f, 0f });
            setVec("u_gradeHighlight", new[] { 0f, 0f, 0f, 0f });
            setVec("u_gradeMix", new[] { 0.5f, 0f });
            return;
        }

        setVec("u_gradeShadow", PackRange(s.GradingShadowHue, s.GradingShadowSat, s.GradingShadowLum, 0.1f, 0.5f));
        setVec("u_gradeMid", PackRange(s.GradingMidHue, s.GradingMidSat, s.GradingMidLum, 0.6f, 0.8f));
        setVec("u_gradeHighlight", PackRange(s.GradingHighlightHue, s.GradingHighlightSat, s.GradingHighlightLum, 0.8f, 1.0f));
        setVec("u_gradeMix", new[] { Math.Clamp(s.GradingBlending / 100f, 0f, 1f), s.GradingBalance / BalanceScale });
    }

    public static void Apply(ref float r, ref float g, ref float b, DevelopSettings s)
    {
        if (!IsActive(s))
            return;

        float[] sh = PackRange(s.GradingShadowHue, s.GradingShadowSat, s.GradingShadowLum, 0.1f, 0.5f);
        float[] mid = PackRange(s.GradingMidHue, s.GradingMidSat, s.GradingMidLum, 0.6f, 0.8f);
        float[] hl = PackRange(s.GradingHighlightHue, s.GradingHighlightSat, s.GradingHighlightLum, 0.8f, 1.0f);
        float blending = Math.Clamp(s.GradingBlending / 100f, 0f, 1f);
        float balance = s.GradingBalance / BalanceScale;

        float luma = MathF.Max(0.2627f * r + 0.6780f * g + 0.0593f * b, 0f);
        float shadowCrossover = 0.1f + MathF.Max(0f, -balance) * 0.5f;
        float highlightCrossover = 0.5f - MathF.Max(0f, balance) * 0.5f;
        float feather = MathF.Max(0.001f, 0.2f * blending);
        float finalShadow = MathF.Min(shadowCrossover, highlightCrossover - 0.01f);
        float shadowMask = 1f - Smooth(luma, finalShadow - feather, finalShadow + feather);
        float highlightMask = Smooth(luma, highlightCrossover - feather, highlightCrossover + feather);
        float midMask = MathF.Max(0f, 1f - shadowMask - highlightMask);

        r += sh[0] * shadowMask + mid[0] * midMask + hl[0] * highlightMask;
        g += sh[1] * shadowMask + mid[1] * midMask + hl[1] * highlightMask;
        b += sh[2] * shadowMask + mid[2] * midMask + hl[2] * highlightMask;
        float add = sh[3] * shadowMask + mid[3] * midMask + hl[3] * highlightMask;
        r += add;
        g += add;
        b += add;
    }

    private static float Smooth(float x, float a, float b)
    {
        float span = b - a;
        if (MathF.Abs(span) < 1e-6f)
            return x >= b ? 1f : 0f;
        float t = Math.Clamp((x - a) / span, 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
