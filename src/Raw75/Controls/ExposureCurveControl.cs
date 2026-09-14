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

/// <summary>
/// Pro-grade interactive Exposure Curve editor.
/// Features:
/// - Freeform monotone cubic Hermite spline with per-point Curvature / Tension control.
/// - Controls scene-referred optical exposure gain from -5.0 EV (bottom) to +5.0 EV (top).
/// - Neutral 0.0 EV identity centerline with dual-tint gradient fill (amber positive, slate negative).
/// - Per-point Curvature slider: smoothly transitions individual anchor points between straight linear corners and curved splines.
/// - Interactive control points: add on click, drag with monotonic X clamping, remove on right/double click.
/// - Live histogram underlay and tabular point coordinate readouts.
/// </summary>
public sealed class ExposureCurveControl : VisualElement
{
    private List<ExposureCurvePoint> _points = new(CurveMath.DefaultExposureCurve());
    private int _selectedPointIdx = 0;
    private int _hoveredPointIdx = -1;
    private int _draggedPointIdx = -1;
    private bool _dragging;
    private bool _draggingCurvature;
    private bool _hoveredReset;
    private bool _hoveredSlider;
    private bool _hoveringGraph;

    private SKRect _plotRect;
    private SKRect _resetRect;
    private SKRect _sliderTrackRect;

    private readonly float[] _histY = new float[256];
    private bool _hasHistogram;

    public event Action? CurveChanged;
    public event Action? DragEnded;

    public ExposureCurveControl()
    {
        Name = "ExposureCurveControl";
        Cursor = StandardCursor.Default;
        Transform.Height = 186f;

        Events.OnMouseDown += OnMouseDown;
        Events.OnMouseMove += OnMouseMove;
        Events.OnMouseUp += OnMouseUp;
        Events.OnMouseLeave += _ =>
        {
            _hoveredPointIdx = -1;
            _hoveredReset = false;
            _hoveredSlider = false;
            _hoveringGraph = false;
            InvalidatePaint();
        };
    }

    public void LoadFromSettings(DevelopSettings s)
    {
        if (s.ExposureCurve != null && s.ExposureCurve.Length >= 2)
            _points = new List<ExposureCurvePoint>(s.ExposureCurve);
        else
            _points = new List<ExposureCurvePoint>(CurveMath.DefaultExposureCurve());

        _selectedPointIdx = Math.Clamp(_selectedPointIdx, 0, _points.Count - 1);
        InvalidatePaint();
    }

    public void SaveToSettings(DevelopSettings s)
    {
        s.ExposureCurve = _points.ToArray();
    }

    public void SetHistogramBins(float[]? r, float[]? g, float[]? b, float[]? y)
    {
        if (y != null && y.Length == 256)
        {
            Array.Copy(y, _histY, 256);
            _hasHistogram = true;
        }
        else if (r != null && g != null && b != null && r.Length == 256)
        {
            for (int i = 0; i < 256; i++)
                _histY[i] = 0.2627f * r[i] + 0.6780f * g[i] + 0.0593f * b[i];
            _hasHistogram = true;
        }
        else
        {
            _hasHistogram = false;
        }
        InvalidatePaint();
    }

    public override SKSize GetPreferredSize(float maxWidth, float maxHeight)
    {
        float w = maxWidth > 0 ? maxWidth : Theme.RightW;
        return new SKSize(w, 186f);
    }

    protected override void OnAfterStyleDraw(List<DrawCommand> cmds)
    {
        cmds.Add(new DrawCallbackCommand(DrawControl));
    }

    private void DrawControl(SKCanvas canvas)
    {
        float w = Transform.Computed.Width;
        float h = Transform.Computed.Height;
        if (w < 40f || h < 60f) return;

        // Top Header: Readout on left, Reset button on right
        float topH = 20f;
        _resetRect = new SKRect(w - 22f, 1f, w - 2f, 19f);
        DrawHeader(canvas, w);

        // Bottom Curvature Slider (Theme.RowH = 24f)
        float sliderH = Theme.RowH;
        float sliderY = h - sliderH;
        DrawCurvatureSlider(canvas, w, sliderY, sliderH);

        // Center Curve Plot Area
        float plotTop = topH + 4f;
        float plotBottom = sliderY - 6f;
        _plotRect = new SKRect(4f, plotTop, w - 4f, plotBottom);
        DrawPlotArea(canvas, _plotRect);
    }

