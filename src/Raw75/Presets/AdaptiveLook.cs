using System;
using Raw75.Develop;
using Raw75.Pipeline;

namespace Raw75.Presets;

/// <summary>
/// Retargets tone sliders by inverting the luma model toward the saved look's
/// percentiles (especially highlight hold). Color, HSL, LUT, RGB curves stay as saved.
/// </summary>
internal static class AdaptiveLook
{
    private const float TinyNeed = 0.12f;
    private const float MaxScale = 1.35f;
    private const float HighlightHeadroom = 0.25f;

    public static void Apply(LookPreset preset, DevelopSettings dest, SceneMetrics? destSource)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(dest);

        float cropX = dest.CropX, cropY = dest.CropY, cropW = dest.CropW, cropH = dest.CropH;
        float straighten = dest.Straighten;
        int rotate90 = dest.Rotate90;
        bool flipH = dest.FlipH, flipV = dest.FlipV;
        bool enableGeom = dest.EnableGeometry;
        float baseEv = dest.BaseExposure;
        float temp = dest.Temperature, tint = dest.Tint;
        float ev = dest.Exposure;

        var recipe = preset.Settings ?? new DevelopSettings();
        bool keepWb = Math.Abs(recipe.Temperature) < 0.01f && Math.Abs(recipe.Tint) < 0.01f;
        bool keepEv = Math.Abs(recipe.Exposure) < 0.001f && !recipe.EnableExposureCurve;

        dest.CopyFrom(recipe);

        dest.CropX = cropX;
        dest.CropY = cropY;
        dest.CropW = cropW;
        dest.CropH = cropH;
        dest.Straighten = straighten;
        dest.Rotate90 = rotate90;
        dest.FlipH = flipH;
        dest.FlipV = flipV;
        dest.EnableGeometry = enableGeom;
        dest.BaseExposure = baseEv;
        if (keepWb)
        {
            dest.Temperature = temp;
            dest.Tint = tint;
        }

