using System;
using Blossom;
using Blossom.Core;
using Raw75.Develop;
using Raw75.Imaging;
using SkiaSharp;

namespace Raw75.Pipeline;

/// <summary>
/// GPU develop look drawn into the photo pane using a high-performance 3-stage pipeline:
/// Stage 1: Sensor &amp; RAW Inpainting (Vignetting + Highlight Recon) -> cached offscreen linear HDR image.
/// Stage 2: Spatial &amp; Frequency Domain (Bilateral Local Contrast + Texture + Denoise) -> cached offscreen linear HDR image (bypassed if inactive).
/// Stage 3: Creative Look &amp; Display Transform (WB + EV + Tone + HSL + Display Curve) -> direct to destination canvas (1-tap, 120+ FPS).
/// Monolithic fallback is used if offscreen allocations or staged shaders fail.
/// </summary>
internal sealed class DevelopLook : IDisposable
{
    private SKImage? _source;
    private SKShader? _img;
    private SKShader? _lut;
    private SKShader? _mask;
    private readonly SKPaint _paint = new() { IsAntialias = false, FilterQuality = SKFilterQuality.Medium };
    private bool _loggedOk;
    private bool _dirty = true;

    // Cached intermediate images
    private SKImage? _stage1Image;
    private SKImage? _stage2Image;

    // Stage 1 tracking
    private HighlightMode _cachedReconMode;
    private float _cachedHlThreshold;
    private float _cachedReconColorAmount;
    private float _cachedReconColorSpatial;
    private bool _cachedStage1Fast;
    private bool _stage1Dirty = true;

    // Stage 2 tracking
    private float _cachedLocalDetail;
    private float _cachedTexture;
    private float _cachedDenoiseLuma;
    private float _cachedDenoiseChroma;
    private bool _cachedStage2Fast;
    private bool _stage2Dirty = true;

    // Monolithic fallback cache
    private SKShader? _fxFast;
    private SKShader? _fxQuality;
    private bool _cachedCrop;
    private bool _cachedClipping;
    private float _cachedTx, _cachedTy, _cachedTw = 1f, _cachedTh = 1f;
    private int _cachedFw, _cachedFh;

    public bool Failed { get; private set; }

    public void Invalidate() => _dirty = true;

    public void InvalidateAll()
    {
        _dirty = true;
        _stage1Dirty = true;
        _stage2Dirty = true;
    }

    public bool Draw(
        SKCanvas canvas,
        SKRect dest,
        SKRect clip,
        SKImage source,
        DevelopSettings settings,
        bool fast,
        bool applyCrop = false,
        float tileX = 0f,
        float tileY = 0f,
        float tileW = 1f,
        float tileH = 1f,
        float viewCropX = float.NaN,
        float viewCropY = float.NaN,
        float viewCropW = float.NaN,
        float viewCropH = float.NaN,
        int frameW = 0,
        int frameH = 0,
        bool showClipping = false)
    {
        if (!Gpu.IsReady || Failed || canvas == null || source == null || source.Handle == IntPtr.Zero)
            return false;
        if (dest.Width < 1f || dest.Height < 1f || clip.Width < 0.5f || clip.Height < 0.5f)
            return false;

        // Try staged 3-pass rendering first (requires active GPU context)
        if (Gpu.IsReady && DrawStaged(
            canvas, dest, clip, source, settings, fast, applyCrop,
            tileX, tileY, tileW, tileH,
            viewCropX, viewCropY, viewCropW, viewCropH,
            frameW, frameH, showClipping))
        {
            return true;
        }

        // Fallback to monolithic single-shader pipeline if staged pipeline fails
        return DrawMonolithic(
            canvas, dest, clip, source, settings, fast, applyCrop,
            tileX, tileY, tileW, tileH,
            viewCropX, viewCropY, viewCropW, viewCropH,
            frameW, frameH, showClipping);
    }

