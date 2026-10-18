using System;
using Blossom.Animations;
using Blossom.Core;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Blossom.Primitives;
using Blossom.Reactive;
using SkiaSharp;

namespace Raw75.Controls;

public enum ButtonVariant
{
    /// <summary>Raised neutral chrome button.</summary>
    Default,
    /// <summary>Filled accent call to action.</summary>
    Primary,
    /// <summary>No surface until hovered; for dense tool strips and header micro-actions.</summary>
    Ghost,
    /// <summary>Top-level navigation tab: text only, accent underline when active.</summary>
    Tab
}

/// <summary>
/// Chrome button on <see cref="ButtonBase"/>: SVG icon and/or caption, centred as one block,
/// with hover, inset press, and a toggled state. Repaints itself when the theme changes.
/// </summary>
public class IconButton : ButtonBase
{
    private const float UnderlineH = 2f;

    private string _caption;
    private string? _iconName;
    private VisualElement? _iconElement;
    private readonly VisualElement _labelElement;
    private float _hover;
    private ButtonVariant _variant;
    private bool _toggled;
    private float _textOffsetY = 1f;
    private float? _iconSize;
    private SKColor? _iconColor;
    private SKColor? _toggledBackColor;
    private SKColor? _toggledBorderColor;
    private SKColor? _toggledIconColor;
    private float? _toggledBorderWidth;
    private float _contentWidth;

