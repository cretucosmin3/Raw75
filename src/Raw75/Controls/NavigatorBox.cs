using System;
using System.Collections.Generic;
using Blossom.Core;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Silk.NET.Input;
using SkiaSharp;

namespace Raw75.Controls;

/// <summary>Mini contain-fit preview plus Fit / Fill / 1:1 mode buttons with shadows and depth.</summary>
public class NavigatorBox : VisualElement
{
    private readonly Preview _preview;
    private readonly IconButton _fit;
    private readonly IconButton _fill;
    private readonly IconButton _oneToOne;
    private SKImage? _image;

    public SKImage? Image
    {
        get => _image;
        set
        {
            _image = value;
            _preview.Image = value;
            _preview.InvalidatePaint();
            InvalidatePaint();
        }
    }

    public event Action<string>? ModePicked;

    public void SetMode(string mode)
    {
        _fit.Toggled = mode == "Fit";
        _fill.Toggled = mode == "Fill";
        _oneToOne.Toggled = mode == "1:1";
    }
    public event Action<float, float>? PreviewClicked;

    public NavigatorBox()
    {
        Name = "NavigatorBox";
        Overflow = OverflowMode.Clip;
        Style = new ElementStyle
        {
            BackColor = Theme.Section,
            Border = new BorderStyle
            {
                Width = 1,
                Color = Theme.Hairline,
                Roundness = Theme.Radius
            },
            Shadow = new ShadowStyle(0, 2.5f, 3, 3, new SKColor(0, 0, 0, 75))
        };

        _preview = new Preview();
        _preview.ClickedNorm += (nx, ny) => PreviewClicked?.Invoke(nx, ny);

        _fit = MakeMode("Fit", "Fit", "fit");
        _fill = MakeMode("Fill", "Fill", "fill");
        _oneToOne = MakeMode("1:1", "1:1", null);
        SetMode("Fit");

        AddChild(_preview);
        AddChild(_fit);
        AddChild(_fill);
        AddChild(_oneToOne);
    }

    private IconButton MakeMode(string caption, string tag, string? iconName = null)
    {
        var b = new IconButton(caption, iconName);
        b.Clicked += () =>
        {
            SetMode(tag);
            ModePicked?.Invoke(tag);
        };
        return b;
    }

    public override SKSize GetPreferredSize(float maxWidth, float maxHeight)
    {
        float w = maxWidth > 0 ? maxWidth : (Transform.Width > 0 ? Transform.Width : Theme.LeftW);
        float h = 168f;
        if (maxHeight > 0) h = Math.Min(h, maxHeight);
        return new SKSize(w, h);
    }

    protected override void LayoutChildren()
    {
        float originX = Transform.Computed.X;
        float originY = Transform.Computed.Y;
        float w = Math.Max(1f, Transform.Width);
        float h = Math.Max(1f, Transform.Height);
        float btnH = Theme.ToolH;
        float previewH = Math.Max(24f, h - btnH - 12f);

        _preview.Transform.SetAbsoluteFrame(originX + 6, originY + 6, w - 12, previewH - 6);

        float btnY = originY + h - btnH - 6;
        float inner = Math.Max(1f, w - 16);
        float btnW = (inner - 8) / 3f;
        float x = originX + 6;
        _fit.Transform.SetAbsoluteFrame(x, btnY, btnW, btnH);
        x += btnW + 4;
        _fill.Transform.SetAbsoluteFrame(x, btnY, btnW, btnH);
        x += btnW + 4;
        _oneToOne.Transform.SetAbsoluteFrame(x, btnY, btnW, btnH);
    }

    private sealed class Preview : VisualElement
    {
        public SKImage? Image;
        public event Action<float, float>? ClickedNorm;

        public Preview()
        {
            Name = "NavigatorBox_Preview";
            Style = new ElementStyle
            {
                BackColor = Theme.Well,
                Border = new BorderStyle
                {
                    Width = 1,
                    Color = Theme.HairlineSubtle,
                    Roundness = Theme.RadiusSm
                }
            };
            Cursor = StandardCursor.Crosshair;
            Events.OnClick += (_, args) =>
            {
                if (args.Button != (int)MouseButton.Left) return;
                var local = PointToClient(args.Global.X, args.Global.Y);
                float w = Transform.Computed.Width;
                float h = Transform.Computed.Height;
                if (w > 0 && h > 0)
                    ClickedNorm?.Invoke(local.X / w, local.Y / h);
                args.Handled = true;
            };
        }

        protected override void OnAfterStyleDraw(List<DrawCommand> cmds)
        {
            cmds.Add(new DrawCallbackCommand(DrawThumb));
        }

        private void DrawThumb(SKCanvas canvas)
        {
            if (Image == null) return;
            float w = Transform.Computed.Width;
            float h = Transform.Computed.Height;
            if (w < 4 || h < 4) return;

            float iw = Image.Width;
            float ih = Image.Height;
            float scale = Math.Min(w / iw, h / ih);
            float dw = iw * scale;
            float dh = ih * scale;
            float dx = (w - dw) * 0.5f;
            float dy = (h - dh) * 0.5f;

            using var paint = new SKPaint { FilterQuality = SKFilterQuality.Low, IsAntialias = true };
            canvas.DrawImage(Image, new SKRect(dx, dy, dx + dw, dy + dh), paint);

            using var frame = new SKPaint
            {
                Color = Theme.HairlineSubtle,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1f,
                IsAntialias = true
            };
            canvas.DrawRect(new SKRect(dx, dy, dx + dw, dy + dh), frame);
        }
    }
}
