using System;
using Blossom;
using Blossom.Core;
using Raw75.Develop;
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

    public bool Failed { get; private set; }

    public bool Draw(
        SKCanvas canvas,
        SKRect dest,
        SKRect clip,
        SKImage source,
        DevelopSettings settings,
        bool fast)
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
                (int)MathF.Round(dest.Width), (int)MathF.Round(dest.Height),
                split: 0f, before: false, lutSize, lutAmount,
                fast: fast, applyCrop: false, srcLinear: srcLinear);

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

            _fx?.Dispose();
            _fx = fx;
            _paint.Shader = _fx;

            canvas.Save();
            canvas.ClipRect(clip, SKClipOperation.Intersect, false);
            canvas.Translate(dest.Left, dest.Top);
            canvas.DrawRect(new SKRect(0, 0, dest.Width, dest.Height), _paint);
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