    public ButtonVariant Variant
    {
        get => _variant;
        set
        {
            if (_variant == value) return;
            _variant = value;
            ApplyChrome();
            InvalidateLayout();
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
        get => _variant == ButtonVariant.Primary;
        set => Variant = value ? ButtonVariant.Primary : ButtonVariant.Default;
    }

    /// <summary>Navigation-tab look (text with accent underline when toggled).</summary>
    public bool ShowUnderscore
    {
        get => _variant == ButtonVariant.Tab;
        set => Variant = value ? ButtonVariant.Tab : ButtonVariant.Default;
    }

    /// <summary>Optical vertical nudge for the caption (font ascent headroom).</summary>
    public float TextOffsetY
    {
        get => _textOffsetY;
        set
        {
            if (Math.Abs(_textOffsetY - value) < 0.001f) return;
            _textOffsetY = value;
            InvalidateLayout();
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

    public SKColor? IconColor
    {
        get => _iconColor;
        set { if (_iconColor != value) { _iconColor = value; ApplyChrome(); } }
    }

    public SKColor? ToggledBackColor
    {
        get => _toggledBackColor;
        set { if (_toggledBackColor != value) { _toggledBackColor = value; ApplyChrome(); } }
    }

    public SKColor? ToggledBorderColor
    {
        get => _toggledBorderColor;
        set { if (_toggledBorderColor != value) { _toggledBorderColor = value; ApplyChrome(); } }
    }

    public SKColor? ToggledIconColor
    {
        get => _toggledIconColor;
        set { if (_toggledIconColor != value) { _toggledIconColor = value; ApplyChrome(); } }
    }

    public float? ToggledBorderWidth
    {
        get => _toggledBorderWidth;
        set { if (_toggledBorderWidth != value) { _toggledBorderWidth = value; ApplyChrome(); } }
    }

    public float FontSize
    {
        get => _labelElement.Style.Text.Size;
        set
        {
            _labelElement.Style.Text.Size = value;
            InvalidateLayout();
            InvalidatePaint();
        }
    }

    public float? IconSize
    {
        get => _iconSize;
        set
        {
            if (_iconSize == value) return;
            _iconSize = value;
            InvalidateLayout();
        }
    }

    public TextOverflow TextOverflow
    {
        get => _labelElement.Style.Text.Overflow;
        set
        {
            _labelElement.Style.Text.Overflow = value;
            InvalidateLayout();
        }
    }

    public VisualElement? IconElement => _iconElement;
    public VisualElement LabelElement => _labelElement;

    public IconButton(string? caption, bool primary = false)
        : this(caption, iconName: null, primary: primary)
    {
    }

    public IconButton(string? caption, string? iconName, bool primary = false)
    {
        _caption = caption ?? "";
        _variant = primary ? ButtonVariant.Primary : ButtonVariant.Default;

        // Chrome buttons never take keyboard focus: Raw75's shortcuts are global and a focused
        // button would swallow Space/Enter and gate the workspace hotkeys.
        ReceivesKeyboard = false;

        Name = $"IconButton_{_caption}_{iconName}";
        Transform.Height = Theme.ToolH;
        Style = new ElementStyle
        {
            Border = new BorderStyle { Width = 1, Roundness = Theme.RadiusSm },
            Shadow = new ShadowStyle(0, 1, 1, 1, SKColors.Transparent)
        };

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
            SetIcon(iconName);

        Transform.OnChanged += _ => InvalidateLayout();

        // A button owns its click: stop it bubbling into a clickable parent (cells, headers).
        Events.OnClick += (_, e) => { if (e.Button == PointerButton.Left) e.Handled = true; };

        // Re-runs on theme switch: ApplyChrome reads Theme tokens.
        this.Bind(_ => ApplyChrome());
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
                Style = new ElementStyle { BackColor = SKColors.Transparent }
            };
            AddChild(_iconElement);
        }

        _iconElement.BackgroundSvg = IconStore.LoadSvg(iconName);
        ApplyChrome();
        InvalidateLayout();
    }

    protected override void OnHoverChanged()
    {
        _hover = IsHovered ? 1f : 0f;
        ApplyChrome();
    }

    protected override void OnStateChanged() => ApplyChrome();

    private float MeasureCaption() =>
        _labelElement.Style.Text.SkFont?.MeasureText(_caption) ?? _caption.Length * 7f;

    protected override void LayoutChildren()
    {
        float ox = Transform.Computed.X;
        float oy = Transform.Computed.Y;
        float w = Transform.Computed.Width;
        float h = Transform.Computed.Height;

        bool tab = _variant == ButtonVariant.Tab;
        bool hasIcon = _iconElement != null;
        bool hasText = !string.IsNullOrEmpty(_caption);

        if (hasIcon && hasText)
        {
            float iconSz = _iconSize ?? (tab ? 18f : Math.Clamp(Math.Min(15f, h - 10f), 12f, 15f));
            float gap = tab ? 7f : 6f;
            float textW = MeasureCaption();
            _contentWidth = iconSz + gap + textW;

            // Centre [icon + gap + caption] as one block; hug the left edge when tight.
            float startX = Math.Max(4f, (w - _contentWidth) * 0.5f);
            _iconElement!.Transform.SetAbsoluteFrame(ox + startX, oy + (h - iconSz) * 0.5f, iconSz, iconSz);
            _iconElement.Visible = true;

            float textX = ox + startX + iconSz + gap;
            float textW2 = Math.Max(textW + 2f, ox + w - textX - 2f);
            _labelElement.Transform.SetAbsoluteFrame(textX, oy + _textOffsetY, textW2, h);
            _labelElement.Style.Text.Alignment = TextAlign.Left;
            _labelElement.Visible = true;
        }
        else if (hasIcon)
        {
            float sz = _iconSize ?? (tab ? 20f : Math.Clamp(Math.Min(w - 12f, h - 12f), 14f, 17f));
            _contentWidth = sz;
            _iconElement!.Transform.SetAbsoluteFrame(ox + (w - sz) * 0.5f, oy + (h - sz) * 0.5f, sz, sz);
            _iconElement.Visible = true;
            _labelElement.Visible = false;
        }
        else if (hasText)
        {
            if (_iconElement != null) _iconElement.Visible = false;
            float textW = MeasureCaption();
            _contentWidth = textW;
            float actualW = Math.Max(w, textW + 4f);
            _labelElement.Transform.SetAbsoluteFrame(ox + (w - actualW) * 0.5f, oy + _textOffsetY, actualW, h);
            _labelElement.Style.Text.Alignment = TextAlign.Center;
            _labelElement.Visible = true;
        }
        else
        {
            if (_iconElement != null) _iconElement.Visible = false;
            _labelElement.Visible = false;
            _contentWidth = 0f;
        }
    }

    public override SKSize GetPreferredSize(float maxWidth, float maxHeight)
    {
        float h = Theme.ToolH;
        float w = string.IsNullOrEmpty(_caption)
            ? Theme.ToolH
            : Math.Max(52f, MeasureCaption() + (_iconElement != null ? 15f + 6f : 0f) + 22f);

        if (MinWidth.HasValue) w = Math.Max(w, MinWidth.Value);
        if (MaxWidth.HasValue) w = Math.Min(w, MaxWidth.Value);
        if (MinHeight.HasValue) h = Math.Max(h, MinHeight.Value);
        if (MaxHeight.HasValue) h = Math.Min(h, MaxHeight.Value);
        if (maxWidth > 0) w = Math.Min(w, maxWidth);
        if (maxHeight > 0) h = Math.Min(h, maxHeight);
        return new SKSize(w, h);
    }

    private void ApplyChrome()
    {
        float hover = Enabled ? _hover : 0f;
        bool pressed = IsPressed;
        bool customToggled = _toggled && _toggledBackColor.HasValue;

        SKColor fill, border, ink;
        float borderW = 1f;
        bool shadow = false;
        int weight = 500;

        switch (_variant)
        {
            case ButtonVariant.Primary:
                fill = Anim.Lerp(Theme.Accent, Theme.AccentHover, hover);
                border = Theme.Darken(Theme.AccentPressed, 10);
                ink = Theme.ButtonTextOnAccent;
                weight = 600;
                shadow = !pressed;
                if (pressed) fill = Theme.AccentPressed;
                if (Theme.OutlineAccent)
                {
                    fill = Anim.Lerp(Theme.Button, Theme.ButtonHover, hover);
                    border = Theme.Accent;
                    ink = Theme.Text;
                    borderW = 1.5f;
                }
                break;

            case ButtonVariant.Ghost:
                fill = Anim.Lerp(Theme.Hover.WithAlpha(0), Theme.Hover, hover);
                border = SKColors.Transparent;
                borderW = 0f;
                ink = Anim.Lerp(Theme.TextSecondary, Theme.Text, hover);
                if (_toggled)
                {
                    fill = Theme.AccentSoft;
                    ink = Theme.Accent;
                }
                if (pressed) fill = Theme.Darken(Theme.Hover, 8);
                break;

            case ButtonVariant.Tab:
                fill = SKColors.Transparent;
                border = SKColors.Transparent;
                borderW = 0f;
                ink = _toggled ? Theme.Text : Anim.Lerp(Theme.TextDim, Theme.Text, hover);
                weight = _toggled ? 600 : 500;
                break;

            default:
                if (customToggled)
                {
                    fill = _toggledBackColor!.Value;
                    border = _toggledBorderColor ?? Theme.HairlineStrong;
                    ink = _toggledIconColor ?? _iconColor ?? Theme.Text;
                }
                else if (_toggled)
                {
                    // Selected tool / mode: a lit surface rather than a solid accent slab.
                    fill = Anim.Lerp(Blend(Theme.Button, Theme.Accent, 0.16f), Blend(Theme.ButtonHover, Theme.Accent, 0.2f), hover);
                    border = Theme.Accent.WithAlpha(Theme.OutlineAccent ? (byte)255 : (byte)170);
                    ink = Theme.OutlineAccent ? Theme.Text : Theme.AccentHover;
                    weight = 600;
                }
                else
                {
                    fill = Anim.Lerp(Theme.Button, Theme.ButtonHover, hover);
                    border = Anim.Lerp(Theme.Hairline, Theme.HairlineStrong, hover);
                    ink = Anim.Lerp(Theme.TextSecondary, Theme.Text, Math.Max(hover, 0.35f));
                }
                shadow = !pressed;
                if (pressed) fill = Theme.Darken(fill, 10);
                break;
        }

        if (_toggled && _toggledBorderWidth.HasValue)
            borderW = _toggledBorderWidth.Value;

        if (!Enabled)
        {
            if (_variant is ButtonVariant.Default or ButtonVariant.Primary)
            {
                fill = Theme.Darken(Theme.Button, 6);
                border = Theme.HairlineSubtle;
            }
            ink = Theme.TextDisabled;
            shadow = false;
        }

        Style.BackColor = fill;
        Style.Border.Color = border;
        Style.Border.Width = borderW;
        Style.Border.Roundness = Theme.RadiusSm;
        if (Style.Shadow != null)
        {
            Style.Shadow.OffsetY = 1f;
            Style.Shadow.SpreadX = 1.5f;
            Style.Shadow.SpreadY = 1.5f;
            Style.Shadow.Color = shadow && Theme.UseShadows ? new SKColor(0, 0, 0, (byte)Math.Max(0, Theme.ShadowAlpha - 25)) : SKColors.Transparent;
        }

        var text = _labelElement.Style.Text;
        if (text.Color != ink) text.Color = ink;
        if (text.Weight != weight) text.Weight = weight;

        if (_iconElement != null)
        {
            SKColor iconInk = _toggled && _toggledIconColor.HasValue
                ? _toggledIconColor.Value
                : _iconColor ?? ink;
            _iconElement.BackgroundImageTintColor = iconInk;
        }

        InvalidatePaint();
    }

    protected override void Paint(SKCanvas c)
    {
        float w = W, h = H;
        float r = Theme.RadiusSm;

        if (_variant == ButtonVariant.Tab)
        {
            if (!_toggled) return;
            float barW = Math.Min(w - 12f, Math.Max(18f, _contentWidth + 18f));
            using var bar = new SKPaint { Color = Theme.Accent, IsAntialias = true };
            var rect = SKRect.Create((w - barW) * 0.5f, h - UnderlineH - 1f, barW, UnderlineH);
            c.DrawRoundRect(rect, UnderlineH * 0.5f, UnderlineH * 0.5f, bar);
            return;
        }

        if (_variant == ButtonVariant.Ghost || IsPressed || !Enabled || w < 6f || h < 6f)
            return;

        // Soft top sheen and a one-pixel lip: reads as a machined surface, not a flat slab.
        bool primary = _variant == ButtonVariant.Primary && !Theme.OutlineAccent;
        var body = new SKRoundRect(SKRect.Create(1f, 1f, w - 2f, h - 2f), Math.Max(0f, r - 1f));
        using (var sheen = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(0, h),
                new[] { SKColors.White.WithAlpha(primary ? (byte)34 : (byte)10), SKColors.White.WithAlpha(0) },
                new[] { 0f, 0.6f },
                SKShaderTileMode.Clamp)
        })
        {
            c.DrawRoundRect(body, sheen);
        }

        using var lip = new SKPaint
        {
            Color = SKColors.White.WithAlpha(primary ? (byte)70 : (byte)16),
            IsAntialias = true,
            StrokeWidth = 1f
        };
        float inset = Math.Max(r, 2f);
        c.DrawLine(inset, 1.5f, w - inset, 1.5f, lip);
    }

    private static SKColor Blend(SKColor a, SKColor b, float t) => Anim.Lerp(a, b.WithAlpha(a.Alpha), t);
}