    private void DrawHeader(SKCanvas canvas, float width)
    {
        // 1. Text Readout
        using var textPaint = new SKPaint
        {
            IsAntialias = true,
            TextSize = 11f,
            Typeface = Theme.GetTypeface(500),
            Color = Theme.TextDim,
            TextAlign = SKTextAlign.Left
        };

        if (_selectedPointIdx >= 0 && _selectedPointIdx < _points.Count)
        {
            var pt = _points[_selectedPointIdx];
            float deltaEv = (pt.Y - 128f) * (5.0f / 127.0f);
            string zone = pt.X < 75f ? "Darks" : (pt.X > 180f ? "Lights" : "Midtones");
            string sign = deltaEv > 0.001f ? "+" : "";
            string text = $"Point {_selectedPointIdx + 1}: {zone} ({pt.X / 2.55f:0}%)  →  {sign}{deltaEv:0.00} EV";

            using var activePaint = new SKPaint
            {
                IsAntialias = true,
                TextSize = 11f,
                Typeface = Theme.MonospaceTypeface,
                Color = Theme.Text,
                TextAlign = SKTextAlign.Left
            };
            canvas.DrawText(text, 6f, 15f, activePaint);
        }
        else
        {
            canvas.DrawText("Click curve to add point • Drag to adjust", 6f, 15f, textPaint);
        }

        // 2. Reset Button
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
        canvas.DrawRoundRect(_resetRect, Theme.RadiusSm, Theme.RadiusSm, resetBg);
        canvas.DrawRoundRect(_resetRect, Theme.RadiusSm, Theme.RadiusSm, resetBorder);

        using var iconPaint = new SKPaint
        {
            IsAntialias = true,
            TextSize = 12f,
            Typeface = Theme.GetTypeface(600),
            Color = _hoveredReset ? Theme.Accent : Theme.TextDim,
            TextAlign = SKTextAlign.Center
        };
        canvas.DrawText("↺", _resetRect.MidX, _resetRect.MidY + 4.5f, iconPaint);
    }

    private void DrawPlotArea(SKCanvas canvas, SKRect plot)
    {
        // 1. Background & Border
        using var bgPaint = new SKPaint { Color = Theme.Well, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var borderPaint = new SKPaint { Color = Theme.Hairline, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = true };
        canvas.DrawRoundRect(plot, Theme.RadiusSm, Theme.RadiusSm, bgPaint);

        canvas.Save();
        canvas.ClipRoundRect(new SKRoundRect(plot, Theme.RadiusSm, Theme.RadiusSm), SKClipOperation.Intersect, antialias: true);

        // 2. Histogram Underlay
        if (_hasHistogram)
        {
            DrawHistogram(canvas, plot);
        }

        // 3. Grid Lines
        using var gridPaint = new SKPaint
        {
            Color = Theme.HairlineSubtle,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            IsAntialias = true
        };

        // Vertical divisional lines (25%, 50%, 75%)
        for (int i = 1; i <= 3; i++)
        {
            float gx = plot.Left + (i / 4f) * plot.Width;
            canvas.DrawLine(gx, plot.Top, gx, plot.Bottom, gridPaint);
        }

        // Horizontal quarter lines (+2.5 EV, -2.5 EV)
        float yUpper = plot.Top + 0.25f * plot.Height;
        float yLower = plot.Top + 0.75f * plot.Height;
        canvas.DrawLine(plot.Left, yUpper, plot.Right, yUpper, gridPaint);
        canvas.DrawLine(plot.Left, yLower, plot.Right, yLower, gridPaint);

        // Horizontal Center Reference Line at Y = 128 (0.0 EV)
        float yCenter = plot.Top + 0.5f * plot.Height;
        using var centerLinePaint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 60),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.2f,
            IsAntialias = true
        };
        canvas.DrawLine(plot.Left, yCenter, plot.Right, yCenter, centerLinePaint);

        // "0 EV" center tag
        using var tagPaint = new SKPaint
        {
            IsAntialias = true,
            TextSize = 9f,
            Typeface = Theme.MonospaceTypeface,
            Color = new SKColor(255, 255, 255, 75),
            TextAlign = SKTextAlign.Right
        };
        canvas.DrawText("0 EV", plot.Right - 4f, yCenter - 3f, tagPaint);

