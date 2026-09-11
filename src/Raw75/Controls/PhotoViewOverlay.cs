using System;
using Blossom.Core;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using SkiaSharp;

namespace Raw75.Controls;

/// <summary>
/// Floating vertical toolbar overlay situated on the top-right corner of the active photo view.
/// Houses tactile controls for Before/After toggle, Split view, and Clipping warnings.
/// </summary>
public class PhotoViewOverlay : VisualElement
{
    private const float Pad = 5f;
    private const float Gap = 4f;
    private const float BtnH = 28f;

    private readonly IconButton _btnInfo;
    private readonly IconButton _btnBefore;
    private readonly IconButton _btnSplit;
    private readonly IconButton _btnClip;

    public IconButton BtnInfo => _btnInfo;
    public IconButton BtnBefore => _btnBefore;
    public IconButton BtnSplit => _btnSplit;
    public IconButton BtnClip => _btnClip;

    public PhotoViewOverlay()
    {
        Name = "PhotoViewOverlay";
        Style = new ElementStyle
        {
            BackColor = new SKColor(20, 20, 24, 191),
            Border = new BorderStyle
            {
                Width = 1,
                Color = Theme.Hairline,
                Roundness = Theme.Radius
            },
            Shadow = new ShadowStyle(0, 2.5f, 4, 4, new SKColor(0, 0, 0, 95))
        };

        _btnInfo = new IconButton("Info", "info");
        _btnBefore = new IconButton("Before", "before");
        _btnSplit = new IconButton("Split", "split");
        _btnClip = new IconButton("Clip", "clipping");

        AddChild(_btnInfo);
        AddChild(_btnBefore);
        AddChild(_btnSplit);
        AddChild(_btnClip);

        Events.OnMouseDown += (_, args) => args.Handled = true;
        Events.OnMouseUp += (_, args) => args.Handled = true;
    }

    public override SKSize GetPreferredSize(float maxWidth, float maxHeight)
    {
        float w = 88f;
        float h = Pad * 2f + BtnH * 4f + Gap * 3f;
        return new SKSize(w, h);
    }

    protected override void LayoutChildren()
    {
        float ox = Transform.Computed.X;
        float oy = Transform.Computed.Y;
        float w = Transform.Computed.Width;
        float btnW = Math.Max(0f, w - Pad * 2f);

        float curY = oy + Pad;
        _btnInfo.Transform.SetAbsoluteFrame(ox + Pad, curY, btnW, BtnH);
        curY += BtnH + Gap;
        _btnBefore.Transform.SetAbsoluteFrame(ox + Pad, curY, btnW, BtnH);
        curY += BtnH + Gap;
        _btnSplit.Transform.SetAbsoluteFrame(ox + Pad, curY, btnW, BtnH);
        curY += BtnH + Gap;
        _btnClip.Transform.SetAbsoluteFrame(ox + Pad, curY, btnW, BtnH);
    }
}
