using System;
using System.Collections.Generic;
using Blossom.Core;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Silk.NET.Input;
using SkiaSharp;

namespace Raw75.Controls;

/// <summary>
/// Minimal, high-precision checkbox toggle (no text).
/// Sits on the left side of section panel headers to enable/disable module adjustments.
/// </summary>
public sealed class CheckToggle : VisualElement
{
    private bool _checked = true;
    private bool _hovered;

    public event Action<bool>? CheckedChanged;

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            InvalidatePaint();
            CheckedChanged?.Invoke(_checked);
        }
    }

    public CheckToggle(bool initialChecked = true)
    {
        Name = "CheckToggle";
        _checked = initialChecked;
        Cursor = StandardCursor.Hand;
        Style = new ElementStyle
        {
            BackColor = SKColors.Transparent,
            Border = new BorderStyle { Width = 0 }
        };

        Events.OnMouseEnter += _ =>
        {
            _hovered = true;
            InvalidatePaint();
        };

        Events.OnMouseLeave += _ =>
        {
            _hovered = false;
            InvalidatePaint();
        };

        Events.OnClick += (_, args) =>
        {
            if (args.Button != (int)MouseButton.Left) return;
            Checked = !Checked;
            args.Handled = true; // Crucial: prevent parent header collapse/expand
        };
    }

    public override SKSize GetPreferredSize(float maxWidth, float maxHeight) => new(14f, 14f);

    protected override void OnAfterStyleDraw(List<DrawCommand> cmds)
    {
        cmds.Add(new DrawCallbackCommand(DrawCheck));
    }

    private void DrawCheck(SKCanvas canvas)
    {
        float w = Transform.Computed.Width;
        float h = Transform.Computed.Height;
        if (w < 4f || h < 4f) return;

        var box = new SKRect(0.5f, 0.5f, w - 0.5f, h - 0.5f);
        float radius = 3f;

        if (_checked)
        {
            // Checked: filled amber pill box
            using var bgPaint = new SKPaint
            {
                Color = _hovered ? Theme.AccentHover : Theme.Accent,
                Style = SKPaintStyle.Fill,
                IsAntialias = true
            };
            canvas.DrawRoundRect(box, radius, radius, bgPaint);

            // Vector checkmark
            using var checkPaint = new SKPaint
            {
                Color = SKColors.White,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.7f,
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round,
                IsAntialias = true
            };

            using var path = new SKPath();
            path.MoveTo(w * 0.24f, h * 0.52f);
            path.LineTo(w * 0.44f, h * 0.74f);
            path.LineTo(w * 0.78f, h * 0.28f);
            canvas.DrawPath(path, checkPaint);
        }
        else
        {
            // Unchecked: dark recessed well with subtle boundary
            using var bgPaint = new SKPaint
            {
                Color = _hovered ? Theme.WellHover : Theme.Well,
                Style = SKPaintStyle.Fill,
                IsAntialias = true
            };
            canvas.DrawRoundRect(box, radius, radius, bgPaint);

            using var borderPaint = new SKPaint
            {
                Color = _hovered ? Theme.HairlineStrong : Theme.Hairline,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1f,
                IsAntialias = true
            };
            canvas.DrawRoundRect(box, radius, radius, borderPaint);
        }
    }
}