        // Axis boundary indicators
        using var evLabelPaint = new SKPaint
        {
            IsAntialias = true,
            TextSize = 8.5f,
            Typeface = Theme.MonospaceTypeface,
            Color = Theme.TextDim,
            TextAlign = SKTextAlign.Left
        };
        canvas.DrawText("+5 EV", plot.Left + 4f, plot.Top + 10f, evLabelPaint);
        canvas.DrawText("-5 EV", plot.Left + 4f, plot.Bottom - 4f, evLabelPaint);

        // Tonal zone labels at bottom
        using var zonePaint = new SKPaint
        {
            IsAntialias = true,
            TextSize = 8.5f,
            Typeface = Theme.GetTypeface(500),
            Color = new SKColor(160, 160, 170, 70),
            TextAlign = SKTextAlign.Center
        };
        canvas.DrawText("Darks", plot.Left + plot.Width * 0.15f, plot.Bottom - 4f, zonePaint);
        canvas.DrawText("Midtones", plot.Left + plot.Width * 0.50f, plot.Bottom - 4f, zonePaint);
        canvas.DrawText("Lights", plot.Left + plot.Width * 0.85f, plot.Bottom - 4f, zonePaint);

        // 4. Spline Curve & Dual Shaded Fill
        DrawExposureCurveAndFill(canvas, plot, yCenter);

        // 5. Control Points
        DrawControlPoints(canvas, plot);

