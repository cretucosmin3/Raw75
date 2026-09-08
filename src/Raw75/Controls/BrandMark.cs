using System.Collections.Generic;
using Blossom.Core;
using Blossom.Core.Visual;
using Blossom.Utils;
using SkiaSharp;

namespace Raw75.Controls;

/// <summary>In-chrome wordmark: Raw in white, 75 in accent. Text only.</summary>
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
            TextSize = 20,
            Typeface = Fonts.GetTypeface("Roboto", 700),
            IsAntialias = true,
            SubpixelText = true
        };
        using var numPaint = new SKPaint
        {
            Color = Theme.Accent,
            TextSize = 20,
            Typeface = Fonts.GetTypeface("Roboto", 700),
            IsAntialias = true,
            SubpixelText = true
        };

        float y = 20f;
        canvas.DrawText("Raw", 0, y, rawPaint);
        float x = rawPaint.MeasureText("Raw") + 2f;
        canvas.DrawText("75", x, y, numPaint);
    }
}
