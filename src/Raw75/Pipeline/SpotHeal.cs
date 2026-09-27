using System;
using System.Collections.Generic;
using Blossom;
using Blossom.Core;
using Raw75.Develop;
using Raw75.Imaging;
using SkiaSharp;

namespace Raw75.Pipeline;

/// <summary>
/// Copies a source circle onto a destination circle with a feathered edge.
/// GPU: sequential offscreen passes. CPU: per-pixel blend on linear samples.
/// </summary>
internal static class SpotHeal
{
    private const int MaxSpots = 32;
    private static SKRuntimeEffect? _effect;
    private static bool _failed;

    public static bool HasSpots(DevelopSettings? s) =>
        s != null && s.EnableSpotRemoval && s.Spots != null && s.Spots.Count > 0;

    public static int Signature(DevelopSettings s, float tileX, float tileY, float tileW, float tileH)
    {
        if (!HasSpots(s))
            return 0;
        unchecked
        {
            int h = 17;
            h = h * 31 + s.Spots.Count;
            h = h * 31 + tileX.GetHashCode();
            h = h * 31 + tileY.GetHashCode();
            h = h * 31 + tileW.GetHashCode();
            h = h * 31 + tileH.GetHashCode();
            int n = Math.Min(s.Spots.Count, MaxSpots);
            for (int i = 0; i < n; i++)
            {
                var p = s.Spots[i];
                h = h * 31 + p.DestX.GetHashCode();
                h = h * 31 + p.DestY.GetHashCode();
                h = h * 31 + p.SrcX.GetHashCode();
                h = h * 31 + p.SrcY.GetHashCode();
                h = h * 31 + p.Radius.GetHashCode();
                h = h * 31 + p.Feather.GetHashCode();
            }
            return h;
        }
    }

    public static SKImage? ApplyGpu(SKImage input, DevelopSettings s, float tileX, float tileY, float tileW, float tileH)
    {
        if (input == null || !HasSpots(s) || !Gpu.IsReady)
            return null;
        SKRuntimeEffect? fx = EnsureEffect();
        if (fx == null)
            return null;

        SKImage current = input;
        bool owned = false;
        int w = input.Width;
        int h = input.Height;
        float minSide = Math.Min(w, h);
        int n = Math.Min(s.Spots.Count, MaxSpots);
        for (int i = 0; i < n; i++)
        {
            var patch = s.Spots[i];
            if (!MapToImage(patch, tileX, tileY, tileW, tileH, w, h, minSide,
                    out float dstX, out float dstY, out float srcX, out float srcY, out float radiusPx))
                continue;

            using var imgShader = current.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
            if (imgShader == null)
                break;

            var u = new SKRuntimeEffectUniforms(fx);
            try
            {
                u["u_dst"] = new[] { dstX, dstY };
                u["u_src"] = new[] { srcX, srcY };
                u["u_radius"] = radiusPx;
                u["u_feather"] = Math.Clamp(patch.Feather, 0f, 1f);
                u["u_size"] = new[] { (float)w, (float)h };
            }
            catch (ArgumentException)
            {
                break;
            }

            SKImage? next = DevelopRenderer.RenderOffscreenStage(fx, u, imgShader, w, h);
            if (next == null)
                break;
            if (owned)
                GpuRetain.Retire(current);
            current = next;
            owned = true;
        }

        return owned ? current : null;
    }

    public static void ApplyCpu(
        ref float r, ref float g, ref float b,
        int px, int py, int sw, int sh,
        float[]? linear, byte[]? rgba, bool isLinear,
        List<SpotPatch> spots)
    {
        if (spots == null || spots.Count == 0 || sw < 1 || sh < 1)
            return;
        float minSide = Math.Min(sw, sh);
        int n = Math.Min(spots.Count, MaxSpots);
        for (int i = 0; i < n; i++)
        {
            var p = spots[i];
            float cx = p.DestX * sw;
            float cy = p.DestY * sh;
            float radiusPx = Math.Clamp(p.Radius, SpotPatch.MinRadius, SpotPatch.MaxRadius) * minSide;
            if (radiusPx < 0.5f)
                continue;
            float dx = px + 0.5f - cx;
            float dy = py + 0.5f - cy;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            if (dist > radiusPx)
                continue;
            float feather = Math.Clamp(p.Feather, 0f, 1f);
            float inner = radiusPx * (1f - feather);
            float m = 1f - Smooth(dist, inner, radiusPx);
            if (m < 0.001f)
                continue;

            float sx = px + 0.5f + (p.SrcX - p.DestX) * sw;
            float sy = py + 0.5f + (p.SrcY - p.DestY) * sh;
            SampleBilinear(linear, rgba, isLinear, sw, sh, sx, sy, out float sr, out float sg, out float sb);
            r += (sr - r) * m;
            g += (sg - g) * m;
            b += (sb - b) * m;
        }
    }

