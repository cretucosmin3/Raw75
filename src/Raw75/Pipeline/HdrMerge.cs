using System;
using System.Collections.Generic;
using System.Threading;
using Raw75.Develop;
using Raw75.Imaging;

namespace Raw75.Pipeline;

/// <summary>
/// Scene-linear HDR exposure merge pipeline.
/// Merges bracketed exposures using Debevec-style weights on scene-linear samples.
/// Operates in memory-bounded horizontal tiles.
/// </summary>
internal static class HdrMerge
{
    public const int DefaultTileRows = 512;

    /// <summary>
    /// Photographic exposure EV (log2 relative exposure index).
    /// Higher EV = more exposure to the sensor (brighter frame),
    /// consistent with darkest -> brightest sorting, pred_i = ref * 2^(ev_i - ev_ref),
    /// and linear_i / 2^ev_i.
    /// </summary>
    public static float ComputeEv(PhotoMetadata? meta)
    {
        if (meta == null || meta.Aperture <= 0.01f || meta.ShutterSpeed <= 1e-6f || meta.Iso <= 0.1f)
            return 0f;

        return ComputeEv(meta.ShutterSpeed, meta.Aperture, meta.Iso);
    }

    public static float ComputeEv(float shutter, float aperture, float iso)
    {
        if (aperture <= 0.01f || shutter <= 1e-6f || iso <= 0.1f)
            return 0f;

        return MathF.Log2((shutter * (iso / 100f)) / (aperture * aperture));
    }

    /// <summary>
    /// Standard APEX EV formula: log2(Aperture^2 / Shutter) + log2(Iso / 100).
    /// </summary>
    public static float ComputeApexEv(PhotoMetadata? meta)
    {
        if (meta == null || meta.Aperture <= 0.01f || meta.ShutterSpeed <= 1e-6f || meta.Iso <= 0.1f)
            return 0f;

        return MathF.Log2((meta.Aperture * meta.Aperture) / Math.Max(meta.ShutterSpeed, 1e-6f))
             + MathF.Log2(Math.Max(meta.Iso, 1f) / 100f);
    }

    /// <summary>
    /// Weighting envelope over [0, 1]. Smooth hermite roll-off near highlights,
    /// smooth taper near shadows with non-zero floor to prevent zero-weight dropout.
    /// </summary>
    public static float Hat(float v)
    {
        if (v >= 0.995f)
            return 0f;
        if (v <= 0f)
            return 1e-4f;

        // Smooth roll-off near clipping: [0.85, 0.995]
        if (v > 0.85f)
        {
            float t = (0.995f - v) / (0.995f - 0.85f);
            return t * t * (3f - 2f * t);
        }

        // Shadows [0, 0.02]: smooth transition up to full weight with floor
        if (v < 0.02f)
        {
            float t = v / 0.02f;
            float smooth = t * t * (3f - 2f * t);
            return 1e-4f + (1f - 1e-4f) * smooth;
        }

        return 1.0f;
    }

    /// <summary>
    /// Find the reference frame index whose EV is closest to the median EV.
    /// </summary>
    public static int FindMedianEvIndex(IReadOnlyList<float> evs)
    {
        if (evs == null || evs.Count == 0)
            return 0;
        if (evs.Count == 1)
            return 0;

        var sorted = new List<(float ev, int idx)>(evs.Count);
        for (int i = 0; i < evs.Count; i++)
            sorted.Add((evs[i], i));
        sorted.Sort((a, b) => a.ev.CompareTo(b.ev));

        int medianPos = sorted.Count / 2;
        return sorted[medianPos].idx;
    }

