using System;
using Blossom.Animations;
using Blossom.Core;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Blossom.Hosting;
using Blossom.Primitives;
using Blossom.Reactive;
using SkiaSharp;

namespace Raw75.Controls;

/// <summary>
/// Collapsible section card. A vertical <see cref="Stack"/> of header row, hairline divider and body.
/// The header carries the module bypass checkbox, a disclosure caret, the uppercase title and
/// ghost micro-actions (Auto, Reset, Presets). A bypassed module dims its body.
/// </summary>
public class PanelGroup : Stack
{
    private const float BodyDimmed = 0.45f;

    private readonly Stack _header;
    private readonly VisualElement _divider;
    private readonly Stack _body;
    private readonly CheckToggle _toggle;
    private readonly VisualElement _chevronEl;
    private readonly VisualElement _titleEl;
    private float _headerHover;
    private bool _expanded = true;
    private bool _sectionEnabled = true;

    public event Action<bool>? ExpandedChanged;
    public event Action<bool>? EnabledChanged;

    public bool SectionEnabled
    {
        get => _sectionEnabled;
        set
        {
            if (_sectionEnabled == value) return;
            _sectionEnabled = value;
            _toggle.Checked = value;
            ApplyEnabledVisuals();
        }
    }

    public bool Expanded
    {
        get => _expanded;
        set
        {
            if (_expanded == value) return;
            _expanded = value;
            ApplyExpanded();
            ExpandedChanged?.Invoke(_expanded);
        }
    }

    /// <summary>The body host; rows added with <see cref="AddBody"/> stack here.</summary>
    public Stack Body => _body;

    public PanelGroup(string title)
    {
        Name = $"PanelGroup_{title}";
        Orientation = Orientation.Vertical;
        Gap = 0f;
        Padding = new Thickness(1f);
        Overflow = OverflowMode.Clip;
        Style = new ElementStyle
        {
            Border = new BorderStyle { Width = 1 },
            Shadow = new ShadowStyle(0, 2f, 3, 3, SKColors.Transparent)
        };

        _header = new Stack
        {
            Name = $"{Name}_Header",
            Orientation = Orientation.Horizontal,
            Align = LayoutAlign.Center,
            Gap = 6f,
            Padding = new Thickness(9, 0, 6, 0),
            Cursor = UiCursor.Hand,
            Style = new ElementStyle { Border = new BorderStyle { Width = 0 } }
        };
        _header.MinHeight = Theme.GroupHeadH - 2f;

        _toggle = new CheckToggle(initialChecked: true);
        _toggle.Transform.Width = 16f;
        _toggle.Transform.Height = 16f;
        _toggle.CheckedChanged += isChecked =>
        {
            _sectionEnabled = isChecked;
            ApplyEnabledVisuals();
            EnabledChanged?.Invoke(_sectionEnabled);
        };
        _header.AddChild(_toggle);

        _chevronEl = new VisualElement
        {
            Name = $"{Name}_Chevron",
            IsClickthrough = true,
            BackgroundImageScale = ImageScaleMode.Contain,
            BackgroundImageTintBlendMode = SKBlendMode.SrcIn,
        };
        _chevronEl.Transform.Width = 11f;
        _chevronEl.Transform.Height = 11f;
        // A fixed glyph: never let the header stack measure it from a collapsed first frame.
        _chevronEl.MinWidth = _chevronEl.MaxWidth = 11f;
        _chevronEl.MinHeight = _chevronEl.MaxHeight = 11f;
        _header.AddChild(_chevronEl);

        _titleEl = new RichBox
        {
            Name = $"{Name}_Title",
            IsClickthrough = true,
            Text = (title ?? "").ToUpperInvariant(),
            Overflow = OverflowMode.Visible,
            TextOverflow = TextOverflow.Ellipsis,
            Style = new ElementStyle
            {
                BackColor = SKColors.Transparent,
                Text = new TextStyle
                {
                    Size = 11f,
                    Weight = 600,
                    Spacing = 1,
                    Alignment = TextAlign.Left,
                    Padding = 0,
                    Overflow = TextOverflow.Ellipsis,
                    MaxLines = 1
                }
            }
        };
        _titleEl.Transform.Height = 18f;
        _header.AddChild(_titleEl);
        SetGrow(_titleEl, 1f);
        _header.Events.OnMouseEnter += _ => { _headerHover = 1f; ApplyHeaderChrome(); };
        _header.Events.OnMouseLeave += _ => { _headerHover = 0f; ApplyHeaderChrome(); };
        _header.Events.OnClick += (_, args) =>
        {
            if (args.Button != PointerButton.Left) return;
            Expanded = !Expanded;
            args.Handled = true;
        };
        AddChild(_header);

        _divider = new VisualElement { Name = $"{Name}_Divider", IsClickthrough = true };
        _divider.Transform.Height = 1f;
        AddChild(_divider);

        _body = new Stack
        {
            Name = $"{Name}_Body",
            Orientation = Orientation.Vertical,
            Gap = Theme.SliderGap,
            Padding = new Thickness(10, 8, 10, 10),
            Style = new ElementStyle { BackColor = SKColors.Transparent }
        };
        AddChild(_body);

        ApplyExpanded();
        this.Bind(_ => ApplyTheme());
    }

