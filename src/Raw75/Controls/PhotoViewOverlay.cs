using System;
using Blossom.Core;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Blossom.Primitives;
using Blossom.Reactive;
using SkiaSharp;

namespace Raw75.Controls;

/// <summary>
/// Floating tool stack in the top-right corner of the photo view: Info, Before, Split and Clip
/// as ghost toggles on a near-opaque window surface.
/// </summary>
public class PhotoViewOverlay : Stack
{
    private const float Inset = 5f;
    private const float ItemGap = 4f;
    private const float BtnH = 28f;
    // No backdrop blur: a hovered button repaints only its own rect, so a blur would sample the
    // stack's previous pixels there and smear; it also forces continuous repaints.
    private const float SurfaceAlpha = 0.92f;

    public IconButton BtnInfo { get; }
    public IconButton BtnBefore { get; }
    public IconButton BtnSplit { get; }
    public IconButton BtnClip { get; }

    public PhotoViewOverlay()
    {
        Name = "PhotoViewOverlay";
        Orientation = Orientation.Vertical;
        Gap = ItemGap;
        Padding = new Thickness(Inset);
        Style = new ElementStyle
        {
            Border = new BorderStyle { Width = 1 },
            Shadow = new ShadowStyle(0, 2.5f, 4, 4, SKColors.Transparent)
        };

        BtnInfo = Tool("Info", "info");
        BtnBefore = Tool("Before", "before");
        BtnSplit = Tool("Split", "split");
        BtnClip = Tool("Clip", "clipping");

        // Swallow presses on the stack's padding so they never reach the photo pane underneath.
        Events.OnMouseDown += (_, args) => args.Handled = true;
        Events.OnMouseUp += (_, args) => args.Handled = true;

        this.Bind(_ => ApplyTheme());
    }

    private IconButton Tool(string caption, string icon)
    {
        var btn = new IconButton(caption, icon)
        {
            Variant = ButtonVariant.Ghost,
            IconSize = 14f,
            FontSize = 11.5f
        };
        btn.MinHeight = BtnH;
        AddChild(btn);
        SetGrow(btn, 1f);
        return btn;
    }

    private void ApplyTheme()
    {
        Style.BackColor = Theme.Window.WithAlpha((byte)(255 * SurfaceAlpha));
        Style.Border.Color = Theme.Hairline;
        Style.Border.Roundness = Theme.Radius;
        Theme.ApplyCardShadow(Style);
        InvalidatePaint();
    }

    public override SKSize GetPreferredSize(float maxWidth, float maxHeight)
    {
        float w = 88f;
        float h = Inset * 2f + BtnH * 4f + ItemGap * 3f;
        return new SKSize(w, h);
    }
}
