using System;
using Blossom.Core.Visual;
using Silk.NET.Input;
using SkiaSharp;

namespace Raw75.Controls;

/// <summary>Dark tool-strip button; Accent fill when <see cref="Toggled"/>.</summary>
public class IconButton : VisualElement
{
    private readonly string _caption;
    private bool _toggled;
    private bool _pressed;
    private bool _hovered;

    public bool Toggled
    {
        get => _toggled;
        set
        {
            if (_toggled == value) return;
            _toggled = value;
            ApplyChrome();
        }
    }

    public event Action? Clicked;

    public IconButton(string caption)
    {
        _caption = caption ?? "";
        Name = $"IconButton_{_caption}";
        Text = _caption;
        Cursor = StandardCursor.Hand;
        Transform.Height = Theme.ToolH;
        Style = new ElementStyle
        {
            Border = new BorderStyle
            {
                Width = 1,
                Color = Theme.Hairline,
                Roundness = Theme.RadiusSm
            },
            Text = new TextStyle
            {
                Color = Theme.Text,
                Size = 12,
                Weight = 500,
                Alignment = TextAlign.Center,
                Padding = 0
            }
        };

        Events.OnMouseEnter += _ =>
        {
            _hovered = true;
            ApplyChrome();
        };
        Events.OnMouseLeave += _ =>
        {
            _hovered = false;
            _pressed = false;
            ApplyChrome();
        };
        Events.OnMouseDown += (_, args) =>
        {
            if (args.Button != (int)MouseButton.Left) return;
            _pressed = true;
            args.Handled = true;
            ApplyChrome();
        };
        Events.OnMouseUp += (_, args) =>
        {
            if (args.Button != (int)MouseButton.Left) return;
            _pressed = false;
            ApplyChrome();
        };
        Events.OnClick += (_, args) =>
        {
            if (args.Button != (int)MouseButton.Left) return;
            Clicked?.Invoke();
            args.Handled = true;
        };

        ApplyChrome();
    }

    public override SKSize GetPreferredSize(float maxWidth, float maxHeight)
    {
        float textW = Math.Max(56f, _caption.Length * 7.2f + 16f);
        float w = maxWidth > 0 ? Math.Min(textW, maxWidth) : textW;
        float h = Theme.ToolH;
        if (maxHeight > 0) h = Math.Min(h, maxHeight);
        return new SKSize(w, h);
    }

    private void ApplyChrome()
    {
        SKColor fill = _toggled ? Theme.Accent : Theme.Button;
        if (_pressed)
            fill = Darken(fill, 20);
        else if (_hovered && !_toggled)
            fill = Lighten(fill, 16);

        Style.BackColor = fill;
        Style.Text.Color = _toggled ? new SKColor(250, 245, 238) : Theme.Text;
        InvalidatePaint();
    }

    private static SKColor Lighten(SKColor c, int d) => new(
        (byte)Math.Min(255, c.Red + d),
        (byte)Math.Min(255, c.Green + d),
        (byte)Math.Min(255, c.Blue + d),
        c.Alpha);

    private static SKColor Darken(SKColor c, int d) => new(
        (byte)Math.Max(0, c.Red - d),
        (byte)Math.Max(0, c.Green - d),
        (byte)Math.Max(0, c.Blue - d),
        c.Alpha);
}
