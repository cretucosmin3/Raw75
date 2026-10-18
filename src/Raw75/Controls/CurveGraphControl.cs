using System;
using System.Collections.Generic;
using Blossom.Core.Input;
using Blossom.Hosting;
using Blossom.Primitives;
using Blossom.Reactive;
using Raw75.Develop;
using Raw75.Pipeline;
using SkiaSharp;

namespace Raw75.Controls;

public enum CurveChannel
{
    Rgb = 0,
    Red = 1,
    Green = 2,
    Blue = 3
}

/// <summary>
/// Interactive 4-channel Tone & Color Curves editor on <see cref="Control"/>.
/// - Channel switch: [RGB] [Red] [Green] [Blue] in a recessed well with a sliding raised segment.
/// - Hermite monotone cubic spline curve interpolation with zero overshoot.
/// - Recessed plot with quarter grid, diagonal identity guide, and live histogram underlay.
/// - Control points: add on click, drag with monotonic clamping, remove on right/double click.
/// - Tabular coordinate readout (In / Out) and reset action.
/// </summary>
public sealed class CurveGraphControl : Control
{
    private static readonly string[] ChannelNames = { "RGB", "Red", "Green", "Blue" };
    private const float TabH = 24f;
    private const float TabInset = 2f;
    private const float FooterH = 20f;
    private const float PointHitR = 18f;

    private CurveChannel _activeChannel = CurveChannel.Rgb;
    private CurvePoint[] _curveRgb = CurveMath.DefaultCurve();
    private CurvePoint[] _curveRed = CurveMath.DefaultCurve();
    private CurvePoint[] _curveGreen = CurveMath.DefaultCurve();
    private CurvePoint[] _curveBlue = CurveMath.DefaultCurve();

    private int _hoveredPointIdx = -1;
    private int _draggedPointIdx = -1;
    private bool _dragging;
    private bool _hoveredReset;
    private int _hoveredTabIdx = -1;
    private float _cursorX = -1f;
    private float _cursorY = -1f;
    private bool _hoveringGraph;

    // _litIdx is the highlighted point whose ring is lit.
    private int _litIdx = -1;
    private float _lit;
    private float _plotHover;
    private float _resetHover;
    private float _tabSlide;

    private SKRect _plotRect;
    private SKRect _resetRect;

    private readonly float[] _histR = new float[256];
    private readonly float[] _histG = new float[256];
    private readonly float[] _histB = new float[256];
    private readonly float[] _histY = new float[256];
    private bool _hasHistogram;

    public event Action? CurveChanged;
    public event Action? DragEnded;

    public CurveChannel ActiveChannel
    {
        get => _activeChannel;
        set
        {
            if (_activeChannel == value) return;
            _activeChannel = value;
            _hoveredPointIdx = -1;
            _draggedPointIdx = -1;
            ClearLit();
            _tabSlide = (int)value;
            InvalidatePaint();
            InvalidatePaint();
        }
    }

    public const float DefaultHeight = 290f;

    public CurveGraphControl()
    {
        Name = "CurveGraphControl";
        Transform.Height = DefaultHeight;
        Cursor = UiCursor.Default;
        Style.BackColor = SKColors.Transparent;

        Events.OnMouseDown += OnMouseDown;
        Events.OnMouseMove += OnMouseMove;
        Events.OnMouseUp += OnMouseUp;
        Events.OnMouseDoubleClick += OnMouseDoubleClick;

        this.Bind(_ => { Theme.Observe(); InvalidatePaint(); });
    }

    protected override void OnHoverChanged()
    {
        if (IsHovered) return;
        SetPointHover(-1);
        _hoveredTabIdx = -1;
        SetResetHover(false);
        SetGraphHover(false);
        _cursorX = -1f;
        _cursorY = -1f;
        Cursor = UiCursor.Default;
    }

    public void LoadFromSettings(DevelopSettings s)
    {
        _curveRgb = (CurvePoint[])s.CurveRgb.Clone();
        _curveRed = (CurvePoint[])s.CurveRed.Clone();
        _curveGreen = (CurvePoint[])s.CurveGreen.Clone();
        _curveBlue = (CurvePoint[])s.CurveBlue.Clone();
        ClearLit();
        InvalidatePaint();
    }

