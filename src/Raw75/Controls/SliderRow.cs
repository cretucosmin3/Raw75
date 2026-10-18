using System;
using Blossom.Core;
using Blossom.Core.Input;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Blossom.Hosting;
using Blossom.Primitives;
using Blossom.Reactive;
using SkiaSharp;

namespace Raw75.Controls;

public enum SliderGradientMode
{
    None = 0,
    Kelvin = 1,
    Tint = 2
}

/// <summary>
/// Compact slider on <see cref="Control"/>: inline single row or micro-stacked, with the proud
/// pill thumb and recessed groove. Shift-drag / shift-wheel for fine control, double-click to reset.
/// The readout turns accent when the value differs from <see cref="DefaultValue"/>.
/// </summary>
public class SliderRow : Control
{
    private const float LabelW = Theme.SliderLabelW; // 72f
    private const float ValueW = Theme.SliderValueW; // 42f
    private const float TrackMarginX = 5f;
    private static readonly TimeSpan DoubleClickWindow = TimeSpan.FromMilliseconds(280);

    private readonly VisualElement _labelEl;
    private readonly VisualElement _valueEl;

    private float _value;
    private float _min;
    private float _max;
    private string _label;
    private readonly string _valueFormat;
    private readonly bool _inline;
    private bool _dragging;

    // A burst of shift-wheel notches is one gesture: live updates per notch, one settle at the end.
    private static readonly TimeSpan ScrollSettleDelay = TimeSpan.FromMilliseconds(250);
    private bool _scrolling;
    private IDisposable? _scrollSettle;
    private float _hover;
    private float _lastGlobalX;
    private DateTime _lastDownUtc = DateTime.MinValue;
    private SliderGradientMode _gradientMode = SliderGradientMode.None;

    private bool IsShiftHeld => (ParentView?.Events.IsShiftDown ?? false) || Events.IsShiftDown;

    private float GetTrackWidth()
    {
        float w = Math.Max(1f, Transform.Computed.Width);
        if (_inline)
        {
            return Math.Max(10f, w - LabelW - ValueW - (TrackMarginX * 2f));
        }
        return w;
    }

    private float _defaultValue;
    public float DefaultValue
    {
        get => _defaultValue;
        set
        {
            _defaultValue = value;
            UpdateValueColor();
        }
    }

    private bool IsModified => Math.Abs(_value - _defaultValue) > (_max - _min) * 0.0005f;

    /// <summary>
    /// Set from code (e.g. a photo's stored edit) the value is kept as is, even outside the range, so the
    /// readout stays true; the bar and thumb are drawn clamped to the track. Dragging and scrolling clamp.
    /// </summary>
    public float Value
    {
        get => _value;
        set => SetValue(value, fire: true, clamp: false);
    }

    public float Min => _min;
    public float Max => _max;

    /// <summary>Changes how far the slider reaches; the current value is kept (see <see cref="Value"/>).</summary>
    public void SetRange(float min, float max)
    {
        max = max <= min ? min + 1f : max;
        if (min == _min && max == _max) return;
        _min = min;
        _max = max;
        UpdateValueColor();
        InvalidatePaint();
    }

    public string Label
    {
        get => _label;
        set
        {
            _label = value ?? "";
            _labelEl.Text = _label;
            InvalidatePaint();
        }
    }

    public SliderGradientMode GradientMode
    {
        get => _gradientMode;
        set
        {
            _gradientMode = value;
            InvalidatePaint();
        }
    }

    public event Action<float>? Changed;
    public event Action? DragStarted;
    public event Action? DragEnded;

    public SliderRow(string label, float min, float max, string valueFormat = "0", bool inline = true)
    {
        Name = $"SliderRow_{label}";
        _label = label ?? "";
        _min = min;
        _max = max <= min ? min + 1f : max;
        _valueFormat = string.IsNullOrEmpty(valueFormat) ? "0" : valueFormat;
        _value = Math.Clamp(0f, _min, _max);
        _inline = inline;

        Transform.Height = _inline ? Theme.RowH : Theme.RowHStacked;
        Cursor = UiCursor.HResize;
        Style.BackColor = SKColors.Transparent;

        _labelEl = new VisualElement
        {
            Name = $"{Name}_Label",
            Text = _label,
            IsClickthrough = true,
            Overflow = OverflowMode.Visible,
            Style = LabelStyle(TextAlign.Left, isValue: false)
        };

        _valueEl = new VisualElement
        {
            Name = $"{Name}_Value",
            IsClickthrough = true,
            Overflow = OverflowMode.Visible,
            Style = LabelStyle(TextAlign.Right, isValue: true)
        };

        AddChild(_labelEl);
        AddChild(_valueEl);
        UpdateValueText();

        Events.OnMouseDown += OnTrackDown;
        Events.OnMouseMove += OnTrackMove;
        Events.OnMouseUp += OnTrackUp;
        Events.OnMouseDoubleClick += OnResetClick;
        Events.OnScroll += OnTrackScroll;

        this.Bind(_ => ApplyTheme());
    }