        canvas.Restore();
        canvas.DrawRoundRect(plot, Theme.RadiusSm, Theme.RadiusSm, borderPaint);
    }

    private void DrawHistogram(SKCanvas canvas, SKRect plot)
    {
        float peak = 0f;
        for (int i = 0; i < 256; i++)
            if (_histY[i] > peak) peak = _histY[i];

        if (peak <= 1e-5f) return;

        float logPeak = MathF.Log(1f + peak);
        using var path = new SKPath();
        float stepX = plot.Width / 255f;
        path.MoveTo(plot.Left, plot.Bottom);

        for (int i = 0; i < 256; i++)
        {
            float norm = MathF.Log(1f + _histY[i]) / logPeak;
            float x = plot.Left + i * stepX;
            float y = plot.Bottom - Math.Clamp(norm, 0f, 1f) * (plot.Height * 0.85f);
            path.LineTo(x, y);
        }
        path.LineTo(plot.Right, plot.Bottom);
        path.Close();

        using var fillPaint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 14),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        canvas.DrawPath(path, fillPaint);
    }

    private void DrawExposureCurveAndFill(SKCanvas canvas, SKRect plot, float yCenter)
    {
        int resolution = (int)Math.Max(20f, plot.Width);
        float[] rawLut = CurveMath.EvaluateExposureRaw(_points.ToArray(), resolution);
        float stepX = plot.Width / (resolution - 1);

        // Positive Shaded Fill (Above 0 EV: Y < yCenter)
        using (var posFillPath = new SKPath())
        {
            posFillPath.MoveTo(plot.Left, yCenter);
            for (int i = 0; i < resolution; i++)
            {
                float x = plot.Left + i * stepX;
                float cy = plot.Bottom - Math.Clamp(rawLut[i], 0f, 1f) * plot.Height;
                float y = Math.Min(cy, yCenter); // only above center
                posFillPath.LineTo(x, y);
            }
            posFillPath.LineTo(plot.Right, yCenter);
            posFillPath.Close();

            using var posPaint = new SKPaint
            {
                Color = new SKColor(Theme.Accent.Red, Theme.Accent.Green, Theme.Accent.Blue, 32),
                Style = SKPaintStyle.Fill,
                IsAntialias = true
            };
            canvas.DrawPath(posFillPath, posPaint);
        }

        // Negative Shaded Fill (Below 0 EV: Y > yCenter)
        using (var negFillPath = new SKPath())
        {
            negFillPath.MoveTo(plot.Left, yCenter);
            for (int i = 0; i < resolution; i++)
            {
                float x = plot.Left + i * stepX;
                float cy = plot.Bottom - Math.Clamp(rawLut[i], 0f, 1f) * plot.Height;
                float y = Math.Max(cy, yCenter); // only below center
                negFillPath.LineTo(x, y);
            }
            negFillPath.LineTo(plot.Right, yCenter);
            negFillPath.Close();

            using var negPaint = new SKPaint
            {
                Color = new SKColor(59, 130, 246, 28),
                Style = SKPaintStyle.Fill,
                IsAntialias = true
            };
            canvas.DrawPath(negFillPath, negPaint);
        }

        // Spline Curve Stroke
        using var curvePath = new SKPath();
        curvePath.MoveTo(plot.Left, plot.Bottom - Math.Clamp(rawLut[0], 0f, 1f) * plot.Height);
        for (int i = 1; i < resolution; i++)
        {
            float x = plot.Left + i * stepX;
            float y = plot.Bottom - Math.Clamp(rawLut[i], 0f, 1f) * plot.Height;
            curvePath.LineTo(x, y);
        }

        using var curvePaint = new SKPaint
        {
            Color = Theme.Accent,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2.0f,
            IsAntialias = true
        };
        canvas.DrawPath(curvePath, curvePaint);
    }

    private void DrawControlPoints(SKCanvas canvas, SKRect plot)
    {
        using var fillPaint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
        using var ringPaint = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true };
        using var shadowPaint = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = new SKColor(0, 0, 0, 110), IsAntialias = true };
        using var glowPaint = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f, IsAntialias = true };
        using var pipPaint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };

        for (int i = 0; i < _points.Count; i++)
        {
            var pt = _points[i];
            float sx = plot.Left + (pt.X / 255f) * plot.Width;
            float sy = plot.Bottom - (pt.Y / 255f) * plot.Height;

            bool isSelected = (i == _selectedPointIdx);
            bool isHighlighted = isSelected || (i == _hoveredPointIdx) || (i == _draggedPointIdx && _dragging);
            float radius = isHighlighted ? 8.5f : 6.5f;

            if (isHighlighted)
            {
                glowPaint.Color = Theme.Accent;
                canvas.DrawCircle(sx, sy, radius + 3.5f, glowPaint);
            }

            canvas.DrawCircle(sx, sy, radius + 1f, shadowPaint);

            fillPaint.Color = isHighlighted ? SKColors.White : Theme.Accent;
            canvas.DrawCircle(sx, sy, radius, fillPaint);

            ringPaint.Color = isHighlighted ? Theme.Accent : SKColors.White;
            ringPaint.StrokeWidth = isHighlighted ? 2.5f : 1.8f;
            canvas.DrawCircle(sx, sy, radius, ringPaint);

            // Center pip: diamond for straight corner (< 0.2), circle for curve
            if (pt.Curvature < 0.2f)
            {
                pipPaint.Color = isHighlighted ? Theme.Accent : Theme.Well;
                float d = isHighlighted ? 3.0f : 2.0f;
                using var diamond = new SKPath();
                diamond.MoveTo(sx, sy - d);
                diamond.LineTo(sx + d, sy);
                diamond.LineTo(sx, sy + d);
                diamond.LineTo(sx - d, sy);
                diamond.Close();
                canvas.DrawPath(diamond, pipPaint);
            }
            else if (isHighlighted)
            {
                pipPaint.Color = Theme.Accent;
                canvas.DrawCircle(sx, sy, 2.2f, pipPaint);
            }
        }
    }

    private void DrawCurvatureSlider(SKCanvas canvas, float width, float y, float height)
    {
        float labelW = 68f;
        float valueW = 48f;
        float gap = 6f;

        float trackLeft = labelW + gap;
        float trackRight = width - valueW - gap;
        float trackW = Math.Max(20f, trackRight - trackLeft);
        float trackY = y + (height - Theme.TrackH) * 0.5f;
        _sliderTrackRect = new SKRect(trackLeft, trackY, trackRight, trackY + Theme.TrackH);

        // Get curvature of selected point
        float curv = 1.0f;
        if (_selectedPointIdx >= 0 && _selectedPointIdx < _points.Count)
            curv = _points[_selectedPointIdx].Curvature;

        // 1. Label on Left
        using var labelPaint = new SKPaint
        {
            IsAntialias = true,
            TextSize = 11f,
            Typeface = Theme.GetTypeface(500),
            Color = Theme.TextSecondary,
            TextAlign = SKTextAlign.Left
        };
        canvas.DrawText("Curvature", 4f, y + (height + 7.5f) * 0.5f, labelPaint);

        // 2. Recessed Pill Track
        using var trackBg = new SKPaint { Color = Theme.Well, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var trackBorder = new SKPaint
        {
            Color = _hoveredSlider || _draggingCurvature ? Theme.HairlineStrong : Theme.Hairline,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            IsAntialias = true
        };
        canvas.DrawRoundRect(_sliderTrackRect, 5f, 5f, trackBg);
        canvas.DrawRoundRect(_sliderTrackRect, 5f, 5f, trackBorder);

        // 3. Glowing Accent Fill Bar (Height 4f)
        float thumbX = trackLeft + curv * trackW;
        float fillY = trackY + (Theme.TrackH - Theme.FillH) * 0.5f;
        var fillRect = new SKRect(trackLeft + 3f, fillY, thumbX, fillY + Theme.FillH);
        if (fillRect.Width > 0f)
        {
            using var fillPaint = new SKPaint { Color = Theme.Accent, Style = SKPaintStyle.Fill, IsAntialias = true };
            canvas.DrawRoundRect(fillRect, 2f, 2f, fillPaint);
        }

        // 4. Proud Vertical Pill Thumb (Theme.ThumbW = 8f, Theme.ThumbH = 16f, radius 4f)
        float thumbY = trackY + (Theme.TrackH - Theme.ThumbH) * 0.5f;
        var thumbRect = new SKRect(thumbX - Theme.ThumbW * 0.5f, thumbY, thumbX + Theme.ThumbW * 0.5f, thumbY + Theme.ThumbH);

        using var thumbShadow = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 80),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        canvas.DrawRoundRect(new SKRect(thumbRect.Left, thumbRect.Top + 1.2f, thumbRect.Right, thumbRect.Bottom + 1.2f), 4f, 4f, thumbShadow);

        bool thumbActive = _draggingCurvature || _hoveredSlider;
        using var thumbBg = new SKPaint
        {
            Color = thumbActive ? Theme.Accent : SKColors.White,
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        using var thumbBorder = new SKPaint
        {
            Color = thumbActive ? SKColors.White : Theme.HairlineStrong,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            IsAntialias = true
        };
        canvas.DrawRoundRect(thumbRect, 4f, 4f, thumbBg);
        canvas.DrawRoundRect(thumbRect, 4f, 4f, thumbBorder);

        // 5. Monospace Value Readout on Right
        string valStr = curv < 0.05f ? "Sharp" : (curv > 0.95f ? "Curve" : $"{curv * 100f:0}%");
        using var valPaint = new SKPaint
        {
            IsAntialias = true,
            TextSize = 11f,
            Typeface = Theme.MonospaceTypeface,
            Color = thumbActive ? Theme.Accent : Theme.TextDim,
            TextAlign = SKTextAlign.Right
        };
        canvas.DrawText(valStr, width - 4f, y + (height + 7.5f) * 0.5f, valPaint);
    }

    private int HitTestPoint(float screenX, float screenY)
    {
        const float hitRadiusSq = 16f * 16f;
        int bestIdx = -1;
        float bestDistSq = hitRadiusSq;

        for (int i = 0; i < _points.Count; i++)
        {
            float sx = _plotRect.Left + (_points[i].X / 255f) * _plotRect.Width;
            float sy = _plotRect.Bottom - (_points[i].Y / 255f) * _plotRect.Height;
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
        _points.Add(new ExposureCurvePoint(cx, cy, 1.0f));
        _points.Sort((a, b) => a.X.CompareTo(b.X));
        for (int i = 0; i < _points.Count; i++)
        {
            if (MathF.Abs(_points[i].X - cx) < 0.01f && MathF.Abs(_points[i].Y - cy) < 0.01f)
                return i;
        }
        return 0;
    }

    private void RemovePoint(int index)
    {
        if (index > 0 && index < _points.Count - 1)
        {
            _points.RemoveAt(index);
            _selectedPointIdx = Math.Clamp(_selectedPointIdx, 0, _points.Count - 1);
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

        // 1. Reset Button
        if (_resetRect.Contains(lx, ly) && args.Button == (int)MouseButton.Left)
        {
            _points = new List<ExposureCurvePoint>(CurveMath.DefaultExposureCurve());
            _selectedPointIdx = 0;
            CurveChanged?.Invoke();
            DragEnded?.Invoke();
            InvalidatePaint();
            args.Handled = true;
            return;
        }

        // 2. Curvature Slider
        if (_sliderTrackRect.Contains(lx, ly) || (ly >= _sliderTrackRect.Top - 6f && ly <= _sliderTrackRect.Bottom + 6f && lx >= _sliderTrackRect.Left && lx <= _sliderTrackRect.Right))
        {
            if (args.Button == (int)MouseButton.Left && _selectedPointIdx >= 0 && _selectedPointIdx < _points.Count)
            {
                _draggingCurvature = true;
                CapturePointer();
                float newCurv = Math.Clamp((lx - _sliderTrackRect.Left) / _sliderTrackRect.Width, 0f, 1f);
                var pt = _points[_selectedPointIdx];
                _points[_selectedPointIdx] = new ExposureCurvePoint(pt.X, pt.Y, newCurv);
                CurveChanged?.Invoke();
                InvalidatePaint();
                args.Handled = true;
                return;
            }
        }

        // 3. Graph Area & Control Points
        int hit = HitTestPoint(lx, ly);
        if (hit >= 0)
        {
            if (args.Button == (int)MouseButton.Right)
            {
                if (hit > 0 && hit < _points.Count - 1)
                {
                    RemovePoint(hit);
                    args.Handled = true;
                    return;
                }
            }
            else if (args.Button == (int)MouseButton.Left)
            {
                _selectedPointIdx = hit;
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
            _selectedPointIdx = newIdx;
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
        _hoveredSlider = _sliderTrackRect.Contains(lx, ly) || (ly >= _sliderTrackRect.Top - 6f && ly <= _sliderTrackRect.Bottom + 6f && lx >= _sliderTrackRect.Left && lx <= _sliderTrackRect.Right);

        if (_draggingCurvature && HasPointerCapture && _selectedPointIdx >= 0 && _selectedPointIdx < _points.Count)
        {
            float newCurv = Math.Clamp((lx - _sliderTrackRect.Left) / _sliderTrackRect.Width, 0f, 1f);
            var pt = _points[_selectedPointIdx];
            _points[_selectedPointIdx] = new ExposureCurvePoint(pt.X, pt.Y, newCurv);
            CurveChanged?.Invoke();
            InvalidatePaint();
            args.Handled = true;
            return;
        }

        if (_dragging && _draggedPointIdx >= 0 && HasPointerCapture)
        {
            ScreenToCurve(lx, ly, out float cx, out float cy);
            int idx = _draggedPointIdx;
            var curPt = _points[idx];

            if (idx == 0)
            {
                _points[0] = new ExposureCurvePoint(0f, cy, curPt.Curvature);
            }
            else if (idx == _points.Count - 1)
            {
                _points[^1] = new ExposureCurvePoint(255f, cy, curPt.Curvature);
            }
            else
            {
                float minX = _points[idx - 1].X + 1f;
                float maxX = _points[idx + 1].X - 1f;
                cx = Math.Clamp(cx, minX, maxX);
                _points[idx] = new ExposureCurvePoint(cx, cy, curPt.Curvature);
            }

            Cursor = StandardCursor.Hand;
            CurveChanged?.Invoke();
            InvalidatePaint();
            args.Handled = true;
            return;
        }

        int prevHover = _hoveredPointIdx;
        _hoveredPointIdx = HitTestPoint(lx, ly);
        if (_hoveredPointIdx != prevHover)
            InvalidatePaint();
    }

    private void OnMouseUp(object sender, MouseEventArgs args)
    {
        if (_draggingCurvature)
        {
            _draggingCurvature = false;
            ReleasePointer();
            DragEnded?.Invoke();
            InvalidatePaint();
            args.Handled = true;
            return;
        }

        if (_dragging)
        {
            _dragging = false;
            _draggedPointIdx = -1;
            ReleasePointer();
            Cursor = StandardCursor.Default;
            DragEnded?.Invoke();
            InvalidatePaint();
            args.Handled = true;
        }
    }
}
