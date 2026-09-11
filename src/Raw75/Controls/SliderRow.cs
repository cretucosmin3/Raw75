using System;
using System.Collections.Generic;
using Blossom.Core;
using Blossom.Core.Input;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Silk.NET.Input;
using SkiaSharp;

namespace Raw75.Controls;

public enum SliderGradientMode
{
    None = 0,
    Kelvin = 1,
    Tint = 2
}

/// <summary>
/// Compact Capture One Pro-inspired slider:
/// Inline Single-Row (24px) or Micro-Stacked (30px) with proud pill thumb and recessed track.
/// </summary>
public class SliderRow : VisualElement
{
    private const float LabelW = Theme.SliderLabelW; // 72f
    private const float ValueW = Theme.SliderValueW; // 42f
    private const float TrackMarginX = 5f;
    private static readonly TimeSpan DoubleClickWindow = TimeSpan.FromMilliseconds(280);

    private readonly VisualElement _labelEl;
    private readonly VisualElement _valueEl;

    private float _value;
    private readonly float _min;
    private readonly float _max;
    private string _label;
    private readonly string _valueFormat;
    private readonly bool _inline;
    private bool _dragging;
    private bool _hovered;
    private float _lastGlobalX;
    private DateTime _lastDownUtc = DateTime.MinValue;
    private SliderGradientMode _gradientMode = SliderGradientMode.None;

    private bool IsShiftHeld => (ParentView?.Events.IsShiftDown ?? false) || Events.IsShiftDown;

    private float GetTrackWidth()
    {
        float w = Math.Max(1f, Transform.Computed.Width);
        if (_inline)
        {
            return Math.Max(1f, w - LabelW - ValueW - (TrackMarginX * 2f));
        }
        return w;
    }

    public float DefaultValue { get; set; }

    public float Value
    {
        get => _value;
        set => SetValue(value, fire: true);
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
        Cursor = StandardCursor.HResize;
        Style = new ElementStyle
        {
            BackColor = SKColors.Transparent
        };

        _labelEl = new VisualElement
        {
            Name = $"{Name}_Label",
            Text = _label,
            IsClickthrough = true,
            Style = LabelStyle(TextAlign.Left, isValue: false)
        };

        _valueEl = new VisualElement
        {
            Name = $"{Name}_Value",
            IsClickthrough = true,
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
        Events.OnMouseEnter += _ => { _hovered = true; UpdateValueColor(); InvalidatePaint(); };
        Events.OnMouseLeave += _ => { _hovered = false; UpdateValueColor(); InvalidatePaint(); };
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
            _labelEl.Transform.SetAbsoluteFrame(originX, originY, Math.Max(1f, w - ValueW), labelH);
            _valueEl.Transform.SetAbsoluteFrame(originX + w - ValueW, originY, ValueW, labelH);
        }
    }

    protected override void OnAfterStyleDraw(List<DrawCommand> cmds)
    {
        cmds.Add(new DrawCallbackCommand(DrawTrack));
    }

    private void DrawTrack(SKCanvas canvas)
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
            trackY = 17f;
            trackAreaH = Theme.RowHStacked - 17f;
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

        SliderChrome.Draw(canvas, trackW, trackAreaH, t, zeroT, bipolar, _hovered, _dragging, shader);
        shader?.Dispose();

        canvas.Restore();
    }

    private void OnTrackDown(object sender, MouseEventArgs args)
    {
        if (args.Button != 0) return;

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

        float deltaX = args.Global.X - _lastGlobalX;
        _lastGlobalX = args.Global.X;

        if (Math.Abs(deltaX) < 1e-5f)
            return;

        float trackW = GetTrackWidth();
        float speed = IsShiftHeld ? 0.30f : 1.0f;
        float deltaV = (deltaX / trackW) * (_max - _min) * speed;

        SetValue(_value + deltaV, fire: true);
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
        bool wasDragging = _dragging;
        if (!wasDragging)
            DragStarted?.Invoke();

        SetValue(targetValue, fire: true);

        if (!wasDragging)
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

        float t;
        if (_inline)
        {
            float trackX = LabelW + TrackMarginX;
            float trackW = Math.Max(1f, w - LabelW - ValueW - (TrackMarginX * 2f));
            t = Math.Clamp((local.X - trackX) / trackW, 0f, 1f);
        }
        else
        {
            t = Math.Clamp(local.X / w, 0f, 1f);
        }

        SetValue(_min + t * (_max - _min), fire: true);
    }

    private void SetValue(float value, bool fire)
    {
        float clamped = Math.Clamp(value, _min, _max);
        if (Math.Abs(_value - clamped) < 0.0001f)
            return;

        _value = clamped;
        UpdateValueText();
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

    private void UpdateValueColor()
    {
        if (_valueEl.Style?.Text != null)
        {
            _valueEl.Style.Text.Color = (_dragging || _hovered) ? Theme.Text : Theme.TextDim;
        }
    }

    private static ElementStyle LabelStyle(TextAlign align, bool isValue) => new()
    {
        BackColor = SKColors.Transparent,
        Text = new TextStyle
        {
            Color = isValue ? Theme.TextDim : Theme.TextSecondary,
            Size = 12,
            Weight = 500,
            Alignment = align,
            Padding = 0
        }
    };
}
