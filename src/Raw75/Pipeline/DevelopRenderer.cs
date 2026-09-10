using System;
using Blossom;
using Blossom.Core;
using Raw75.Develop;
using SkiaSharp;

namespace Raw75.Pipeline;

/// <summary>
/// GPU develop path (SkSL). Geometry → WB/EV/tone/HSL → cheap denoise/sharpen → LUT → sRGB.
/// Same uniforms for proxy, 1:1, and export; this type only produces an 8-bit display snapshot.
/// </summary>
public static class DevelopRenderer
{
    private static readonly object Gate = new();
    private static SKRuntimeEffect? _effect;
    private static bool _compileAttempted;
    private static SKImage? _white;
    private static string? _cachedLutPath;
    private static SKImage? _cachedLut;

    public static SKImage? Apply(SKImage source, DevelopSettings s)
    {
        if (source == null)
            return null;
        s ??= new DevelopSettings();
        int w = source.Width;
        int h = source.Height;
        int rot = s.Rotate90 & 3;
        int fw = (rot & 1) != 0 ? h : w;
        int fh = (rot & 1) != 0 ? w : h;
        int outW = fw;
        int outH = fh;
        if (s.HasCrop)
        {
            outW = Math.Max(1, (int)Math.Round(fw * s.CropW));
            outH = Math.Max(1, (int)Math.Round(fh * s.CropH));
        }
        return Apply(source, s, outW, outH, 0f, false);
    }

    public static SKImage? Apply(SKImage source, DevelopSettings s, int outW, int outH)
    {
        return Apply(source, s, outW, outH, 0f, false);
    }

