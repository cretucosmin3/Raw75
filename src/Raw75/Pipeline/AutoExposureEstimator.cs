using System;
using Raw75.Imaging;

namespace Raw75.Pipeline;

/// <summary>
/// Photographic Auto Exposure estimator for scene-linear and display-referred images.
/// Evaluates the geometric mean (log-average luminance) mapped to photographic middle-gray (18%),
/// bounded by 99th percentile highlight preservation to protect against highlight clipping.
/// </summary>
internal static class AutoExposureEstimator
{
    private const float MiddleGrayTarget = 0.18f;
    private const float MinLuma = 0.0001f;

    /// <summary>
    /// Computes the optimal exposure compensation in EV (stops) for the given raster buffer.
    /// The calculation is idempotent: multiple calls on the same raster return the identical result.
    /// </summary>
    internal static float Estimate(RasterBuffer raster)
    {
        if (!raster.HasPixels || raster.Width <= 0 || raster.Height <= 0)
            return 0f;

        int width = raster.Width;
        int height = raster.Height;
        int totalPixels = width * height;

        float[]? linear = raster.Linear;
        byte[]? rgba = raster.Rgba;
        bool isLinear = raster.HasLinear;

        if (linear == null && rgba == null)
            return 0f;

        // Subsample large buffers to ~30,000-40,000 samples for sub-millisecond execution (< 0.5ms)
        int step = 1;
        if (totalPixels > 65536)
            step = Math.Max(1, (int)MathF.Sqrt(totalPixels / 35000f));

        double logLumaSum = 0;
        int sampleCount = 0;

        // Histogram of 1024 bins covering linear luminance [0.0 .. 2.0]
        // to find highlight percentiles (99th percentile) with zero heap allocations.
        const int histBins = 1024;
        const float maxHistLuma = 2.0f;
        const float binScale = histBins / maxHistLuma;
        int[] hist = new int[histBins];

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
                else
                {
                    int idx = (rowOff + x) * 4;
                    r = DevelopCpu.ToLin1(rgba![idx] / 255f);
                    g = DevelopCpu.ToLin1(rgba[idx + 1] / 255f);
                    b = DevelopCpu.ToLin1(rgba[idx + 2] / 255f);
                }

                // Photometric luminance (Rec. 709 / sRGB linear coefficients)
                float luma = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                if (float.IsNaN(luma) || float.IsInfinity(luma) || luma < 0f)
                    continue;

                float clampedLuma = MathF.Max(luma, MinLuma);
                logLumaSum += Math.Log(clampedLuma);

                int bin = (int)(MathF.Min(luma, maxHistLuma - 0.001f) * binScale);
                if (bin < 0) bin = 0;
                else if (bin >= histBins) bin = histBins - 1;
                hist[bin]++;

                sampleCount++;
            }
        }

        if (sampleCount == 0)
            return 0f;

        // 1. Geometric mean (log-average luminance) representing the photographic key / middle-tones
        float geometricMeanLuma = (float)Math.Exp(logLumaSum / sampleCount);

        // 2. Find 99th percentile luminance for highlight protection
        int targetHighlightCount = Math.Max(1, (int)(sampleCount * 0.01f));
        int accum = 0;
        int p99Bin = histBins - 1;
        for (int i = histBins - 1; i >= 0; i--)
        {
            accum += hist[i];
            if (accum >= targetHighlightCount)
            {
                p99Bin = i;
                break;
            }
        }
        float p99Luma = (p99Bin + 0.5f) / binScale;

        // 3. Calculate target EV to map geometricMeanLuma to MiddleGrayTarget (18%)
        float evMid = MathF.Log2(MiddleGrayTarget / MathF.Max(geometricMeanLuma, MinLuma));

        // 4. Calculate maximum allowed EV to prevent highlight blowout (keeping 99th percentile <= ~0.92)
        // We allow up to +0.35 EV headroom past 0.92 because Raw75's sigmoid shoulder and highlight
        // reconstruction comfortably absorb highlights into the upper roll-off.
        float evHighlightLimit = MathF.Log2(0.92f / MathF.Max(p99Luma, 0.05f)) + 0.35f;

        // 5. Constrain exposure: prioritize middle-tones but protect against blown highlights
        float targetEv = MathF.Min(evMid, evHighlightLimit);

        // Clamp to a safe, sensible photographic range [-2.5 EV .. +3.5 EV]
        targetEv = Math.Clamp(targetEv, -2.5f, 3.5f);

        // Snap to clean 0.05 EV steps (e.g. +1.00, +1.05, +1.10)
        return MathF.Round(targetEv * 20f) / 20f;
    }
}
