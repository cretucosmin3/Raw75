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

/// <summary>
/// Pro-grade interactive Exposure Curve editor on <see cref="Control"/>.
/// - Freeform monotone cubic Hermite spline with per-point Curvature / Tension control.
/// - Controls scene-referred optical exposure gain from -5.0 EV (bottom) to +5.0 EV (top).
/// - Neutral 0.0 EV identity centerline with dual-tint fill (accent positive, cool negative).
/// - Per-point Curvature slider: smoothly transitions individual anchor points between straight linear corners and curved splines.
/// - Interactive control points: add on click, drag with monotonic X clamping, remove on right click.
/// - Live histogram underlay and tabular point coordinate readouts.
/// </summary>
public sealed class ExposureCurveControl : Control
{
    private const float ControlH = 186f;
    private const float HeaderH = 20f;
    private const float CurvLabelW = 68f;
    private const float CurvValueW = 48f;
    private const float CurvGap = 6f;
    private const float PointHitR = 16f;

    private List<ExposureCurvePoint> _points = new(CurveMath.DefaultExposureCurve());
    private int _selectedPointIdx = 0;
    private int _hoveredPointIdx = -1;
    private int _draggedPointIdx = -1;
    private bool _dragging;
    private bool _draggingCurvature;
    private bool _hoveredReset;
    private bool _hoveredSlider;
    private bool _hoveringGraph;

    // _litIdx is the hovered point whose ring is lit.
    private int _litIdx = -1;
    private float _lit;
    private float _plotHover;
    private float _resetHover;
    private float _sliderHover;

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
        Cursor = UiCursor.Default;
        Transform.Height = ControlH;
        Style.BackColor = SKColors.Transparent;

        Events.OnMouseDown += OnMouseDown;
        Events.OnMouseMove += OnMouseMove;
        Events.OnMouseUp += OnMouseUp;