        if (preset.CanAdapt && destSource != null && destSource.HasData)
        {
            if (preset.Look!.HasToneStats && destSource.HasToneStats)
                InvertTone(preset, dest, destSource);
            else
                RetargetLegacy(preset, dest, destSource);
        }
        else if (keepEv)
            dest.Exposure = ev;
    }

    /// <summary>
    /// Work backwards: match look percentiles with the slider that owns that region.
    /// Exposure (or the curve) hits midtones; Highlights/Whites hold the bright end.
    /// </summary>
    private static void InvertTone(LookPreset preset, DevelopSettings dest, SceneMetrics destSource)
    {
        var look = preset.Look!;
        var source = preset.Source!;

        float evMean = look.P50Ev - destSource.P50Ev;
        float p99If = destSource.P99Ev + evMean;
        if (p99If > look.P99Ev + HighlightHeadroom)
            evMean -= p99If - look.P99Ev - HighlightHeadroom;
        evMean = Math.Clamp(evMean, -5f, 5f);

        if (dest.EnableExposure && dest.EnableExposureCurve)
        {
            dest.ExposureCurve = InvertCurve(preset, dest.ExposureCurve, destSource);
        }
        else if (dest.EnableExposure)
        {
            dest.Exposure = MathF.Round(evMean * 100f) / 100f;
        }

        if (dest.EnableHdr && !dest.EnableExposureCurve)
        {
            float destP99 = destSource.P99Ev + dest.Exposure;
            float destP95 = destSource.P95Ev + dest.Exposure;
            float destP5 = destSource.P5Ev + dest.Exposure;
            float destP1 = destSource.P1Ev + dest.Exposure;

            dest.Highlights = SceneTone.InvertHighlights(destP95, look.P95Ev);
            float whites = SceneTone.InvertWhites(destP99, look.P99Ev);
            if (look.ClipHigh + 0.002f < destSource.ClipHigh && destSource.ClipHigh > 0.008f)
                whites = Math.Min(whites, SceneTone.InvertWhites(MathF.Max(destP99, 1.5f), look.P99Ev));
            dest.Whites = whites;

            dest.Shadows = InvertLift(destP5, look.P5Ev, shadow: true);
            dest.Blacks = InvertLift(destP1, look.P1Ev, shadow: false);
        }

        float authorC = look.ContrastEv - source.ContrastEv;
        float destCNeed = look.ContrastEv - destSource.ContrastEv;
        dest.Contrast = Math.Clamp(dest.Contrast * NeedScale(authorC, destCNeed), -100f, 100f);
    }

    private static float PercentileNeed(SceneMetrics look, SceneMetrics dest, float p)
    {
        float lookEv = EvAt(look, p);
        float destEv = EvAt(dest, p);
        return lookEv - destEv;
    }

    private static float EvAt(SceneMetrics m, float p)
    {
        if (m.EvAtP == null || m.EvAtP.Length < 2)
        {
            if (p <= 0.05f) return m.P5Ev;
            if (p >= 0.99f) return m.P99Ev;
            if (p >= 0.95f) return m.P95Ev;
            if (p <= 0.5f) return m.P50Ev;
            return m.LogMeanEv;
        }
        return AdaptiveLook.XAtPercentile(ToFloatKnots(m.EvAtP), p * 100f);
    }

    private static float[] ToFloatKnots(float[] ev)
    {
        return ev;
    }

    private static ExposureCurvePoint[] InvertCurve(LookPreset preset, ExposureCurvePoint[]? points, SceneMetrics destSource)
    {
        var source = preset.Source!;
        var look = preset.Look!;
        if (points == null || points.Length < 2)
            points = CurveMath.DefaultExposureCurve();

        var mapped = new ExposureCurvePoint[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            float p = PercentileOfX(source.GammaX, points[i].X);
            float x = XAtPercentile(destSource.GammaX, p);
            float neededEv = EvAt(look, p / 100f) - EvAt(destSource, p / 100f);
            float y = 128f + neededEv * (127f / 5f);
            mapped[i] = new ExposureCurvePoint(x, Math.Clamp(y, 0f, 255f), points[i].Curvature);
        }

        Array.Sort(mapped, (a, b) => a.X.CompareTo(b.X));
        mapped[0] = new ExposureCurvePoint(0f, mapped[0].Y, mapped[0].Curvature);
        mapped[^1] = new ExposureCurvePoint(255f, mapped[^1].Y, mapped[^1].Curvature);
        const float minDx = 1f;
        for (int i = 1; i < mapped.Length; i++)
        {
            if (mapped[i].X <= mapped[i - 1].X + minDx)
            {
                float x = Math.Min(255f, mapped[i - 1].X + minDx);
                mapped[i] = new ExposureCurvePoint(x, mapped[i].Y, mapped[i].Curvature);
            }
        }
        mapped[^1] = new ExposureCurvePoint(255f, mapped[^1].Y, mapped[^1].Curvature);
        return mapped;
    }

    private static float InvertLift(float destEv, float lookEv, bool shadow)
    {
        float delta = lookEv - destEv;
        if (MathF.Abs(delta) < 0.04f)
            return 0f;
        float k = shadow ? 55f : 40f;
        return Math.Clamp(delta * k, -100f, 100f);
    }

    private static void RetargetLegacy(LookPreset preset, DevelopSettings dest, SceneMetrics destSource)
    {
        var source = preset.Source!;
        var look = preset.Look!;
        float authorLift = look.LogMeanEv - source.LogMeanEv;
        float destNeed = look.LogMeanEv - destSource.LogMeanEv;
        float liftScale = NeedScale(authorLift, destNeed);

        if (dest.EnableExposure && dest.EnableExposureCurve)
            dest.ExposureCurve = RemapCurve(dest.ExposureCurve, source.GammaX, destSource.GammaX, liftScale);
        else if (dest.EnableExposure)
            dest.Exposure = MathF.Round(Math.Clamp(destNeed, -5f, 5f) * 100f) / 100f;

        float authorC = look.ContrastEv - source.ContrastEv;
        float destCNeed = look.ContrastEv - destSource.ContrastEv;
        float cScale = NeedScale(authorC, destCNeed);
        dest.Contrast = Math.Clamp(dest.Contrast * cScale, -100f, 100f);
        dest.Highlights = Math.Clamp(dest.Highlights * cScale, -100f, 100f);
        dest.Shadows = Math.Clamp(dest.Shadows * cScale, -100f, 100f);
        dest.Whites = Math.Clamp(dest.Whites * cScale, -100f, 100f);
        dest.Blacks = Math.Clamp(dest.Blacks * cScale, -100f, 100f);
    }

    internal static float NeedScale(float authorDelta, float destNeed)
    {
        if (MathF.Abs(authorDelta) < TinyNeed)
            return 1f;
        return Math.Clamp(destNeed / authorDelta, 0f, MaxScale);
    }

    internal static ExposureCurvePoint[] RemapCurve(
        ExposureCurvePoint[]? points,
        float[] sourceX,
        float[] destX,
        float yScale)
    {
        if (points == null || points.Length < 2)
            return CurveMath.DefaultExposureCurve();

        var mapped = new ExposureCurvePoint[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            float p = PercentileOfX(sourceX, points[i].X);
            float x = XAtPercentile(destX, p);
            float y = 128f + (points[i].Y - 128f) * yScale;
            mapped[i] = new ExposureCurvePoint(x, Math.Clamp(y, 0f, 255f), points[i].Curvature);
        }

        Array.Sort(mapped, (a, b) => a.X.CompareTo(b.X));
        mapped[0] = new ExposureCurvePoint(0f, mapped[0].Y, mapped[0].Curvature);
        mapped[^1] = new ExposureCurvePoint(255f, mapped[^1].Y, mapped[^1].Curvature);
        const float minDx = 1f;
        for (int i = 1; i < mapped.Length; i++)
        {
            if (mapped[i].X <= mapped[i - 1].X + minDx)
            {
                float x = Math.Min(255f, mapped[i - 1].X + minDx);
                mapped[i] = new ExposureCurvePoint(x, mapped[i].Y, mapped[i].Curvature);
            }
        }
        mapped[^1] = new ExposureCurvePoint(255f, mapped[^1].Y, mapped[^1].Curvature);
        return mapped;
    }

    internal static float PercentileOfX(float[] gammaX, float x)
    {
        if (gammaX == null || gammaX.Length < 2)
            return Math.Clamp(x / 2.55f, 0f, 100f);
        if (x <= gammaX[0])
            return 0f;
        int last = gammaX.Length - 1;
        if (x >= gammaX[last])
            return 100f;
        for (int i = 0; i < last; i++)
        {
            float a = gammaX[i];
            float b = gammaX[i + 1];
            if (x <= b || i == last - 1)
            {
                float span = b - a;
                float t = span > 1e-3f ? (x - a) / span : 0f;
                return (i + Math.Clamp(t, 0f, 1f)) * (100f / last);
            }
        }
        return 100f;
    }

    internal static float XAtPercentile(float[] gammaX, float percentile)
    {
        if (gammaX == null || gammaX.Length < 2)
            return Math.Clamp(percentile * 2.55f, 0f, 255f);
        float p = Math.Clamp(percentile, 0f, 100f);
        int last = gammaX.Length - 1;
        float f = p / (100f / last);
        int i = (int)f;
        if (i >= last)
            return gammaX[last];
        float t = f - i;
        return gammaX[i] + (gammaX[i + 1] - gammaX[i]) * t;
    }
}
