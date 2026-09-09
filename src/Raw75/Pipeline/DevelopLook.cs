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
    private SKShader? _fx;
    private readonly SKPaint _paint = new() { IsAntialias = false, FilterQuality = SKFilterQuality.Medium };
    private bool _loggedOk;
    private bool _dirty = true;
    private bool _cachedFast;
    private bool _cachedCrop;
    private float _cachedDx, _cachedDy;
    private float _cachedDw, _cachedDh;
    private float _cachedTx, _cachedTy, _cachedTw = 1f, _cachedTh = 1f;
    private float _cachedCx, _cachedCy, _cachedCw = 1f, _cachedCh = 1f;
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
            float dx = dest.Left;
            float dy = dest.Top;
            float dw = Math.Max(1f, dest.Width);
            float dh = Math.Max(1f, dest.Height);
            bool needShader = _dirty || _fx == null || _paint.Shader == null
                || _cachedFast != fast || _cachedCrop != applyCrop
                || Math.Abs(_cachedDx - dx) > 0.01f || Math.Abs(_cachedDy - dy) > 0.01f
                || Math.Abs(_cachedDw - dw) > 0.01f || Math.Abs(_cachedDh - dh) > 0.01f
                || _cachedTx != tileX || _cachedTy != tileY
                || _cachedTw != tileW || _cachedTh != tileH
                || _cachedCx != viewCropX || _cachedCy != viewCropY
                || _cachedCw != viewCropW || _cachedCh != viewCropH
                || _cachedFw != frameW || _cachedFh != frameH
                || !ReferenceEquals(_source, source);

            if (needShader)
            {
                BindSource(source);
                SKImage lut = DevelopRenderer.GetLut(settings, out float lutSize, out float lutAmount);
                BindLut(lut);
                _mask ??= DevelopRenderer.WhitePixel().ToShader();
                if (_img == null)
                    return false;

                var uniforms = new SKRuntimeEffectUniforms(effect);
                bool srcLinear = source.ColorType == SKColorType.RgbaF16
                    || source.ColorType == SKColorType.RgbaF32;
                DevelopRenderer.BindUniforms(
                    uniforms, settings, source.Width, source.Height,
                    dx, dy, dw, dh,
                    split: 0f, before: false, lutSize, lutAmount,
                    fast: fast, applyCrop: applyCrop, srcLinear: srcLinear,
                    tileX: tileX, tileY: tileY, tileW: tileW, tileH: tileH,
                    viewCropX: viewCropX, viewCropY: viewCropY,
                    viewCropW: viewCropW, viewCropH: viewCropH,
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

                SKShader? prev = _fx;
                _fx = fx;
                _paint.Shader = _fx;
                if (prev != null)
                    GpuRetain.RetireShader(prev);

                _dirty = false;
                _cachedFast = fast;
                _cachedCrop = applyCrop;
                _cachedDx = dx;
                _cachedDy = dy;
                _cachedDw = dw;
                _cachedDh = dh;
                _cachedTx = tileX;
                _cachedTy = tileY;
                _cachedTw = tileW;
                _cachedTh = tileH;
                _cachedCx = viewCropX;
                _cachedCy = viewCropY;
                _cachedCw = viewCropW;
                _cachedCh = viewCropH;
                _cachedFw = frameW;
                _cachedFh = frameH;
            }

            canvas.Save();
            canvas.ClipRect(clip);
            canvas.DrawRect(dest, _paint);
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
        _fx?.Dispose();
        _img?.Dispose();
        _lut?.Dispose();
        _mask?.Dispose();
        _fx = _img = _lut = _mask = null;
        _source = null;
    }
}
