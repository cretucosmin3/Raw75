using System;
using Blossom.Primitives;
using Blossom.Reactive;
using SkiaSharp;

namespace Raw75.Controls;

/// <summary>
/// Compact checkbox (no text) on <see cref="ToggleBase"/>. Sits on the left of section headers
/// to enable or bypass a module: amber when on, a recessed well when off.
/// </summary>
public sealed class CheckToggle : ToggleBase
{
    private bool _hovered;

    public CheckToggle(bool initialChecked = true) : base(initialChecked)
    {
        Name = "CheckToggle";
        ReceivesKeyboard = false;
        Style.BackColor = SKColors.Transparent;

        // The header behind the box toggles collapse on click; the box keeps its click.
        Events.OnClick += (_, e) => { if (e.Button == PointerButton.Left) e.Handled = true; };

        this.Bind(_ => { Theme.Observe(); InvalidatePaint(); });
    }

    /// <summary>Alias of <see cref="ToggleBase.IsChecked"/>.</summary>
    public bool Checked
    {
        get => IsChecked;
        set => IsChecked = value;
    }

    protected override void OnHoverChanged()
    {
        _hovered = IsHovered;
        InvalidatePaint();
    }

    public override SKSize GetPreferredSize(float maxWidth, float maxHeight) => new(16f, 16f);

    protected override void Paint(SKCanvas canvas)
    {
        float w = W, h = H;
        if (w < 4f || h < 4f) return;

        bool on = IsChecked;
        float r = Math.Min(Theme.RadiusSm, 4f);
        var box = new SKRect(0.5f, 0.5f, w - 0.5f, h - 0.5f);
        bool outline = Theme.OutlineAccent;

        SKColor fillColor = on && !outline
            ? (_hovered ? Theme.AccentHover : Theme.Accent)
            : (_hovered ? Theme.WellHover : Theme.Well);
        using (var fill = new SKPaint { Color = fillColor, IsAntialias = true })
            canvas.DrawRoundRect(box, r, r, fill);

        SKColor edge = on
            ? (outline ? Theme.Accent : Theme.AccentPressed)
            : (_hovered ? Theme.HairlineStrong : Theme.Hairline);
        using (var stroke = new SKPaint { Color = edge, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = true })
            canvas.DrawRoundRect(box, r, r, stroke);

        if (!on) return;

        using var path = new SKPath();
        path.MoveTo(w * 0.27f, h * 0.53f);
        path.LineTo(w * 0.44f, h * 0.70f);
        path.LineTo(w * 0.75f, h * 0.33f);
        using var check = new SKPaint
        {
            Color = outline ? Theme.Accent : Theme.ButtonTextOnAccent,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.8f,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            IsAntialias = true
        };
        canvas.DrawPath(path, check);
    }
}