    public void SaveToSettings(DevelopSettings s)
    {
        s.CurveRgb = (CurvePoint[])_curveRgb.Clone();
        s.CurveRed = (CurvePoint[])_curveRed.Clone();
        s.CurveGreen = (CurvePoint[])_curveGreen.Clone();
        s.CurveBlue = (CurvePoint[])_curveBlue.Clone();
    }

    public void SetHistogramBins(float[]? r, float[]? g, float[]? b, float[]? y)
    {
        if (r == null || g == null || b == null || y == null)
        {
            _hasHistogram = false;
            InvalidatePaint();
            return;
        }

        Array.Copy(r, _histR, Math.Min(r.Length, 256));
        Array.Copy(g, _histG, Math.Min(g.Length, 256));
        Array.Copy(b, _histB, Math.Min(b.Length, 256));
        Array.Copy(y, _histY, Math.Min(y.Length, 256));
        _hasHistogram = true;
        InvalidatePaint();
    }

    public void ResetActiveChannel()
    {
        SetActivePoints(CurveMath.DefaultCurve());
        _hoveredPointIdx = -1;
        _draggedPointIdx = -1;
        ClearLit();
        CurveChanged?.Invoke();
        DragEnded?.Invoke();
        InvalidatePaint();
    }

    public void ResetAllChannels()
    {
        _curveRgb = CurveMath.DefaultCurve();
        _curveRed = CurveMath.DefaultCurve();
        _curveGreen = CurveMath.DefaultCurve();
        _curveBlue = CurveMath.DefaultCurve();
        _hoveredPointIdx = -1;
        _draggedPointIdx = -1;
        ClearLit();
        CurveChanged?.Invoke();
        DragEnded?.Invoke();
        InvalidatePaint();
    }

    public override SKSize GetPreferredSize(float maxWidth, float maxHeight)
    {
        float w = maxWidth > 0 ? maxWidth : Theme.RightW;
        return new SKSize(w, DefaultHeight);
    }

    private static SKColor ChannelColor(int channel) => channel switch
    {
        1 => Theme.RedChannel,
        2 => Theme.GreenChannel,
        3 => Theme.BlueChannel,
        _ => Theme.Text
    };

    private CurvePoint[] GetActivePoints() => _activeChannel switch
    {
        CurveChannel.Red => _curveRed,
        CurveChannel.Green => _curveGreen,
        CurveChannel.Blue => _curveBlue,
        _ => _curveRgb
    };

    private CurvePoint[] GetPoints(int channel) => channel switch
    {
        1 => _curveRed,
        2 => _curveGreen,
        3 => _curveBlue,
        _ => _curveRgb
    };

    private void SetActivePoints(CurvePoint[] pts)
    {
        switch (_activeChannel)
        {
            case CurveChannel.Red: _curveRed = pts; break;
            case CurveChannel.Green: _curveGreen = pts; break;
            case CurveChannel.Blue: _curveBlue = pts; break;
            default: _curveRgb = pts; break;
        }
    }

    protected override void Paint(SKCanvas canvas)
    {
        float w = W;
        float h = H;
        if (w < 20f || h < 40f) return;

        DrawChannelTabs(canvas, w);

        float footerTop = h - FooterH - 4f;
        _resetRect = new SKRect(w - 22f, footerTop + 1f, w - 2f, footerTop + 19f);
        _plotRect = new SKRect(1f, TabH + 6f, w - 1f, footerTop - 6f);
        DrawPlotArea(canvas, _plotRect);

        DrawFooter(canvas, _resetRect);
    }

    private float TabWidth(float width) => Math.Max(1f, (width - TabInset * 2f) / 4f);

    private int TabAt(float lx) => Math.Clamp((int)((lx - TabInset) / TabWidth(W)), 0, 3);

