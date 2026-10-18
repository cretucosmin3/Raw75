using System;
using Blossom.Primitives;
using Blossom.Reactive;
using SkiaSharp;

namespace Raw75.Controls;

/// <summary>
/// Hairline separator that turns into a thin accent progress bar while <c>active</c> is set
/// (dialog footers: HDR merge, updates). Repaints when either signal changes.
/// </summary>
internal sealed class ProgressLine : Control
{
    private const float LineH = 2f;
    private readonly Signal<float> _fraction;
    private readonly Signal<bool> _active;

    public ProgressLine(Signal<float> fraction, Signal<bool> active)
    {
        _fraction = fraction;
        _active = active;
        Name = "ProgressLine";
        IsClickthrough = true;
        Transform.Height = LineH;
        Style.BackColor = SKColors.Transparent;
        this.Bind(line =>
        {
            Theme.Observe();
            _ = fraction.Value;
            _ = active.Value;
            line.InvalidatePaint();
        });
    }

    public override SKSize GetPreferredSize(float maxWidth, float maxHeight) => new(maxWidth, LineH);

    protected override void Paint(SKCanvas c)
    {
        if (!_active.Peek())
        {
            using var hairline = new SKPaint { Color = Theme.HairlineSubtle };
            c.DrawRect(0, 0, W, 1f, hairline);
            return;
        }

        using (var track = new SKPaint { Color = Theme.Well })
            c.DrawRect(0, 0, W, H, track);
        using var fill = new SKPaint { Color = Theme.Accent };
        c.DrawRect(0, 0, W * Math.Clamp(_fraction.Peek(), 0f, 1f), H, fill);
    }
}