        this.Bind(_ => { Theme.Observe(); InvalidatePaint(); });
    }

    protected override void OnHoverChanged()
    {
        if (IsHovered) return;
        SetPointHover(-1);
        SetResetHover(false);
        SetSliderHover(false);
        SetGraphHover(false);
    }

    public void LoadFromSettings(DevelopSettings s)
    {
        if (s.ExposureCurve != null && s.ExposureCurve.Length >= 2)
            _points = new List<ExposureCurvePoint>(s.ExposureCurve);
        else
            _points = new List<ExposureCurvePoint>(CurveMath.DefaultExposureCurve());

        _selectedPointIdx = Math.Clamp(_selectedPointIdx, 0, _points.Count - 1);
        ClearLit();
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
        return new SKSize(w, ControlH);
    }

    protected override void Paint(SKCanvas canvas)
    {
        float w = W;
        float h = H;
        if (w < 40f || h < 60f) return;

        // Header: readout on the left, reset on the right.
        _resetRect = new SKRect(w - 22f, 1f, w - 2f, 19f);
        DrawHeader(canvas);

        float sliderH = Theme.RowH;
        float sliderY = h - sliderH;
        DrawCurvatureSlider(canvas, w, sliderY, sliderH);

        _plotRect = new SKRect(1f, HeaderH + 4f, w - 1f, sliderY - 6f);
        DrawPlotArea(canvas, _plotRect);
    }

    private void DrawHeader(SKCanvas canvas)
    {
        float baselineY = 14.5f;
        if (_selectedPointIdx >= 0 && _selectedPointIdx < _points.Count)
        {
            var pt = _points[_selectedPointIdx];
            float deltaEv = (pt.Y - 128f) * (5.0f / 127.0f);
            string zone = pt.X < 75f ? "Darks" : (pt.X > 180f ? "Lights" : "Midtones");
            string sign = deltaEv > 0.001f ? "+" : "";

            using var label = new TextPaint { TextSize = 11f, Typeface = Theme.GetTypeface(600), Color = Theme.TextSecondary };
            using var value = new TextPaint
            {
                TextSize = 11f,
                Typeface = Theme.MonospaceTypeface,
                Color = Math.Abs(deltaEv) > 0.005f ? Theme.Modified : Theme.TextDim
            };

            string head = $"Point {_selectedPointIdx + 1}";
            string place = $"{zone} {pt.X / 2.55f:0}%";
            float x = 3f;
            canvas.DrawText(head, x, baselineY, label);
            x += label.MeasureText(head) + 8f;
            label.Typeface = Theme.GetTypeface(500);
            label.Color = Theme.TextDim;
            canvas.DrawText(place, x, baselineY, label);
            x += label.MeasureText(place) + 8f;
            canvas.DrawText($"{sign}{deltaEv:0.00} EV", x, baselineY, value);
        }
        else
        {
            using var hint = new TextPaint { TextSize = 11f, Typeface = Theme.GetTypeface(500), Color = Theme.TextDim };
            canvas.DrawText("Click curve to add point • Drag to adjust", 3f, baselineY, hint);
        }

        PlotChrome.DrawResetButton(canvas, _resetRect, _resetHover);
    }

    private void DrawPlotArea(SKCanvas canvas, SKRect plot)
    {
        PlotChrome.DrawWell(canvas, plot, _plotHover);

        canvas.Save();
        canvas.ClipRoundRect(new SKRoundRect(plot, Theme.Radius), antialias: true);

        if (_hasHistogram)
            DrawHistogram(canvas, plot);

        // Vertical tonal quarters, horizontal ±2.5 EV (the 0 EV line is drawn stronger below).
        PlotChrome.DrawGrid(canvas, plot);

        float yCenter = MathF.Round(plot.Top + 0.5f * plot.Height) + 0.5f;
        using (var centerLine = new SKPaint { Color = Theme.HairlineStrong, StrokeWidth = 1f })
            canvas.DrawLine(plot.Left + 1f, yCenter, plot.Right - 1f, yCenter, centerLine);

        using (var tag = PlotChrome.AxisText(SKTextAlign.Right, mono: true))
            canvas.DrawText("0 EV", plot.Right - 5f, yCenter - 4f, tag);

        // ±5 EV sit on the left edge; -5 clears the zone row along the floor.
        using (var ev = PlotChrome.AxisText(SKTextAlign.Left, mono: true))
        {
            canvas.DrawText("+5 EV", plot.Left + 5f, plot.Top + 12f, ev);
            canvas.DrawText("-5 EV", plot.Left + 5f, plot.Bottom - 17f, ev);
        }

        using (var zone = PlotChrome.AxisText(SKTextAlign.Center))
        {
            zone.Color = Theme.TextDim.WithAlpha(150);
            canvas.DrawText("Darks", plot.Left + plot.Width * 0.15f, plot.Bottom - 5f, zone);
            canvas.DrawText("Midtones", plot.Left + plot.Width * 0.50f, plot.Bottom - 5f, zone);
            canvas.DrawText("Lights", plot.Left + plot.Width * 0.85f, plot.Bottom - 5f, zone);
        }

        DrawExposureCurveAndFill(canvas, plot, yCenter);

        canvas.Restore();

        PlotChrome.DrawFrame(canvas, plot);
        DrawControlPoints(canvas, plot);
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
            path.LineTo(plot.Left + i * stepX, plot.Bottom - Math.Clamp(norm, 0f, 1f) * (plot.Height * 0.85f));
        }
        path.LineTo(plot.Right, plot.Bottom);
        path.Close();

        using var fill = new SKPaint { Color = Theme.Text.WithAlpha(14), IsAntialias = true };
        canvas.DrawPath(path, fill);
    }

    private void DrawExposureCurveAndFill(SKCanvas canvas, SKRect plot, float yCenter)
    {
        int resolution = (int)Math.Max(20f, plot.Width);
        float[] rawLut = CurveMath.EvaluateExposureRaw(_points.ToArray(), resolution);
        float stepX = plot.Width / (resolution - 1);

        float CurveY(int i) => plot.Bottom - Math.Clamp(rawLut[i], 0f, 1f) * plot.Height;

        // Gain above 0 EV washes accent toward the curve; cut below washes cool.
        FillBand(canvas, plot, yCenter, resolution, stepX, CurveY, above: true, Theme.Accent);
        FillBand(canvas, plot, yCenter, resolution, stepX, CurveY, above: false, Theme.BlueChannel);

        using var curvePath = new SKPath();
        curvePath.MoveTo(plot.Left, CurveY(0));
        for (int i = 1; i < resolution; i++)
            curvePath.LineTo(plot.Left + i * stepX, CurveY(i));

        PlotChrome.StrokeCurve(canvas, curvePath, Theme.Accent, glow: _dragging ? 1f : 0.7f);
    }

    private static void FillBand(SKCanvas canvas, SKRect plot, float yCenter, int resolution, float stepX,
        Func<int, float> curveY, bool above, SKColor color)
    {
        using var path = new SKPath();
        path.MoveTo(plot.Left, yCenter);
        for (int i = 0; i < resolution; i++)
        {
            float cy = curveY(i);
            path.LineTo(plot.Left + i * stepX, above ? Math.Min(cy, yCenter) : Math.Max(cy, yCenter));
        }
        path.LineTo(plot.Right, yCenter);
        path.Close();

        float far = above ? plot.Top : plot.Bottom;
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, yCenter), new SKPoint(0, far),
                new[] { color.WithAlpha(18), color.WithAlpha(above ? (byte)52 : (byte)44) },
                SKShaderTileMode.Clamp)
        };
        canvas.DrawPath(path, paint);
    }

    private void DrawControlPoints(SKCanvas canvas, SKRect plot)
    {
        for (int i = 0; i < _points.Count; i++)
        {
            var pt = _points[i];
            float sx = plot.Left + (pt.X / 255f) * plot.Width;
            float sy = plot.Bottom - (pt.Y / 255f) * plot.Height;

            bool selected = i == _selectedPointIdx;
            bool active = _dragging && i == _draggedPointIdx;
            float lit = Math.Max(selected ? 0.65f : 0f, i == _litIdx ? _lit : 0f);
            PlotChrome.DrawHandle(canvas, sx, sy, lit, active);

            // Corner (straight) anchors carry a diamond pip.
            if (pt.Curvature < 0.2f)
            {
                using var pip = new SKPaint { Color = selected || active ? Theme.Accent : Theme.Well, IsAntialias = true };
                float d = 2.2f;
                using var diamond = new SKPath();
                diamond.MoveTo(sx, sy - d);
                diamond.LineTo(sx + d, sy);
                diamond.LineTo(sx, sy + d);
                diamond.LineTo(sx - d, sy);
                diamond.Close();
                canvas.DrawPath(diamond, pip);
            }
        }
    }

    private void DrawCurvatureSlider(SKCanvas canvas, float width, float y, float height)
    {
        float trackLeft = CurvLabelW + CurvGap;
        float trackRight = width - CurvValueW - CurvGap;
        float trackW = Math.Max(20f, trackRight - trackLeft);
        float trackY = y + (height - Theme.TrackH) * 0.5f;
        _sliderTrackRect = new SKRect(trackLeft, trackY, trackLeft + trackW, trackY + Theme.TrackH);

        float curv = 1.0f;
        if (_selectedPointIdx >= 0 && _selectedPointIdx < _points.Count)
            curv = _points[_selectedPointIdx].Curvature;

        bool live = _draggingCurvature || _hoveredSlider;
        using var label = new TextPaint
        {
            TextSize = 12f,
            Typeface = Theme.GetTypeface(500),
            Color = live ? Theme.Text : Theme.TextSecondary
        };
        var m = label.FontMetrics;
        float baseline = y + height * 0.5f - (m.Ascent + m.Descent) * 0.5f;
        canvas.DrawText("Curvature", 0f, baseline, label);

        canvas.Save();
        canvas.Translate(trackLeft, y);
        SliderChrome.Draw(canvas, trackW, height, curv, 0f, bipolar: false, _sliderHover, _draggingCurvature);
        canvas.Restore();

        string valStr = curv < 0.05f ? "Sharp" : (curv > 0.95f ? "Curve" : $"{curv * 100f:0}%");
        bool modified = Math.Abs(curv - 1f) > 0.005f;
        using var value = new TextPaint
        {
            TextSize = 11.5f,
            Typeface = Theme.MonospaceTypeface,
            TextAlign = SKTextAlign.Right,
            Color = modified ? (live ? Theme.AccentHover : Theme.Modified) : (live ? Theme.Text : Theme.TextDim)
        };
        canvas.DrawText(valStr, width - 2f, baseline, value);
    }

    private float CurvatureAt(float lx)
    {
        float travel = Math.Max(1f, _sliderTrackRect.Width - Theme.ThumbW);
        return Math.Clamp((lx - _sliderTrackRect.Left - Theme.ThumbW * 0.5f) / travel, 0f, 1f);
    }

    private bool HitSlider(float lx, float ly) =>
        ly >= _sliderTrackRect.Top - 6f && ly <= _sliderTrackRect.Bottom + 6f &&
        lx >= _sliderTrackRect.Left && lx <= _sliderTrackRect.Right;

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

    private void SetSliderHover(bool hover)
    {
        if (_hoveredSlider == hover) return;
        _hoveredSlider = hover;
        _sliderHover = hover ? 1f : 0f;
        InvalidatePaint();
    }

    private void SetGraphHover(bool hover)
    {
        if (_hoveringGraph == hover) return;
        _hoveringGraph = hover;
        _plotHover = hover ? 1f : 0f;
        InvalidatePaint();
    }

    private int HitTestPoint(float screenX, float screenY)
    {
        const float hitRadiusSq = PointHitR * PointHitR;
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
            ClearLit();
            CurveChanged?.Invoke();
            DragEnded?.Invoke();
            InvalidatePaint();
        }
    }

    private void SetSelectedCurvature(float curvature)
    {
        var pt = _points[_selectedPointIdx];
        _points[_selectedPointIdx] = new ExposureCurvePoint(pt.X, pt.Y, curvature);
        CurveChanged?.Invoke();
        InvalidatePaint();
    }

    private void BeginPointDrag(int index)
    {
        _selectedPointIdx = index;
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

        // 1. Reset button
        if (_resetRect.Contains(lx, ly) && args.Button == PointerButton.Left)
        {
            _points = new List<ExposureCurvePoint>(CurveMath.DefaultExposureCurve());
            _selectedPointIdx = 0;
            ClearLit();
            CurveChanged?.Invoke();
            DragEnded?.Invoke();
            InvalidatePaint();
            args.Handled = true;
            return;
        }

        // 2. Curvature slider
        if (HitSlider(lx, ly))
        {
            if (args.Button == PointerButton.Left && _selectedPointIdx >= 0 && _selectedPointIdx < _points.Count)
            {
                _draggingCurvature = true;
                CapturePointer();
                SetSelectedCurvature(CurvatureAt(lx));
                args.Handled = true;
                return;
            }
        }

        // 3. Graph area & control points
        int hit = HitTestPoint(lx, ly);
        if (hit >= 0)
        {
            if (args.Button == PointerButton.Right)
            {
                if (hit > 0 && hit < _points.Count - 1)
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
        SetSliderHover(HitSlider(lx, ly));

        if (_draggingCurvature && HasPointerCapture && _selectedPointIdx >= 0 && _selectedPointIdx < _points.Count)
        {
            SetSelectedCurvature(CurvatureAt(lx));
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

            Cursor = UiCursor.Hand;
            CurveChanged?.Invoke();
            InvalidatePaint();
            args.Handled = true;
            return;
        }

        SetPointHover(HitTestPoint(lx, ly));
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
            if (_hoveredPointIdx < 0)
                _lit = 0f;
                InvalidatePaint();
            Cursor = UiCursor.Default;
            DragEnded?.Invoke();
            InvalidatePaint();
            args.Handled = true;
        }
    }
}