    private void DrawChannelTabs(SKCanvas canvas, float width)
    {
        float r = Math.Min(Theme.RadiusSm + 1f, TabH * 0.5f);
        var well = new SKRect(0.5f, 0.5f, width - 0.5f, TabH - 0.5f);
        using (var fill = new SKPaint { Color = Theme.Well, IsAntialias = true })
            canvas.DrawRoundRect(well, r, r, fill);
        using (var edge = new SKPaint { Color = Theme.HairlineSubtle, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = true })
            canvas.DrawRoundRect(well, r, r, edge);

        // Raised segment slides to the active channel.
        float segW = TabWidth(width);
        float pos = Math.Clamp(_tabSlide, 0f, 3f);
        var ind = SKRect.Create(TabInset + segW * pos, TabInset, segW, TabH - TabInset * 2f);
        float ir = Math.Max(0f, r - 1.5f);
        if (Theme.UseShadows)
        {
            using var blur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1.4f);
            using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 90), MaskFilter = blur, IsAntialias = true };
            var s = ind;
            s.Offset(0, 1f);
            canvas.DrawRoundRect(s, ir, ir, shadow);
        }
        using (var raised = new SKPaint { Color = Theme.Lighten(Theme.Button, 8), IsAntialias = true })
            canvas.DrawRoundRect(ind, ir, ir, raised);
        using (var rim = new SKPaint
        {
            Color = Theme.OutlineAccent ? Theme.Accent : Theme.HairlineStrong,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            IsAntialias = true
        })
        {
            var rr = ind;
            rr.Inflate(-0.5f, -0.5f);
            canvas.DrawRoundRect(rr, ir, ir, rim);
        }

        using var dot = new SKPaint { IsAntialias = true };
        using var text = new TextPaint { TextSize = 11f, TextAlign = SKTextAlign.Left };
        for (int i = 0; i < 4; i++)
        {
            bool active = (int)_activeChannel == i;
            text.Typeface = Theme.GetTypeface(active ? 600 : 500);
            text.Color = active ? Theme.Text : (_hoveredTabIdx == i ? Theme.TextSecondary : Theme.TextDim);

            const float DotD = 6f;
            const float DotGap = 5f;
            float x0 = TabInset + segW * i;
            float x = x0 + (segW - (DotD + DotGap + text.MeasureText(ChannelNames[i]))) * 0.5f;
            float cy = TabH * 0.5f;

            SKColor col = ChannelColor(i);
            dot.Color = active ? col : col.WithAlpha(150);
            canvas.DrawCircle(x + DotD * 0.5f, cy, DotD * 0.5f, dot);

            var m = text.FontMetrics;
            canvas.DrawText(ChannelNames[i], x + DotD + DotGap, cy - (m.Ascent + m.Descent) * 0.5f, text);
        }
    }

    private void DrawPlotArea(SKCanvas canvas, SKRect plot)
    {
        PlotChrome.DrawWell(canvas, plot, _plotHover);
        PlotChrome.DrawGrid(canvas, plot);

        canvas.Save();
        canvas.ClipRoundRect(new SKRoundRect(plot, Theme.Radius), antialias: true);

        // Diagonal identity reference.
        using (var diag = new SKPaint { Color = Theme.Hairline, StrokeWidth = 1f, IsAntialias = true })
            canvas.DrawLine(plot.Left, plot.Bottom, plot.Right, plot.Top, diag);

        if (_hasHistogram)
            DrawHistogramUnderlay(canvas, plot);

        DrawGhostCurves(canvas, plot);
        DrawActiveCurve(canvas, plot);

        canvas.Restore();

        PlotChrome.DrawFrame(canvas, plot);

        // Handles draw over the frame so edge points stay whole.
        DrawControlPoints(canvas, plot);
    }

    private void DrawHistogramUnderlay(SKCanvas canvas, SKRect plot)
    {
        float[] data = _activeChannel switch
        {
            CurveChannel.Red => _histR,
            CurveChannel.Green => _histG,
            CurveChannel.Blue => _histB,
            _ => _histY
        };

        float peak = 0f;
        for (int i = 0; i < 256; i++)
            peak = Math.Max(peak, data[i]);
        if (peak <= 0.001f) return;

        float logPeak = MathF.Log(1f + peak);
        using var path = new SKPath();
        path.MoveTo(plot.Left, plot.Bottom);
        float stepX = plot.Width / 255f;

        for (int i = 0; i < 256; i++)
        {
            float norm = MathF.Log(1f + data[i]) / logPeak;
            path.LineTo(plot.Left + i * stepX, plot.Bottom - Math.Clamp(norm, 0f, 1f) * plot.Height);
        }
        path.LineTo(plot.Right, plot.Bottom);
        path.Close();

        SKColor col = ChannelColor((int)_activeChannel);
        using var fill = new SKPaint { Color = col.WithAlpha(_activeChannel == CurveChannel.Rgb ? (byte)18 : (byte)30), IsAntialias = true };
        canvas.DrawPath(path, fill);
    }

    private static SKPath BuildCurvePath(CurvePoint[] points, SKRect plot)
    {
        float[] lut = CurveMath.EvaluateSpline(points, (int)Math.Max(10f, plot.Width));
        var path = new SKPath();
        float stepX = plot.Width / (lut.Length - 1);
        path.MoveTo(plot.Left, plot.Bottom - Math.Clamp(lut[0], 0f, 1f) * plot.Height);
        for (int i = 1; i < lut.Length; i++)
            path.LineTo(plot.Left + i * stepX, plot.Bottom - Math.Clamp(lut[i], 0f, 1f) * plot.Height);
        return path;
    }

    private void DrawGhostCurves(SKCanvas canvas, SKRect plot)
    {
        for (int ch = 0; ch < 4; ch++)
        {
            if (ch == (int)_activeChannel) continue;
            CurvePoint[] pts = GetPoints(ch);
            if (CurveMath.IsIdentity(pts)) continue;

            using var path = BuildCurvePath(pts, plot);
            using var ghost = new SKPaint
            {
                Color = ChannelColor(ch).WithAlpha(70),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.2f,
                IsAntialias = true
            };
            canvas.DrawPath(path, ghost);
        }
    }

    private void DrawActiveCurve(SKCanvas canvas, SKRect plot)
    {
        using var path = BuildCurvePath(GetActivePoints(), plot);
        SKColor c = ChannelColor((int)_activeChannel);
        PlotChrome.DrawUnderFill(canvas, path, plot, c, alpha: 26);
        PlotChrome.StrokeCurve(canvas, path, c, glow: _dragging ? 1f : 0.7f);
    }

    private void DrawControlPoints(SKCanvas canvas, SKRect plot)
    {
        CurvePoint[] points = GetActivePoints();
        for (int i = 0; i < points.Length; i++)
        {
            float sx = plot.Left + (points[i].X / 255f) * plot.Width;
            float sy = plot.Bottom - (points[i].Y / 255f) * plot.Height;
            bool active = _dragging && i == _draggedPointIdx;
            PlotChrome.DrawHandle(canvas, sx, sy, i == _litIdx ? _lit : 0f, active);
        }
    }

    private void DrawFooter(SKCanvas canvas, SKRect resetRect)
    {
        bool live = _dragging || _hoveredPointIdx >= 0;
        using var label = new TextPaint { TextSize = 10f, Typeface = Theme.GetTypeface(500), Color = Theme.TextDim };
        using var value = new TextPaint
        {
            TextSize = 11f,
            Typeface = Theme.MonospaceTypeface,
            Color = live ? Theme.Accent : (_cursorX >= 0f ? Theme.TextSecondary : Theme.TextDim)
        };

        var m = value.FontMetrics;
        float baseline = resetRect.MidY - (m.Ascent + m.Descent) * 0.5f;
        string inStr = _cursorX >= 0f ? $"{_cursorX,3:0}" : "  –";
        string outStr = _cursorY >= 0f ? $"{_cursorY,3:0}" : "  –";

        float x = 3f;
        canvas.DrawText("In", x, baseline, label);
        x += label.MeasureText("In") + 5f;
        canvas.DrawText(inStr, x, baseline, value);
        x += value.MeasureText("000") + 14f;
        canvas.DrawText("Out", x, baseline, label);
        x += label.MeasureText("Out") + 5f;
        canvas.DrawText(outStr, x, baseline, value);

        PlotChrome.DrawResetButton(canvas, resetRect, _resetHover);
    }

    private void ClearLit()
    {
        _litIdx = -1;
        _lit = 0f;
        InvalidatePaint();
    }

    private void SetPointHover(int index)
    {
        if (_hoveredPointIdx == index) return;
        _hoveredPointIdx = index;
        if (index >= 0)
        {
            if (index != _litIdx)
            {
                _litIdx = index;
                _lit = 0f;
                InvalidatePaint();
            }
            _lit = 1f;
            InvalidatePaint();
        }
        else if (!_dragging)
        {
            _lit = 0f;
            InvalidatePaint();
        }
        InvalidatePaint();
    }

    private void SetResetHover(bool hover)
    {
        if (_hoveredReset == hover) return;
        _hoveredReset = hover;
        _resetHover = hover ? 1f : 0f;
        InvalidatePaint();
    }

    private void SetGraphHover(bool hover)
    {
        if (_hoveringGraph == hover) return;
        _hoveringGraph = hover;
        _plotHover = hover ? 1f : 0f;
        InvalidatePaint();
    }

    private int HitTestPoint(CurvePoint[] points, float screenX, float screenY)
    {
        const float thresholdSq = PointHitR * PointHitR;
        int bestIdx = -1;
        float bestDistSq = thresholdSq;
        for (int i = 0; i < points.Length; i++)
        {
            float sx = _plotRect.Left + (points[i].X / 255f) * _plotRect.Width;
            float sy = _plotRect.Bottom - (points[i].Y / 255f) * _plotRect.Height;
            float dx = screenX - sx;
            float dy = screenY - sy;
            float distSq = dx * dx + dy * dy;
            if (distSq <= bestDistSq)
            {
                bestDistSq = distSq;
                bestIdx = i;
            }
        }
        return bestIdx;
    }

    private void ScreenToCurve(float screenX, float screenY, out float cx, out float cy)
    {
        float nx = (screenX - _plotRect.Left) / Math.Max(1f, _plotRect.Width);
        float ny = (_plotRect.Bottom - screenY) / Math.Max(1f, _plotRect.Height);
        cx = Math.Clamp(nx * 255f, 0f, 255f);
        cy = Math.Clamp(ny * 255f, 0f, 255f);
    }

    private int InsertPoint(float cx, float cy)
    {
        var points = new List<CurvePoint>(GetActivePoints())
        {
            new CurvePoint(cx, cy)
        };
        points.Sort((a, b) => a.X.CompareTo(b.X));
        var arr = points.ToArray();
        SetActivePoints(arr);
        for (int i = 0; i < arr.Length; i++)
        {
            if (MathF.Abs(arr[i].X - cx) < 0.01f && MathF.Abs(arr[i].Y - cy) < 0.01f)
                return i;
        }
        return 0;
    }

    private void RemovePoint(int index)
    {
        var points = new List<CurvePoint>(GetActivePoints());
        if (index > 0 && index < points.Count - 1)
        {
            points.RemoveAt(index);
            SetActivePoints(points.ToArray());
            _hoveredPointIdx = -1;
            _draggedPointIdx = -1;
            ClearLit();
            CurveChanged?.Invoke();
            DragEnded?.Invoke();
            InvalidatePaint();
        }
    }

    private void BeginPointDrag(int index)
    {
        _draggedPointIdx = index;
        _dragging = true;
        _litIdx = index;
        _lit = 1f;
        InvalidatePaint();
        CapturePointer();
        Cursor = UiCursor.Hand;
    }

    private void OnMouseDown(object sender, MouseEventArgs args)
    {
        var local = PointToClient(args.Global.X, args.Global.Y);
        float lx = local.X;
        float ly = local.Y;

        // 1. Channel tabs
        if (ly >= 0f && ly <= TabH + 2f)
        {
            if (args.Button == PointerButton.Left)
            {
                ActiveChannel = (CurveChannel)TabAt(lx);
                args.Handled = true;
                return;
            }
        }

        // 2. Reset button
        if (_resetRect.Contains(lx, ly))
        {
            if (args.Button == PointerButton.Left)
            {
                ResetActiveChannel();
                args.Handled = true;
                return;
            }
        }

        // 3. Graph area & control points (generous grab target)
        var points = GetActivePoints();
        int hit = HitTestPoint(points, lx, ly);

        if (hit >= 0)
        {
            if (args.Button == PointerButton.Right)
            {
                if (hit > 0 && hit < points.Length - 1)
                {
                    RemovePoint(hit);
                    args.Handled = true;
                    return;
                }
            }
            else if (args.Button == PointerButton.Left)
            {
                BeginPointDrag(hit);
                InvalidatePaint();
                args.Handled = true;
                return;
            }
        }
        else if (_plotRect.Contains(lx, ly) && args.Button == PointerButton.Left)
        {
            ScreenToCurve(lx, ly, out float cx, out float cy);
            BeginPointDrag(InsertPoint(cx, cy));
            CurveChanged?.Invoke();
            InvalidatePaint();
            args.Handled = true;
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs args)
    {
        var local = PointToClient(args.Global.X, args.Global.Y);
        float lx = local.X;
        float ly = local.Y;

        SetGraphHover(_plotRect.Contains(lx, ly));
        SetResetHover(_resetRect.Contains(lx, ly));
        _hoveredTabIdx = ly >= 0f && ly <= TabH + 2f ? TabAt(lx) : -1;

        if (_dragging && _draggedPointIdx >= 0 && HasPointerCapture)
        {
            ScreenToCurve(lx, ly, out float cx, out float cy);
            var points = GetActivePoints();
            int idx = _draggedPointIdx;

            if (idx == 0)
            {
                points[0] = new CurvePoint(0f, cy);
            }
            else if (idx == points.Length - 1)
            {
                points[^1] = new CurvePoint(255f, cy);
            }
            else
            {
                float minX = points[idx - 1].X + 1f;
                float maxX = points[idx + 1].X - 1f;
                cx = Math.Clamp(cx, minX, maxX);
                points[idx] = new CurvePoint(cx, cy);
            }

            _cursorX = points[idx].X;
            _cursorY = points[idx].Y;

            Cursor = UiCursor.Hand;
            CurveChanged?.Invoke();
            InvalidatePaint();
            args.Handled = true;
            return;
        }

        var pts = GetActivePoints();
        SetPointHover(HitTestPoint(pts, lx, ly));

        if (_hoveredPointIdx >= 0)
        {
            _cursorX = pts[_hoveredPointIdx].X;
            _cursorY = pts[_hoveredPointIdx].Y;
            Cursor = UiCursor.Hand;
        }
        else if (_hoveringGraph)
        {
            ScreenToCurve(lx, ly, out float cx, out float cy);
            _cursorX = cx;
            _cursorY = cy;
            Cursor = UiCursor.Crosshair;
        }
        else
        {
            _cursorX = -1f;
            _cursorY = -1f;
            Cursor = (_hoveredReset || _hoveredTabIdx >= 0) ? UiCursor.Hand : UiCursor.Default;
        }

        InvalidatePaint();
    }

    private void OnMouseUp(object sender, MouseEventArgs args)
    {
        if (_dragging)
        {
            _dragging = false;
            _draggedPointIdx = -1;
            if (HasPointerCapture)
                ReleasePointer();
            if (_hoveredPointIdx < 0)
                _lit = 0f;
                InvalidatePaint();
            DragEnded?.Invoke();
            Cursor = _hoveredPointIdx >= 0 ? UiCursor.Hand : (_hoveringGraph ? UiCursor.Crosshair : UiCursor.Default);
            InvalidatePaint();
            args.Handled = true;
        }
    }

    private void OnMouseDoubleClick(object sender, MouseEventArgs args)
    {
        var local = PointToClient(args.Global.X, args.Global.Y);
        float lx = local.X;
        float ly = local.Y;

        var points = GetActivePoints();
        int hit = HitTestPoint(points, lx, ly);
        if (hit > 0 && hit < points.Length - 1)
        {
            RemovePoint(hit);
            args.Handled = true;
        }
        else if (_plotRect.Contains(lx, ly) && hit < 0)
        {
            ResetActiveChannel();
            args.Handled = true;
        }
    }
}
