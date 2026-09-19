using System;
using System.Reflection;
using Blossom.Core;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Silk.NET.Input;
using SkiaSharp;
using SkiaSharp.Extended.Svg;

namespace Raw75.Controls;

/// <summary>
/// Tactile chrome button supporting vector SVG icons, text captions, and hover/press/toggle states.
/// Automatically balances and centers [Icon + Gap + Text] horizontally within the button bounds.
/// </summary>
public class IconButton : VisualElement
{
    private string _caption;
    private string? _iconName;
    private VisualElement? _iconElement;
    private readonly VisualElement _labelElement;
    private bool _toggled;
    private bool _primary;
    private bool _pressed;
    private bool _hovered;
    private bool _enable3DEffect = true;
    private float _pressScale = 0.95f;

    [BuilderProperty("3D Effect", "Appearance")]
    public bool Enable3DEffect
    {
        get => _enable3DEffect;
        set
        {
            if (_enable3DEffect == value) return;
            _enable3DEffect = value;
            ApplyChrome();
        }
    }

    [BuilderProperty("Press Scale", "Appearance", min: 0.1f, max: 1f, step: 0.05f)]
    public float PressScale
    {
        get => _pressScale;
        set
        {
            if (Math.Abs(_pressScale - value) < 0.001f) return;
            _pressScale = value;
            if (_pressed && _enable3DEffect)
                ApplyChrome();
        }
    }

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

    private float _textOffsetY = 2f;

    /// <summary>
    /// Optical vertical adjustment in pixels to center text inside button pill/border.
    /// Neutralizes font ascent headroom and brings cap-height into visual balance.
    /// </summary>
    public float TextOffsetY
    {
        get => _textOffsetY;
        set
        {
            if (Math.Abs(_textOffsetY - value) < 0.001f) return;
            _textOffsetY = value;
            InvalidateLayout();
            InvalidatePaint();
        }
    }

    public string Caption
    {
        get => _caption;
        set
        {
            var next = value ?? "";
            if (_caption == next) return;
            _caption = next;
            _labelElement.Text = _caption;
            _labelElement.Visible = !string.IsNullOrEmpty(_caption);
            InvalidateLayout();
            InvalidatePaint();
        }
    }

    public new string Text
    {
        get => _caption;
        set => Caption = value;
    }

    public string? IconName
    {
        get => _iconName;
        set => SetIcon(value);
    }

    public float FontSize
    {
        get => _labelElement.Style?.Text?.Size ?? 12f;
        set
        {
            if (_labelElement.Style?.Text != null)
                _labelElement.Style.Text.Size = value;
            if (Style?.Text != null)
                Style.Text.Size = value;
            InvalidateLayout();
            InvalidatePaint();
        }
    }

    public TextOverflow TextOverflow
    {
        get => _labelElement.Style?.Text?.Overflow ?? TextOverflow.Visible;
        set
        {
            if (_labelElement.Style?.Text != null)
                _labelElement.Style.Text.Overflow = value;
            if (Style?.Text != null)
                Style.Text.Overflow = value;
            InvalidateLayout();
            InvalidatePaint();
        }
    }

    public VisualElement? IconElement => _iconElement;
    public VisualElement LabelElement => _labelElement;

    public event Action? Clicked;

    public IconButton(string? caption, bool primary = false, bool enable3DEffect = true)
        : this(caption, iconName: null, primary: primary, enable3DEffect: enable3DEffect)
    {
    }