    protected override void OnHoverChanged()
    {
        _hover = IsHovered ? 1f : 0f;
        InvalidatePaint();
        UpdateValueColor();
    }

    public override SKSize GetPreferredSize(float maxWidth, float maxHeight)
    {
        float w = maxWidth > 0 ? maxWidth : (Transform.Width > 0 ? Transform.Width : 220f);
        return new SKSize(w, _inline ? Theme.RowH : Theme.RowHStacked);
    }

    protected override void LayoutChildren()
    {
        float originX = Transform.Computed.X;
        float originY = Transform.Computed.Y;
        float w = Math.Max(1f, Transform.Width);

        if (_inline)
        {
            float rowH = Theme.RowH;
            float textH = 18f;
            float textY = originY + (rowH - textH) * 0.5f;

            _labelEl.Transform.SetAbsoluteFrame(originX, textY, LabelW, textH);
            _valueEl.Transform.SetAbsoluteFrame(originX + w - ValueW, textY, ValueW, textH);
        }
        else
        {
            float labelH = 15f;
            float labelY = originY + Theme.StackedTopGap;
            _labelEl.Transform.SetAbsoluteFrame(originX, labelY, Math.Max(1f, w - ValueW), labelH);
            _valueEl.Transform.SetAbsoluteFrame(originX + w - ValueW, labelY, ValueW, labelH);
        }
    }

