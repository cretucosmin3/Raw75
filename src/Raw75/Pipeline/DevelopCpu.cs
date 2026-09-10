using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
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
        int fw = (rot & 1) != 0 ? sh : sw;
        int fh = (rot & 1) != 0 ? sw : sh;
        bool hasCrop = s.HasCrop;
        float cx = s.CropX, cy = s.CropY, cw = s.CropW, ch = s.CropH;
        int dw = hasCrop ? Math.Max(1, (int)Math.Round(fw * cw)) : fw;
        int dh = hasCrop ? Math.Max(1, (int)Math.Round(fh * ch)) : fh;
        byte[] dst = new byte[dw * dh * 4];

        float temp = s.EnableWhiteBalance ? s.Temperature / 100f : 0f;
        float tint = s.EnableWhiteBalance ? s.Tint / 100f : 0f;
        float ev = s.EnableExposure ? MathF.Pow(2f, s.Exposure) : 1f;
        float contrast = s.EnableExposure ? s.Contrast / 100f : 0f;
        float hi = s.EnableHdr ? s.Highlights / 100f : 0f;
        float shd = s.EnableHdr ? s.Shadows / 100f : 0f;
        float whites = s.EnableHdr ? s.Whites / 100f : 0f;
        float blacks = s.EnableHdr ? s.Blacks / 100f : 0f;
        float vib = s.EnableHsl ? s.Vibrance / 100f : 0f;
        float sat = s.EnableExposure ? s.Saturation / 100f : 0f;
        float sharp = !s.EnableDetail ? 0f : s.Sharpen / 150f;
        float noiseVal = s.EnableDetail ? s.Noise : 0f;
        if (noiseVal == 0f && s.EnableDetail && (s.DenoiseLuma > 0f || s.DenoiseChroma > 0f))
            noiseVal = -Math.Max(s.DenoiseLuma, s.DenoiseChroma);
        bool doDenoise = noiseVal < -0.5f;
        bool doGrain = noiseVal > 0.5f;
        float denoiseAmt = doDenoise ? -noiseVal / 100f : 0f;
        float grainAmt = doGrain ? noiseVal / 100f : 0f;

        float straight = s.EnableGeometry ? s.Straighten : 0f;
        bool flipH = s.FlipH;
        bool flipV = s.FlipV;
        ToneMode toneMode = s.EnableTone ? s.ToneMode : ToneMode.Sigmoid;
        SigmoidParams sig = (s.EnableTone && toneMode == ToneMode.Sigmoid)
            ? SigmoidParams.Compute(s.SigmoidContrast, s.SigmoidSkew)
            : (s.EnableTone ? default : SigmoidParams.Compute(1.0f, 0.0f));
        HighlightMode reconMode = s.EnableReconstruction ? s.ReconstructionMode : HighlightMode.Off;
        float hlThreshold = s.HighlightThreshold;
        bool doRecon = reconMode != HighlightMode.Off;
        float clipThresh = Math.Clamp(hlThreshold * 0.985f, 0.05f, 1.0f);

        bool hasColorRecon = s.EnableReconstruction && s.ColorReconstructionAmount > 0.001f;
        bool hasLocalContrast = s.EnableLocalContrast && (
            Math.Abs(s.LocalContrastDetail) > 0.001f ||
            Math.Abs(s.LocalContrastHighlights) > 0.001f ||
            Math.Abs(s.LocalContrastShadows) > 0.001f ||
            Math.Abs(s.LocalContrastMidtones - 50.0f) > 0.5f ||
            Math.Abs(s.Texture) > 0.001f);
        bool neighbor = doDenoise;
        bool needBuffer = hasColorRecon || hasLocalContrast || neighbor || doGrain;

        bool linearSrc = src.HasLinear;
        float[]? linSrc = src.Linear;
        byte[]? rgba = src.Rgba;
        if (!linearSrc && rgba == null)
            return new RasterBuffer(new byte[dw * dh * 4], dw, dh);

        bool remap = hasCrop || rot != 0 || flipH || flipV || Math.Abs(straight) > 0.05f;
        float[]? preLook = hasColorRecon ? new float[dw * dh * 3] : null;
        float[]? look = needBuffer ? new float[dw * dh * 3] : null;
        for (int y = 0; y < dh; y++)
        {
            for (int x = 0; x < dw; x++)
            {
                int sx, sy;
                float u = (x + 0.5f) / dw;
                float v = (y + 0.5f) / dh;
                if (hasCrop)
                {
                    u = cx + u * cw;
                    v = cy + v * ch;
                }
                if (!remap)
                {
                    sx = Math.Clamp((int)(u * sw), 0, sw - 1);
                    sy = Math.Clamp((int)(v * sh), 0, sh - 1);
                }
                else
                    MapSrc(u, v, fw, fh, rot, flipH, flipV, straight, sw, sh, out sx, out sy);
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

                // Lens vignetting compensation / creative vignette
                if (s.EnableGeometry && MathF.Abs(s.VignetteAmount) > 0.001f)
                {
                    float aspect = (float)sw / Math.Max(sh, 1);
                    float su = (sx + 0.5f) / sw;
                    float sv = (sy + 0.5f) / sh;
                    float dx = (su - 0.5f) * aspect;
                    float dy = (sv - 0.5f);
                    float maxDist = MathF.Sqrt(0.25f * aspect * aspect + 0.25f);
                    float d = MathF.Sqrt(dx * dx + dy * dy) / maxDist;
                    float mp = Math.Clamp(s.VignetteMidpoint / 100f, 0.05f, 0.95f);
                    float vFactor = Smooth(d, mp * 0.40f, 1.15f);
                    float gain = MathF.Pow(2f, (s.VignetteAmount / 100f) * 2.5f * vFactor);
                    r = MathF.Max(r * gain, 0f);
                    g = MathF.Max(g * gain, 0f);
                    b = MathF.Max(b * gain, 0f);
                }

                // Highlight reconstruction (step 2)
                // ref: highlight reconstruction (opposed & LCh)
                if (reconMode == HighlightMode.Off)
                {
                    if (hlThreshold < 0.999f)
                    {
                        r = MathF.Min(r, hlThreshold);
                        g = MathF.Min(g, hlThreshold);
                        b = MathF.Min(b, hlThreshold);
                    }
                }
                else if (doRecon)
                {
                    if (r >= clipThresh || g >= clipThresh || b >= clipThresh)
                    {
                        if (reconMode == HighlightMode.LCh)
                        {
                            ApplyLchReconstruction(
                                sx, sy, sw, sh, linearSrc, linSrc, rgba,
                                clipThresh, ref r, ref g, ref b);
                        }
                        else
                        {
                            ApplyOpposedReconstruction(
                                sx, sy, sw, sh, linearSrc, linSrc, rgba,
                                clipThresh, ref r, ref g, ref b);
                        }

                        // Smooth highlight rolloff: compress excess into [clipThresh..1.0]
                        // so recovered highlights show visible texture instead of blowing out
                        float maxCh = Math.Max(r, Math.Max(g, b));
                        if (maxCh > clipThresh)
                        {
                            float over = maxCh - clipThresh;
                            float headroom = Math.Max(1.0f - clipThresh, 0.08f);
                            float compressed = clipThresh + headroom * (1f - MathF.Exp(-over / headroom));
                            float scale = compressed / maxCh;
                            r *= scale;
                            g *= scale;
                            b *= scale;
                        }
                    }
                }

                if (preLook != null)
                {
                    int pi = (y * dw + x) * 3;
                    preLook[pi] = r;
                    preLook[pi + 1] = g;
                    preLook[pi + 2] = b;
                }
                else
                {
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
                        ToneCurveSrgb(r, g, b, toneMode, sig, out float or, out float og, out float ob);
                        dst[di] = ToByte(or);
                        dst[di + 1] = ToByte(og);
                        dst[di + 2] = ToByte(ob);
                        dst[di + 3] = 255;
                    }
                }
            }
        }

        if (preLook != null)
        {
            // Color reconstruction (step 2)
            // ref: color reconstruction
            if (fast)
                ApplyColorReconstructionFast(preLook, dw, dh, hlThreshold, s.ColorReconstructionAmount, s.ColorReconstructionSpatial);
            else
                ApplyColorReconstructionBilateral(preLook, dw, dh, hlThreshold, s.ColorReconstructionAmount, s.ColorReconstructionSpatial);

            for (int y = 0; y < dh; y++)
            {
                for (int x = 0; x < dw; x++)
                {
                    int i = (y * dw + x) * 3;
                    float r = preLook[i];
                    float g = preLook[i + 1];
                    float b = preLook[i + 2];
                    ApplyLook(ref r, ref g, ref b, temp, tint, ev, contrast, hi, shd, whites, blacks, vib, sat, s);
                    look![i] = r;
                    look[i + 1] = g;
                    look[i + 2] = b;
                }
            }
        }

        if (look == null)
            return new RasterBuffer(dst, dw, dh);

        // Local contrast (step 8)
        // ref: bilateral filter & local laplacian
        if (hasLocalContrast)
        {
            ApplyLocalContrast(look, dw, dh, s.LocalContrastDetail, s.LocalContrastMidtones, s.LocalContrastShadows, s.LocalContrastHighlights, s.Texture / 100f, fast);
        }

        // Denoise (step 9) + Tone curve (step 12)
        for (int y = 0; y < dh; y++)
        {
            for (int x = 0; x < dw; x++)
            {
                int li = (y * dw + x) * 3;
                float r = look[li];
                float g = look[li + 1];
                float b = look[li + 2];
                if (doDenoise)
                {
                    float lc = Luma(r, g, b);
                    float c0_r = r - lc;
                    float c0_g = g - lc;
                    float c0_b = b - lc;

                    float rangeSigma = 0.05f + 0.35f * denoiseAmt;
                    float sumL = lc;
                    float sumW_L = 1.0f;
                    float sumCr = c0_r, sumCg = c0_g, sumCb = c0_b;
                    float sumW_C = 1.0f;

                    void Tap(int dx, int dy, float sw)
                    {
                        SampleLook(look, dw, dh, x + dx, y + dy, out float nr, out float ng, out float nb);
                        float nl = Luma(nr, ng, nb);
                        float wl = sw * MathF.Exp(-MathF.Abs(nl - lc) / rangeSigma);
                        sumL += nl * wl;
                        sumW_L += wl;
                        sumCr += (nr - nl) * sw;
                        sumCg += (ng - nl) * sw;
                        sumCb += (nb - nl) * sw;
                        sumW_C += sw;
                    }

                    // Inner taps (radius 1 & 2)
                    Tap(-1, 0, 1.0f); Tap(1, 0, 1.0f); Tap(0, -1, 1.0f); Tap(0, 1, 1.0f);
                    Tap(-2, 0, 0.8f); Tap(2, 0, 0.8f); Tap(0, -2, 0.8f); Tap(0, 2, 0.8f);

                    if (!fast)
                    {
                        // Ring 1 diagonals
                        Tap(-1, -1, 0.8f); Tap(1, -1, 0.8f); Tap(-1, 1, 0.8f); Tap(1, 1, 0.8f);

                        // Ring 2 diagonals & ring 3
                        Tap(-2, -2, 0.5f); Tap(2, -2, 0.5f); Tap(-2, 2, 0.5f); Tap(2, 2, 0.5f);
                        Tap(-3, 0, 0.4f); Tap(3, 0, 0.4f); Tap(0, -3, 0.4f); Tap(0, 3, 0.4f);

                        // Ring 4
                        Tap(-4, 0, 0.25f); Tap(4, 0, 0.25f); Tap(0, -4, 0.25f); Tap(0, 4, 0.25f);
                    }

                    float kL = fast ? denoiseAmt * 0.80f : denoiseAmt;
                    float kC = fast ? denoiseAmt * 0.90f : denoiseAmt;

                    float finalL = lc + (sumL / sumW_L - lc) * kL;
                    float finalCr = c0_r + (sumCr / sumW_C - c0_r) * kC;
                    float finalCg = c0_g + (sumCg / sumW_C - c0_g) * kC;
                    float finalCb = c0_b + (sumCb / sumW_C - c0_b) * kC;

                    r = MathF.Max(0f, finalL + finalCr);
                    g = MathF.Max(0f, finalL + finalCg);
                    b = MathF.Max(0f, finalL + finalCb);
                }

                if (doGrain)
                {
                    float lum = Luma(r, g, b);
                    float midtoneCurve = MathF.Sin(Math.Clamp(lum, 0f, 1f) * MathF.PI);
                    uint n1 = (uint)(x * 73856093 ^ y * 19349663 ^ 12345);
                    n1 = (n1 ^ (n1 >> 13)) * 0x5bd1e995;
                    n1 ^= n1 >> 15;
                    float g1 = (n1 & 0xFFFF) / 32768.0f - 1.0f;

                    uint n2 = (uint)((x >> 1) * 73856093 ^ (y >> 1) * 19349663 ^ 67891);
                    n2 = (n2 ^ (n2 >> 13)) * 0x5bd1e995;
                    n2 ^= n2 >> 15;
                    float g2 = (n2 & 0xFFFF) / 32768.0f - 1.0f;

                    float rawGrain = g1 * 0.70f + g2 * 0.30f;
                    float grain = rawGrain * grainAmt * 0.12f * (0.35f + 0.65f * midtoneCurve);
                    r = MathF.Max(0f, r + grain);
                    g = MathF.Max(0f, g + grain);
                    b = MathF.Max(0f, b + grain);
                }

                int di = (y * dw + x) * 4;
                ToneCurveSrgb(r, g, b, toneMode, sig, out float or, out float og, out float ob);
                dst[di] = ToByte(or);
                dst[di + 1] = ToByte(og);
                dst[di + 2] = ToByte(ob);
                dst[di + 3] = 255;
            }
        }

        if (sharp > 0.001f)
        {
            if (fast)
                UnsharpBytesFast(dst, dw, dh, sharp);
            else
                UnsharpBytes(dst, dw, dh, sharp);
        }
        return new RasterBuffer(dst, dw, dh);
    }

    private static void UnsharpBytes(byte[] dst, int dw, int dh, float sharp)
    {
        byte[] copy = (byte[])dst.Clone();
        float amt = sharp * 2.4f;
        for (int y = 0; y < dh; y++)
        {
            for (int x = 0; x < dw; x++)
            {
                int di = (y * dw + x) * 4;
                int accR = 0, accG = 0, accB = 0;
                AccByte(copy, dw, dh, x - 1, y, ref accR, ref accG, ref accB);
                AccByte(copy, dw, dh, x + 1, y, ref accR, ref accG, ref accB);
                AccByte(copy, dw, dh, x, y - 1, ref accR, ref accG, ref accB);
                AccByte(copy, dw, dh, x, y + 1, ref accR, ref accG, ref accB);
                float nr = accR * 0.25f, ng = accG * 0.25f, nb = accB * 0.25f;
                dst[di] = ToByte((copy[di] + (copy[di] - nr) * amt) / 255f);
                dst[di + 1] = ToByte((copy[di + 1] + (copy[di + 1] - ng) * amt) / 255f);
                dst[di + 2] = ToByte((copy[di + 2] + (copy[di + 2] - nb) * amt) / 255f);
            }
        }
    }

    private static void UnsharpBytesFast(byte[] dst, int dw, int dh, float sharp)
    {
        byte[] copy = (byte[])dst.Clone();
        float amt = sharp * 2.0f;
        for (int y = 1; y < dh - 1; y++)
        {
            int row = y * dw * 4;
            int prevRow = (y - 1) * dw * 4;
            int nextRow = (y + 1) * dw * 4;
            for (int x = 1; x < dw - 1; x++)
            {
                int di = row + x * 4;
                float nr = (copy[di - 4] + copy[di + 4] + copy[prevRow + x * 4] + copy[nextRow + x * 4]) * 0.25f;
                float ng = (copy[di - 3] + copy[di + 5] + copy[prevRow + x * 4 + 1] + copy[nextRow + x * 4 + 1]) * 0.25f;
                float nb = (copy[di - 2] + copy[di + 6] + copy[prevRow + x * 4 + 2] + copy[nextRow + x * 4 + 2]) * 0.25f;

                dst[di] = ToByte((copy[di] + (copy[di] - nr) * amt) / 255f);
                dst[di + 1] = ToByte((copy[di + 1] + (copy[di + 1] - ng) * amt) / 255f);
                dst[di + 2] = ToByte((copy[di + 2] + (copy[di + 2] - nb) * amt) / 255f);
            }
        }
    }

    private static void AccByte(byte[] p, int dw, int dh, int x, int y, ref int r, ref int g, ref int b)
    {
        if (x < 0) x = 0; else if (x >= dw) x = dw - 1;
        if (y < 0) y = 0; else if (y >= dh) y = dh - 1;
        int i = (y * dw + x) * 4;
        r += p[i];
        g += p[i + 1];
        b += p[i + 2];
    }

    private static void ApplyLook(
        ref float r, ref float g, ref float b,
        float temp, float tint, float ev, float contrast,
        float hi, float shd, float whites, float blacks,
        float vib, float sat, DevelopSettings s)
    {
        // 1. Multiplicative chromatic white balance (preserves black levels and photon ratios)
        float wbR = MathF.Exp(0.55f * temp + 0.18f * tint);
        float wbB = MathF.Exp(-0.55f * temp - 0.18f * tint);
        float wbG = MathF.Exp(-0.35f * tint);
        r *= wbR;
        g *= wbG;
        b *= wbB;
        if (r < 0f) r = 0f;
        if (g < 0f) g = 0f;
        if (b < 0f) b = 0f;

        // 2. Linear Exposure gain
        r *= ev;
        g *= ev;
        b *= ev;

        // 3. Match Gray (target middle gray at 0.18)
        if (s.EnableHsl && s.MatchGray)
        {
            float lum0 = Luma(r, g, b);
            if (lum0 < 1e-4f) lum0 = 1e-4f;
            float mg = 1f + (0.18f / lum0 - 1f) * 0.35f;
            r *= mg;
            g *= mg;
            b *= mg;
        }

        // 4. High Dynamic Range (HDR) in perceptual log-EV space relative to 18% middle gray
        float lum = Luma(r, g, b);
        if (lum < 1e-5f) lum = 1e-5f;
        float evVal = MathF.Log2(lum / 0.18f);

        // Highlights: Compressive rational shoulder to recover texture without muddy flat gray
        if (MathF.Abs(hi) > 0.001f)
        {
            if (evVal > 0f)
            {
                if (hi < 0f)
                {
                    float hlAmt = -hi;
                    evVal = evVal / (1f + hlAmt * 0.40f * evVal);
                }
                else
                {
                    evVal += hi * 0.45f * Smooth(evVal, 0f, 3.5f);
                }
            }
        }

        // Shadows: Grounded lift/drop with pinned black anchor (prevents milky haze)
        if (MathF.Abs(shd) > 0.001f)
        {
            if (evVal < 0f)
            {
                float t = MathF.Max(1f + evVal / 7f, 0f);
                float w = 1f - Smooth(evVal, -6f, 0f);
                if (shd > 0f)
                    evVal += shd * 1.50f * t * w;
                else
                    evVal += shd * 1.20f * t * w;
            }
        }

        // Whites: Range anchor for extreme highlight/specular rolloff
        if (MathF.Abs(whites) > 0.001f)
        {
            if (evVal > 1f)
            {
                float ww = Smooth(evVal, 1f, 3.5f);
                evVal += whites * 1.25f * ww;
            }
        }

        // Blacks: Range anchor for deep toe/shadow threshold
        if (MathF.Abs(blacks) > 0.001f)
        {
            if (evVal < -1f)
            {
                float bw = 1f - Smooth(evVal, -5.5f, -1.2f);
                evVal += blacks * 1.10f * bw;
            }
        }

        float targetLum = 0.18f * MathF.Pow(2f, evVal);
        float hdrScale = MathF.Max(targetLum, 0f) / lum;
        r *= hdrScale;
        g *= hdrScale;
        b *= hdrScale;

        // 5. Contrast: Smooth S-curve pivoted at 18% middle gray with soft roll-off
        if (MathF.Abs(contrast) > 0.0005f)
        {
            float lumC = Luma(r, g, b);
            if (lumC < 1e-5f) lumC = 1e-5f;
            float evC = MathF.Log2(lumC / 0.18f);
            float deltaEv = contrast * 0.85f * (evC / (1f + 0.12f * evC * evC));
            float evNew = evC + deltaEv;
            float newLumC = 0.18f * MathF.Pow(2f, evNew);
            float contrastScale = MathF.Max(newLumC, 0f) / lumC;
            r *= contrastScale;
            g *= contrastScale;
            b *= contrastScale;
        }

        if (r < 0f) r = 0f;
        if (g < 0f) g = 0f;
        if (b < 0f) b = 0f;

        // Dehaze: Atmospheric scattering transmission recovery / mist
        if (s.EnableLocalContrast && MathF.Abs(s.Dehaze) > 0.001f)
        {
            float dehazeAmt = s.Dehaze / 100f;
            float lumD = MathF.Max(Luma(r, g, b), 0.0001f);
            float darkCh = MathF.Min(r, MathF.Min(g, b));
            float hazeRatio = Math.Clamp(darkCh / lumD, 0f, 1f);
            if (dehazeAmt > 0f)
            {
                float t = Math.Clamp(1f - dehazeAmt * hazeRatio * 0.70f, 0.20f, 1f);
                float airScale = MathF.Min(lumD, 1f) * (1f - t);
                r = MathF.Max((r - 0.80f * airScale) / t, 0f);
                g = MathF.Max((g - 0.84f * airScale) / t, 0f);
                b = MathF.Max((b - 0.90f * airScale) / t, 0f);
            }
            else
            {
                float mist = -dehazeAmt * 0.50f * hazeRatio;
                float airScale = MathF.Max(lumD, 0.2f);
                r = r * (1f - mist) + 0.80f * airScale * mist;
                g = g * (1f - mist) + 0.84f * airScale * mist;
                b = b * (1f - mist) + 0.90f * airScale * mist;
            }
        }

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

        // Color Grading (Split Toning)
        if (s.EnableHsl && (s.GradingShadowSat > 0.001f || s.GradingHighlightSat > 0.001f))
        {
            float lumG = MathF.Max(Luma(r, g, b), 0.00001f);
            float evG = MathF.Log2(lumG / 0.18f) + (s.GradingBalance / 100f) * 1.5f;
            float shdW = 1f - Smooth(evG, -2.5f, 0.5f);
            float hlW = Smooth(evG, -0.5f, 2.5f);

            if (s.GradingShadowSat > 0.001f && shdW > 0.001f)
            {
                HslToRgb(s.GradingShadowHue, 1f, 0.5f, out float tr, out float tg, out float tb);
                float tLum = Luma(tr, tg, tb);
                float vr = tr - tLum;
                float vg = tg - tLum;
                float vb = tb - tLum;
                float amt = (s.GradingShadowSat / 100f) * shdW * 0.50f * lumG;
                r = MathF.Max(r + vr * amt, 0f);
                g = MathF.Max(g + vg * amt, 0f);
                b = MathF.Max(b + vb * amt, 0f);
            }

            if (s.GradingHighlightSat > 0.001f && hlW > 0.001f)
            {
                HslToRgb(s.GradingHighlightHue, 1f, 0.5f, out float tr, out float tg, out float tb);
                float tLum = Luma(tr, tg, tb);
                float vr = tr - tLum;
                float vg = tg - tLum;
                float vb = tb - tLum;
                float amt = (s.GradingHighlightSat / 100f) * hlW * 0.50f * lumG;
                r = MathF.Max(r + vr * amt, 0f);
                g = MathF.Max(g + vg * amt, 0f);
                b = MathF.Max(b + vb * amt, 0f);
            }
        }
    }

    // ref: sigmoid tone curve
    private static void ToneCurveSrgb(float r, float g, float b, ToneMode mode, in SigmoidParams sig, out float or, out float og, out float ob)
    {
        if (mode == ToneMode.Filmic)
        {
            or = ToSrgb1(Filmic1(r));
            og = ToSrgb1(Filmic1(g));
            ob = ToSrgb1(Filmic1(b));
        }
        else
        {
            or = ToSrgb1(sig.Eval(r));
            og = ToSrgb1(sig.Eval(g));
            ob = ToSrgb1(sig.Eval(b));
        }
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

    private static void HslToRgb(float hDeg, float s, float l, out float r, out float g, out float b)
    {
        float h = (hDeg % 360f) / 360f;
        if (h < 0f) h += 1f;
        s = Math.Clamp(s, 0f, 1f);
        l = Math.Clamp(l, 0f, 1f);
        if (s < 1e-5f)
        {
            r = g = b = l;
            return;
        }
        float q = l < 0.5f ? l * (1f + s) : l + s - l * s;
        float p = 2f * l - q;
        r = Hue2Rgb(p, q, h + 0.3333333f);
        g = Hue2Rgb(p, q, h);
        b = Hue2Rgb(p, q, h - 0.3333333f);
    }

    private static float Hue2Rgb(float p, float q, float t)
    {
        if (t < 0f) t += 1f;
        if (t > 1f) t -= 1f;
        if (t < 0.1666667f) return p + (q - p) * 6f * t;
        if (t < 0.5f) return q;
        if (t < 0.6666667f) return p + (q - p) * (0.6666667f - t) * 6f;
        return p;
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SampleLook(float[] look, int dw, int dh, int x, int y, out float r, out float g, out float b)
    {
        if (x < 0) x = 0;
        else if (x >= dw) x = dw - 1;
        if (y < 0) y = 0;
        else if (y >= dh) y = dh - 1;
        int i = (y * dw + x) * 3;
        r = look[i];
        g = look[i + 1];
        b = look[i + 2];
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
        float u, float v, int fw, int fh, int rot, bool flipH, bool flipV, float straightDeg,
        int sw, int sh, out int sx, out int sy)
    {
        float p = u;
        float q = v;

        if (Math.Abs(straightDeg) > 0.001f && fw > 0 && fh > 0)
        {
            float a = straightDeg * 0.01745329251f;
            float c = MathF.Cos(a);
            float sn = MathF.Sin(a);
            float qx = p - 0.5f;
            float qy = q - 0.5f;
            float invAspect = fh / (float)fw;
            float aspect = fw / (float)fh;
            p = qx * c - qy * invAspect * sn + 0.5f;
            q = qx * aspect * sn + qy * c + 0.5f;
        }

        rot &= 3;
        if (rot == 1) { float t = p; p = q; q = 1f - t; }
        else if (rot == 2) { p = 1f - p; q = 1f - q; }
        else if (rot == 3) { float t = p; p = 1f - q; q = t; }

        if (flipH) p = 1f - p;
        if (flipV) q = 1f - q;

        sx = Math.Clamp((int)(p * sw), 0, sw - 1);
        sy = Math.Clamp((int)(q * sh), 0, sh - 1);
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

    // ref: highlight reconstruction (opposed)
    private static void ApplyOpposedReconstruction(
        int sx, int sy, int sw, int sh, bool linearSrc, float[]? linSrc, byte[]? rgba,
        float clipThresh, ref float r, ref float g, ref float b)
    {
        float u0r = MathF.Pow(MathF.Max(r, 0f), 0.33333333f);
        float u0g = MathF.Pow(MathF.Max(g, 0f), 0.33333333f);
        float u0b = MathF.Pow(MathF.Max(b, 0f), 0.33333333f);

        float opp0r = 0.5f * (u0g + u0b);
        float opp0g = 0.5f * (u0r + u0b);
        float opp0b = 0.5f * (u0r + u0g);

        float ref0r = opp0r * opp0r * opp0r;
        float ref0g = opp0g * opp0g * opp0g;
        float ref0b = opp0b * opp0b * opp0b;

        float sumChromaR = 0f, sumChromaG = 0f, sumChromaB = 0f;
        float cntChromaR = 0f, cntChromaG = 0f, cntChromaB = 0f;
        float loThresh = 0.2f * clipThresh;

        AccumulateOpposedNeighbor(sx - 1, sy, sw, sh, linearSrc, linSrc, rgba, loThresh, clipThresh,
            ref sumChromaR, ref sumChromaG, ref sumChromaB, ref cntChromaR, ref cntChromaG, ref cntChromaB);
        AccumulateOpposedNeighbor(sx + 1, sy, sw, sh, linearSrc, linSrc, rgba, loThresh, clipThresh,
            ref sumChromaR, ref sumChromaG, ref sumChromaB, ref cntChromaR, ref cntChromaG, ref cntChromaB);
        AccumulateOpposedNeighbor(sx, sy - 1, sw, sh, linearSrc, linSrc, rgba, loThresh, clipThresh,
            ref sumChromaR, ref sumChromaG, ref sumChromaB, ref cntChromaR, ref cntChromaG, ref cntChromaB);
        AccumulateOpposedNeighbor(sx, sy + 1, sw, sh, linearSrc, linSrc, rgba, loThresh, clipThresh,
            ref sumChromaR, ref sumChromaG, ref sumChromaB, ref cntChromaR, ref cntChromaG, ref cntChromaB);

        float chromaR = cntChromaR > 0.5f ? sumChromaR / cntChromaR : 0f;
        float chromaG = cntChromaG > 0.5f ? sumChromaG / cntChromaG : 0f;
        float chromaB = cntChromaB > 0.5f ? sumChromaB / cntChromaB : 0f;

        if (r >= clipThresh) r = ref0r + chromaR;
        if (g >= clipThresh) g = ref0g + chromaG;
        if (b >= clipThresh) b = ref0b + chromaB;
    }

    private static void AccumulateOpposedNeighbor(
        int nx, int ny, int sw, int sh, bool linearSrc, float[]? linSrc, byte[]? rgba,
        float loThresh, float clipThresh,
        ref float sumR, ref float sumG, ref float sumB,
        ref float cntR, ref float cntG, ref float cntB)
    {
        if (nx < 0) nx = 0; else if (nx >= sw) nx = sw - 1;
        if (ny < 0) ny = 0; else if (ny >= sh) ny = sh - 1;
        int si = (ny * sw + nx) * 4;
        float nr, ng, nb;
        if (linearSrc)
        {
            nr = linSrc![si];
            ng = linSrc[si + 1];
            nb = linSrc[si + 2];
        }
        else
        {
            nr = ToLin1(rgba![si] / 255f);
            ng = ToLin1(rgba[si + 1] / 255f);
            nb = ToLin1(rgba[si + 2] / 255f);
        }

        float nur = MathF.Pow(MathF.Max(nr, 0f), 0.33333333f);
        float nug = MathF.Pow(MathF.Max(ng, 0f), 0.33333333f);
        float nub = MathF.Pow(MathF.Max(nb, 0f), 0.33333333f);

        float noppR = 0.5f * (nug + nub);
        float noppG = 0.5f * (nur + nub);
        float noppB = 0.5f * (nur + nug);

        float nrefR = noppR * noppR * noppR;
        float nrefG = noppG * noppG * noppG;
        float nrefB = noppB * noppB * noppB;

        if (nr > loThresh && nr < clipThresh) { sumR += nr - nrefR; cntR += 1f; }
        if (ng > loThresh && ng < clipThresh) { sumG += ng - nrefG; cntG += 1f; }
        if (nb > loThresh && nb < clipThresh) { sumB += nb - nrefB; cntB += 1f; }
    }

    // ref: highlight reconstruction (LCh)
    private static void ApplyLchReconstruction(
        int sx, int sy, int sw, int sh, bool linearSrc, float[]? linSrc, byte[]? rgba,
        float clipThresh, ref float r, ref float g, ref float b)
    {
        const float Sqrt3 = 1.7320508f;
        const float Sqrt12 = 3.4641016f;

        float sumR = r, sumG = g, sumB = b;
        float maxR = r, maxG = g, maxB = b;
        int cnt = 1;

        AccumulateLchNeighbor(sx - 1, sy, sw, sh, linearSrc, linSrc, rgba, ref sumR, ref sumG, ref sumB, ref maxR, ref maxG, ref maxB, ref cnt);
        AccumulateLchNeighbor(sx + 1, sy, sw, sh, linearSrc, linSrc, rgba, ref sumR, ref sumG, ref sumB, ref maxR, ref maxG, ref maxB, ref cnt);
        AccumulateLchNeighbor(sx, sy - 1, sw, sh, linearSrc, linSrc, rgba, ref sumR, ref sumG, ref sumB, ref maxR, ref maxG, ref maxB, ref cnt);
        AccumulateLchNeighbor(sx, sy + 1, sw, sh, linearSrc, linSrc, rgba, ref sumR, ref sumG, ref sumB, ref maxR, ref maxG, ref maxB, ref cnt);

        float invCnt = 1f / cnt;
        float meanR = sumR * invCnt;
        float meanG = sumG * invCnt;
        float meanB = sumB * invCnt;

        float ro = MathF.Min(meanR, clipThresh);
        float go = MathF.Min(meanG, clipThresh);
        float bo = MathF.Min(meanB, clipThresh);

        float lum = (maxR + maxG + maxB) * 0.33333333f;
        float c = Sqrt3 * (maxR - maxG);
        float h = 2f * maxB - maxG - maxR;

        float co = Sqrt3 * (ro - go);
        float ho = 2f * bo - go - ro;

        float ch2 = c * c + h * h;
        if (ch2 > 1e-6f)
        {
            float ratio = MathF.Sqrt((co * co + ho * ho) / ch2);
            c *= ratio;
            h *= ratio;
        }

        float recR = lum - h * 0.16666667f + c / Sqrt12;
        float recG = lum - h * 0.16666667f - c / Sqrt12;
        float recB = lum + h * 0.33333333f;

        if (r >= clipThresh) r = recR;
        if (g >= clipThresh) g = recG;
        if (b >= clipThresh) b = recB;
    }

    private static void AccumulateLchNeighbor(
        int nx, int ny, int sw, int sh, bool linearSrc, float[]? linSrc, byte[]? rgba,
        ref float sumR, ref float sumG, ref float sumB,
        ref float maxR, ref float maxG, ref float maxB,
        ref int cnt)
    {
        if (nx < 0) nx = 0; else if (nx >= sw) nx = sw - 1;
        if (ny < 0) ny = 0; else if (ny >= sh) ny = sh - 1;
        int si = (ny * sw + nx) * 4;
        float nr, ng, nb;
        if (linearSrc)
        {
            nr = linSrc![si];
            ng = linSrc[si + 1];
            nb = linSrc[si + 2];
        }
        else
        {
            nr = ToLin1(rgba![si] / 255f);
            ng = ToLin1(rgba[si + 1] / 255f);
            nb = ToLin1(rgba[si + 2] / 255f);
        }
        sumR += nr;
        sumG += ng;
        sumB += nb;
        if (nr > maxR) maxR = nr;
        if (ng > maxG) maxG = ng;
        if (nb > maxB) maxB = nb;
        cnt++;
    }

    // ref: color reconstruction
    private static void ApplyColorReconstructionBilateral(
        float[] img, int w, int h, float hlThreshold, float amount, float spatialExtent)
    {
        float clipThresh = hlThreshold * 0.987f;
        float spatial = Math.Max(spatialExtent * 0.5f, 4f);
        int gx = Math.Clamp((int)Math.Round(w / spatial), 4, 64) + 1;
        int gy = Math.Clamp((int)Math.Round(h / spatial), 4, 64) + 1;
        int gz = 8;
        int gridSize = gx * gy * gz;

        float[] gridL = new float[gridSize];
        float[] gridA = new float[gridSize];
        float[] gridB = new float[gridSize];
        float[] gridW = new float[gridSize];

        float[] lab = new float[w * h * 3];

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 3;
                float r = img[i];
                float g = img[i + 1];
                float b = img[i + 2];

                RgbToLab(r, g, b, out float l, out float a, out float bOut);
                lab[i] = l;
                lab[i + 1] = a;
                lab[i + 2] = bOut;

                if (r < clipThresh && g < clipThresh && b < clipThresh)
                {
                    int xi = Math.Clamp((int)Math.Round((x / (float)w) * (gx - 1)), 0, gx - 1);
                    int yi = Math.Clamp((int)Math.Round((y / (float)h) * (gy - 1)), 0, gy - 1);
                    int zi = Math.Clamp((int)Math.Round(Math.Clamp(l / 100f, 0f, 1f) * (gz - 1)), 0, gz - 1);
                    int gi = (zi * gy + yi) * gx + xi;

                    float weight = MathF.Sqrt(a * a + bOut * bOut) + 0.01f;
                    gridL[gi] += l * weight;
                    gridA[gi] += a * weight;
                    gridB[gi] += bOut * weight;
                    gridW[gi] += weight;
                }
            }
        }

        BlurGrid3D(gridL, gx, gy, gz);
        BlurGrid3D(gridA, gx, gy, gz);
        BlurGrid3D(gridB, gx, gy, gz);
        BlurGrid3D(gridW, gx, gy, gz);

        float effAmount = Math.Clamp(amount / 100f, 0f, 1f);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 3;
                float r = img[i];
                float g = img[i + 1];
                float b = img[i + 2];

                if (r >= clipThresh || g >= clipThresh || b >= clipThresh)
                {
                    float l = lab[i];
                    float a = lab[i + 1];
                    float bOut = lab[i + 2];

                    float gxPos = (x / (float)w) * (gx - 1);
                    float gyPos = (y / (float)h) * (gy - 1);
                    float gzPos = Math.Clamp(l / 100f, 0f, 1f) * (gz - 1);

                    TrilinearSample(gridL, gx, gy, gz, gxPos, gyPos, gzPos, out float sL);
                    TrilinearSample(gridA, gx, gy, gz, gxPos, gyPos, gzPos, out float sA);
                    TrilinearSample(gridB, gx, gy, gz, gxPos, gyPos, gzPos, out float sB);
                    TrilinearSample(gridW, gx, gy, gz, gxPos, gyPos, gzPos, out float sW);

                    if (sW > 0.001f)
                    {
                        float normL = Math.Max(sL / sW, 1f);
                        float normA = sA / sW;
                        float normB = sB / sW;

                        float blend = effAmount;
                        float newA = a * (1f - blend) + normA * (l / normL) * blend;
                        float newB = bOut * (1f - blend) + normB * (l / normL) * blend;

                        LabToRgb(l, newA, newB, out float recR, out float recG, out float recB);
                        img[i] = recR;
                        img[i + 1] = recG;
                        img[i + 2] = recB;
                    }
                }
            }
        }
    }

    // ref: color reconstruction
    private static void ApplyColorReconstructionFast(
        float[] img, int w, int h, float hlThreshold, float amount, float spatialExtent)
    {
        float clipThresh = hlThreshold * 0.987f;
        float effAmount = Math.Clamp(amount / 100f, 0f, 1f);
        int step = Math.Max(1, (int)(spatialExtent * 0.15f));

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int idx = (y * w + x) * 3;
                float r = img[idx];
                float g = img[idx + 1];
                float b = img[idx + 2];

                if (r >= clipThresh || g >= clipThresh || b >= clipThresh)
                {
                    float sumR = 0, sumG = 0, sumB = 0, sumW = 0;
                    AccumulateColorReconFastSample(img, w, h, x - step, y, clipThresh, ref sumR, ref sumG, ref sumB, ref sumW);
                    AccumulateColorReconFastSample(img, w, h, x + step, y, clipThresh, ref sumR, ref sumG, ref sumB, ref sumW);
                    AccumulateColorReconFastSample(img, w, h, x, y - step, clipThresh, ref sumR, ref sumG, ref sumB, ref sumW);
                    AccumulateColorReconFastSample(img, w, h, x, y + step, clipThresh, ref sumR, ref sumG, ref sumB, ref sumW);

                    if (sumW > 0.001f)
                    {
                        float avgR = sumR / sumW;
                        float avgG = sumG / sumW;
                        float avgB = sumB / sumW;
                        float lCur = Luma(r, g, b);
                        float lAvg = Math.Max(Luma(avgR, avgG, avgB), 0.001f);
                        float scale = lCur / lAvg;

                        img[idx] = r * (1f - effAmount) + (avgR * scale) * effAmount;
                        img[idx + 1] = g * (1f - effAmount) + (avgG * scale) * effAmount;
                        img[idx + 2] = b * (1f - effAmount) + (avgB * scale) * effAmount;
                    }
                }
            }
        }
    }

    private static void AccumulateColorReconFastSample(
        float[] img, int w, int h, int nx, int ny, float clipThresh,
        ref float sumR, ref float sumG, ref float sumB, ref float sumW)
    {
        if (nx < 0 || nx >= w || ny < 0 || ny >= h) return;
        int i = (ny * w + nx) * 3;
        float r = img[i];
        float g = img[i + 1];
        float b = img[i + 2];
        if (r < clipThresh && g < clipThresh && b < clipThresh)
        {
            float maxC = Math.Max(Math.Max(r, g), b);
            float minC = Math.Min(Math.Min(r, g), b);
            float wgt = (maxC - minC) + 0.01f;
            sumR += r * wgt;
            sumG += g * wgt;
            sumB += b * wgt;
            sumW += wgt;
        }
    }

    private static void BlurGrid3D(float[] grid, int gx, int gy, int gz)
    {
        float[] temp = new float[grid.Length];

        for (int z = 0; z < gz; z++)
        {
            for (int y = 0; y < gy; y++)
            {
                int rowOffset = (z * gy + y) * gx;
                for (int x = 0; x < gx; x++)
                {
                    int xM2 = Math.Max(0, x - 2);
                    int xM1 = Math.Max(0, x - 1);
                    int xP1 = Math.Min(gx - 1, x + 1);
                    int xP2 = Math.Min(gx - 1, x + 2);
                    temp[rowOffset + x] = (grid[rowOffset + xM2] + 4f * grid[rowOffset + xM1] + 6f * grid[rowOffset + x] + 4f * grid[rowOffset + xP1] + grid[rowOffset + xP2]) / 16f;
                }
            }
        }

        for (int z = 0; z < gz; z++)
        {
            for (int x = 0; x < gx; x++)
            {
                for (int y = 0; y < gy; y++)
                {
                    int yM2 = Math.Max(0, y - 2);
                    int yM1 = Math.Max(0, y - 1);
                    int yP1 = Math.Min(gy - 1, y + 1);
                    int yP2 = Math.Min(gy - 1, y + 2);
                    int zOff = z * gy * gx + x;
                    grid[zOff + y * gx] = (temp[zOff + yM2 * gx] + 4f * temp[zOff + yM1 * gx] + 6f * temp[zOff + y * gx] + 4f * temp[zOff + yP1 * gx] + temp[zOff + yP2 * gx]) / 16f;
                }
            }
        }

        for (int y = 0; y < gy; y++)
        {
            for (int x = 0; x < gx; x++)
            {
                int xyOff = y * gx + x;
                for (int z = 0; z < gz; z++)
                {
                    int zM1 = Math.Max(0, z - 1);
                    int zP1 = Math.Min(gz - 1, z + 1);
                    temp[z * gy * gx + xyOff] = (grid[zM1 * gy * gx + xyOff] + 2f * grid[z * gy * gx + xyOff] + grid[zP1 * gy * gx + xyOff]) / 4f;
                }
            }
        }

        Array.Copy(temp, grid, grid.Length);
    }

    private static void TrilinearSample(float[] grid, int gx, int gy, int gz, float x, float y, float z, out float val)
    {
        int x0 = Math.Clamp((int)x, 0, gx - 1);
        int y0 = Math.Clamp((int)y, 0, gy - 1);
        int z0 = Math.Clamp((int)z, 0, gz - 1);
        int x1 = Math.Min(gx - 1, x0 + 1);
        int y1 = Math.Min(gy - 1, y0 + 1);
        int z1 = Math.Min(gz - 1, z0 + 1);

        float fx = x - x0;
        float fy = y - y0;
        float fz = z - z0;

        int i000 = (z0 * gy + y0) * gx + x0;
        int i100 = (z0 * gy + y0) * gx + x1;
        int i010 = (z0 * gy + y1) * gx + x0;
        int i110 = (z0 * gy + y1) * gx + x1;
        int i001 = (z1 * gy + y0) * gx + x0;
        int i101 = (z1 * gy + y0) * gx + x1;
        int i011 = (z1 * gy + y1) * gx + x0;
        int i111 = (z1 * gy + y1) * gx + x1;

        float c00 = grid[i000] * (1f - fx) + grid[i100] * fx;
        float c10 = grid[i010] * (1f - fx) + grid[i110] * fx;
        float c01 = grid[i001] * (1f - fx) + grid[i101] * fx;
        float c11 = grid[i011] * (1f - fx) + grid[i111] * fx;

        float c0 = c00 * (1f - fy) + c10 * fy;
        float c1 = c01 * (1f - fy) + c11 * fy;

        val = c0 * (1f - fz) + c1 * fz;
    }

    private static void RgbToLab(float r, float g, float b, out float l, out float a, out float bOut)
    {
        float x = 0.636958f * r + 0.144617f * g + 0.168881f * b;
        float y = 0.262700f * r + 0.677998f * g + 0.059302f * b;
        float z = 0.000000f * r + 0.028073f * g + 1.060985f * b;

        x /= 0.95047f;
        z /= 1.08883f;

        float fx = x > 0.008856f ? MathF.Pow(x, 0.33333333f) : (7.787f * x + 16f / 116f);
        float fy = y > 0.008856f ? MathF.Pow(y, 0.33333333f) : (7.787f * y + 16f / 116f);
        float fz = z > 0.008856f ? MathF.Pow(z, 0.33333333f) : (7.787f * z + 16f / 116f);

        l = 116f * fy - 16f;
        a = 500f * (fx - fy);
        bOut = 200f * (fy - fz);
    }

    private static void LabToRgb(float l, float a, float bIn, out float r, out float g, out float b)
    {
        float fy = (l + 16f) / 116f;
        float fx = a / 500f + fy;
        float fz = fy - bIn / 200f;

        float fx3 = fx * fx * fx;
        float fz3 = fz * fz * fz;

        float x = (fx3 > 0.008856f ? fx3 : (fx - 16f / 116f) / 7.787f) * 0.95047f;
        float y = (l > 8f ? MathF.Pow((l + 16f) / 116f, 3f) : l / 903.3f);
        float z = (fz3 > 0.008856f ? fz3 : (fz - 16f / 116f) / 7.787f) * 1.08883f;

        r = 1.716651f * x - 0.355671f * y - 0.253366f * z;
        g = -0.666684f * x + 1.616481f * y + 0.015769f * z;
        b = 0.017640f * x - 0.042771f * y + 0.942103f * z;

        if (r < 0f) r = 0f;
        if (g < 0f) g = 0f;
        if (b < 0f) b = 0f;
    }

    // ref: bilateral filter & local laplacian
    private static void ApplyLocalContrast(
        float[] look, int w, int h, float detail, float midtones, float shadows, float highlights, float texture, bool fast)
    {
        if (Math.Abs(detail) < 1e-4f && Math.Abs(highlights) < 1e-4f && Math.Abs(shadows) < 1e-4f && Math.Abs(midtones - 50f) < 0.1f && Math.Abs(texture) < 1e-4f)
            return;

        float sigma = Math.Clamp(midtones / 100f, 0.05f, 1f);
        float shd = shadows / 100f;
        float hi = highlights / 100f;

        // Precompute perceptual lightness L = cbrt(lum)
        float[] lums = new float[w * h];
        float[] lumsL = new float[w * h];
        Parallel.For(0, h, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int idx = row + x;
                float lum = MathF.Max(Luma(look[3 * idx], look[3 * idx + 1], look[3 * idx + 2]), 1e-5f);
                lums[idx] = lum;
                lumsL[idx] = MathF.Pow(lum, 0.33333333f);
            }
        });

        int minDim = Math.Min(w, h);
        int r1 = Math.Max(3, (int)MathF.Round(minDim * 0.002f));
        int r2 = Math.Max(8, (int)MathF.Round(minDim * 0.006f));
        int r3 = Math.Max(18, (int)MathF.Round(minDim * 0.014f));

        float rangeSigma = 0.16f;
        float invTwoSigmaSq = 0.5f / (rangeSigma * rangeSigma);

        Parallel.For(0, h, y =>
        {
            int yT1 = Math.Max(0, y - r1);
            int yB1 = Math.Min(h - 1, y + r1);
            int yT2 = Math.Max(0, y - r2);
            int yB2 = Math.Min(h - 1, y + r2);
            int yT3 = Math.Max(0, y - r3);
            int yB3 = Math.Min(h - 1, y + r3);

            for (int x = 0; x < w; x++)
            {
                int idx = y * w + x;
                float lum = lums[idx];
                float xL = lumsL[idx];

                // Ring 1: 4 cardinal taps at r1
                int xL1 = Math.Max(0, x - r1);
                int xR1 = Math.Min(w - 1, x + r1);
                float l1 = lumsL[y * w + xR1];
                float l2 = lumsL[y * w + xL1];
                float l3 = lumsL[yB1 * w + x];
                float l4 = lumsL[yT1 * w + x];

                float grad = 0.25f * (MathF.Abs(l1 - xL) + MathF.Abs(l2 - xL) + MathF.Abs(l3 - xL) + MathF.Abs(l4 - xL));

                float sumL = xL * 2.0f;
                float sumW = 2.0f;

                float d1 = l1 - xL; float w1 = MathF.Exp(-d1 * d1 * invTwoSigmaSq); sumL += l1 * w1; sumW += w1;
                float d2 = l2 - xL; float w2 = MathF.Exp(-d2 * d2 * invTwoSigmaSq); sumL += l2 * w2; sumW += w2;
                float d3 = l3 - xL; float w3 = MathF.Exp(-d3 * d3 * invTwoSigmaSq); sumL += l3 * w3; sumW += w3;
                float d4 = l4 - xL; float w4 = MathF.Exp(-d4 * d4 * invTwoSigmaSq); sumL += l4 * w4; sumW += w4;

                // Ring 2: 4 diagonal taps at r2
                int xL2 = Math.Max(0, x - r2);
                int xR2 = Math.Min(w - 1, x + r2);
                float l5 = lumsL[yT2 * w + xR2];
                float l6 = lumsL[yT2 * w + xL2];
                float l7 = lumsL[yB2 * w + xR2];
                float l8 = lumsL[yB2 * w + xL2];

                float d5 = l5 - xL; float w5 = 0.8f * MathF.Exp(-d5 * d5 * invTwoSigmaSq); sumL += l5 * w5; sumW += w5;
                float d6 = l6 - xL; float w6 = 0.8f * MathF.Exp(-d6 * d6 * invTwoSigmaSq); sumL += l6 * w6; sumW += w6;
                float d7 = l7 - xL; float w7 = 0.8f * MathF.Exp(-d7 * d7 * invTwoSigmaSq); sumL += l7 * w7; sumW += w7;
                float d8 = l8 - xL; float w8 = 0.8f * MathF.Exp(-d8 * d8 * invTwoSigmaSq); sumL += l8 * w8; sumW += w8;

                if (!fast)
                {
                    // Ring 3: 4 cardinal taps at r3
                    int xL3 = Math.Max(0, x - r3);
                    int xR3 = Math.Min(w - 1, x + r3);
                    float l9 = lumsL[y * w + xR3];
                    float l10 = lumsL[y * w + xL3];
                    float l11 = lumsL[yB3 * w + x];
                    float l12 = lumsL[yT3 * w + x];

                    float d9 = l9 - xL; float w9 = 0.5f * MathF.Exp(-d9 * d9 * invTwoSigmaSq); sumL += l9 * w9; sumW += w9;
                    float d10 = l10 - xL; float w10 = 0.5f * MathF.Exp(-d10 * d10 * invTwoSigmaSq); sumL += l10 * w10; sumW += w10;
                    float d11 = l11 - xL; float w11 = 0.5f * MathF.Exp(-d11 * d11 * invTwoSigmaSq); sumL += l11 * w11; sumW += w11;
                    float d12 = l12 - xL; float w12 = 0.5f * MathF.Exp(-d12 * d12 * invTwoSigmaSq); sumL += l12 * w12; sumW += w12;
                }

                float gL = sumL / sumW;
                float newL = CurveScalar(xL, gL, sigma, shd, hi, detail);

                // Edge-guided halo suppression matching GPU SkSL
                float edgeFactor = MathF.Exp(-grad * grad / 0.0064f);
                float finalL = xL + (newL - xL) * edgeFactor;
                if (MathF.Abs(texture) > 0.001f)
                {
                    float microAvg = 0.25f * (l1 + l2 + l3 + l4);
                    float microDiff = xL - microAvg;
                    finalL += texture * microDiff * 0.75f * edgeFactor;
                }
                finalL = Math.Clamp(finalL, 0.0001f, 2.0f);

                float newLum = finalL * finalL * finalL;
                float gain = newLum / lum;

                look[3 * idx] *= gain;
                look[3 * idx + 1] *= gain;
                look[3 * idx + 2] *= gain;
            }
        });
    }

    // ref: local laplacian
    private static float CurveScalar(float x, float g, float sigma, float shadows, float highlights, float clarity)
    {
        float c = x - g;
        if (MathF.Abs(clarity) < 1e-4f && MathF.Abs(highlights) < 1e-4f && MathF.Abs(shadows) < 1e-4f)
            return x;

        float dtShadows = 1.0f + shadows;
        float dtHighlights = 1.0f + highlights;
        float effSigma = Math.Clamp(sigma * 0.40f, 0.04f, 0.50f);
        float val;
        if (c > 2.0f * effSigma)
        {
            val = g + effSigma + dtShadows * (c - effSigma);
        }
        else if (c < -2.0f * effSigma)
        {
            val = g - effSigma + dtHighlights * (c + effSigma);
        }
        else if (c > 0.0f)
        {
            float t = Math.Clamp(c / (2.0f * effSigma), 0.0f, 1.0f);
            float t2 = t * t;
            float mt = 1.0f - t;
            val = g + effSigma * 2.0f * mt * t + t2 * (effSigma + effSigma * dtShadows);
        }
        else
        {
            float t = Math.Clamp(-c / (2.0f * effSigma), 0.0f, 1.0f);
            float t2 = t * t;
            float mt = 1.0f - t;
            val = g - effSigma * 2.0f * mt * t + t2 * (-effSigma - effSigma * dtHighlights);
        }

        // midtone local contrast (clarity)
        val += clarity * c * MathF.Exp(-c * c / (0.6666667f * effSigma * effSigma));
        return Math.Clamp(val, 0.0001f, 2.5f);
    }
}