    /// <summary>
    /// Merge multiple aligned same-size scene-linear buffers into a single linear buffer.
    /// Memory bounded: processes in tiles of <paramref name="tileRows"/>.
    /// </summary>
    public static RasterBuffer Merge(
        IReadOnlyList<RasterBuffer> frames,
        IReadOnlyList<float> evs,
        int refIndex = -1,
        int tileRows = DefaultTileRows,
        CancellationToken ct = default,
        Action<float>? progress = null,
        Func<int, int, int, int, float[]?>? deghostMaskProvider = null)
    {
        if (frames == null || frames.Count < 2)
            throw new ArgumentException("At least two frames are required for HDR merge.", nameof(frames));
        if (frames.Count > 7)
            throw new ArgumentException("A maximum of 7 frames can be merged.", nameof(frames));
        if (evs == null || evs.Count != frames.Count)
            throw new ArgumentException("EV array must match the number of frames.", nameof(evs));

        int width = frames[0].Width;
        int height = frames[0].Height;

        for (int i = 1; i < frames.Count; i++)
        {
            if (frames[i].Width != width || frames[i].Height != height)
                throw new InvalidOperationException($"Frame {i} size ({frames[i].Width}×{frames[i].Height}) does not match frame 0 ({width}×{height}).");
        }

        if (refIndex < 0 || refIndex >= frames.Count)
            refIndex = FindMedianEvIndex(evs);

        float refEv = evs[refIndex];

        // Scales to normalize each frame to the reference frame's exposure
        float[] scales = new float[frames.Count];
        for (int i = 0; i < frames.Count; i++)
        {
            scales[i] = MathF.Pow(2f, evs[i] - refEv);
            if (scales[i] <= 1e-7f)
                scales[i] = 1e-7f;
        }

        // Find darkest frame index (least exposure / scale) for fallback on all-clipped pixels
        int darkestIdx = 0;
        float minScale = scales[0];
        for (int i = 1; i < frames.Count; i++)
        {
            if (scales[i] < minScale)
            {
                minScale = scales[i];
                darkestIdx = i;
            }
        }

        float[] merged = new float[width * height * 4];
        int rowsPerTile = Math.Clamp(tileRows, 16, 2048);
        int numTiles = (height + rowsPerTile - 1) / rowsPerTile;

        for (int t = 0; t < numTiles; t++)
        {
            ct.ThrowIfCancellationRequested();

            int yStart = t * rowsPerTile;
            int yEnd = Math.Min(height, yStart + rowsPerTile);
            int currentTileH = yEnd - yStart;

            float[]? deghostWeights = deghostMaskProvider?.Invoke(yStart, currentTileH, width, height);

            MergeTile(
                frames, scales, darkestIdx,
                width, yStart, yEnd, merged,
                deghostWeights);

            progress?.Invoke((float)(t + 1) / numTiles);
        }

        return new RasterBuffer(merged, width, height);
    }

    private static void MergeTile(
        IReadOnlyList<RasterBuffer> frames,
        float[] scales,
        int darkestIdx,
        int width,
        int yStart,
        int yEnd,
        float[] dst,
        float[]? deghostWeights)
    {
        int nFrames = frames.Count;

        for (int y = yStart; y < yEnd; y++)
        {
            int rowOffset = y * width * 4;
            int tilePixelIndex = (y - yStart) * width;

            for (int x = 0; x < width; x++)
            {
                int pxOffset = rowOffset + x * 4;
                int maskBase = deghostWeights != null ? (tilePixelIndex + x) * nFrames : -1;

                float sumR = 0f;
                float sumG = 0f;
                float sumB = 0f;
                float sumW = 0f;

                for (int i = 0; i < nFrames; i++)
                {
                    float[]? lin = frames[i].Linear;
                    byte[]? rgba = frames[i].Rgba;

                    float r = lin != null ? lin[pxOffset] : (rgba![pxOffset] / 255f);
                    float g = lin != null ? lin[pxOffset + 1] : (rgba![pxOffset + 1] / 255f);
                    float b = lin != null ? lin[pxOffset + 2] : (rgba![pxOffset + 2] / 255f);

                    // darktable rule: link channel weights to max channel
                    // to prevent chromatic color fringing along high-contrast edges.
                    float maxC = MathF.Max(r, MathF.Max(g, b));
                    float w = Hat(maxC);

                    if (maskBase >= 0)
                    {
                        w *= deghostWeights![maskBase + i];
                    }

                    if (w > 0f)
                    {
                        float invScale = 1f / scales[i];
                        sumR += w * (r * invScale);
                        sumG += w * (g * invScale);
                        sumB += w * (b * invScale);
                        sumW += w;
                    }
                }

                if (sumW > 1e-6f)
                {
                    float invSumW = 1f / sumW;
                    dst[pxOffset]     = sumR * invSumW;
                    dst[pxOffset + 1] = sumG * invSumW;
                    dst[pxOffset + 2] = sumB * invSumW;
                }
                else
                {
                    // All clipped or zero weight: fallback to darkest frame's value normalized
                    float[]? darkLin = frames[darkestIdx].Linear;
                    byte[]? darkRgba = frames[darkestIdx].Rgba;
                    float invDarkScale = 1f / scales[darkestIdx];
                    dst[pxOffset]     = (darkLin != null ? darkLin[pxOffset]     : darkRgba![pxOffset]     / 255f) * invDarkScale;
                    dst[pxOffset + 1] = (darkLin != null ? darkLin[pxOffset + 1] : darkRgba![pxOffset + 1] / 255f) * invDarkScale;
                    dst[pxOffset + 2] = (darkLin != null ? darkLin[pxOffset + 2] : darkRgba![pxOffset + 2] / 255f) * invDarkScale;
                }

                dst[pxOffset + 3] = 1.0f; // Alpha
            }
        }
    }
}