    public IconButton(string? caption, string? iconName, bool primary = false, bool enable3DEffect = true)
    {
        _caption = caption ?? "";
        _primary = primary;
        _iconName = iconName;

        var attr = GetType().GetCustomAttribute<Enable3DEffectAttribute>();
        _enable3DEffect = attr != null ? attr.Enabled : enable3DEffect;
        if (attr != null)
        {
            _pressScale = attr.Scale;
        }

        Name = $"IconButton_{_caption}_{_iconName}";
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
                Padding = 0,
                Overflow = TextOverflow.Visible
            }
        };
        Theme.ApplyButtonShadow(Style);

        _labelElement = new RichBox
        {
            Name = $"{Name}_Label",
            IsClickthrough = true,
            Interactive = false,
            Text = _caption,
            Visible = !string.IsNullOrEmpty(_caption),
            Overflow = OverflowMode.Visible,
            TextOverflow = TextOverflow.Visible,
            Style = new ElementStyle
            {
                BackColor = SKColors.Transparent,
                Text = new TextStyle
                {
                    Color = Theme.Text,
                    Size = 12,
                    Weight = 500,
                    Alignment = TextAlign.Center,
                    Padding = 0,
                    Overflow = TextOverflow.Visible,
                    MaxLines = 1
                }
            }
        };
        AddChild(_labelElement);

        if (!string.IsNullOrEmpty(iconName))
        {
            SetIcon(iconName);
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
            args.Handled = true;
        };
        Events.OnMouseUp += (_, args) =>
        {
            if (args.Button != (int)MouseButton.Left) return;
            if (_pressed)
            {
                _pressed = false;
                ApplyChrome();
                Clicked?.Invoke();
                args.Handled = true;
            }
        };

        ApplyChrome();
    }

    public static IconButton Icon(string iconName, bool primary = false, bool enable3DEffect = true)
        => new(caption: null, iconName: iconName, primary: primary, enable3DEffect: enable3DEffect);

    public static IconButton IconWithText(string iconName, string caption, bool primary = false, bool enable3DEffect = true)
        => new(caption: caption, iconName: iconName, primary: primary, enable3DEffect: enable3DEffect);

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
                Interactive = false,
                BackgroundImageScale = ImageScaleMode.Contain,
                BackgroundImageTintBlendMode = SKBlendMode.SrcIn,
                BackgroundImageTintColor = Theme.Text,
                Style = new ElementStyle { BackColor = SKColors.Transparent }
            };
            AddChild(_iconElement);
        }

        _iconElement.BackgroundSvg = IconStore.LoadSvg(iconName);
        ApplyChrome();
        InvalidateLayout();
    }

    protected override void LayoutChildren()
    {
        float ox = Transform.Computed.X;
        float oy = Transform.Computed.Y;
        float w = Transform.Computed.Width;
        float h = Transform.Computed.Height;

        bool hasIcon = _iconElement != null;
        bool hasText = !string.IsNullOrEmpty(_caption);

        if (hasIcon && hasText)
        {
            float iconSz = Math.Clamp(Math.Min(15f, h - 8f), 12f, 16f);
            float iconGap = 5f;
            float textW = _labelElement.Style?.Text?.Paint?.MeasureText(_caption) ?? (_caption.Length * 7.5f);
            float totalW = iconSz + iconGap + textW;

            // Smart centering: center [Icon + Gap + Text] as a unified block within the button width.
            // When button width is tight, allow hugging closer to left margin so the icon and text fit comfortably.
            float startX = (w > totalW + 8f)
                ? (w - totalW) * 0.5f
                : Math.Max(3f, (w - totalW) * 0.5f);

            _iconElement!.Transform.SetAbsoluteFrame(ox + startX, oy + (h - iconSz) * 0.5f, iconSz, iconSz);
            _iconElement.Visible = true;

            float textX = ox + startX + iconSz + iconGap;
            float maxTextW = Math.Max(0f, ox + w - textX - 2f);
            float actualTextW = Math.Max(textW + 2f, maxTextW);

            _labelElement.Transform.SetAbsoluteFrame(textX, oy + _textOffsetY, actualTextW, h);
            if (_labelElement.Style?.Text != null)
            {
                _labelElement.Style.Text.Alignment = TextAlign.Left;
            }
            _labelElement.Visible = true;
        }
        else if (hasIcon)
        {
            float sz = Math.Clamp(Math.Min(w - 6f, h - 6f), 14f, 18f);
            _iconElement!.Transform.SetAbsoluteFrame(ox + (w - sz) * 0.5f, oy + (h - sz) * 0.5f, sz, sz);
            _iconElement.Visible = true;

            _labelElement.Visible = false;
        }
        else if (hasText)
        {
            if (_iconElement != null)
            {
                _iconElement.Visible = false;
            }

            float textW = _labelElement.Style?.Text?.Paint?.MeasureText(_caption) ?? (_caption.Length * 7.5f);
            float actualW = Math.Max(w, textW + 4f);
            float textOx = (w >= actualW) ? ox : ox + (w - actualW) * 0.5f;

            _labelElement.Transform.SetAbsoluteFrame(textOx, oy + _textOffsetY, actualW, h);
            if (_labelElement.Style?.Text != null)
            {
                _labelElement.Style.Text.Alignment = TextAlign.Center;
            }
            _labelElement.Visible = true;
        }
        else
        {
            if (_iconElement != null) _iconElement.Visible = false;
            _labelElement.Visible = false;
        }
    }

    public override SKSize GetPreferredSize(float maxWidth, float maxHeight)
    {
        float h = Theme.ToolH;
        if (maxHeight > 0) h = Math.Min(h, maxHeight);

        if (string.IsNullOrEmpty(_caption))
        {
            float w = maxWidth > 0 ? Math.Min(Theme.ToolH, maxWidth) : Theme.ToolH;
            return new SKSize(w, h);
        }

        float textW = _labelElement.Style?.Text?.Paint?.MeasureText(_caption) ?? (_caption.Length * 7.5f);
        float iconPad = _iconElement != null ? (15f + 6f) : 0f;
        float preferredW = Math.Max(52f, textW + iconPad + 18f);
        float width = maxWidth > 0 ? Math.Min(preferredW, maxWidth) : preferredW;
        return new SKSize(width, h);
    }

    private void ApplyChrome()
    {
        bool accent = _primary || _toggled;
        bool outline = Theme.OutlineAccent && accent;
        SKColor fill;
        SKColor border;
        SKColor text;

        if (outline)
        {
            fill = Theme.Button;
            border = Theme.Accent;
            text = Theme.Text;
        }
        else if (accent)
        {
            fill = Theme.Accent;
            border = Theme.AccentHover;
            text = Theme.ButtonTextOnAccent;
        }
        else
        {
            fill = Theme.Button;
            border = Theme.HairlineStrong;
            text = Theme.Text;
        }

        if (_pressed)
        {
            fill = Theme.Darken(fill, 24);
            border = Theme.Darken(border, 18);
        }
        else if (_hovered)
        {
            fill = outline ? Theme.ButtonHover : (accent ? Theme.Lighten(fill, 16) : Theme.ButtonHover);
            border = outline ? Theme.AccentHover : (accent ? Theme.Lighten(Theme.Accent, 24) : Theme.HairlineStrong);
        }

        float borderW = outline ? 2f : 1f;
        bool fillChanged = Style.BackColor != fill;
        bool borderChanged = Style.Border.Color != border || Math.Abs(Style.Border.Width - borderW) > 0.01f;
        Style.BackColor = fill;
        Style.Border.Color = border;
        Style.Border.Width = borderW;

        bool textChanged = false;
        if (_labelElement.Style?.Text != null)
        {
            if (Style?.Text != null && Math.Abs(_labelElement.Style.Text.Size - Style.Text.Size) > 0.1f)
            {
                _labelElement.Style.Text.Size = Style.Text.Size;
            }

            textChanged = _labelElement.Style.Text.Color != text;
            int weight = accent ? 600 : 500;
            if (_labelElement.Style.Text.Weight != weight)
                _labelElement.Style.Text.Weight = weight;
            if (textChanged)
                _labelElement.Style.Text.Color = text;
        }

        bool iconChanged = false;
        if (_iconElement != null && _iconElement.BackgroundImageTintColor != text)
        {
            _iconElement.BackgroundImageTintColor = text;
            iconChanged = true;
        }

        float targetScale = (_enable3DEffect && _pressed) ? _pressScale : 1f;
        bool scaleChanged = Math.Abs(Transform.ScaleX - targetScale) > 0.001f || Math.Abs(Transform.ScaleY - targetScale) > 0.001f;
        if (scaleChanged)
        {
            Transform.ScaleX = targetScale;
            Transform.ScaleY = targetScale;
        }

        if (fillChanged || borderChanged || textChanged || iconChanged || scaleChanged)
            InvalidatePaint();
    }

    public void RefreshTheme()
    {
        if (Style?.Border != null)
            Style.Border.Roundness = Theme.RadiusSm;
        Theme.ApplyButtonShadow(Style);
        ApplyChrome();
    }
}
