using System;
using Blossom.Core;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Silk.NET.Input;
using SkiaSharp;
using SkiaSharp.Extended.Svg;

namespace Raw75.Controls;

/// <summary>
/// Tactile chrome button supporting vector SVG icons, text captions, and hover/press/toggle states.
/// Uses Blossom VisualElement BackgroundSvg and tinting.
/// </summary>
public class IconButton : VisualElement
{
    private string _caption;
    private string? _iconName;
    private VisualElement? _iconElement;
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

    public string Caption
    {
        get => _caption;
        set
        {
            _caption = value ?? "";
            Text = _caption;
            UpdateTextLayout();
            InvalidateLayout();
            InvalidatePaint();
        }
    }

    public string? IconName
    {
        get => _iconName;
        set => SetIcon(value);
    }

    public event Action? Clicked;

    public IconButton(string? caption, bool primary = false)
        : this(caption, iconName: null, primary: primary)
    {
    }

    public IconButton(string? caption, string? iconName, bool primary = false)
    {
        _caption = caption ?? "";
        _primary = primary;
        _iconName = iconName;
        Name = $"IconButton_{_caption}_{_iconName}";
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
            Shadow = new ShadowStyle(0, 1.5f, 2, 2, new SKColor(0, 0, 0, 45)),
            Text = new TextStyle
            {
                Color = Theme.Text,
                Size = 12,
                Weight = 500,
                Alignment = TextAlign.Center,
                Padding = 0
            }
        };

        if (!string.IsNullOrEmpty(iconName))
        {
            SetIcon(iconName);
        }
        else
        {
            UpdateTextLayout();
        }

        Transform.OnChanged += _ => InvalidateLayout();

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
            ApplyChrome();
        };
        Events.OnMouseUp += (_, args) =>
        {
            if (args.Button != (int)MouseButton.Left) return;
            if (_pressed)
            {
                _pressed = false;
                ApplyChrome();
                Clicked?.Invoke();
            }
        };

        ApplyChrome();
    }

    public static IconButton Icon(string iconName, bool primary = false)
        => new(caption: null, iconName: iconName, primary: primary);

    public static IconButton IconWithText(string iconName, string caption, bool primary = false)
        => new(caption: caption, iconName: iconName, primary: primary);

    public void SetIcon(string? iconName)
    {
        _iconName = iconName;
        if (string.IsNullOrEmpty(iconName))
        {
            if (_iconElement != null)
            {
                RemoveChild(_iconElement);
                _iconElement.Dispose();
                _iconElement = null;
            }
            UpdateTextLayout();
            ApplyChrome();
            InvalidateLayout();
            return;
        }

        if (_iconElement == null)
        {
            _iconElement = new VisualElement
            {
                Name = $"{Name}_Icon",
                IsClickthrough = true,
                BackgroundImageScale = ImageScaleMode.Contain,
                BackgroundImageTintBlendMode = SKBlendMode.SrcIn,
                BackgroundImageTintColor = Theme.Text,
                Style = new ElementStyle { BackColor = SKColors.Transparent }
            };
            AddChild(_iconElement);
        }

        _iconElement.BackgroundSvg = IconStore.LoadSvg(iconName);
        UpdateTextLayout();
        ApplyChrome();
        InvalidateLayout();
    }

    private void UpdateTextLayout()
    {
        bool hasIcon = _iconElement != null;
        bool hasText = !string.IsNullOrEmpty(Text);

        if (hasIcon && hasText)
        {
            if (Style?.Text != null)
            {
                Style.Text.Alignment = TextAlign.Left;
            }
            Padding = new Thickness(29f, 0, 8f, 0);
        }
        else
        {
            if (Style?.Text != null)
            {
                Style.Text.Alignment = TextAlign.Center;
            }
            Padding = new Thickness(0);
        }
    }

    protected override void LayoutChildren()
    {
        if (_iconElement == null) return;

        float ox = Transform.Computed.X;
        float oy = Transform.Computed.Y;
        float w = Transform.Computed.Width;
        float h = Transform.Computed.Height;
        bool hasText = !string.IsNullOrEmpty(Text);

        if (hasText)
        {
            float sz = 15f;
            _iconElement.Transform.Width = sz;
            _iconElement.Transform.Height = sz;
            _iconElement.Transform.X = ox + 8f;
            _iconElement.Transform.Y = oy + (h - sz) / 2f;
        }
        else
        {
            float sz = Math.Clamp(Math.Min(w - 6f, h - 6f), 14f, 18f);
            _iconElement.Transform.Width = sz;
            _iconElement.Transform.Height = sz;
            _iconElement.Transform.X = ox + (w - sz) / 2f;
            _iconElement.Transform.Y = oy + (h - sz) / 2f;
        }
    }

    public override SKSize GetPreferredSize(float maxWidth, float maxHeight)
    {
        float h = Theme.ToolH;
        if (maxHeight > 0) h = Math.Min(h, maxHeight);

        if (string.IsNullOrEmpty(Text))
        {
            float w = maxWidth > 0 ? Math.Min(Theme.ToolH, maxWidth) : Theme.ToolH;
            return new SKSize(w, h);
        }

        float iconPad = _iconElement != null ? 24f : 0f;
        float textW = Math.Max(56f, Text.Length * 8f + 18f + iconPad);
        float width = maxWidth > 0 ? Math.Min(textW, maxWidth) : textW;
        return new SKSize(width, h);
    }

    private void ApplyChrome()
    {
        bool accent = _primary || _toggled;
        SKColor fill = accent ? Theme.Accent : Theme.Button;
        SKColor border = accent ? Theme.AccentHover : Theme.Hairline;
        SKColor text = accent ? Theme.ButtonTextOnAccent : Theme.Text;

        if (_pressed)
        {
            fill = Theme.Darken(fill, 24);
            border = Theme.Darken(border, 18);
        }
        else if (_hovered)
        {
            fill = accent ? Theme.Lighten(fill, 16) : Theme.ButtonHover;
            border = accent ? Theme.Lighten(Theme.Accent, 24) : Theme.HairlineStrong;
        }

        Style.BackColor = fill;
        Style.Border.Color = border;
        Style.Text.Color = text;
        Style.Text.Weight = accent ? 600 : 500;

        if (_iconElement != null)
        {
            _iconElement.BackgroundImageTintColor = text;
            _iconElement.InvalidatePaint();
        }

        InvalidatePaint();
    }
}
