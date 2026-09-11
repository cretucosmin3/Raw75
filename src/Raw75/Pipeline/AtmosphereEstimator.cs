using System;
using Raw75.Imaging;

namespace Raw75.Pipeline;

/// <summary>
/// High-precision single-image atmospheric airlight estimator based on
/// Kaiming He et al.'s Dark Channel Prior (IEEE TPAMI 2011).
/// Analyzes the scene's dark channel 95th quantile and brightness distribution
/// to extract the atmospheric airlight vector A = (Ar, Ag, Ab) and maximum depth.
/// </summary>
internal static class AtmosphereEstimator
{
    private const int HistBins = 1024;
    private const float MaxDarkVal = 2.0f;
    private const float MaxBrightVal = 6.0f;

    internal static void Estimate(
        RasterBuffer raster,
        out float airR,
        out float airG,
        out float airB,
        out float depthMax)
    {
        // High quality default: natural neutral-cool daylight atmospheric scattering
        airR = 0.82f;
        airG = 0.86f;
        airB = 0.92f;
        depthMax = 3.2f;

        if (!raster.HasPixels || raster.Width <= 0 || raster.Height <= 0)
            return;

        int width = raster.Width;
        int height = raster.Height;

        // Subsample grid to ~20,000 - 35,000 points for sub-millisecond execution (< 0.5ms)
        int step = Math.Max(1, (int)MathF.Sqrt((width * height) / 30000f));

        // 1. Build Dark Channel Histogram (1024 bins)
        int[] darkHist = new int[HistBins];
        int totalSamples = 0;

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
                    r = rgba[idx] / 255f;
                    g = rgba[idx + 1] / 255f;
                    b = rgba[idx + 2] / 255f;
                }
                else break;

                float dark = MathF.Min(MathF.Max(r, 0f), MathF.Min(MathF.Max(g, 0f), MathF.Max(b, 0f)));
                int bin = Math.Clamp((int)((dark / MaxDarkVal) * (HistBins - 1)), 0, HistBins - 1);
                darkHist[bin]++;
                totalSamples++;
            }
        }

        if (totalSamples < 50) return;

        // 2. Find 95th Percentile of Dark Channel (crit_haze_level in atmospheric estimator)
        int targetHazyCount = Math.Max(1, (int)(totalSamples * 0.05f)); // Top 5% most hazy pixels
        int accHazy = 0;
        int critHazeBin = HistBins - 1;
        for (int i = HistBins - 1; i >= 0; i--)
        {
            accHazy += darkHist[i];
            if (accHazy >= targetHazyCount)
            {
                critHazeBin = i;
                break;
            }
        }
        float critHazeLevel = (critHazeBin / (float)(HistBins - 1)) * MaxDarkVal;

        // 3. Among the top 5% most hazy pixels, find the 95th percentile of brightness (R + G + B)
        int[] brightHist = new int[HistBins];
        int hazyPixelCount = 0;

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
                    r = rgba[idx] / 255f;
                    g = rgba[idx + 1] / 255f;
                    b = rgba[idx + 2] / 255f;
                }
                else break;

                float dark = MathF.Min(MathF.Max(r, 0f), MathF.Min(MathF.Max(g, 0f), MathF.Max(b, 0f)));
                if (dark >= critHazeLevel)
                {
                    float bright = MathF.Max(r, 0f) + MathF.Max(g, 0f) + MathF.Max(b, 0f);
                    int bBin = Math.Clamp((int)((bright / MaxBrightVal) * (HistBins - 1)), 0, HistBins - 1);
                    brightHist[bBin]++;
                    hazyPixelCount++;
                }
            }
        }

        if (hazyPixelCount < 5) return;

        int targetBrightCount = Math.Max(1, (int)(hazyPixelCount * 0.05f)); // Top 5% brightest among hazy
        int accBright = 0;
        int critBrightBin = HistBins - 1;
        for (int i = HistBins - 1; i >= 0; i--)
        {
            accBright += brightHist[i];
            if (accBright >= targetBrightCount)
            {
                critBrightBin = i;
                break;
            }
        }
        float critBrightness = (critBrightBin / (float)(HistBins - 1)) * MaxBrightVal;

        // 4. Average RGB of the brightest pixels among the most hazy pixels
        double sumR = 0.0;
        double sumG = 0.0;
        double sumB = 0.0;
        int count = 0;

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
                    r = rgba[idx] / 255f;
                    g = rgba[idx + 1] / 255f;
                    b = rgba[idx + 2] / 255f;
                }
                else break;

                float dark = MathF.Min(MathF.Max(r, 0f), MathF.Min(MathF.Max(g, 0f), MathF.Max(b, 0f)));
                float bright = MathF.Max(r, 0f) + MathF.Max(g, 0f) + MathF.Max(b, 0f);

                if (dark >= critHazeLevel && bright >= critBrightness)
                {
                    sumR += MathF.Max(r, 0f);
                    sumG += MathF.Max(g, 0f);
                    sumB += MathF.Max(b, 0f);
                    count++;
                }
            }
        }

        if (count > 0)
        {
            airR = Math.Clamp((float)(sumR / count), 0.15f, 2.5f);
            airG = Math.Clamp((float)(sumG / count), 0.15f, 2.5f);
            airB = Math.Clamp((float)(sumB / count), 0.15f, 2.5f);

            // Atmospheric distance_max formula: -1.125 * ln(crit_haze_level)
            if (critHazeLevel > 0.001f)
            {
                float d = -1.125f * MathF.Log(critHazeLevel);
                depthMax = Math.Clamp(d, 1.0f, 8.0f);
            }
            else
            {
                depthMax = 4.0f;
            }
        }
    }
}
