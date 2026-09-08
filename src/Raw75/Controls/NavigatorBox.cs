using System;
using System.Collections.Generic;
using Blossom.Core;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Silk.NET.Input;
using SkiaSharp;

namespace Raw75.Controls;

/// <summary>Mini contain-fit preview plus Fit / Fill / 1:1 mode buttons.</summary>
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
            BackColor = Theme.Panel,
            Border = new BorderStyle
            {
                Width = 1,
                Color = Theme.Hairline,
                Roundness = Theme.Radius
            }
        };

        _preview = new Preview();
        _preview.ClickedNorm += (nx, ny) => PreviewClicked?.Invoke(nx, ny);

        _fit = MakeMode("Fit");
        _fill = MakeMode("Fill");
        _oneToOne = MakeMode("1:1");
        SetMode("Fit");

        AddChild(_preview);
        AddChild(_fit);
        AddChild(_fill);
        AddChild(_oneToOne);
    }

    public override SKSize GetPreferredSize(float maxWidth, float maxHeight)
    {
        float w = maxWidth > 0 ? maxWidth : (Transform.Width > 0 ? Transform.Width : Theme.LeftW);
        float h = 140f;
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
        float previewH = Math.Max(24f, h - btnH - 6f);

        _preview.Transform.SetAbsoluteFrame(originX + 4, originY + 4, w - 8, previewH - 4);

        float btnY = originY + h - btnH - 4;
        float inner = Math.Max(1f, w - 12);
        float btnW = (inner - 8) / 3f;
        float x = originX + 4;
        _fit.Transform.SetAbsoluteFrame(x, btnY, btnW, btnH);
        x += btnW + 4;
        _fill.Transform.SetAbsoluteFrame(x, btnY, btnW, btnH);
        x += btnW + 4;
        _oneToOne.Transform.SetAbsoluteFrame(x, btnY, btnW, btnH);
    }

    private IconButton MakeMode(string caption)
    {
        var btn = new IconButton(caption);
        btn.Clicked += () => ModePicked?.Invoke(caption);
        return btn;
    }

    private sealed class Preview : VisualElement
    {
        public SKImage? Image { get; set; }
        public event Action<float, float>? ClickedNorm;
        private SKRect _dest;

        public Preview()
        {
            Name = "NavigatorPreview";
            Overflow = OverflowMode.Clip;
            Cursor = StandardCursor.Hand;
            Style = new ElementStyle { BackColor = Theme.PhotoWell };

            Events.OnClick += (_, args) =>
            {
                if (args.Button != (int)MouseButton.Left) return;
                if (Image == null || Image.Handle == IntPtr.Zero) return;
                if (_dest.Width <= 0 || _dest.Height <= 0) return;

                var local = PointToClient(args.Global.X, args.Global.Y);
                if (local.X < _dest.Left || local.X > _dest.Right ||
                    local.Y < _dest.Top || local.Y > _dest.Bottom)
                    return;

                float nx = (local.X - _dest.Left) / _dest.Width;
                float ny = (local.Y - _dest.Top) / _dest.Height;
                ClickedNorm?.Invoke(Math.Clamp(nx, 0f, 1f), Math.Clamp(ny, 0f, 1f));
                args.Handled = true;
            };
        }

        protected override void OnAfterStyleDraw(List<DrawCommand> cmds)
        {
            float w = Transform.Computed.Width;
            float h = Transform.Computed.Height;
            if (Image == null || Image.Handle == IntPtr.Zero || w <= 1 || h <= 1)
            {
                _dest = default;
                return;
            }

            _dest = Contain(w, h, Image.Width, Image.Height);
            cmds.Add(new DrawSkImageCommand(Image, _dest));
        }

        private static SKRect Contain(float boxW, float boxH, int imgW, int imgH)
        {
            if (imgW <= 0 || imgH <= 0) return new SKRect(0, 0, boxW, boxH);
            float scale = Math.Min(boxW / imgW, boxH / imgH);
            float dw = imgW * scale;
            float dh = imgH * scale;
            float x = (boxW - dw) * 0.5f;
            float y = (boxH - dh) * 0.5f;
            return new SKRect(x, y, x + dw, y + dh);
        }
    }
}