    public void EnableAuto(Action onAuto, string tooltip = "Auto Adjust") =>
        AddHeaderAction("A", tooltip, onAuto, isAccentHover: true);

    public void EnableReset(Action onReset, string tooltip = "Reset Section") =>
        AddHeaderAction("Reset", tooltip, onReset, iconName: "rotate_left");

    public void EnablePresets(Action onPresets, string tooltip = "Presets") =>
        AddHeaderAction("Presets", tooltip, onPresets, iconName: "dots");

    public void AddHeaderAction(string label, string tooltip, Action onClick, bool isAccentHover = false, string? iconName = null)
    {
        var btn = new IconButton(string.IsNullOrEmpty(iconName) ? label : null, iconName)
        {
            Name = $"{Name}_Action_{label}",
            Variant = ButtonVariant.Ghost,
            IconSize = 13f,
            FontSize = 11f,
        };
        btn.MinWidth = btn.MaxWidth = 24f;
        btn.MinHeight = btn.MaxHeight = 22f;
        if (isAccentHover)
            btn.LabelElement.Style.Text.Weight = 700;
        btn.Clicked += () => onClick?.Invoke();
        _header.AddChild(btn);
        InvalidateLayout();
    }

    public void AddBody(VisualElement child)
    {
        ArgumentNullException.ThrowIfNull(child);
        _body.AddChild(child);
        InvalidateLayout();
    }

    public override SKSize GetPreferredSize(float maxWidth, float maxHeight)
    {
        var size = base.GetPreferredSize(maxWidth, 0f);
        float w = maxWidth > 0 ? maxWidth : (Transform.Width > 0 ? Transform.Width : Theme.RightW);
        return new SKSize(w, size.Height);
    }

    private void ApplyTheme()
    {
        Style.BackColor = Theme.Section;
        Style.Border.Color = Theme.Hairline;
        Style.Border.Roundness = Theme.Radius;
        if (Style.Shadow != null)
            Style.Shadow.Color = Theme.UseShadows ? new SKColor(0, 0, 0, Theme.ShadowAlpha) : SKColors.Transparent;
        _divider.Style.BackColor = Theme.HairlineSubtle;
        ApplyHeaderChrome();
        ApplyEnabledVisuals();
        InvalidatePaint();
    }

    private void ApplyHeaderChrome()
    {
        float innerR = Math.Max(0, Theme.Radius - 1f);
        _header.Style.BackColor = Anim.Lerp(Theme.SectionHeader, Theme.SectionHeaderHover, _headerHover);
        var b = _header.Style.Border;
        b.RoundnessTopLeft = innerR;
        b.RoundnessTopRight = innerR;
        b.RoundnessBottomLeft = _expanded ? 0f : innerR;
        b.RoundnessBottomRight = _expanded ? 0f : innerR;
        _header.InvalidatePaint();
    }

    private void ApplyEnabledVisuals()
    {
        _titleEl.Style.Text.Color = _sectionEnabled ? Theme.Text : Theme.TextDisabled;
        _chevronEl.BackgroundImageTintColor = _sectionEnabled ? Theme.TextDim : Theme.TextDisabled;
        _body.Opacity = _sectionEnabled ? 1f : BodyDimmed;
        InvalidatePaint();
    }

    private void ApplyExpanded()
    {
        _chevronEl.BackgroundSvg = IconStore.LoadSvg(_expanded ? "chevron_down" : "chevron_right");
        _body.Visible = _expanded;
        _divider.Visible = _expanded;
        ApplyHeaderChrome();
        InvalidateLayout();
        Parent?.InvalidateLayout();
        InvalidatePaint();
    }
}
