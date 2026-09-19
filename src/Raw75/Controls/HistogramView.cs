using System;
using System.Collections.Generic;
using Blossom.Core;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using SkiaSharp;

namespace Raw75.Controls;

/// <summary>RGB + luma histogram chrome. Bins are supplied; this type does not sample pixels.</summary>
public class HistogramView : VisualElement
{
    private const int BinCount = 256;
    private readonly float[] _r = new float[BinCount];
    private readonly float[] _g = new float[BinCount];
    private readonly float[] _b = new float[BinCount];
    private readonly float[] _y = new float[BinCount];
    private bool _hasData;

    public HistogramView()
    {
        Name = "HistogramView";
        Transform.Height = 72;
        Overflow = OverflowMode.Clip;
        Style = new ElementStyle
        {
            BackColor = Theme.Section,
            Border = new BorderStyle
            {
                Width = 1,
                Color = Theme.Hairline,
                Roundness = Theme.Radius
            },
            Shadow = new ShadowStyle(0, 2.5f, 3, 3, new SKColor(0, 0, 0, 75))
        };
    }

    public void RefreshTheme()
    {
        Style.BackColor = Theme.Section;
        if (Style.Border != null)
        {
            Style.Border.Color = Theme.Hairline;
            Style.Border.Roundness = Theme.Radius;
        }
        Theme.ApplyCardShadow(Style);
        InvalidatePaint();
    }

    public void SetBins(float[] r, float[] g, float[] b, float[] y)
    {
        _hasData = Copy(r, _r) | Copy(g, _g) | Copy(b, _b) | Copy(y, _y);
        InvalidatePaint();
    }

    public override SKSize GetPreferredSize(float maxWidth, float maxHeight)
    {
        float w = maxWidth > 0 ? maxWidth : (Transform.Width > 0 ? Transform.Width : Theme.RightW);
        float h = 72f;
        if (maxHeight > 0) h = Math.Min(h, maxHeight);
        return new SKSize(w, h);
    }

    protected override void OnAfterStyleDraw(List<DrawCommand> cmds)
    {
        cmds.Add(new DrawCallbackCommand(DrawBins));
    }

    private void DrawBins(SKCanvas canvas)
    {
        float w = Transform.Computed.Width;
        float h = Transform.Computed.Height;
        if (w <= 2 || h <= 2) return;

        var plot = new SKRect(4, 4, w - 4, h - 4);
        using (var bgPaint = new SKPaint { Color = Theme.Well, Style = SKPaintStyle.Fill, IsAntialias = true })
        {
            canvas.DrawRoundRect(plot, Theme.RadiusSm, Theme.RadiusSm, bgPaint);
        }

        using (var gridPaint = new SKPaint { Color = Theme.HairlineSubtle, StrokeWidth = 1f, IsAntialias = true })
        {
            canvas.DrawLine(plot.Left + plot.Width * 0.25f, plot.Top, plot.Left + plot.Width * 0.25f, plot.Bottom, gridPaint);
            canvas.DrawLine(plot.Left + plot.Width * 0.50f, plot.Top, plot.Left + plot.Width * 0.50f, plot.Bottom, gridPaint);
            canvas.DrawLine(plot.Left + plot.Width * 0.75f, plot.Top, plot.Left + plot.Width * 0.75f, plot.Bottom, gridPaint);
        }

        if (!_hasData)
        {
            using var borderPaint = new SKPaint { Color = Theme.HairlineSubtle, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = true };
            canvas.DrawRoundRect(plot, Theme.RadiusSm, Theme.RadiusSm, borderPaint);
            return;
        }

        float peak = 0f;
        for (int i = 0; i < BinCount; i++)
        {
            peak = Math.Max(peak, _r[i]);
            peak = Math.Max(peak, _g[i]);
            peak = Math.Max(peak, _b[i]);
            peak = Math.Max(peak, _y[i]);
        }
        if (peak <= 0f) return;
        float logPeak = MathF.Log(1f + peak);

        using var rPaint = ChannelPaint(new SKColor(220, 50, 40, 160), SKBlendMode.Plus);
        using var gPaint = ChannelPaint(new SKColor(40, 200, 70, 160), SKBlendMode.Plus);
        using var bPaint = ChannelPaint(new SKColor(50, 110, 230, 160), SKBlendMode.Plus);
        using var yPaint = ChannelPaint(new SKColor(230, 230, 230, 90), SKBlendMode.SrcOver);
        using var yStroke = new SKPaint
        {
            Color = new SKColor(240, 240, 240, 180),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
            StrokeJoin = SKStrokeJoin.Round
        };

        using (var path = BuildFill(_r, plot, logPeak))
            canvas.DrawPath(path, rPaint);
        using (var path = BuildFill(_g, plot, logPeak))
            canvas.DrawPath(path, gPaint);
        using (var path = BuildFill(_b, plot, logPeak))
            canvas.DrawPath(path, bPaint);
        using (var path = BuildFill(_y, plot, logPeak))
            canvas.DrawPath(path, yPaint);
        using (var path = BuildStroke(_y, plot, logPeak))
            canvas.DrawPath(path, yStroke);

        float shadowClip = _y[0] + _r[0] + _g[0] + _b[0];
        float hiClip = _y[255] + _r[255] + _g[255] + _b[255];
        if (shadowClip > peak * 0.02f)
        {
            using var mark = new SKPaint { Color = new SKColor(80, 140, 255, 220), StrokeWidth = 2, IsAntialias = true };
            canvas.DrawLine(plot.Left + 2, plot.Top + 4, plot.Left + 2, plot.Bottom - 4, mark);
        }
        if (hiClip > peak * 0.02f)
        {
            using var mark = new SKPaint { Color = new SKColor(255, 70, 60, 220), StrokeWidth = 2, IsAntialias = true };
            canvas.DrawLine(plot.Right - 2, plot.Top + 4, plot.Right - 2, plot.Bottom - 4, mark);
        }
    }

    private static SKPaint ChannelPaint(SKColor color, SKBlendMode blend) => new()
    {
        Color = color,
        IsAntialias = true,
        Style = SKPaintStyle.Fill,
        BlendMode = blend
    };

    private static SKPath BuildFill(float[] bins, SKRect plot, float peak)
    {
        var path = new SKPath();
        float bottom = plot.Bottom;
        float left = plot.Left;
        float span = plot.Width;
        float height = plot.Height;
        path.MoveTo(left, bottom);
        for (int i = 0; i < BinCount; i++)
        {
            float x = left + span * (i / (float)(BinCount - 1));
            float y = bottom - height * (peak > 1e-6f ? MathF.Log(1f + bins[i]) / peak : 0f);
            path.LineTo(x, y);
        }
        path.LineTo(plot.Right, bottom);
        path.Close();
        return path;
    }

    private static SKPath BuildStroke(float[] bins, SKRect plot, float peak)
    {
        var path = new SKPath();
        float bottom = plot.Bottom;
        float left = plot.Left;
        float span = plot.Width;
        float height = plot.Height;
        for (int i = 0; i < BinCount; i++)
        {
            float x = left + span * (i / (float)(BinCount - 1));
            float y = bottom - height * (peak > 1e-6f ? MathF.Log(1f + bins[i]) / peak : 0f);
            if (i == 0) path.MoveTo(x, y);
            else path.LineTo(x, y);
        }
        return path;
    }

    private static bool Copy(float[]? src, float[] dest)
    {
        Array.Clear(dest);
        if (src == null || src.Length == 0) return false;
        int n = Math.Min(BinCount, src.Length);
        Array.Copy(src, dest, n);
        return true;
    }
}
