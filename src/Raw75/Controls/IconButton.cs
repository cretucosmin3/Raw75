using System;
using Blossom.Core.Visual;
using Silk.NET.Input;
using SkiaSharp;

namespace Raw75.Controls;

/// <summary>Shared chrome: hover, press, optional checked (toggle) and primary (accent) fill.</summary>
public class IconButton : VisualElement
{
    private readonly string _caption;
    private bool _toggled;
    private bool _primary;
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

    public bool Primary
    {
        get => _primary;
        set
        {
            if (_primary == value) return;
            _primary = value;
            ApplyChrome();
        }
    }

    public event Action? Clicked;

    public IconButton(string caption, bool primary = false)
    {
        _caption = caption ?? "";
        _primary = primary;
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
                Weight = 600,
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
        bool accent = _primary || _toggled;
        SKColor fill = accent ? Theme.Accent : Theme.Button;
        SKColor border = accent ? Theme.Accent : Theme.Hairline;
        SKColor text = accent ? Theme.ButtonTextOnAccent : Theme.Text;

        if (_pressed)
        {
            fill = Theme.Darken(fill, 28);
            border = Theme.Darken(border, 20);
        }
        else if (_hovered)
        {
            fill = Theme.Lighten(fill, accent ? 18 : 22);
            border = accent ? Theme.Lighten(Theme.Accent, 24) : Theme.Lighten(Theme.Hairline, 40);
        }

        Style.BackColor = fill;
        Style.Border.Color = border;
        Style.Text.Color = text;
        InvalidatePaint();
    }
}