    protected override void Paint(SKCanvas canvas)
    {
        float w = Transform.Computed.Width;
        if (w < 10f)
            return;

        float trackX;
        float trackY;
        float trackW;
        float trackAreaH;

        if (_inline)
        {
            trackX = LabelW + TrackMarginX;
            trackW = Math.Max(10f, w - LabelW - ValueW - (TrackMarginX * 2f));
            trackY = 0;
            trackAreaH = Theme.RowH;
        }
        else
        {
            trackX = 0;
            trackW = w;
            trackY = Theme.StackedTopGap + 17f;
            trackAreaH = Theme.RowHStacked - trackY;
        }

        canvas.Save();
        canvas.Translate(trackX, trackY);

        float range = Math.Max(0.0001f, _max - _min);
        float t = Math.Clamp((_value - _min) / range, 0f, 1f);
        bool bipolar = _min < 0f && _max > 0f;
        float zeroT = Math.Clamp((0f - _min) / range, 0f, 1f);

        SKShader? shader = null;
        if (_gradientMode == SliderGradientMode.Kelvin)
        {
            shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(trackW, 0),
                new[] { Theme.KelvinCold, new SKColor(230, 230, 235), Theme.KelvinWarm },
                new[] { 0.0f, 0.5f, 1.0f },
                SKShaderTileMode.Clamp);
        }
        else if (_gradientMode == SliderGradientMode.Tint)
        {
            shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(trackW, 0),
                new[] { Theme.TintGreen, new SKColor(200, 200, 205), Theme.TintMagenta },
                new[] { 0.0f, 0.5f, 1.0f },
                SKShaderTileMode.Clamp);
        }

        SliderChrome.Draw(canvas, trackW, trackAreaH, t, zeroT, bipolar, _hover, _dragging, shader);
        shader?.Dispose();

        canvas.Restore();
    }

    private void OnTrackDown(object sender, MouseEventArgs args)
    {
        if (args.Button != 0) return;
        EndScroll();

        DateTime now = DateTime.UtcNow;
        if (now - _lastDownUtc < DoubleClickWindow)
        {
            _lastDownUtc = DateTime.MinValue;
            EndDrag();
            Value = DefaultValue;
            args.Handled = true;
            return;
        }

        _lastDownUtc = now;
        _dragging = true;
        _lastGlobalX = args.Global.X;
        CapturePointer();
        UpdateValueColor();
        args.Handled = true;
        DragStarted?.Invoke();

        if (!IsShiftHeld)
        {
            SetValueFromGlobal(args.Global.X);
        }
    }

    private void OnTrackMove(object sender, MouseEventArgs args)
    {
        if (!HasPointerCapture)
        {
            if (_dragging)
                EndDrag();
            return;
        }
        args.Handled = true;

        if (IsShiftHeld)
        {
            float deltaX = args.Global.X - _lastGlobalX;
            _lastGlobalX = args.Global.X;

            if (Math.Abs(deltaX) < 1e-5f)
                return;

            float trackW = GetTrackWidth();
            float travel = Math.Max(1f, trackW - Theme.ThumbW);
            float speed = 0.25f;
            float deltaV = (deltaX / travel) * (_max - _min) * speed;

            SetValue(_value + deltaV, fire: true);
        }
        else
        {
            _lastGlobalX = args.Global.X;
            SetValueFromGlobal(args.Global.X);
        }
    }

    private void OnTrackScroll(object sender, MouseScrollEventArgs args)
    {
        if (!IsShiftHeld)
            return;

        float delta = Math.Abs(args.Offset.Y) >= Math.Abs(args.Offset.X) ? args.Offset.Y : args.Offset.X;
        if (Math.Abs(delta) < 1e-5f)
            return;

        args.Handled = true;

        float range = Math.Max(0.0001f, _max - _min);
        float step;

        if (_valueFormat == "0" || _valueFormat.Contains('°'))
        {
            step = 1f;
            if (Math.Abs(delta) >= 0.8f)
            {
                float next = MathF.Round(_value) + MathF.Sign(delta) * step;
                ApplyScrollDelta(next);
                return;
            }
        }
        else if (_valueFormat == "0.0")
        {
            step = 0.1f;
            if (Math.Abs(delta) >= 0.8f)
            {
                float next = MathF.Round((_value + MathF.Sign(delta) * step) * 10f) / 10f;
                ApplyScrollDelta(next);
                return;
            }
        }
        else if (_valueFormat == "0.00")
        {
            step = range <= 2.0f ? 0.01f : (range <= 5.0f ? 0.02f : 0.05f);
            if (Math.Abs(delta) >= 0.8f)
            {
                float next = MathF.Round((_value + MathF.Sign(delta) * step) * 100f) / 100f;
                ApplyScrollDelta(next);
                return;
            }
        }
        else
        {
            step = Math.Max(0.01f, range * 0.005f);
        }

        ApplyScrollDelta(_value + delta * step);
    }

    private void ApplyScrollDelta(float targetValue)
    {
        if (!_dragging && !_scrolling)
        {
            _scrolling = true;
            DragStarted?.Invoke();
        }

        SetValue(targetValue, fire: true);

        if (_scrolling)
        {
            _scrollSettle?.Dispose();
            _scrollSettle = UiHost.Current.PostDelayed(ScrollSettleDelay, EndScroll);
        }
    }

    private void EndScroll()
    {
        _scrollSettle?.Dispose();
        _scrollSettle = null;
        if (!_scrolling) return;
        _scrolling = false;
        DragEnded?.Invoke();
    }

    private void OnTrackUp(object sender, MouseEventArgs args)
    {
        if (!HasPointerCapture && !_dragging)
            return;
        EndDrag();
        args.Handled = true;
    }

    private void OnResetClick(object sender, MouseEventArgs args)
    {
        if (args.Button != 0) return;
        EndDrag();
        Value = DefaultValue;
        args.Handled = true;
    }

    private void EndDrag()
    {
        bool was = _dragging || HasPointerCapture;
        _dragging = false;
        if (HasPointerCapture)
            ReleasePointer();
        UpdateValueColor();
        if (was)
            DragEnded?.Invoke();
    }

    private void SetValueFromGlobal(float globalX)
    {
        var local = PointToClient(globalX, 0);
        float w = Math.Max(1f, Transform.Computed.Width);

        float trackX;
        float trackW;
        if (_inline)
        {
            trackX = LabelW + TrackMarginX;
            trackW = Math.Max(10f, w - LabelW - ValueW - (TrackMarginX * 2f));
        }
        else
        {
            trackX = 0f;
            trackW = w;
        }

        float thumbRadius = Theme.ThumbW * 0.5f;
        float travel = Math.Max(1f, trackW - Theme.ThumbW);
        float t = Math.Clamp((local.X - trackX - thumbRadius) / travel, 0f, 1f);

        SetValue(_min + t * (_max - _min), fire: true);
    }

    private void SetValue(float value, bool fire, bool clamp = true)
    {
        float next = clamp ? Math.Clamp(value, _min, _max) : value;
        if (Math.Abs(_value - next) < 0.0001f)
            return;

        _value = next;
        UpdateValueText();
        UpdateValueColor();
        InvalidatePaint();
        if (fire)
            Changed?.Invoke(_value);
    }

    private void UpdateValueText()
    {
        try
        {
            _valueEl.Text = _value.ToString(_valueFormat);
        }
        catch (FormatException)
        {
            _valueEl.Text = _value.ToString("0");
        }
    }

    private void ApplyTheme()
    {
        _labelEl.Style.Text.Color = Theme.TextSecondary;
        UpdateValueColor();
        InvalidatePaint();
    }

    private void UpdateValueColor()
    {
        var text = _valueEl.Style?.Text;
        if (text == null) return;
        bool live = _dragging || _scrolling || IsHovered;
        text.Color = IsModified
            ? (live ? Theme.AccentHover : Theme.Modified)
            : (live ? Theme.Text : Theme.TextDim);
        text.Weight = IsModified ? 600 : 500;
        _labelEl.Style.Text.Color = live ? Theme.Text : Theme.TextSecondary;
    }

    private static ElementStyle LabelStyle(TextAlign align, bool isValue) => new()
    {
        BackColor = SKColors.Transparent,
        Text = new TextStyle
        {
            Color = isValue ? Theme.TextDim : Theme.TextSecondary,
            Size = isValue ? 11.5f : 12f,
            Weight = 500,
            Alignment = align,
            Padding = 0,
            Overflow = TextOverflow.Visible,
            MaxLines = 1
        }
    };

    public override void Dispose()
    {
        _scrollSettle?.Dispose();
        _scrollSettle = null;
        base.Dispose();
    }
}
