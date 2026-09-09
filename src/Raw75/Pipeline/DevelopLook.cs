using System;
using Blossom;
using Blossom.Core;
using Raw75.Develop;
using Raw75.Imaging;
using SkiaSharp;

namespace Raw75.Pipeline;

/// <summary>
/// GPU develop look drawn into the photo pane. Uniforms update per slider tick;
/// the source texture stays put. No offscreen snapshot while dragging.
/// </summary>
internal sealed class DevelopLook : IDisposable
{
    private SKImage? _source;
    private SKShader? _img;
    private SKShader? _lut;
    private SKShader? _mask;
    private SKShader? _fxFast;
    private SKShader? _fxQuality;
    private readonly SKPaint _paint = new() { IsAntialias = false, FilterQuality = SKFilterQuality.Medium };
    private bool _loggedOk;
    private bool _dirty = true;
    private bool _cachedCrop;
    private float _cachedTx, _cachedTy, _cachedTw = 1f, _cachedTh = 1f;
    private int _cachedFw, _cachedFh;

    public bool Failed { get; private set; }

    public void Invalidate() => _dirty = true;

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
        int frameH = 0)
    {
        if (Failed || canvas == null || source == null || source.Handle == IntPtr.Zero)
            return false;
        if (dest.Width < 1f || dest.Height < 1f || clip.Width < 0.5f || clip.Height < 0.5f)
            return false;

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
                    fast: fast, applyCrop: applyCrop, srcLinear: srcLinear,
                    tileX: tileX, tileY: tileY, tileW: tileW, tileH: tileH,
                    viewCropX: float.NaN, viewCropY: float.NaN,
                    viewCropW: float.NaN, viewCropH: float.NaN,
                    frameW: frameW, frameH: frameH);

                var children = new SKRuntimeEffectChildren(effect);
                children.Add("u_image", _img);
                children.Add("u_lut", _lut);
                children.Add("u_mask", _mask);

                SKShader? fx = effect.ToShader(true, uniforms, children);
                if (fx == null)
                {
                    Failed = true;
                    return false;
                }

                activeFx = fx;
            }

            _paint.Shader = activeFx;

            canvas.Save();
            canvas.ClipRect(clip);
            canvas.Translate(dest.Left, dest.Top);
            canvas.Scale(dest.Width, dest.Height);
            canvas.DrawRect(new SKRect(0, 0, 1, 1), _paint);
            canvas.Restore();

            if (!_loggedOk)
            {
                _loggedOk = true;
                Log.Info($"GPU look {source.Width}x{source.Height} → {dest.Width:0}x{dest.Height:0}");
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Error("GPU look failed: " + ex.Message);
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

    public void Dispose()
    {
        _paint.Shader = null;
        _paint.Dispose();
        if (_fxFast != null) GpuRetain.RetireShader(_fxFast);
        if (_fxQuality != null) GpuRetain.RetireShader(_fxQuality);
        _fxFast = null;
        _fxQuality = null;
        _img?.Dispose();
        _lut?.Dispose();
        _mask?.Dispose();
        _img = _lut = _mask = null;
        _source = null;
    }
}
