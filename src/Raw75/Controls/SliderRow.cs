using System;
using System.Collections.Generic;
using Blossom.Core;
using Blossom.Core.Input;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Silk.NET.Input;
using SkiaSharp;

namespace Raw75.Controls;

/// <summary>
/// Label above, value on the right, Export-quality track: fill bar + vertical handle.
/// </summary>
public class SliderRow : VisualElement
{
    private const float LabelH = 16f;
    private const float ValueW = 44f;
    private const float TrackAreaH = 22f;
    private const float LabelTrackGap = 6f;
    private static readonly TimeSpan DoubleClickWindow = TimeSpan.FromMilliseconds(280);

    private readonly VisualElement _labelEl;
    private readonly VisualElement _valueEl;

    private float _value;
    private readonly float _min;
    private readonly float _max;
    private string _label;
    private readonly string _valueFormat;
    private bool _dragging;
    private bool _hovered;
    private DateTime _lastDownUtc = DateTime.MinValue;

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

    public event Action<float>? Changed;
    public event Action? DragStarted;
    public event Action? DragEnded;

    public SliderRow(string label, float min, float max, string valueFormat = "0")
    {
        Name = $"SliderRow_{label}";
        _label = label ?? "";
        _min = min;
        _max = max <= min ? min + 1f : max;
        _valueFormat = string.IsNullOrEmpty(valueFormat) ? "0" : valueFormat;
        _value = Math.Clamp(0f, _min, _max);

        Transform.Height = Theme.RowH;
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
            Style = LabelStyle(TextAlign.Left)
        };

        _valueEl = new VisualElement
        {
            Name = $"{Name}_Value",
            IsClickthrough = true,
            Style = LabelStyle(TextAlign.Right)
        };

        AddChild(_labelEl);
        AddChild(_valueEl);
        UpdateValueText();

        Events.OnMouseDown += OnTrackDown;
        Events.OnMouseMove += OnTrackMove;
        Events.OnMouseUp += OnTrackUp;
        Events.OnMouseDoubleClick += OnResetClick;
        Events.OnMouseEnter += _ => { _hovered = true; InvalidatePaint(); };
        Events.OnMouseLeave += _ => { _hovered = false; InvalidatePaint(); };
    }

    public override SKSize GetPreferredSize(float maxWidth, float maxHeight)
    {
        float w = maxWidth > 0 ? maxWidth : (Transform.Width > 0 ? Transform.Width : 220f);
        return new SKSize(w, Theme.RowH);
    }

    protected override void LayoutChildren()
    {
        float originX = Transform.Computed.X;
        float originY = Transform.Computed.Y;
        float w = Math.Max(1f, Transform.Width);

        _labelEl.Transform.SetAbsoluteFrame(originX, originY, Math.Max(1f, w - ValueW), LabelH);
        _valueEl.Transform.SetAbsoluteFrame(originX + w - ValueW, originY, ValueW, LabelH);
    }

    protected override void OnAfterStyleDraw(List<DrawCommand> cmds)
    {
        cmds.Add(new DrawCallbackCommand(DrawTrack));
    }

    private void DrawTrack(SKCanvas canvas)
    {
        float w = Transform.Computed.Width;
        float y = LabelH + LabelTrackGap;
        if (w < 2 || TrackAreaH < 2)
            return;

        canvas.Save();
        canvas.Translate(0, y);
        float range = Math.Max(0.0001f, _max - _min);
        float t = Math.Clamp((_value - _min) / range, 0f, 1f);
        bool bipolar = _min < 0f && _max > 0f;
        float zeroT = Math.Clamp((0f - _min) / range, 0f, 1f);
        SliderChrome.Draw(canvas, w, TrackAreaH, t, zeroT, bipolar, _hovered, _dragging);
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
        CapturePointer();
        args.Handled = true;
        DragStarted?.Invoke();
        SetValueFromGlobal(args.Global.X);
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
        SetValueFromGlobal(args.Global.X);
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
        if (was)
            DragEnded?.Invoke();
    }

    private void SetValueFromGlobal(float globalX)
    {
        var local = PointToClient(globalX, 0);
        float w = Math.Max(1f, Transform.Computed.Width);
        float t = Math.Clamp(local.X / w, 0f, 1f);
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

    private static ElementStyle LabelStyle(TextAlign align) => new()
    {
        BackColor = SKColors.Transparent,
        Text = new TextStyle
        {
            Color = align == TextAlign.Right ? Theme.TextDim : Theme.Text,
            Size = 11,
            Weight = 500,
            Alignment = align,
            Padding = 0
        }
    };
}