    private bool DrawStaged(
        SKCanvas canvas,
        SKRect dest,
        SKRect clip,
        SKImage source,
        DevelopSettings settings,
        bool fast,
        bool applyCrop,
        float tileX, float tileY, float tileW, float tileH,
        float viewCropX, float viewCropY, float viewCropW, float viewCropH,
        int frameW, int frameH, bool showClipping)
    {
        SKRuntimeEffect? eff1 = DevelopRenderer.EnsureStage1Effect();
        SKRuntimeEffect? eff3 = DevelopRenderer.EnsureStage3Effect();
        if (eff1 == null || eff3 == null)
            return false;

        try
        {
            // If source image changed, all caches must be purged
            if (!ReferenceEquals(_source, source))
            {
                if (_stage1Image != null) { GpuRetain.Retire(_stage1Image); _stage1Image = null; }
                if (_stage2Image != null) { GpuRetain.Retire(_stage2Image); _stage2Image = null; }
                _source = source;
                _stage1Dirty = true;
                _stage2Dirty = true;
            }

            bool srcLinear = source.ColorType == SKColorType.RgbaF16
                || source.ColorType == SKColorType.RgbaF32;

            // --- STAGE 1: Sensor & RAW Inpainting ---
            HighlightMode curReconMode = settings.EnableReconstruction ? settings.ReconstructionMode : HighlightMode.Off;
            float curHlThresh = settings.HighlightThreshold;
            float curColorAmt = settings.EnableReconstruction ? settings.ColorReconstructionAmount / 100f : 0f;
            float curColorSpatial = settings.ColorReconstructionSpatial;
            bool needStage1 = _stage1Dirty
                || _stage1Image == null
                || curReconMode != _cachedReconMode
                || curHlThresh != _cachedHlThreshold
                || curColorAmt != _cachedReconColorAmount
                || curColorSpatial != _cachedReconColorSpatial
                || (curReconMode != HighlightMode.Off && fast != _cachedStage1Fast);

            if (needStage1)
            {
                var u1 = new SKRuntimeEffectUniforms(eff1);
                DevelopRenderer.BindStage1Uniforms(u1, settings, source.Width, source.Height, fast, srcLinear);

                using var srcShader = source.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
                if (srcShader == null) return false;

                SKImage? newStage1 = DevelopRenderer.RenderOffscreenStage(eff1, u1, srcShader, source.Width, source.Height);
                if (newStage1 == null) return false;

                if (_stage1Image != null) GpuRetain.Retire(_stage1Image);
                _stage1Image = newStage1;
                _cachedReconMode = curReconMode;
                _cachedHlThreshold = curHlThresh;
                _cachedReconColorAmount = curColorAmt;
                _cachedReconColorSpatial = curColorSpatial;
                _cachedStage1Fast = fast;
                _stage1Dirty = false;
                _stage2Dirty = true; // Stage 1 change forces Stage 2 update
            }

            // --- STAGE 2: Spatial & Frequency Domain ---
            bool stage2Active = DevelopRenderer.IsStage2Active(settings);
            if (stage2Active)
            {
                SKRuntimeEffect? eff2 = DevelopRenderer.EnsureStage2Effect();
                if (eff2 == null) return false;

                float curDetail = settings.EnableLocalContrast ? settings.LocalContrastDetail : 0f;
                float curTex = settings.EnableLocalContrast ? settings.Texture / 100f : 0f;
                float noiseVal = settings.EnableDetail ? settings.Noise : 0f;
                if (noiseVal == 0f && settings.EnableDetail && (settings.DenoiseLuma > 0f || settings.DenoiseChroma > 0f))
                    noiseVal = -Math.Max(settings.DenoiseLuma, settings.DenoiseChroma);
                float curDenoiseLuma = noiseVal < 0f ? -noiseVal / 100f : 0f;
                float curDenoiseChroma = noiseVal < 0f ? -noiseVal / 100f : 0f;

                bool needStage2 = _stage2Dirty
                    || _stage2Image == null
                    || curDetail != _cachedLocalDetail
                    || curTex != _cachedTexture
                    || curDenoiseLuma != _cachedDenoiseLuma
                    || curDenoiseChroma != _cachedDenoiseChroma
                    || fast != _cachedStage2Fast;

                if (needStage2)
                {
                    var u2 = new SKRuntimeEffectUniforms(eff2);
                    DevelopRenderer.BindStage2Uniforms(u2, settings, source.Width, source.Height, fast);

                    SKImage inputForStage2 = _stage1Image ?? source;
                    using var s1Shader = inputForStage2.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
                    if (s1Shader == null) return false;

                    SKImage? newStage2 = DevelopRenderer.RenderOffscreenStage(eff2, u2, s1Shader, source.Width, source.Height);
                    if (newStage2 == null) return false;

                    if (_stage2Image != null) GpuRetain.Retire(_stage2Image);
                    _stage2Image = newStage2;
                    _cachedLocalDetail = curDetail;
                    _cachedTexture = curTex;
                    _cachedDenoiseLuma = curDenoiseLuma;
                    _cachedDenoiseChroma = curDenoiseChroma;
                    _cachedStage2Fast = fast;
                    _stage2Dirty = false;
                }
            }
            else
            {
                if (_stage2Image != null)
                {
                    GpuRetain.Retire(_stage2Image);
                    _stage2Image = null;
                }
            }

            // --- STAGE 3: Creative Look & Display Transform ---
            SKImage stage3Input = (stage2Active && _stage2Image != null) ? _stage2Image : (_stage1Image ?? source);
            using var stage3InputShader = stage3Input.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
            if (stage3InputShader == null) return false;

            SKImage lut = DevelopRenderer.GetLut(settings, out float lutSize, out float lutAmount);
            BindLut(lut);
            _mask ??= DevelopRenderer.WhitePixel().ToShader();
            if (_lut == null || _mask == null) return false;

            var u3 = new SKRuntimeEffectUniforms(eff3);
            DevelopRenderer.BindStage3Uniforms(
                u3, settings, source.Width, source.Height,
                0f, 0f, 1f, 1f,
                split: 0f, before: false, lutSize, lutAmount,
                applyCrop: applyCrop, showClipping: showClipping,
                tileX: tileX, tileY: tileY, tileW: tileW, tileH: tileH,
                viewCropX: viewCropX, viewCropY: viewCropY,
                viewCropW: viewCropW, viewCropH: viewCropH,
                frameW: frameW, frameH: frameH);

            var ch3 = new SKRuntimeEffectChildren(eff3);
            ch3.Add("u_image", stage3InputShader);
            ch3.Add("u_lut", _lut);
            ch3.Add("u_mask", _mask);
            SKImage curve = DevelopRenderer.GetCurveTexture(settings, out _, out _);
            using var stage3CurveShader = curve.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
            if (stage3CurveShader == null) return false;
            ch3.Add("u_curve", stage3CurveShader);

            bool tilePartial = tileW < 0.999f || tileH < 0.999f || tileX > 0.0001f || tileY > 0.0001f;
            using SKShader? fx = eff3.ToShader(!tilePartial, u3, ch3);
            if (fx == null) return false;

            try
            {
                _paint.Shader = fx;
                _paint.BlendMode = SKBlendMode.SrcOver;

                canvas.Save();
                canvas.ClipRect(clip);
                canvas.Translate(dest.Left, dest.Top);
                canvas.Scale(dest.Width, dest.Height);
                canvas.DrawRect(new SKRect(0, 0, 1, 1), _paint);
                canvas.Restore();
            }
            finally
            {
                _paint.Shader = null;
            }

            if (!_loggedOk)
            {
                _loggedOk = true;
                Log.Info($"GPU staged look (3-pass) {source.Width}x{source.Height} → {dest.Width:0}x{dest.Height:0}");
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Warning("GPU staged look encountered error, falling back to monolithic: " + ex.Message);
            return false;
        }
    }

    private bool DrawMonolithic(
        SKCanvas canvas,
        SKRect dest,
        SKRect clip,
        SKImage source,
        DevelopSettings settings,
        bool fast,
        bool applyCrop,
        float tileX, float tileY, float tileW, float tileH,
        float viewCropX, float viewCropY, float viewCropW, float viewCropH,
        int frameW, int frameH, bool showClipping)
    {
        SKRuntimeEffect? effect = DevelopRenderer.EnsureEffect();
        if (effect == null)
        {
            Failed = true;
            return false;
        }

        try
        {
            bool needRebind = _dirty
                || _cachedCrop != applyCrop
                || _cachedClipping != showClipping
                || _cachedTx != tileX || _cachedTy != tileY
                || _cachedTw != tileW || _cachedTh != tileH
                || _cachedFw != frameW || _cachedFh != frameH
                || !ReferenceEquals(_source, source);

            if (needRebind)
            {
                if (_fxFast != null) GpuRetain.RetireShader(_fxFast);
                if (_fxQuality != null) GpuRetain.RetireShader(_fxQuality);
                _fxFast = null;
                _fxQuality = null;

                BindSource(source);
                SKImage lut = DevelopRenderer.GetLut(settings, out float lutSize, out float lutAmount);
                BindLut(lut);
                _mask ??= DevelopRenderer.WhitePixel().ToShader();
                if (_img == null)
                    return false;

                _dirty = false;
                _cachedCrop = applyCrop;
                _cachedClipping = showClipping;
                _cachedTx = tileX;
                _cachedTy = tileY;
                _cachedTw = tileW;
                _cachedTh = tileH;
                _cachedFw = frameW;
                _cachedFh = frameH;
            }

            ref SKShader? activeFx = ref (fast ? ref _fxFast : ref _fxQuality);
            if (activeFx == null)
            {
                if (_img == null)
                    return false;

                var uniforms = new SKRuntimeEffectUniforms(effect);
                bool srcLinear = source.ColorType == SKColorType.RgbaF16
                    || source.ColorType == SKColorType.RgbaF32;
                SKImage lut = DevelopRenderer.GetLut(settings, out float lutSize, out float lutAmount);
                DevelopRenderer.BindUniforms(
                    uniforms, settings, source.Width, source.Height,
                    0f, 0f, 1f, 1f,
                    split: 0f, before: false, lutSize, lutAmount,
                    fast: fast, applyCrop: applyCrop, srcLinear: srcLinear, showClipping: showClipping,
                    tileX: tileX, tileY: tileY, tileW: tileW, tileH: tileH,
                    viewCropX: viewCropX, viewCropY: viewCropY,
                    viewCropW: viewCropW, viewCropH: viewCropH,
                    frameW: frameW, frameH: frameH);

                var children = new SKRuntimeEffectChildren(effect);
                children.Add("u_image", _img);
                children.Add("u_lut", _lut);
                children.Add("u_mask", _mask);
                SKImage curve = DevelopRenderer.GetCurveTexture(settings, out _, out _);
                using var curveShader = curve.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
                if (curveShader == null) return false;
                children.Add("u_curve", curveShader);

                bool tilePartial = tileW < 0.999f || tileH < 0.999f || tileX > 0.0001f || tileY > 0.0001f;
                SKShader? fx = effect.ToShader(!tilePartial, uniforms, children);
                if (fx == null)
                {
                    Failed = true;
                    return false;
                }

                activeFx = fx;
            }

            try
            {
                _paint.Shader = activeFx;
                _paint.BlendMode = SKBlendMode.SrcOver;

                canvas.Save();
                canvas.ClipRect(clip);
                canvas.Translate(dest.Left, dest.Top);
                canvas.Scale(dest.Width, dest.Height);
                canvas.DrawRect(new SKRect(0, 0, 1, 1), _paint);
                canvas.Restore();
            }
            finally
            {
                _paint.Shader = null;
            }

            if (!_loggedOk)
            {
                _loggedOk = true;
                Log.Info($"GPU look monolithic fallback {source.Width}x{source.Height} → {dest.Width:0}x{dest.Height:0}");
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Error("GPU look monolithic fallback failed: " + ex.Message);
            Failed = true;
            return false;
        }
    }

    private void BindSource(SKImage source)
    {
        if (ReferenceEquals(_source, source) && _img != null)
            return;
        _img?.Dispose();
        _img = source.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        _source = source;
    }

    private void BindLut(SKImage lut)
    {
        if (_lut != null && ReferenceEquals(lut, DevelopRenderer.LastLut))
            return;
        _lut?.Dispose();
        _lut = lut.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
    }

    public void Reset()
    {
        _paint.Shader = null;
        if (_stage1Image != null) GpuRetain.Retire(_stage1Image);
        if (_stage2Image != null) GpuRetain.Retire(_stage2Image);
        _stage1Image = null;
        _stage2Image = null;
        if (_fxFast != null) GpuRetain.RetireShader(_fxFast);
        if (_fxQuality != null) GpuRetain.RetireShader(_fxQuality);
        _fxFast = null;
        _fxQuality = null;
        _img?.Dispose();
        _lut?.Dispose();
        _mask?.Dispose();
        _img = _lut = _mask = null;
        _source = null;
        _dirty = true;
        _stage1Dirty = true;
        _stage2Dirty = true;
        _loggedOk = false;
    }

    public void Dispose()
    {
        Reset();
        _paint.Dispose();
    }
}
