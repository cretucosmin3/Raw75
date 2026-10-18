using System;
using Raw75.Develop;
using Raw75.Imaging;

namespace Raw75.Pipeline;

/// <summary>
/// Subsampled scene metrics for adaptive presets. Same cost class as auto-exposure.
/// </summary>
internal static class SceneAnalyzer
{
    public static SceneMetrics Measure(RasterBuffer raster)
    {
        return FromLumas(Collect(raster, applyLook: null), n: -1);
    }

    public static SceneMetrics MeasureLook(RasterBuffer raster, DevelopSettings s)
    {
        return FromLumas(Collect(raster, s), n: -1);
    }

    private static float[] Collect(RasterBuffer raster, DevelopSettings? applyLook)
    {
        if (!raster.HasPixels || raster.Width <= 0 || raster.Height <= 0)
            return Array.Empty<float>();

        float[]? curveLut = null;
        if (applyLook != null && applyLook.EnableExposure && applyLook.EnableExposureCurve)
            curveLut = CurveMath.EvaluateExposureSpline(applyLook.ExposureCurve, 256);

        int width = raster.Width;
        int height = raster.Height;
        int total = width * height;
        int step = 1;
        if (total > 65536)
            step = Math.Max(1, (int)MathF.Sqrt(total / 35000f));

        int cap = ((height + step - 1) / step) * ((width + step - 1) / step);
        float[] lumas = new float[cap];
        int n = 0;
        float[]? linear = raster.Linear;
        byte[]? rgba = raster.Rgba;
        bool isLinear = raster.HasLinear;

        for (int y = 0; y < height; y += step)
        {
            int rowOff = y * width;
            for (int x = 0; x < width; x += step)
            {
                float r, g, b;
                if (isLinear && linear != null)
                {
                    int idx = (rowOff + x) * 4;
                    r = linear[idx];
                    g = linear[idx + 1];
                    b = linear[idx + 2];
                }
                else if (rgba != null)
                {
                    int idx = (rowOff + x) * 4;
                    r = DevelopCpu.ToLin1(rgba[idx] / 255f);
                    g = DevelopCpu.ToLin1(rgba[idx + 1] / 255f);
                    b = DevelopCpu.ToLin1(rgba[idx + 2] / 255f);
                }
                else
                    continue;

                float luma = 0.2627f * r + 0.6780f * g + 0.0593f * b;
                if (float.IsNaN(luma) || float.IsInfinity(luma) || luma < 0f)
                    continue;

                if (applyLook != null)
                    luma = SceneTone.Apply(luma, applyLook, curveLut);
                else
                    luma = MathF.Max(luma, SceneTone.MinLuma);

                if (n < lumas.Length)
                    lumas[n++] = luma;
            }
        }

        if (n == lumas.Length)
            return lumas;
        var trim = new float[n];
        Array.Copy(lumas, trim, n);
        return trim;
    }

    internal static SceneMetrics FromLumas(float[] lumas, int n)
    {
        var metrics = new SceneMetrics();
        if (lumas == null || lumas.Length == 0)
            return metrics;
        if (n < 0 || n > lumas.Length)
            n = lumas.Length;
        if (n == 0)
            return metrics;
        metrics.MetricVersion = SceneMetrics.CurrentVersion;

        int[] hist = new int[256];
        double logSum = 0;
        int clipHigh = 0, clipOpen = 0;
        for (int i = 0; i < n; i++)
        {
            float luma = MathF.Max(lumas[i], SceneTone.MinLuma);
            logSum += Math.Log(luma);
            if (luma > SceneTone.ClipSoft) clipHigh++;
            if (luma > SceneTone.ClipOpen) clipOpen++;
            float t = Math.Clamp(MathF.Pow(luma, SceneTone.Gamma), 0f, 1f);
            int bin = (int)(t * 255f);
            if (bin > 255) bin = 255;
            hist[bin]++;
        }

        metrics.LogMeanEv = MathF.Log2((float)Math.Exp(logSum / n) / SceneTone.MidGray);
        metrics.ClipHigh = clipHigh / (float)n;
        metrics.ClipOpen = clipOpen / (float)n;

        int[] cdf = new int[256];
        int run = 0;
        for (int i = 0; i < 256; i++)
        {
            run += hist[i];
            cdf[i] = run;
        }

        metrics.GammaX = new float[SceneMetrics.PercentileKnots];
        metrics.EvAtP = new float[SceneMetrics.PercentileKnots];
        for (int k = 0; k < SceneMetrics.PercentileKnots; k++)
        {
            int bin = BinAtPercentile(cdf, n, k * 0.1f);
            metrics.GammaX[k] = bin;
            metrics.EvAtP[k] = BinToEv(bin);
        }

        metrics.P1Ev = BinToEv(BinAtPercentile(cdf, n, 0.01f));
        metrics.P5Ev = BinToEv(BinAtPercentile(cdf, n, 0.05f));
        metrics.P50Ev = BinToEv(BinAtPercentile(cdf, n, 0.50f));
        metrics.P95Ev = BinToEv(BinAtPercentile(cdf, n, 0.95f));
        metrics.P99Ev = BinToEv(BinAtPercentile(cdf, n, 0.99f));
        metrics.ContrastEv = MathF.Max(0f, metrics.P95Ev - metrics.P5Ev);
        return metrics;
    }

    private static int BinAtPercentile(int[] cdf, int n, float p)
    {
        int target = Math.Max(1, (int)Math.Round(p * n));
        if (target > n) target = n;
        for (int i = 0; i < 256; i++)
        {
            if (cdf[i] >= target)
                return i;
        }
        return 255;
    }

    private static float BinToEv(int bin)
    {
        float t = bin / 255f;
        float lin = MathF.Max(SceneTone.MinLuma, MathF.Pow(t, 1f / SceneTone.Gamma));
        return SceneTone.LumaEv(lin);
    }
}
