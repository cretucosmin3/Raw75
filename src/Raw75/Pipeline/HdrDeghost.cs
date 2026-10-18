using System;
using System.Collections.Generic;
using Raw75.Imaging;

namespace Raw75.Pipeline;

public enum HdrDeghostMode
{
    Off,
    Low,
    High
}

/// <summary>
/// Motion deghosting for multi-exposure HDR merge.
/// Evaluates log-space residuals against the EV-predicted reference frame.
/// Low mode: rejects frames with high residual (outlier motion suppression).
/// High mode: selects the single best-exposed frame in ghost regions (no ghost blend).
/// </summary>
internal static class HdrDeghost
{
    public const float LowStopThreshold = 1.5f;
    public const float HighStopThreshold = 1.0f;

    /// <summary>
    /// Computes per-pixel, per-frame weight multipliers for a tile [yStart .. yStart + tileH).
    /// Output buffer format: [pixel * nFrames + frameIndex], values in [0, 1].
    /// </summary>
    public static float[]? ComputeTileMask(
        IReadOnlyList<RasterBuffer> frames,
        IReadOnlyList<float> evs,
        int refIndex,
        HdrDeghostMode mode,
        int yStart,
        int tileH,
        int width,
        int totalH)
    {
        if (mode == HdrDeghostMode.Off || frames.Count < 2)
            return null;

        int nFrames = frames.Count;
        float[] mask = new float[tileH * width * nFrames];
        Array.Fill(mask, 1.0f);

        float refEv = evs[refIndex];
        float[] scales = new float[nFrames];
        for (int i = 0; i < nFrames; i++)
        {
            scales[i] = MathF.Pow(2f, evs[i] - refEv);
            if (scales[i] <= 1e-6f) scales[i] = 1e-6f;
        }

        RasterBuffer refBuf = frames[refIndex];
        float[]? refLin = refBuf.Linear;
        byte[]? refRgba = refBuf.Rgba;

        for (int r = 0; r < tileH; r++)
        {
            int y = yStart + r;
            if (y >= totalH) break;

            int rowOffset = y * width * 4;
            int tileRowBase = r * width * nFrames;

            for (int x = 0; x < width; x++)
            {
                int pxOffset = rowOffset + x * 4;
                int maskBase = tileRowBase + x * nFrames;

                float refR, refG, refB;
                if (refLin != null)
                {
                    refR = refLin[pxOffset];
                    refG = refLin[pxOffset + 1];
                    refB = refLin[pxOffset + 2];
                }
                else
                {
                    refR = refRgba![pxOffset] / 255f;
                    refG = refRgba[pxOffset + 1] / 255f;
                    refB = refRgba[pxOffset + 2] / 255f;
                }

                float refLuma = 0.2126f * refR + 0.7152f * refG + 0.0722f * refB;

                bool anyGhost = false;
                int bestFrame = refIndex;
                float bestQuality = -1f;

                for (int i = 0; i < nFrames; i++)
                {
                    float[]? lin = frames[i].Linear;
                    byte[]? rgba = frames[i].Rgba;

                    float rVal, gVal, bVal;
                    if (lin != null)
                    {
                        rVal = lin[pxOffset];
                        gVal = lin[pxOffset + 1];
                        bVal = lin[pxOffset + 2];
                    }
                    else
                    {
                        rVal = rgba![pxOffset] / 255f;
                        gVal = rgba[pxOffset + 1] / 255f;
                        bVal = rgba[pxOffset + 2] / 255f;
                    }

                    float actLuma = 0.2126f * rVal + 0.7152f * gVal + 0.0722f * bVal;
                    float predLuma = refLuma * scales[i];

                    // Quality score: preference for well-exposed midtones
                    float quality = 1f - MathF.Abs(2f * Math.Clamp(actLuma, 0f, 1f) - 1f);
                    if (quality > bestQuality)
                    {
                        bestQuality = quality;
                        bestFrame = i;
                    }

                    if (i == refIndex)
                        continue;

                    // Log-space residual (stops)
                    float logPred = MathF.Log2(Math.Max(predLuma, 1e-4f));
                    float logAct = MathF.Log2(Math.Max(actLuma, 1e-4f));
                    float diffStops = MathF.Abs(logAct - logPred);

                    // Don't flag shadows or extreme clipped highlights where sensor saturation or noise dominates
                    if ((actLuma < 0.02f && predLuma < 0.02f) || (actLuma > 0.95f && predLuma > 0.95f))
                        continue;

                    float thresh = mode == HdrDeghostMode.Low ? LowStopThreshold : HighStopThreshold;
                    if (diffStops > thresh)
                    {
                        // Soft Hermite transition over 1 stop to avoid harsh stepped edges
                        float t = Math.Clamp((diffStops - thresh) / 1.0f, 0f, 1f);
                        float frameWeight = (1f - t) * (1f - t) * (3f - 2f * (1f - t));
                        mask[maskBase + i] = frameWeight;
                        if (frameWeight < 0.5f)
                            anyGhost = true;
                    }
                }

                if (mode == HdrDeghostMode.High && anyGhost)
                {
                    // High mode: strictly lock to reference frame to avoid any ghost blending
                    for (int i = 0; i < nFrames; i++)
                    {
                        mask[maskBase + i] = (i == refIndex) ? 1.0f : 0f;
                    }
                }
            }
        }

        FeatherTileMask(mask, tileH, width, nFrames);

        return mask;
    }

    /// <summary>
    /// Separable spatial binomial smoothing (3x3) to feather deghost boundaries across pixels.
    /// </summary>
    private static void FeatherTileMask(float[] mask, int tileH, int width, int nFrames)
    {
        if (tileH <= 2 || width <= 2)
            return;

        float[] temp = new float[tileH * width * nFrames];

        // Horizontal pass: [1, 2, 1] / 4
        for (int r = 0; r < tileH; r++)
        {
            int rowBase = r * width * nFrames;
            for (int x = 0; x < width; x++)
            {
                int xm1 = Math.Max(0, x - 1);
                int xp1 = Math.Min(width - 1, x + 1);

                int idxC = rowBase + x * nFrames;
                int idxL = rowBase + xm1 * nFrames;
                int idxR = rowBase + xp1 * nFrames;

                for (int i = 0; i < nFrames; i++)
                {
                    temp[idxC + i] = 0.25f * mask[idxL + i] + 0.5f * mask[idxC + i] + 0.25f * mask[idxR + i];
                }
            }
        }

        // Vertical pass: [1, 2, 1] / 4
        for (int r = 0; r < tileH; r++)
        {
            int rm1 = Math.Max(0, r - 1);
            int rp1 = Math.Min(tileH - 1, r + 1);

            int rowBaseC = r * width * nFrames;
            int rowBaseU = rm1 * width * nFrames;
            int rowBaseD = rp1 * width * nFrames;

            for (int x = 0; x < width; x++)
            {
                int xOffset = x * nFrames;
                int idxC = rowBaseC + xOffset;
                int idxU = rowBaseU + xOffset;
                int idxD = rowBaseD + xOffset;

                for (int i = 0; i < nFrames; i++)
                {
                    mask[idxC + i] = 0.25f * temp[idxU + i] + 0.5f * temp[idxC + i] + 0.25f * temp[idxD + i];
                }
            }
        }
    }
}
