using System.Collections.Generic;
using Blossom.Core;
using Blossom.Core.Visual;
using Blossom.Utils;
using SkiaSharp;

namespace Raw75.Controls;

/// <summary>In-chrome wordmark: RAW in white, 75 in accent. Text only.</summary>
public sealed class BrandMark : VisualElement
{
    public BrandMark()
    {
        Name = "BrandMark";
        IsClickthrough = true;
        Style = new ElementStyle { BackColor = SKColors.Transparent };
    }

    protected override void OnAfterStyleDraw(List<DrawCommand> cmds)
    {
        cmds.Add(new DrawCallbackCommand(Draw));
    }

    private static void Draw(SKCanvas canvas)
    {
        using var rawPaint = new SKPaint
        {
            Color = Theme.Text,
            TextSize = 22,
            Typeface = Fonts.GetTypeface("Liberation Sans, Noto Sans", 700),
            IsAntialias = true,
            SubpixelText = true,
            LcdRenderText = true,
            HintingLevel = SKPaintHinting.Normal
        };
        using var numPaint = new SKPaint
        {
            Color = Theme.Accent,
            TextSize = 22,
            Typeface = Fonts.GetTypeface("Liberation Sans, Noto Sans", 700),
            IsAntialias = true,
            SubpixelText = true,
            LcdRenderText = true,
            HintingLevel = SKPaintHinting.Normal
        };

        float y = 22f;
        canvas.DrawText("RAW", 0, y, rawPaint);
        float x = rawPaint.MeasureText("RAW") + 1f;
        canvas.DrawText("75", x, y, numPaint);
    }
}
