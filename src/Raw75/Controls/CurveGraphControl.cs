using System;
using System.Collections.Generic;
using Blossom.Core;
using Blossom.Core.Input;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Raw75.Develop;
using Raw75.Pipeline;
using Silk.NET.Input;
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
/// Capture One Pro-style interactive 4-channel Tone & Color Curves editor.
/// Features:
/// - 4 tabs: [RGB] [Red] [Green] [Blue] with channel-colored dots and accents.
/// - Hermite monotone cubic spline curve interpolation with zero overshoot.
/// - Responsive grid with 4x4 divisional guides, diagonal identity guide, and live histogram underlay.
/// - Interactive control points: add on click, drag with monotonic clamping, remove on right/double click.
/// - Tabular coordinate readout (In: X  Out: Y) and reset action.
/// </summary>
public sealed class CurveGraphControl : VisualElement
{
    private static readonly string[] ChannelNames = { "RGB", "Red", "Green", "Blue" };
    private static readonly SKColor[] ChannelColors =
    {
        new(245, 245, 250), // RGB (Clean White/Luma)
        new(239, 68, 68),   // Red
        new(34, 197, 94),   // Green
        new(59, 130, 246)   // Blue
    };

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
            InvalidatePaint();
        }
    }

    public const float DefaultHeight = 290f;

    public CurveGraphControl()
    {
        Name = "CurveGraphControl";
        Transform.Height = DefaultHeight;
        Cursor = StandardCursor.Default;
        Style = new ElementStyle
        {
            BackColor = SKColors.Transparent,
            Border = new BorderStyle { Width = 0 }
        };

        Events.OnMouseDown += OnMouseDown;
        Events.OnMouseMove += OnMouseMove;
        Events.OnMouseUp += OnMouseUp;
        Events.OnMouseDoubleClick += OnMouseDoubleClick;
        Events.OnMouseLeave += _ =>
        {
            _hoveredPointIdx = -1;
            _hoveredTabIdx = -1;
            _hoveredReset = false;
            _hoveringGraph = false;
            _cursorX = -1f;
            _cursorY = -1f;
            Cursor = StandardCursor.Default;
            InvalidatePaint();
        };
    }

    public void LoadFromSettings(DevelopSettings s)
    {
        _curveRgb = (CurvePoint[])s.CurveRgb.Clone();
        _curveRed = (CurvePoint[])s.CurveRed.Clone();
        _curveGreen = (CurvePoint[])s.CurveGreen.Clone();
        _curveBlue = (CurvePoint[])s.CurveBlue.Clone();
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
        CurveChanged?.Invoke();
        DragEnded?.Invoke();
        InvalidatePaint();
    }

    public override SKSize GetPreferredSize(float maxWidth, float maxHeight)
    {
        float w = maxWidth > 0 ? maxWidth : Theme.RightW;
        return new SKSize(w, DefaultHeight);
    }

    protected override void OnAfterStyleDraw(List<DrawCommand> cmds)
    {
        cmds.Add(new DrawCallbackCommand(DrawGraph));
    }

    private CurvePoint[] GetActivePoints() => _activeChannel switch
    {
        CurveChannel.Red => _curveRed,
        CurveChannel.Green => _curveGreen,
        CurveChannel.Blue => _curveBlue,
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

    private void DrawGraph(SKCanvas canvas)
    {
        float w = Transform.Computed.Width;
        float h = Transform.Computed.Height;
        if (w < 20f || h < 40f) return;

        // Top Channel Tabs strip (0..24px)
        DrawChannelTabs(canvas, w);

        // Footer readout & reset button at bottom
        float footerH = 20f;
        float footerTop = h - footerH - 4f;
        _resetRect = new SKRect(w - 24f, footerTop, w - 4f, footerTop + 18f);

        // Curve Graph Rectangle (30 .. footerTop - 6)
        _plotRect = new SKRect(4f, 30f, w - 4f, footerTop - 6f);
        DrawPlotArea(canvas, _plotRect);

        DrawFooter(canvas, w, _resetRect);
    }

    private void DrawChannelTabs(SKCanvas canvas, float width)
    {
        float tabW = (width - 9f) / 4f;
        float tabH = 24f;

        using var bgPaint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
        using var borderPaint = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = true };
        using var dotPaint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
        using var textPaint = new SKPaint
        {
            IsAntialias = true,
            TextSize = 11f,
            Typeface = Theme.GetTypeface(500),
            TextAlign = SKTextAlign.Center
        };

        for (int i = 0; i < 4; i++)
        {
            float x = i * (tabW + 3f);
            var rect = new SKRect(x, 0f, x + tabW, tabH);
            bool isActive = (int)_activeChannel == i;
            bool isHover = _hoveredTabIdx == i;

            bgPaint.Color = isActive ? Theme.SectionHeaderHover : (isHover ? Theme.WellHover : Theme.Well);
            borderPaint.Color = isActive ? Theme.HairlineStrong : Theme.Hairline;
            canvas.DrawRoundRect(rect, Theme.RadiusSm, Theme.RadiusSm, bgPaint);
            canvas.DrawRoundRect(rect, Theme.RadiusSm, Theme.RadiusSm, borderPaint);

            // Channel Indicator Dot
            SKColor col = ChannelColors[i];
            dotPaint.Color = col;
            float dotX = rect.Left + 10f;
            float dotY = rect.MidY;
            canvas.DrawCircle(dotX, dotY, 3.5f, dotPaint);

            // Channel Label Text
            textPaint.Color = isActive ? Theme.Text : Theme.TextDim;
            float textX = rect.Left + (tabW + 8f) * 0.5f;
            float textY = rect.MidY + 3.8f;
            canvas.DrawText(ChannelNames[i], textX, textY, textPaint);
        }
    }

    private void DrawPlotArea(SKCanvas canvas, SKRect plot)
    {
        // 1. Inset Well Background
        using (var bgPaint = new SKPaint { Color = Theme.Well, Style = SKPaintStyle.Fill, IsAntialias = true })
        {
            canvas.DrawRoundRect(plot, Theme.RadiusSm, Theme.RadiusSm, bgPaint);
        }

        // 2. 4x4 Inner Grid
        using (var gridPaint = new SKPaint { Color = Theme.HairlineSubtle, StrokeWidth = 1f, IsAntialias = true })
        {
            for (int i = 1; i <= 3; i++)
            {
                float frac = i * 0.25f;
                float gx = plot.Left + plot.Width * frac;
                float gy = plot.Top + plot.Height * frac;
                canvas.DrawLine(gx, plot.Top, gx, plot.Bottom, gridPaint);
                canvas.DrawLine(plot.Left, gy, plot.Right, gy, gridPaint);
            }
        }

        // 3. Diagonal Identity Reference Line
        using (var diagPaint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 22),
            StrokeWidth = 1f,
            IsAntialias = true
        })
        {
            canvas.DrawLine(plot.Left, plot.Bottom, plot.Right, plot.Top, diagPaint);
        }

        // 4. Histogram Underlay
        if (_hasHistogram)
        {
            DrawHistogramUnderlay(canvas, plot);
        }

        // 5. Inactive Channels (faint ghost curves)
        DrawGhostCurves(canvas, plot);

        // 6. Active Channel Curve
        DrawActiveCurve(canvas, plot);

        // 7. Control Points on Active Curve
        DrawControlPoints(canvas, plot);

        // 8. Outer Border Stroke
        using (var border = new SKPaint
        {
            Color = _hoveringGraph ? Theme.HairlineStrong : Theme.Hairline,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            IsAntialias = true
        })
        {
            canvas.DrawRoundRect(plot, Theme.RadiusSm, Theme.RadiusSm, border);
        }
    }

    private void DrawHistogramUnderlay(SKCanvas canvas, SKRect plot)
    {
        float[]? data = _activeChannel switch
        {
            CurveChannel.Red => _histR,
            CurveChannel.Green => _histG,
            CurveChannel.Blue => _histB,
            _ => _histY
        };

        if (data == null || data.Length == 0) return;

        float peak = 0f;
        for (int i = 0; i < 256; i++)
            peak = Math.Max(peak, data[i]);
        if (peak <= 0.001f) return;

        float logPeak = MathF.Log(1f + peak);
        SKColor baseCol = ChannelColors[(int)_activeChannel];
        SKColor fillCol = new(baseCol.Red, baseCol.Green, baseCol.Blue, 38); // ~15% soft alpha underlay

        using var path = new SKPath();
        path.MoveTo(plot.Left, plot.Bottom);
        float stepX = plot.Width / 255f;

        for (int i = 0; i < 256; i++)
        {
            float norm = MathF.Log(1f + data[i]) / logPeak;
            float x = plot.Left + i * stepX;
            float y = plot.Bottom - Math.Clamp(norm, 0f, 1f) * plot.Height;
            path.LineTo(x, y);
        }
        path.LineTo(plot.Right, plot.Bottom);
        path.Close();

        using var fillPaint = new SKPaint { Color = fillCol, Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawPath(path, fillPaint);
    }

    private void DrawGhostCurves(SKCanvas canvas, SKRect plot)
    {
        for (int ch = 0; ch < 4; ch++)
        {
            if (ch == (int)_activeChannel) continue;

            CurvePoint[] pts = ch switch
            {
                1 => _curveRed,
                2 => _curveGreen,
                3 => _curveBlue,
                _ => _curveRgb
            };

            if (CurveMath.IsIdentity(pts)) continue;

            float[] lut = CurveMath.EvaluateSpline(pts, (int)Math.Max(10f, plot.Width));
            using var path = new SKPath();
            float stepX = plot.Width / (lut.Length - 1);
            path.MoveTo(plot.Left, plot.Bottom - lut[0] * plot.Height);

            for (int i = 1; i < lut.Length; i++)
            {
                float x = plot.Left + i * stepX;
                float y = plot.Bottom - Math.Clamp(lut[i], 0f, 1f) * plot.Height;
                path.LineTo(x, y);
            }

            SKColor c = ChannelColors[ch];
            using var ghostPaint = new SKPaint
            {
                Color = new SKColor(c.Red, c.Green, c.Blue, 70), // subtle faint ghost stroke
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.2f,
                IsAntialias = true
            };
            canvas.DrawPath(path, ghostPaint);
        }
    }

    private void DrawActiveCurve(SKCanvas canvas, SKRect plot)
    {
        CurvePoint[] activePoints = GetActivePoints();
        float[] lut = CurveMath.EvaluateSpline(activePoints, (int)Math.Max(10f, plot.Width));
        using var path = new SKPath();
        float stepX = plot.Width / (lut.Length - 1);
        path.MoveTo(plot.Left, plot.Bottom - lut[0] * plot.Height);

        for (int i = 1; i < lut.Length; i++)
        {
            float x = plot.Left + i * stepX;
            float y = plot.Bottom - Math.Clamp(lut[i], 0f, 1f) * plot.Height;
            path.LineTo(x, y);
        }

        SKColor c = ChannelColors[(int)_activeChannel];
        using var curvePaint = new SKPaint
        {
            Color = c,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2.0f,
            IsAntialias = true
        };
        canvas.DrawPath(path, curvePaint);
    }

    private void DrawControlPoints(SKCanvas canvas, SKRect plot)
    {
        CurvePoint[] points = GetActivePoints();
        SKColor activeCol = ChannelColors[(int)_activeChannel];

        using var fillPaint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
        using var ringPaint = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true };
        using var shadowPaint = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = new SKColor(0, 0, 0, 110), IsAntialias = true };
        using var glowPaint = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f, IsAntialias = true };
        using var pipPaint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };

        for (int i = 0; i < points.Length; i++)
        {
            CurvePoint pt = points[i];
            float sx = plot.Left + (pt.X / 255f) * plot.Width;
            float sy = plot.Bottom - (pt.Y / 255f) * plot.Height;

            bool isHighlighted = (i == _hoveredPointIdx) || (i == _draggedPointIdx && _dragging);
            float radius = isHighlighted ? 9.5f : 7.0f;

            if (isHighlighted)
            {
                // Outer warm amber glow halo
                glowPaint.Color = Theme.Accent;
                canvas.DrawCircle(sx, sy, radius + 4.0f, glowPaint);
            }

            // Subtle dark shadow outline for contrast against any background
            canvas.DrawCircle(sx, sy, radius + 1f, shadowPaint);

            // Disc fill
            fillPaint.Color = isHighlighted ? SKColors.White : activeCol;
            canvas.DrawCircle(sx, sy, radius, fillPaint);

            // Ring border
            ringPaint.Color = isHighlighted ? Theme.Accent : SKColors.White;
            ringPaint.StrokeWidth = isHighlighted ? 2.5f : 2.0f;
            canvas.DrawCircle(sx, sy, radius, ringPaint);

            // Precision center pip when hovered or dragged
            if (isHighlighted)
            {
                pipPaint.Color = Theme.Accent;
                canvas.DrawCircle(sx, sy, 2.5f, pipPaint);
            }
        }
    }

    private void DrawFooter(SKCanvas canvas, float width, SKRect resetRect)
    {
        // Coordinate Readout (In: X  Out: Y)
        using var textPaint = new SKPaint
        {
            IsAntialias = true,
            TextSize = 11f,
            Typeface = Theme.GetTypeface(500),
            Color = (_dragging || _hoveredPointIdx >= 0) ? Theme.Accent : Theme.TextDim
        };

        string inStr = _cursorX >= 0f ? $"{_cursorX:0}" : "---";
        string outStr = _cursorY >= 0f ? $"{_cursorY:0}" : "---";
        string readout = $"In: {inStr,-3}   Out: {outStr,-3}";
        canvas.DrawText(readout, 6f, resetRect.MidY + 4f, textPaint);

        // Reset Button ↺
        using var resetBg = new SKPaint
        {
            Color = _hoveredReset ? Theme.WellHover : Theme.Well,
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        using var resetBorder = new SKPaint
        {
            Color = _hoveredReset ? Theme.Accent : Theme.Hairline,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            IsAntialias = true
        };
        canvas.DrawRoundRect(resetRect, Theme.RadiusSm, Theme.RadiusSm, resetBg);
        canvas.DrawRoundRect(resetRect, Theme.RadiusSm, Theme.RadiusSm, resetBorder);

        using var iconPaint = new SKPaint
        {
            IsAntialias = true,
            TextSize = 12f,
            Typeface = Theme.GetTypeface(600),
            Color = _hoveredReset ? Theme.Accent : Theme.TextDim,
            TextAlign = SKTextAlign.Center
        };
        canvas.DrawText("↺", resetRect.MidX, resetRect.MidY + 4.5f, iconPaint);
    }

    private int HitTestPoint(CurvePoint[] points, float screenX, float screenY)
    {
        const float hitRadius = 18f; // 18px radius (36px diameter hit target)
        const float thresholdSq = hitRadius * hitRadius;
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
            CurveChanged?.Invoke();
            DragEnded?.Invoke();
            InvalidatePaint();
        }
    }

    private void OnMouseDown(object sender, MouseEventArgs args)
    {
        var local = PointToClient(args.Global.X, args.Global.Y);
        float lx = local.X;
        float ly = local.Y;

        // 1. Channel Tabs
        if (ly >= 0f && ly <= 26f)
        {
            if (args.Button == (int)MouseButton.Left)
            {
                float tabW = (Transform.Computed.Width - 9f) / 4f;
                int tabIdx = Math.Clamp((int)(lx / (tabW + 3f)), 0, 3);
                ActiveChannel = (CurveChannel)tabIdx;
                args.Handled = true;
                return;
            }
        }

        // 2. Reset Button
        if (_resetRect.Contains(lx, ly))
        {
            if (args.Button == (int)MouseButton.Left)
            {
                ResetActiveChannel();
                args.Handled = true;
                return;
            }
        }

        // 3. Graph Area & Control Points (generous grab target)
        var points = GetActivePoints();
        int hit = HitTestPoint(points, lx, ly);

        if (hit >= 0)
        {
            if (args.Button == (int)MouseButton.Right)
            {
                if (hit > 0 && hit < points.Length - 1)
                {
                    RemovePoint(hit);
                    args.Handled = true;
                    return;
                }
            }
            else if (args.Button == (int)MouseButton.Left)
            {
                _draggedPointIdx = hit;
                _dragging = true;
                CapturePointer();
                Cursor = StandardCursor.Hand;
                InvalidatePaint();
                args.Handled = true;
                return;
            }
        }
        else if (_plotRect.Contains(lx, ly) && args.Button == (int)MouseButton.Left)
        {
            ScreenToCurve(lx, ly, out float cx, out float cy);
            int newIdx = InsertPoint(cx, cy);
            _draggedPointIdx = newIdx;
            _dragging = true;
            CapturePointer();
            Cursor = StandardCursor.Hand;
            CurveChanged?.Invoke();
            InvalidatePaint();
            args.Handled = true;
            return;
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs args)
    {
        var local = PointToClient(args.Global.X, args.Global.Y);
        float lx = local.X;
        float ly = local.Y;

        _hoveringGraph = _plotRect.Contains(lx, ly);
        _hoveredReset = _resetRect.Contains(lx, ly);

        if (ly >= 0f && ly <= 26f)
        {
            float tabW = (Transform.Computed.Width - 9f) / 4f;
            _hoveredTabIdx = Math.Clamp((int)(lx / (tabW + 3f)), 0, 3);
        }
        else
        {
            _hoveredTabIdx = -1;
        }

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

            Cursor = StandardCursor.Hand;
            CurveChanged?.Invoke();
            InvalidatePaint();
            args.Handled = true;
            return;
        }

        var pts = GetActivePoints();
        int hovered = HitTestPoint(pts, lx, ly);
        if (hovered != _hoveredPointIdx)
        {
            _hoveredPointIdx = hovered;
            InvalidatePaint();
        }

        if (_hoveredPointIdx >= 0)
        {
            _cursorX = pts[_hoveredPointIdx].X;
            _cursorY = pts[_hoveredPointIdx].Y;
            Cursor = StandardCursor.Hand;
        }
        else if (_hoveringGraph)
        {
            ScreenToCurve(lx, ly, out float cx, out float cy);
            _cursorX = cx;
            _cursorY = cy;
            Cursor = StandardCursor.Crosshair;
        }
        else
        {
            _cursorX = -1f;
            _cursorY = -1f;
            Cursor = (_hoveredReset || _hoveredTabIdx >= 0) ? StandardCursor.Hand : StandardCursor.Default;
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
            DragEnded?.Invoke();
            Cursor = _hoveredPointIdx >= 0 ? StandardCursor.Hand : (_hoveringGraph ? StandardCursor.Crosshair : StandardCursor.Default);
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