    public static SKImage? Apply(
        SKImage source,
        DevelopSettings s,
        int outW,
        int outH,
        float split,
        bool before)
    {
        if (source == null)
            return null;
        s ??= new DevelopSettings();
        outW = Math.Max(1, outW);
        outH = Math.Max(1, outH);

        SKRuntimeEffect? effect = EnsureEffect();
        if (effect == null)
            return Blit(source, outW, outH);

        SKImage lut = GetLut(s, out float lutSize, out float lutAmount);
        SKImage mask = WhitePixel();

        // CPU surface: a GPU ping-pong on the window GRContext during slider
        // drags (same thread as the frame) was aborting Skia.
        SKSurface? surface = SKSurface.Create(new SKImageInfo(outW, outH, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (surface == null)
            return Blit(source, outW, outH);

        try
        {
            using var imgShader = source.ToShader();
            using var lutShader = lut.ToShader();
            using var maskShader = mask.ToShader();
            if (imgShader == null)
                return Blit(source, outW, outH);

            SKShader? shader;
            lock (Gate)
            {
                var uniforms = new SKRuntimeEffectUniforms(effect);
                BindUniforms(uniforms, s, source.Width, source.Height, 0f, 0f, outW, outH, split, before, lutSize, lutAmount);

                var children = new SKRuntimeEffectChildren(effect);
                children.Add("u_image", imgShader);
                children.Add("u_lut", lutShader);
                children.Add("u_mask", maskShader);

                shader = effect.ToShader(true, uniforms, children);
            }

            if (shader == null)
                return Blit(source, outW, outH);

            using (shader)
            using (var paint = new SKPaint { Shader = shader, IsAntialias = false })
            {
                var canvas = surface.Canvas;
                canvas.Clear(SKColors.Black);
                canvas.DrawRect(new SKRect(0, 0, outW, outH), paint);
                canvas.Flush();
                return surface.Snapshot();
            }
        }
        catch (Exception ex)
        {
            Log.Error("DevelopRenderer.Apply: " + ex.Message);
            return Blit(source, outW, outH);
        }
        finally
        {
            surface.Dispose();
        }
    }

    public static void ComputeHistogramFromDisplay(SKBitmap bgra, PhotoDocument doc)
    {
        if (doc == null)
            return;
        HistogramCompute.Compute(bgra, doc.HistogramR, doc.HistogramG, doc.HistogramB, doc.HistogramY);
    }

    public static void FillHistogram(SKImage display, float[] r, float[] g, float[] b, float[] y)
    {
        HistogramCompute.Compute(display, r, g, b, y);
    }

    public static void FillHistogram(SKImage display, PhotoDocument doc)
    {
        if (doc == null)
            return;
        HistogramCompute.Compute(display, doc.HistogramR, doc.HistogramG, doc.HistogramB, doc.HistogramY);
    }

    internal static SKImage? LastLut { get; private set; }

    /// <summary>One-time blit onto the window GRContext so live SKSL samples a GPU texture.</summary>
    public static SKImage PromoteGpu(SKImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Handle == IntPtr.Zero || !Gpu.IsReady)
            return image;
        try
        {
            SKColorType ct = image.ColorType == SKColorType.RgbaF16
                ? SKColorType.RgbaF16
                : SKColorType.Rgba8888;
            using SKSurface? surface = Gpu.CreateSurface(
                image.Width, image.Height, ct, SKAlphaType.Unpremul);
            if (surface == null)
                return image;
            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.DrawImage(image, 0, 0);
            SKImage? snap = Gpu.Snapshot(surface);
            return snap ?? image;
        }
        catch (Exception ex)
        {
            Log.Warning("GPU promote: " + ex.Message);
            return image;
        }
    }

    internal static SKRuntimeEffect? EnsureEffect()
    {
        if (_effect != null)
            return _effect;
        lock (Gate)
        {
            if (_effect != null)
                return _effect;
            if (_compileAttempted && _effect == null)
                return null;
            _compileAttempted = true;
            _effect = SKRuntimeEffect.Create(DevelopSksl, out string errors);
            if (_effect == null)
                Log.Error("DevelopRenderer SKSL compile failed: " + errors);
            return _effect;
        }
    }

    internal static void BindUniforms(
        SKRuntimeEffectUniforms u,
        DevelopSettings s,
        int srcW,
        int srcH,
        float dstX,
        float dstY,
        float dstW,
        float dstH,
        float split,
        bool before,
        float lutSize,
        float lutAmount,
        bool fast = false,
        bool applyCrop = true,
        bool srcLinear = true,
        float tileX = 0f,
        float tileY = 0f,
        float tileW = 1f,
        float tileH = 1f,
        float viewCropX = float.NaN,
        float viewCropY = float.NaN,
        float viewCropW = float.NaN,
        float viewCropH = float.NaN,
        int frameW = 0,
        int frameH = 0)
    {
        Set(u, "u_destOrigin", new[] { dstX, dstY });
        Set(u, "u_destSize", new[] { dstW, dstH });
        Set(u, "u_srcSize", new[] { (float)srcW, (float)srcH });
        int rot = s.Rotate90 & 3;
        int fw = frameW > 0 ? frameW : ((rot & 1) != 0 ? srcH : srcW);
        int fh = frameH > 0 ? frameH : ((rot & 1) != 0 ? srcW : srcH);
        Set(u, "u_frameSize", new[] { (float)fw, (float)fh });
        if (tileW < 1e-5f) tileW = 1f;
        if (tileH < 1e-5f) tileH = 1f;
        Set(u, "u_tileOrigin", new[] { tileX, tileY });
        Set(u, "u_tileSize", new[] { tileW, tileH });
        Set(u, "u_temp", s.EnableWhiteBalance ? s.Temperature / 100f : 0f);
        Set(u, "u_tint", s.EnableWhiteBalance ? s.Tint / 100f : 0f);
        Set(u, "u_ev", s.EnableExposure ? s.Exposure : 0f);
        Set(u, "u_match", (s.EnableHsl && s.MatchGray) ? 1f : 0f);
        Set(u, "u_contrast", s.EnableExposure ? s.Contrast / 100f : 0f);
        Set(u, "u_highlights", s.EnableHdr ? s.Highlights / 100f : 0f);
        Set(u, "u_shadows", s.EnableHdr ? s.Shadows / 100f : 0f);
        Set(u, "u_whites", s.EnableHdr ? s.Whites / 100f : 0f);
        Set(u, "u_blacks", s.EnableHdr ? s.Blacks / 100f : 0f);
        Set(u, "u_vibrance", s.EnableHsl ? s.Vibrance / 100f : 0f);
        Set(u, "u_saturation", s.EnableExposure ? s.Saturation / 100f : 0f);
        HslBand[] hsl = s.Hsl;
        for (int i = 0; i < 6; i++)
        {
            HslBand band = (s.EnableHsl && hsl != null && hsl.Length == 6) ? hsl[i] : default;
            Set(u, "u_hsl" + i, new[] { band.Hue / 100f, band.Sat / 100f, band.Luma / 100f, 0f });
        }
        Set(u, "u_sharpen", !s.EnableDetail ? 0f : s.Sharpen / 150f);
        float noiseVal = s.EnableDetail ? s.Noise : 0f;
        if (noiseVal == 0f && s.EnableDetail && (s.DenoiseLuma > 0f || s.DenoiseChroma > 0f))
            noiseVal = -Math.Max(s.DenoiseLuma, s.DenoiseChroma);
        Set(u, "u_noise", noiseVal / 100f);
        Set(u, "u_denoiseFast", fast ? 1f : 0f);
        Set(u, "u_denoiseLuma", noiseVal < 0f ? -noiseVal / 100f : 0f);
        Set(u, "u_denoiseChroma", noiseVal < 0f ? -noiseVal / 100f : 0f);
        float cx, cy, cw, ch;
        if (!float.IsNaN(viewCropW) && viewCropW > 0.0001f && viewCropH > 0.0001f)
        {
            cx = viewCropX;
            cy = viewCropY;
            cw = viewCropW;
            ch = viewCropH;
        }
        else
        {
            cw = applyCrop ? s.CropW : 0f;
            ch = applyCrop ? s.CropH : 0f;
            cx = s.CropX;
            cy = s.CropY;
            if (cw < 0.001f || ch < 0.001f)
            {
                cx = 0f;
                cy = 0f;
                cw = 1f;
                ch = 1f;
            }
        }
        Set(u, "u_crop", new[] { cx, cy, cw, ch });
        Set(u, "u_straighten", s.EnableGeometry ? s.Straighten : 0f);
        Set(u, "u_rot", (float)(s.Rotate90 & 3));
        Set(u, "u_flipH", s.FlipH ? 1f : 0f);
        Set(u, "u_flipV", s.FlipV ? 1f : 0f);
        Set(u, "u_split", split);
        Set(u, "u_before", before ? 1f : 0f);
        Set(u, "u_lutSize", lutSize);
        Set(u, "u_lutAmount", lutAmount);
        Set(u, "u_hasLut", lutAmount > 0.001f && lutSize > 1.5f ? 1f : 0f);
        Set(u, "u_srcLinear", srcLinear ? 1f : 0f);
        Set(u, "u_toneMode", (s.EnableTone && s.ToneMode == ToneMode.Filmic) ? 1f : 0f);
        SigmoidParams sig = (s.EnableTone && s.ToneMode == ToneMode.Sigmoid)
            ? SigmoidParams.Compute(s.SigmoidContrast, s.SigmoidSkew)
            : (s.EnableTone ? default : SigmoidParams.Compute(1.0f, 0.0f));
        Set(u, "u_sigMagnitude", sig.Magnitude);
        Set(u, "u_sigPaperExp", sig.PaperExp);
        Set(u, "u_sigFilmFog", sig.FilmFog);
        Set(u, "u_sigFilmPower", sig.FilmPower);
        Set(u, "u_sigPaperPower", sig.PaperPower);
        float reconMode = (s.EnableReconstruction ? s.ReconstructionMode : HighlightMode.Off) switch
        {
            HighlightMode.Opposed => 1f,
            HighlightMode.LCh => 2f,
            _ => 0f
        };
        Set(u, "u_reconMode", reconMode);
        Set(u, "u_hlThreshold", s.HighlightThreshold);
        Set(u, "u_reconFast", fast ? 1f : 0f);
        Set(u, "u_reconColorAmount", s.EnableReconstruction ? s.ColorReconstructionAmount / 100f : 0f);
        Set(u, "u_reconColorSpatial", s.ColorReconstructionSpatial);

        Set(u, "u_localDetail", s.EnableLocalContrast ? s.LocalContrastDetail : 0f);
        Set(u, "u_localHighlights", s.EnableLocalContrast ? s.LocalContrastHighlights / 100f : 0f);
        Set(u, "u_localShadows", s.EnableLocalContrast ? s.LocalContrastShadows / 100f : 0f);
        Set(u, "u_localMidtones", s.EnableLocalContrast ? s.LocalContrastMidtones / 100f : 0.5f);
        Set(u, "u_localFast", fast ? 1f : 0f);
    }

    private static void Set(SKRuntimeEffectUniforms u, string name, float value)
    {
        try { u[name] = value; }
        catch (ArgumentException) { }
    }

    private static void Set(SKRuntimeEffectUniforms u, string name, float[] value)
    {
        try { u[name] = value; }
        catch (ArgumentException) { }
    }

    internal static SKImage GetLut(DevelopSettings s, out float lutSize, out float lutAmount)
    {
        lutAmount = 0f;
        lutSize = 2f;
        string? path = s.LutPath;
        if (string.IsNullOrWhiteSpace(path) || s.LutAmount <= 0.001f)
        {
            LastLut = WhitePixel();
            return LastLut;
        }

        lock (Gate)
        {
            if (_cachedLutPath != path)
            {
                _cachedLut?.Dispose();
                _cachedLut = CubeLut.LoadCubeAs2d(path);
                _cachedLutPath = path;
                if (_cachedLut == null)
                    Log.Error("DevelopRenderer: failed to load LUT " + path);
            }

            if (_cachedLut == null)
            {
                LastLut = WhitePixel();
                return LastLut;
            }

            int h = Math.Max(1, _cachedLut.Height);
            lutSize = h;
            lutAmount = Math.Clamp(s.LutAmount, 0f, 1f);
            LastLut = _cachedLut;
            return _cachedLut;
        }
    }

    internal static SKImage WhitePixel()
    {
        if (_white != null)
            return _white;
        lock (Gate)
        {
            if (_white != null)
                return _white;
            var info = new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Opaque);
            using var bmp = new SKBitmap(info);
            bmp.Erase(SKColors.White);
            bmp.SetImmutable();
            _white = SKImage.FromBitmap(bmp);
            return _white;
        }
    }

    private static SKSurface? CreateSurface(int w, int h)
    {
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        return SKSurface.Create(info);
    }