    private static bool MapToImage(
        SpotPatch patch, float tileX, float tileY, float tileW, float tileH,
        int w, int h, float minSide,
        out float dstX, out float dstY, out float srcX, out float srcY, out float radiusPx)
    {
        dstX = dstY = srcX = srcY = radiusPx = 0f;
        float tw = Math.Max(tileW, 1e-6f);
        float th = Math.Max(tileH, 1e-6f);
        dstX = (patch.DestX - tileX) / tw;
        dstY = (patch.DestY - tileY) / th;
        srcX = (patch.SrcX - tileX) / tw;
        srcY = (patch.SrcY - tileY) / th;
        float radiusUv = Math.Clamp(patch.Radius, SpotPatch.MinRadius, SpotPatch.MaxRadius);
        radiusPx = radiusUv * Math.Min(w / tw, h / th);
        _ = minSide;
        if (dstX < -0.2f || dstY < -0.2f || dstX > 1.2f || dstY > 1.2f)
            return false;
        return radiusPx >= 0.5f;
    }

    private static void SampleBilinear(
        float[]? linear, byte[]? rgba, bool isLinear, int sw, int sh,
        float x, float y, out float r, out float g, out float b)
    {
        x = Math.Clamp(x, 0f, sw - 1.001f);
        y = Math.Clamp(y, 0f, sh - 1.001f);
        int x0 = (int)x;
        int y0 = (int)y;
        int x1 = Math.Min(x0 + 1, sw - 1);
        int y1 = Math.Min(y0 + 1, sh - 1);
        float fx = x - x0;
        float fy = y - y0;
        Sample(linear, rgba, isLinear, sw, x0, y0, out float r00, out float g00, out float b00);
        Sample(linear, rgba, isLinear, sw, x1, y0, out float r10, out float g10, out float b10);
        Sample(linear, rgba, isLinear, sw, x0, y1, out float r01, out float g01, out float b01);
        Sample(linear, rgba, isLinear, sw, x1, y1, out float r11, out float g11, out float b11);
        float r0 = r00 + (r10 - r00) * fx;
        float g0 = g00 + (g10 - g00) * fx;
        float b0 = b00 + (b10 - b00) * fx;
        float r1 = r01 + (r11 - r01) * fx;
        float g1 = g01 + (g11 - g01) * fx;
        float b1 = b01 + (b11 - b01) * fx;
        r = r0 + (r1 - r0) * fy;
        g = g0 + (g1 - g0) * fy;
        b = b0 + (b1 - b0) * fy;
    }

    private static void Sample(
        float[]? linear, byte[]? rgba, bool isLinear, int sw, int x, int y,
        out float r, out float g, out float b)
    {
        int i = (y * sw + x) * 4;
        if (isLinear && linear != null && i + 2 < linear.Length)
        {
            r = linear[i];
            g = linear[i + 1];
            b = linear[i + 2];
            return;
        }
        if (rgba != null && i + 2 < rgba.Length)
        {
            r = DevelopCpu.ToLin1(rgba[i] / 255f);
            g = DevelopCpu.ToLin1(rgba[i + 1] / 255f);
            b = DevelopCpu.ToLin1(rgba[i + 2] / 255f);
            return;
        }
        r = g = b = 0f;
    }

    private static float Smooth(float x, float a, float b)
    {
        float span = b - a;
        if (MathF.Abs(span) < 1e-5f)
            return x >= b ? 1f : 0f;
        float t = Math.Clamp((x - a) / span, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static SKRuntimeEffect? EnsureEffect()
    {
        if (_effect != null)
            return _effect;
        if (_failed)
            return null;
        const string sksl = @"
            uniform shader u_image;
            uniform float2 u_dst;
            uniform float2 u_src;
            uniform float u_radius;
            uniform float u_feather;
            uniform float2 u_size;

            float smoother(float x, float edge0, float edge1) {
                float span = edge1 - edge0;
                if (abs(span) < 0.0001) {
                    return x >= edge1 ? 1.0 : 0.0;
                }
                float t = clamp((x - edge0) / span, 0.0, 1.0);
                return t * t * (3.0 - 2.0 * t);
            }

            half4 main(float2 p) {
                float2 d = p - u_dst * u_size;
                float dist = length(d);
                float inner = u_radius * (1.0 - clamp(u_feather, 0.0, 1.0));
                float m = 1.0 - smoother(dist, inner, u_radius);
                half4 base = sample(u_image, p);
                if (m < 0.001) {
                    return base;
                }
                float2 srcP = p + (u_src - u_dst) * u_size;
                srcP = clamp(srcP, float2(0.5, 0.5), u_size - float2(0.5, 0.5));
                half4 healed = sample(u_image, srcP);
                return mix(base, healed, m);
            }
        ";
        _effect = SKRuntimeEffect.Create(sksl, out string err);
        if (_effect == null)
        {
            _failed = true;
            Log.Warning("Spot heal SKSL failed: " + err);
        }
        return _effect;
    }
}
