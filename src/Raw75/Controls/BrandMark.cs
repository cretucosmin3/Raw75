using Blossom.Core.Visual;
using Blossom.Primitives;
using Blossom.Reactive;
using SkiaSharp;

namespace Raw75.Controls;

/// <summary>In-chrome wordmark: RAW in the text colour, 75 in accent, cap-height centred. Text only.</summary>
public sealed class BrandMark : Control
{
    private const float FontSize = 22f;
    private const float Kerning = 1f;

    public BrandMark()
    {
        Name = "BrandMark";
        IsClickthrough = true;
        Style = new ElementStyle { BackColor = SKColors.Transparent };
        this.Bind(_ => { Theme.Observe(); InvalidatePaint(); });
    }

    protected override void Paint(SKCanvas canvas)
    {
        using var ink = new TextPaint
        {
            Color = Theme.Text,
            TextSize = FontSize,
            Typeface = Theme.GetTypeface(700),
            HintingLevel = SKFontHinting.Normal
        };

        float baseline = Blossom.Utils.Text.Baseline(ink.Font, H * 0.5f);
        canvas.DrawText("RAW", 0f, baseline, ink);
        float x = ink.MeasureText("RAW") + Kerning;
        ink.Color = Theme.Accent;
        canvas.DrawText("75", x, baseline, ink);
    }
}