    private static SKImage? Blit(SKImage source, int outW, int outH)
    {
        SKSurface? surface = CreateSurface(outW, outH);
        if (surface == null)
            return null;
        try
        {
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Black);
            SKRect dest = Contain(outW, outH, source.Width, source.Height);
            canvas.DrawImage(source, dest);
            canvas.Flush();
            return surface.Snapshot();
        }
        finally
        {
            surface.Dispose();
        }
    }

    private static SKRect Contain(int boxW, int boxH, int imgW, int imgH)
    {
        if (imgW < 1 || imgH < 1)
            return new SKRect(0, 0, boxW, boxH);
        float s = Math.Min(boxW / (float)imgW, boxH / (float)imgH);
        float w = imgW * s;
        float h = imgH * s;
        float x = (boxW - w) * 0.5f;
        float y = (boxH - h) * 0.5f;
        return new SKRect(x, y, x + w, y + h);
    }

    // Frozen op order (SPEC): sample+geom → WB → EV → match-gray → contrast (log2(1+rgb))
    // → hi/sh → whites/blacks → vibrance → sat → HSL → denoise → sharpen → LUT → sRGB.
    // Neighborhood filters are fused 4/5-tap approximations for v1.
    public const string DevelopSksl = @"
            uniform shader u_image;
            uniform shader u_lut;
            uniform shader u_mask;
            uniform float2 u_destOrigin;
            uniform float2 u_destSize;
            uniform float2 u_srcSize;
            uniform float2 u_frameSize;
            uniform float2 u_tileOrigin;
            uniform float2 u_tileSize;
            uniform float u_temp;
            uniform float u_tint;
            uniform float u_ev;
            uniform float u_match;
            uniform float u_contrast;
            uniform float u_highlights;
            uniform float u_shadows;
            uniform float u_whites;
            uniform float u_blacks;
            uniform float u_vibrance;
            uniform float u_saturation;
            uniform float4 u_hsl0;
            uniform float4 u_hsl1;
            uniform float4 u_hsl2;
            uniform float4 u_hsl3;
            uniform float4 u_hsl4;
            uniform float4 u_hsl5;
            uniform float u_sharpen;
            uniform float u_noise;
            uniform float u_denoiseLuma;
            uniform float u_denoiseChroma;
            uniform float u_denoiseFast;
            uniform float4 u_crop;
            uniform float u_straighten;
            uniform float u_rot;
            uniform float u_flipH;
            uniform float u_flipV;
            uniform float u_split;
            uniform float u_before;
            uniform float u_lutSize;
            uniform float u_lutAmount;
            uniform float u_hasLut;
            uniform float u_srcLinear;
            uniform float u_toneMode;
            uniform float u_sigMagnitude;
            uniform float u_sigPaperExp;
            uniform float u_sigFilmFog;
            uniform float u_sigFilmPower;
            uniform float u_sigPaperPower;
            uniform float u_reconMode;
            uniform float u_hlThreshold;
            uniform float u_reconFast;
            uniform float u_reconColorAmount;
            uniform float u_reconColorSpatial;
            uniform float u_localDetail;
            uniform float u_localHighlights;
            uniform float u_localShadows;
            uniform float u_localMidtones;
            uniform float u_localFast;

            // ref: local laplacian
            float curve_scalar(float x, float g, float sigma, float shadows, float highlights, float clarity) {
                float c = x - g;
                if (abs(clarity) < 0.001 && abs(highlights) < 0.001 && abs(shadows) < 0.001) {
                    return x;
                }
                float midCenter = clamp(sigma, 0.05, 0.95);
                float dMid = (x - midCenter) / 0.30;
                float midWeight = exp(-0.5 * dMid * dMid);

                float hlMod = 1.0 + highlights * (x > midCenter ? 0.75 : 0.35);
                float shMod = 1.0 - shadows * (x < midCenter ? 0.75 : 0.35);
                float toneGain = midWeight * (c > 0.0 ? hlMod : shMod);

                float detailBoost = c * (clarity * toneGain);
                float maxDelta = 0.07 + 0.05 * max(clarity, 0.0);
                float u = detailBoost / maxDelta;
                float limitedDelta = maxDelta * (u / sqrt(1.0 + u * u));

                return max(x + limitedDelta, 0.0001);
            }

            float to_lin_1(float s) {
                s = clamp(s, 0.0, 1.0);
                if (s <= 0.04045) {
                    return s / 12.92;
                }
                return pow((s + 0.055) / 1.055, 2.4);
            }

            float3 to_lin(float3 s) {
                return float3(to_lin_1(s.r), to_lin_1(s.g), to_lin_1(s.b));
            }

            float to_srgb_1(float l) {
                l = clamp(l, 0.0, 1.0);
                if (l <= 0.0031308) {
                    return 12.92 * l;
                }
                return 1.055 * pow(l, 0.4166667) - 0.055;
            }

            float3 to_srgb(float3 l) {
                return float3(to_srgb_1(l.r), to_srgb_1(l.g), to_srgb_1(l.b));
            }

            float luma2020(float3 c) {
                return dot(c, float3(0.2627, 0.6780, 0.0593));
            }

            float hue_w(float h, float center, float width) {
                float d = abs(h - center);
                if (d > 180.0) {
                    d = 360.0 - d;
                }
                return clamp(1.0 - d / width, 0.0, 1.0);
            }

            float3 rgb_to_hsl(float3 c) {
                float maxc = max(max(c.r, c.g), c.b);
                float minc = min(min(c.r, c.g), c.b);
                float l = (maxc + minc) * 0.5;
                float d = maxc - minc;
                float s = 0.0;
                float h = 0.0;
                if (d > 0.00001) {
                    float den = 1.0 - abs(2.0 * l - 1.0);
                    if (den < 0.00001) {
                        den = 0.00001;
                    }
                    s = d / den;
                    if (c.r >= c.g && c.r >= c.b) {
                        h = (c.g - c.b) / d;
                    } else if (c.g >= c.b) {
                        h = 2.0 + (c.b - c.r) / d;
                    } else {
                        h = 4.0 + (c.r - c.g) / d;
                    }
                    h = h * 60.0;
                    if (h < 0.0) {
                        h += 360.0;
                    }
                }
                return float3(h, s, l);
            }

            float hue2rgb(float p, float q, float t) {
                if (t < 0.0) t += 1.0;
                if (t > 1.0) t -= 1.0;
                if (t < 0.1666667) return p + (q - p) * 6.0 * t;
                if (t < 0.5) return q;
                if (t < 0.6666667) return p + (q - p) * (0.6666667 - t) * 6.0;
                return p;
            }

            float3 hsl_to_rgb(float3 hsl) {
                float h = hsl.x / 360.0;
                float s = clamp(hsl.y, 0.0, 1.0);
                float l = clamp(hsl.z, 0.0, 1.0);
                if (s < 0.00001) {
                    return float3(l);
                }
                float q = l < 0.5 ? l * (1.0 + s) : l + s - l * s;
                float p = 2.0 * l - q;
                return float3(
                    hue2rgb(p, q, h + 0.3333333),
                    hue2rgb(p, q, h),
                    hue2rgb(p, q, h - 0.3333333)
                );
            }

            float2 apply_geom(float2 uv) {
                float2 p = uv;
                if (abs(u_straighten) > 0.001) {
                    float a = u_straighten * 0.01745329251;
                    float c = cos(a);
                    float sn = sin(a);
                    float2 q = p - float2(0.5);
                    float invAspect = u_frameSize.y / u_frameSize.x;
                    float aspect = u_frameSize.x / u_frameSize.y;
                    p = float2(q.x * c - q.y * invAspect * sn, q.x * aspect * sn + q.y * c) + float2(0.5);
                }
                float rot = u_rot;
                if (rot > 0.5 && rot < 1.5) {
                    p = float2(p.y, 1.0 - p.x);
                } else if (rot >= 1.5 && rot < 2.5) {
                    p = float2(1.0 - p.x, 1.0 - p.y);
                } else if (rot >= 2.5) {
                    p = float2(1.0 - p.y, p.x);
                }
                if (u_flipH > 0.5) {
                    p.x = 1.0 - p.x;
                }
                if (u_flipV > 0.5) {
                    p.y = 1.0 - p.y;
                }
                return p;
            }

            float3 sample_lut(float3 rgb) {
                float size = u_lutSize;
                float3 c = clamp(rgb, 0.0, 1.0);
                float b = c.b * (size - 1.0);
                float b0 = floor(b);
                float b1 = min(b0 + 1.0, size - 1.0);
                float bf = b - b0;
                float r = c.r * (size - 1.0);
                float g = c.g * (size - 1.0);
                float2 p0 = float2(r + b0 * size + 0.5, g + 0.5);
                float2 p1 = float2(r + b1 * size + 0.5, g + 0.5);
                float3 s0 = sample(u_lut, p0).rgb;
                float3 s1 = sample(u_lut, p1).rgb;
                return mix(s0, s1, bf);
            }

            float smoother(float x, float a, float b) {
                float t = clamp((x - a) / (b - a), 0.0, 1.0);
                return t * t * (3.0 - 2.0 * t);
            }

            float filmic1(float x) {
                x = max(x, 0.0);
                float a = 2.51;
                float b = 0.03;
                float c = 2.43;
                float d = 0.59;
                float e = 0.14;
                return clamp((x * (a * x + b)) / (x * (c * x + d) + e), 0.0, 1.0);
            }

            float3 filmic(float3 x) {
                return float3(filmic1(x.r), filmic1(x.g), filmic1(x.b));
            }

            // ref: sigmoid tone curve
            float sigmoid1(float x) {
                float clamped = max(x, 0.0);
                float film_response = pow(u_sigFilmFog + clamped, u_sigFilmPower);
                float paper_response = u_sigMagnitude * pow(film_response / (u_sigPaperExp + film_response), u_sigPaperPower);
                return paper_response;
            }

            float3 sigmoid_rgb(float3 x) {
                return float3(sigmoid1(x.r), sigmoid1(x.g), sigmoid1(x.b));
            }

            float3 tone_curve(float3 x) {
                if (u_toneMode > 0.5) {
                    return filmic(x);
                }
                return sigmoid_rgb(x);
            }

            float cbrt_pos(float x) {
                return pow(max(x, 0.0), 0.33333333);
            }

            float3 cbrt_pos3(float3 v) {
                return float3(cbrt_pos(v.r), cbrt_pos(v.g), cbrt_pos(v.b));
            }

            // ref: highlight reconstruction (opposed)
            float3 opposed_ref(float3 u) {
                float opp_r = 0.5 * (u.g + u.b);
                float opp_g = 0.5 * (u.r + u.b);
                float opp_b = 0.5 * (u.r + u.g);
                return float3(opp_r * opp_r * opp_r, opp_g * opp_g * opp_g, opp_b * opp_b * opp_b);
            }

            float3 apply_look(float3 col) {
                col.r += 0.15 * u_temp;
                col.b -= 0.15 * u_temp;
                col.g -= 0.15 * u_tint;
                col = max(col, 0.0);

                col *= pow(2.0, u_ev);

                if (u_match > 0.001) {
                    float lum = max(luma2020(col), 0.0001);
                    float g = 0.18 / lum;
                    col *= mix(1.0, g, u_match * 0.35);
                }

                float3 enc = log2(1.0 + col);
                float k = 1.0 + u_contrast * 0.85;
                enc = (enc - 0.5) * k + 0.5;
                col = max(exp2(enc) - 1.0, 0.0);

                float lum2 = luma2020(col);
                float shw = 1.0 - smoother(lum2, 0.02, 0.28);
                float hiw = smoother(lum2, 0.10, 0.70);
                col *= mix(1.0, pow(2.0, u_shadows * 1.45), shw);
                col *= mix(1.0, pow(2.0, u_highlights * 1.75), hiw);

                lum2 = luma2020(col);
                float ww = smoother(lum2, 0.20, 1.05);
                float bw = 1.0 - smoother(lum2, 0.0, 0.18);
                col *= mix(1.0, pow(2.0, u_whites * 1.55), ww);
                col *= mix(1.0, pow(2.0, u_blacks * 1.25), bw);
                if (u_whites > 0.0) {
                    col *= 1.0 + u_whites * smoother(lum2, 0.35, 1.2) * 0.9;
                }
                col = max(col, 0.0);

                float lum3 = luma2020(col);
                float3 gray = float3(lum3);
                float sat = 0.0;
                float mx = max(max(col.r, col.g), col.b);
                float mn = min(min(col.r, col.g), col.b);
                if (mx > 0.00001) {
                    sat = (mx - mn) / mx;
                }
                float vBoost = u_vibrance * (1.0 - sat);
                col = mix(gray, col, 1.0 + vBoost);
                col = mix(float3(luma2020(col)), col, 1.0 + u_saturation);

                float3 hsl = rgb_to_hsl(max(col, 0.0));
                float w0 = hue_w(hsl.x, 0.0, 35.0);
                float w1 = hue_w(hsl.x, 30.0, 30.0);
                float w2 = hue_w(hsl.x, 60.0, 30.0);
                float w3 = hue_w(hsl.x, 120.0, 45.0);
                float w4 = hue_w(hsl.x, 180.0, 35.0);
                float w5 = hue_w(hsl.x, 240.0, 45.0);
                float hueShift = (u_hsl0.x * w0 + u_hsl1.x * w1 + u_hsl2.x * w2 + u_hsl3.x * w3 + u_hsl4.x * w4 + u_hsl5.x * w5) * 45.0;
                float satMul = 1.0 + (u_hsl0.y * w0 + u_hsl1.y * w1 + u_hsl2.y * w2 + u_hsl3.y * w3 + u_hsl4.y * w4 + u_hsl5.y * w5);
                float lumMul = 1.0 + (u_hsl0.z * w0 + u_hsl1.z * w1 + u_hsl2.z * w2 + u_hsl3.z * w3 + u_hsl4.z * w4 + u_hsl5.z * w5);
                hsl.x = hsl.x + hueShift;
                if (hsl.x < 0.0) hsl.x += 360.0;
                if (hsl.x >= 360.0) hsl.x -= 360.0;
                hsl.y = clamp(hsl.y * satMul, 0.0, 1.0);
                hsl.z = clamp(hsl.z * lumMul, 0.0, 1.0);
                col = hsl_to_rgb(hsl);
                return max(col, 0.0);
            }

            float3 fetch_look(float2 coord, float lookAmt) {
                float3 s = sample(u_image, clamp(coord, float2(0.5), u_srcSize - float2(0.5))).rgb;
                return lookAmt > 0.001 ? apply_look(u_srcLinear > 0.5 ? max(s, 0.0) : to_lin(s)) : (u_srcLinear > 0.5 ? max(s, 0.0) : to_lin(s));
            }

            half4 main(float2 fragCoord) {
                float2 uv = (fragCoord - u_destOrigin) / u_destSize;
                if (u_split > 0.001) {
                    if (abs(uv.x - u_split) < 0.002) {
                        return half4(1.0, 1.0, 1.0, 1.0);
                    }
                }

                float4 crop = u_crop;
                if (crop.z < 0.001 || crop.w < 0.001) {
                    crop = float4(0.0, 0.0, 1.0, 1.0);
                }
                uv = crop.xy + uv * crop.zw;
                float2 srcUv = apply_geom(uv);
                if (srcUv.x < -0.001 || srcUv.x > 1.001 || srcUv.y < -0.001 || srcUv.y > 1.001) {
                    return half4(0.0, 0.0, 0.0, 1.0);
                }
                float2 t0 = u_tileOrigin;
                float2 ts = u_tileSize;
                if (ts.x < 0.00001) ts.x = 1.0;
                if (ts.y < 0.00001) ts.y = 1.0;
                srcUv = (srcUv - t0) / ts;
                srcUv = clamp(srcUv, 0.0, 1.0);
                // Image child shaders are sampled in pixel space of the source.
                float2 srcCoord = srcUv * u_srcSize;
                half4 orig = sample(u_image, srcCoord);

                float lookAmt = 1.0 - u_before;
                if (u_split > 0.001 && uv.x < u_split) {
                    lookAmt = 0.0;
                }
                lookAmt *= sample(u_mask, fragCoord).r;

                float3 lin = u_srcLinear > 0.5 ? max(orig.rgb, 0.0) : to_lin(orig.rgb);

                // Highlight reconstruction (step 2)
                // ref: highlight reconstruction (opposed & LCh)
                if (u_reconMode < 0.5) {
                    lin = min(lin, u_hlThreshold);
                } else if (u_reconFast < 0.5) {
                    float clipThresh = u_hlThreshold * 0.987;
                    if (lin.r >= clipThresh || lin.g >= clipThresh || lin.b >= clipThresh) {
                        float2 c0 = clamp(srcCoord + float2(-1.0, 0.0), float2(0.5), u_srcSize - float2(0.5));
                        float2 c1 = clamp(srcCoord + float2(1.0, 0.0), float2(0.5), u_srcSize - float2(0.5));
                        float2 c2 = clamp(srcCoord + float2(0.0, -1.0), float2(0.5), u_srcSize - float2(0.5));
                        float2 c3 = clamp(srcCoord + float2(0.0, 1.0), float2(0.5), u_srcSize - float2(0.5));
                        float3 n0 = u_srcLinear > 0.5 ? max(sample(u_image, c0).rgb, 0.0) : to_lin(sample(u_image, c0).rgb);
                        float3 n1 = u_srcLinear > 0.5 ? max(sample(u_image, c1).rgb, 0.0) : to_lin(sample(u_image, c1).rgb);
                        float3 n2 = u_srcLinear > 0.5 ? max(sample(u_image, c2).rgb, 0.0) : to_lin(sample(u_image, c2).rgb);
                        float3 n3 = u_srcLinear > 0.5 ? max(sample(u_image, c3).rgb, 0.0) : to_lin(sample(u_image, c3).rgb);

                        if (u_reconMode > 1.5) {
                            // LCh mode: ref: highlight reconstruction (LCh)
                            float3 mean = (lin + n0 + n1 + n2 + n3) * 0.2;
                            float3 rgbMax = max(lin, max(max(n0, n1), max(n2, n3)));

                            float Ro = min(mean.r, clipThresh);
                            float Go = min(mean.g, clipThresh);
                            float Bo = min(mean.b, clipThresh);

                            float R = rgbMax.r;
                            float G = rgbMax.g;
                            float B = rgbMax.b;

                            float L = (R + G + B) * 0.33333333;
                            float C = 1.7320508 * (R - G);
                            float H = 2.0 * B - G - R;

                            float Co = 1.7320508 * (Ro - Go);
                            float Ho = 2.0 * Bo - Go - Ro;

                            float ch2 = C * C + H * H;
                            if (ch2 > 1e-6) {
                                float ratio = sqrt((Co * Co + Ho * Ho) / ch2);
                                C *= ratio;
                                H *= ratio;
                            }

                            float recR = L - H * 0.16666667 + C / 3.4641016;
                            float recG = L - H * 0.16666667 - C / 3.4641016;
                            float recB = L + H * 0.33333333;

                            if (lin.r >= clipThresh) lin.r = recR;
                            if (lin.g >= clipThresh) lin.g = recG;
                            if (lin.b >= clipThresh) lin.b = recB;
                        } else {
                            // Opposed mode: ref: highlight reconstruction (opposed)
                            float3 u0 = cbrt_pos3(lin);
                            float3 ref0 = opposed_ref(u0);

                            float3 sumChroma = float3(0.0);
                            float3 cntChroma = float3(0.0);
                            float loThresh = 0.2 * clipThresh;

                            float3 nu0 = cbrt_pos3(n0);
                            float3 nr0 = opposed_ref(nu0);
                            if (n0.r > loThresh && n0.r < clipThresh) { sumChroma.r += n0.r - nr0.r; cntChroma.r += 1.0; }
                            if (n0.g > loThresh && n0.g < clipThresh) { sumChroma.g += n0.g - nr0.g; cntChroma.g += 1.0; }
                            if (n0.b > loThresh && n0.b < clipThresh) { sumChroma.b += n0.b - nr0.b; cntChroma.b += 1.0; }

                            float3 nu1 = cbrt_pos3(n1);
                            float3 nr1 = opposed_ref(nu1);
                            if (n1.r > loThresh && n1.r < clipThresh) { sumChroma.r += n1.r - nr1.r; cntChroma.r += 1.0; }
                            if (n1.g > loThresh && n1.g < clipThresh) { sumChroma.g += n1.g - nr1.g; cntChroma.g += 1.0; }
                            if (n1.b > loThresh && n1.b < clipThresh) { sumChroma.b += n1.b - nr1.b; cntChroma.b += 1.0; }

                            float3 nu2 = cbrt_pos3(n2);
                            float3 nr2 = opposed_ref(nu2);
                            if (n2.r > loThresh && n2.r < clipThresh) { sumChroma.r += n2.r - nr2.r; cntChroma.r += 1.0; }
                            if (n2.g > loThresh && n2.g < clipThresh) { sumChroma.g += n2.g - nr2.g; cntChroma.g += 1.0; }
                            if (n2.b > loThresh && n2.b < clipThresh) { sumChroma.b += n2.b - nr2.b; cntChroma.b += 1.0; }

                            float3 nu3 = cbrt_pos3(n3);
                            float3 nr3 = opposed_ref(nu3);
                            if (n3.r > loThresh && n3.r < clipThresh) { sumChroma.r += n3.r - nr3.r; cntChroma.r += 1.0; }
                            if (n3.g > loThresh && n3.g < clipThresh) { sumChroma.g += n3.g - nr3.g; cntChroma.g += 1.0; }
                            if (n3.b > loThresh && n3.b < clipThresh) { sumChroma.b += n3.b - nr3.b; cntChroma.b += 1.0; }

                            float3 chroma = float3(
                                cntChroma.r > 0.5 ? sumChroma.r / cntChroma.r : 0.0,
                                cntChroma.g > 0.5 ? sumChroma.g / cntChroma.g : 0.0,
                                cntChroma.b > 0.5 ? sumChroma.b / cntChroma.b : 0.0
                            );

                            if (lin.r >= clipThresh) lin.r = ref0.r + chroma.r;
                            if (lin.g >= clipThresh) lin.g = ref0.g + chroma.g;
                            if (lin.b >= clipThresh) lin.b = ref0.b + chroma.b;
                        }

                        // Smooth highlight rolloff: compress excess into visible highlights
                        float maxCh = max(lin.r, max(lin.g, lin.b));
                        if (maxCh > clipThresh) {
                            float over = maxCh - clipThresh;
                            float headroom = max(1.0 - clipThresh, 0.08);
                            float compressed = clipThresh + headroom * (1.0 - exp(-over / headroom));
                            lin *= compressed / maxCh;
                        }
                    }
                } else {
                    lin = min(lin, float3(u_hlThreshold));
                }

                // Color reconstruction (step 2): ref: color reconstruction
                if (u_reconColorAmount > 0.001) {
                    float clipThresh = u_hlThreshold * 0.987;
                    if (lin.r >= clipThresh || lin.g >= clipThresh || lin.b >= clipThresh) {
                        float rad = max(1.0, u_reconColorSpatial * 0.15);
                        float3 sumCol = float3(0.0);
                        float sumW = 0.0;
                        float2 off1 = float2(rad, 0.0);
                        float2 off2 = float2(-rad, 0.0);
                        float2 off3 = float2(0.0, rad);
                        float2 off4 = float2(0.0, -rad);

                        float3 sn1 = u_srcLinear > 0.5 ? max(sample(u_image, clamp(srcCoord + off1, float2(0.5), u_srcSize - float2(0.5))).rgb, 0.0) : to_lin(sample(u_image, clamp(srcCoord + off1, float2(0.5), u_srcSize - float2(0.5))).rgb);
                        float3 sn2 = u_srcLinear > 0.5 ? max(sample(u_image, clamp(srcCoord + off2, float2(0.5), u_srcSize - float2(0.5))).rgb, 0.0) : to_lin(sample(u_image, clamp(srcCoord + off2, float2(0.5), u_srcSize - float2(0.5))).rgb);
                        float3 sn3 = u_srcLinear > 0.5 ? max(sample(u_image, clamp(srcCoord + off3, float2(0.5), u_srcSize - float2(0.5))).rgb, 0.0) : to_lin(sample(u_image, clamp(srcCoord + off3, float2(0.5), u_srcSize - float2(0.5))).rgb);
                        float3 sn4 = u_srcLinear > 0.5 ? max(sample(u_image, clamp(srcCoord + off4, float2(0.5), u_srcSize - float2(0.5))).rgb, 0.0) : to_lin(sample(u_image, clamp(srcCoord + off4, float2(0.5), u_srcSize - float2(0.5))).rgb);

                        float w1 = (sn1.r < clipThresh && sn1.g < clipThresh && sn1.b < clipThresh) ? (max(max(sn1.r, sn1.g), sn1.b) - min(min(sn1.r, sn1.g), sn1.b) + 0.01) : 0.0;
                        float w2 = (sn2.r < clipThresh && sn2.g < clipThresh && sn2.b < clipThresh) ? (max(max(sn2.r, sn2.g), sn2.b) - min(min(sn2.r, sn2.g), sn2.b) + 0.01) : 0.0;
                        float w3 = (sn3.r < clipThresh && sn3.g < clipThresh && sn3.b < clipThresh) ? (max(max(sn3.r, sn3.g), sn3.b) - min(min(sn3.r, sn3.g), sn3.b) + 0.01) : 0.0;
                        float w4 = (sn4.r < clipThresh && sn4.g < clipThresh && sn4.b < clipThresh) ? (max(max(sn4.r, sn4.g), sn4.b) - min(min(sn4.r, sn4.g), sn4.b) + 0.01) : 0.0;

                        sumCol += sn1 * w1 + sn2 * w2 + sn3 * w3 + sn4 * w4;
                        sumW += w1 + w2 + w3 + w4;

                        if (sumW > 0.001) {
                            float3 avgCol = sumCol / sumW;
                            float lCur = luma2020(lin);
                            float lAvg = max(luma2020(avgCol), 0.001);
                            float3 scaledChroma = avgCol * (lCur / lAvg);
                            lin = mix(lin, scaledChroma, clamp(u_reconColorAmount, 0.0, 1.0));
                        }
                    }
                }

                float3 processed = lin;
                if (lookAmt > 0.001) {
                    processed = apply_look(lin);
                }

                // Local contrast & Clarity (step 8): halo-free bilateral midtone enhancement
                if (abs(u_localDetail) > 0.001 || abs(u_localHighlights) > 0.001 || abs(u_localShadows) > 0.001 || abs(u_localMidtones - 0.5) > 0.01) {
                    float lum = max(luma2020(processed), 0.0001);
                    float minDim = min(u_srcSize.x, u_srcSize.y);
                    float r1 = max(1.5, minDim * 0.0035);
                    float r2 = r1 * 2.2;

                    // Tight bilateral edge-stopping range sigma: prevents stark edges (e.g. animal ears against background)
                    // from bleeding into the base layer and creating dark or white ghost shadows.
                    float rangeSigma = clamp(lum * 0.20 + 0.06, 0.05, 0.22);
                    float invTwoSigmaSq = 0.5 / (rangeSigma * rangeSigma);

                    float sumL = lum * 1.5;
                    float sumW = 1.5;

                    // Ring 1: 4 diagonal taps (radius r1)
                    float2 d1 = float2(r1 * 0.7071, r1 * 0.7071);
                    float2 d2 = float2(-r1 * 0.7071, r1 * 0.7071);
                    {
                        float3 p = fetch_look(srcCoord + d1, lookAmt);
                        float lp = luma2020(p);
                        float diff = lp - lum;
                        float w = exp(-diff * diff * invTwoSigmaSq);
                        sumL += lp * w; sumW += w;
                    }
                    {
                        float3 p = fetch_look(srcCoord - d1, lookAmt);
                        float lp = luma2020(p);
                        float diff = lp - lum;
                        float w = exp(-diff * diff * invTwoSigmaSq);
                        sumL += lp * w; sumW += w;
                    }
                    {
                        float3 p = fetch_look(srcCoord + d2, lookAmt);
                        float lp = luma2020(p);
                        float diff = lp - lum;
                        float w = exp(-diff * diff * invTwoSigmaSq);
                        sumL += lp * w; sumW += w;
                    }
                    {
                        float3 p = fetch_look(srcCoord - d2, lookAmt);
                        float lp = luma2020(p);
                        float diff = lp - lum;
                        float w = exp(-diff * diff * invTwoSigmaSq);
                        sumL += lp * w; sumW += w;
                    }

                    // Ring 2: 4 cardinal taps (radius r2)
                    float2 c1 = float2(r2, 0.0);
                    float2 c2 = float2(0.0, r2);
                    {
                        float3 p = fetch_look(srcCoord + c1, lookAmt);
                        float lp = luma2020(p);
                        float diff = lp - lum;
                        float w = 0.7 * exp(-diff * diff * invTwoSigmaSq);
                        sumL += lp * w; sumW += w;
                    }
                    {
                        float3 p = fetch_look(srcCoord - c1, lookAmt);
                        float lp = luma2020(p);
                        float diff = lp - lum;
                        float w = 0.7 * exp(-diff * diff * invTwoSigmaSq);
                        sumL += lp * w; sumW += w;
                    }
                    {
                        float3 p = fetch_look(srcCoord + c2, lookAmt);
                        float lp = luma2020(p);
                        float diff = lp - lum;
                        float w = 0.7 * exp(-diff * diff * invTwoSigmaSq);
                        sumL += lp * w; sumW += w;
                    }
                    {
                        float3 p = fetch_look(srcCoord - c2, lookAmt);
                        float lp = luma2020(p);
                        float diff = lp - lum;
                        float w = 0.7 * exp(-diff * diff * invTwoSigmaSq);
                        sumL += lp * w; sumW += w;
                    }

                    if (u_localFast < 0.5) {
                        // Ring 3: 4 intermediate taps (radius r1 * 3.4) in settle mode
                        float r3 = r1 * 3.4;
                        float2 d3 = float2(r3 * 0.9239, r3 * 0.3827);
                        float2 d4 = float2(-r3 * 0.3827, r3 * 0.9239);
                        {
                            float3 p = fetch_look(srcCoord + d3, lookAmt);
                            float lp = luma2020(p);
                            float diff = lp - lum;
                            float w = 0.45 * exp(-diff * diff * invTwoSigmaSq);
                            sumL += lp * w; sumW += w;
                        }
                        {
                            float3 p = fetch_look(srcCoord - d3, lookAmt);
                            float lp = luma2020(p);
                            float diff = lp - lum;
                            float w = 0.45 * exp(-diff * diff * invTwoSigmaSq);
                            sumL += lp * w; sumW += w;
                        }
                        {
                            float3 p = fetch_look(srcCoord + d4, lookAmt);
                            float lp = luma2020(p);
                            float diff = lp - lum;
                            float w = 0.45 * exp(-diff * diff * invTwoSigmaSq);
                            sumL += lp * w; sumW += w;
                        }
                        {
                            float3 p = fetch_look(srcCoord - d4, lookAmt);
                            float lp = luma2020(p);
                            float diff = lp - lum;
                            float w = 0.45 * exp(-diff * diff * invTwoSigmaSq);
                            sumL += lp * w; sumW += w;
                        }
                    }

                    float g = sumL / sumW;
                    float sigma = max(u_localMidtones, 0.05);
                    float newLum = curve_scalar(lum, g, sigma, u_localShadows, u_localHighlights, u_localDetail);
                    processed *= max(newLum, 0.0) / lum;
                }

                // Denoise (step 9)
                if (u_denoiseLuma > 0.001 || u_denoiseChroma > 0.001) {
                    float lc = luma2020(processed);
                    float3 cc = processed - lc;
                    float rangeSigma = 0.08 + 0.25 * u_denoiseLuma;
                    float sumL = lc;
                    float sumW_L = 1.0;
                    float3 sumC = cc;
                    float sumW_C = 1.0;

                    // Inner 4 taps (radius 2)
                    {
                        float3 p = fetch_look(srcCoord + float2(-2.0, 0.0), lookAmt);
                        float l = luma2020(p);
                        float wl = 0.8 * exp(-abs(l - lc) / rangeSigma);
                        sumL += l * wl; sumW_L += wl;
                        sumC += (p - l) * 0.8; sumW_C += 0.8;
                    }
                    {
                        float3 p = fetch_look(srcCoord + float2(2.0, 0.0), lookAmt);
                        float l = luma2020(p);
                        float wl = 0.8 * exp(-abs(l - lc) / rangeSigma);
                        sumL += l * wl; sumW_L += wl;
                        sumC += (p - l) * 0.8; sumW_C += 0.8;
                    }
                    {
                        float3 p = fetch_look(srcCoord + float2(0.0, -2.0), lookAmt);
                        float l = luma2020(p);
                        float wl = 0.8 * exp(-abs(l - lc) / rangeSigma);
                        sumL += l * wl; sumW_L += wl;
                        sumC += (p - l) * 0.8; sumW_C += 0.8;
                    }
                    {
                        float3 p = fetch_look(srcCoord + float2(0.0, 2.0), lookAmt);
                        float l = luma2020(p);
                        float wl = 0.8 * exp(-abs(l - lc) / rangeSigma);
                        sumL += l * wl; sumW_L += wl;
                        sumC += (p - l) * 0.8; sumW_C += 0.8;
                    }

                    if (u_denoiseFast < 0.5) {
                        // Ring 1 (radius 1 & 1.41)
                        { float3 p = fetch_look(srcCoord + float2(-1.0, 0.0), lookAmt); float l = luma2020(p); float wl = 1.0 * exp(-abs(l - lc) / rangeSigma); sumL += l * wl; sumW_L += wl; sumC += (p - l) * 1.0; sumW_C += 1.0; }
                        { float3 p = fetch_look(srcCoord + float2(1.0, 0.0), lookAmt); float l = luma2020(p); float wl = 1.0 * exp(-abs(l - lc) / rangeSigma); sumL += l * wl; sumW_L += wl; sumC += (p - l) * 1.0; sumW_C += 1.0; }
                        { float3 p = fetch_look(srcCoord + float2(0.0, -1.0), lookAmt); float l = luma2020(p); float wl = 1.0 * exp(-abs(l - lc) / rangeSigma); sumL += l * wl; sumW_L += wl; sumC += (p - l) * 1.0; sumW_C += 1.0; }
                        { float3 p = fetch_look(srcCoord + float2(0.0, 1.0), lookAmt); float l = luma2020(p); float wl = 1.0 * exp(-abs(l - lc) / rangeSigma); sumL += l * wl; sumW_L += wl; sumC += (p - l) * 1.0; sumW_C += 1.0; }

                        { float3 p = fetch_look(srcCoord + float2(-1.0, -1.0), lookAmt); float l = luma2020(p); float wl = 0.8 * exp(-abs(l - lc) / rangeSigma); sumL += l * wl; sumW_L += wl; sumC += (p - l) * 0.8; sumW_C += 0.8; }
                        { float3 p = fetch_look(srcCoord + float2(1.0, -1.0), lookAmt); float l = luma2020(p); float wl = 0.8 * exp(-abs(l - lc) / rangeSigma); sumL += l * wl; sumW_L += wl; sumC += (p - l) * 0.8; sumW_C += 0.8; }
                        { float3 p = fetch_look(srcCoord + float2(-1.0, 1.0), lookAmt); float l = luma2020(p); float wl = 0.8 * exp(-abs(l - lc) / rangeSigma); sumL += l * wl; sumW_L += wl; sumC += (p - l) * 0.8; sumW_C += 0.8; }
                        { float3 p = fetch_look(srcCoord + float2(1.0, 1.0), lookAmt); float l = luma2020(p); float wl = 0.8 * exp(-abs(l - lc) / rangeSigma); sumL += l * wl; sumW_L += wl; sumC += (p - l) * 0.8; sumW_C += 0.8; }

                        // Ring 2 (radius 2.83 & 3)
                        { float3 p = fetch_look(srcCoord + float2(-2.0, -2.0), lookAmt); float l = luma2020(p); float wl = 0.5 * exp(-abs(l - lc) / rangeSigma); sumL += l * wl; sumW_L += wl; sumC += (p - l) * 0.5; sumW_C += 0.5; }
                        { float3 p = fetch_look(srcCoord + float2(2.0, -2.0), lookAmt); float l = luma2020(p); float wl = 0.5 * exp(-abs(l - lc) / rangeSigma); sumL += l * wl; sumW_L += wl; sumC += (p - l) * 0.5; sumW_C += 0.5; }
                        { float3 p = fetch_look(srcCoord + float2(-2.0, 2.0), lookAmt); float l = luma2020(p); float wl = 0.5 * exp(-abs(l - lc) / rangeSigma); sumL += l * wl; sumW_L += wl; sumC += (p - l) * 0.5; sumW_C += 0.5; }
                        { float3 p = fetch_look(srcCoord + float2(2.0, 2.0), lookAmt); float l = luma2020(p); float wl = 0.5 * exp(-abs(l - lc) / rangeSigma); sumL += l * wl; sumW_L += wl; sumC += (p - l) * 0.5; sumW_C += 0.5; }

                        { float3 p = fetch_look(srcCoord + float2(-3.0, 0.0), lookAmt); float l = luma2020(p); float wl = 0.4 * exp(-abs(l - lc) / rangeSigma); sumL += l * wl; sumW_L += wl; sumC += (p - l) * 0.4; sumW_C += 0.4; }
                        { float3 p = fetch_look(srcCoord + float2(3.0, 0.0), lookAmt); float l = luma2020(p); float wl = 0.4 * exp(-abs(l - lc) / rangeSigma); sumL += l * wl; sumW_L += wl; sumC += (p - l) * 0.4; sumW_C += 0.4; }
                        { float3 p = fetch_look(srcCoord + float2(0.0, -3.0), lookAmt); float l = luma2020(p); float wl = 0.4 * exp(-abs(l - lc) / rangeSigma); sumL += l * wl; sumW_L += wl; sumC += (p - l) * 0.4; sumW_C += 0.4; }
                        { float3 p = fetch_look(srcCoord + float2(0.0, 3.0), lookAmt); float l = luma2020(p); float wl = 0.4 * exp(-abs(l - lc) / rangeSigma); sumL += l * wl; sumW_L += wl; sumC += (p - l) * 0.4; sumW_C += 0.4; }

                        // Ring 3 (radius 4 & 4.24)
                        { float3 p = fetch_look(srcCoord + float2(-4.0, 0.0), lookAmt); float l = luma2020(p); float wl = 0.25 * exp(-abs(l - lc) / rangeSigma); sumL += l * wl; sumW_L += wl; sumC += (p - l) * 0.25; sumW_C += 0.25; }
                        { float3 p = fetch_look(srcCoord + float2(4.0, 0.0), lookAmt); float l = luma2020(p); float wl = 0.25 * exp(-abs(l - lc) / rangeSigma); sumL += l * wl; sumW_L += wl; sumC += (p - l) * 0.25; sumW_C += 0.25; }
                        { float3 p = fetch_look(srcCoord + float2(0.0, -4.0), lookAmt); float l = luma2020(p); float wl = 0.25 * exp(-abs(l - lc) / rangeSigma); sumL += l * wl; sumW_L += wl; sumC += (p - l) * 0.25; sumW_C += 0.25; }
                        { float3 p = fetch_look(srcCoord + float2(0.0, 4.0), lookAmt); float l = luma2020(p); float wl = 0.25 * exp(-abs(l - lc) / rangeSigma); sumL += l * wl; sumW_L += wl; sumC += (p - l) * 0.25; sumW_C += 0.25; }

                        { float3 p = fetch_look(srcCoord + float2(-3.0, -3.0), lookAmt); float l = luma2020(p); float wl = 0.2 * exp(-abs(l - lc) / rangeSigma); sumL += l * wl; sumW_L += wl; sumC += (p - l) * 0.2; sumW_C += 0.2; }
                        { float3 p = fetch_look(srcCoord + float2(3.0, -3.0), lookAmt); float l = luma2020(p); float wl = 0.2 * exp(-abs(l - lc) / rangeSigma); sumL += l * wl; sumW_L += wl; sumC += (p - l) * 0.2; sumW_C += 0.2; }
                        { float3 p = fetch_look(srcCoord + float2(-3.0, 3.0), lookAmt); float l = luma2020(p); float wl = 0.2 * exp(-abs(l - lc) / rangeSigma); sumL += l * wl; sumW_L += wl; sumC += (p - l) * 0.2; sumW_C += 0.2; }
                        { float3 p = fetch_look(srcCoord + float2(3.0, 3.0), lookAmt); float l = luma2020(p); float wl = 0.2 * exp(-abs(l - lc) / rangeSigma); sumL += l * wl; sumW_L += wl; sumC += (p - l) * 0.2; sumW_C += 0.2; }
                    }

                    float blurL = sumL / sumW_L;
                    float3 blurC = sumC / sumW_C;
                    float kL = (u_denoiseFast > 0.5) ? u_denoiseLuma * 0.80 : u_denoiseLuma;
                    float kC = (u_denoiseFast > 0.5) ? u_denoiseChroma * 0.90 : u_denoiseChroma;
                    float finalL = (u_denoiseLuma > 0.001) ? mix(lc, blurL, kL) : lc;
                    float3 finalC = (u_denoiseChroma > 0.001) ? mix(cc, blurC, kC) : cc;
                    processed = max(float3(finalL) + finalC, 0.0);
                }

                if (u_noise > 0.005) {
                    float lum = luma2020(processed);
                    float midCurve = sin(clamp(lum, 0.0, 1.0) * 3.14159265);
                    float2 pCoord = srcCoord;
                    float n1 = fract(sin(dot(pCoord, float2(12.9898, 78.233))) * 43758.5453) * 2.0 - 1.0;
                    float n2 = fract(sin(dot(floor(pCoord * 0.5), float2(39.346, 11.135))) * 23421.631) * 2.0 - 1.0;
                    float rawGrain = n1 * 0.70 + n2 * 0.30;
                    float grain = rawGrain * u_noise * 0.12 * (0.35 + 0.65 * midCurve);
                    processed = max(processed + float3(grain), 0.0);
                }

                float3 disp = to_srgb(tone_curve(processed));
                if (u_sharpen > 0.001) {
                    float3 o0 = u_srcLinear > 0.5 ? orig.rgb : to_lin(orig.rgb);
                    float3 d0 = to_srgb(tone_curve(o0));
                    float3 acc = float3(0.0);
                    acc += to_srgb(tone_curve(u_srcLinear > 0.5 ? sample(u_image, srcCoord + float2(0.0, -1.0)).rgb : to_lin(sample(u_image, srcCoord + float2(0.0, -1.0)).rgb)));
                    acc += to_srgb(tone_curve(u_srcLinear > 0.5 ? sample(u_image, srcCoord + float2(0.0, 1.0)).rgb : to_lin(sample(u_image, srcCoord + float2(0.0, 1.0)).rgb)));
                    acc += to_srgb(tone_curve(u_srcLinear > 0.5 ? sample(u_image, srcCoord + float2(-1.0, 0.0)).rgb : to_lin(sample(u_image, srcCoord + float2(-1.0, 0.0)).rgb)));
                    acc += to_srgb(tone_curve(u_srcLinear > 0.5 ? sample(u_image, srcCoord + float2(1.0, 0.0)).rgb : to_lin(sample(u_image, srcCoord + float2(1.0, 0.0)).rgb)));
                    acc *= 0.25;
                    disp += (d0 - acc) * u_sharpen * 2.4;
                }
                if (u_hasLut > 0.5 && u_lutAmount > 0.001) {
                    disp = mix(disp, sample_lut(disp), u_lutAmount);
                }

                float3 baseDisp = u_srcLinear > 0.5 ? to_srgb(sigmoid_rgb(lin)) : orig.rgb;
                float3 outc = mix(baseDisp, disp, lookAmt);
                outc = clamp(outc, 0.0, 1.0);
                return half4(outc, orig.a);
            }
        ";
}
