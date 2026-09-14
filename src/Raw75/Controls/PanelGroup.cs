using System;
using System.Collections.Generic;
using Blossom.Core;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Silk.NET.Input;
using SkiaSharp;

namespace Raw75.Controls;

/// <summary>
/// Capture One Pro-inspired collapsible section card:
/// Crisp 28px header with rotating disclosure caret, title, 1px bottom separator line,
/// drop shadow, and right-aligned micro-actions (Auto, Reset, Presets).
/// </summary>
public class PanelGroup : VisualElement
{
    private readonly VisualElement _header;
    private readonly CheckToggle _toggle;
    private readonly VisualElement _chevronEl;
    private readonly VisualElement _titleEl;
    private readonly List<VisualElement> _actions = new();
    private readonly List<VisualElement> _body = new();
    private bool _expanded = true;
    private bool _sectionEnabled = true;
    private string _title;

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

    public PanelGroup(string title)
    {
        Name = $"PanelGroup_{title}";
        _title = title ?? "";
        Padding = new Thickness(10, 8, 10, 10);
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

        _header = new VisualElement
        {
            Name = $"{Name}_Header",
            Cursor = StandardCursor.Hand,
            Style = new ElementStyle
            {
                BackColor = Theme.SectionHeader,
                Border = new BorderStyle
                {
                    Width = 1,
                    Color = Theme.HairlineSubtle,
                    Roundness = 0
                }
            }
        };

        _toggle = new CheckToggle(initialChecked: true);
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
            BackgroundImageTintColor = Theme.TextSecondary,
        };
        _header.AddChild(_chevronEl);

        _titleEl = new RichBox
        {
            Name = $"{Name}_Title",
            IsClickthrough = true,
            Overflow = OverflowMode.Visible,
            TextOverflow = TextOverflow.Visible,
            Style = new ElementStyle
            {
                BackColor = SKColors.Transparent,
                Text = new TextStyle
                {
                    Color = Theme.Text,
                    Size = 12,
                    Weight = 600,
                    Alignment = TextAlign.Left,
                    Padding = 0,
                    Overflow = TextOverflow.Visible,
                    MaxLines = 1
                }
            }
        };

        _header.AddChild(_titleEl);
        _header.Events.OnClick += (_, args) =>
        {
            if (args.Button != (int)MouseButton.Left) return;
            Expanded = !Expanded;
            args.Handled = true;
        };

        _header.Events.OnMouseEnter += _ =>
        {
            if (_header.Style != null)
                _header.Style.BackColor = Theme.SectionHeaderHover;
        };

        _header.Events.OnMouseLeave += _ =>
        {
            if (_header.Style != null)
                _header.Style.BackColor = Theme.SectionHeader;
        };

        AddChild(_header);
        ApplyExpanded();
    }

    public void EnableAuto(Action onAuto, string tooltip = "Auto Adjust")
    {
        AddHeaderAction("A", tooltip, onAuto, isAccentHover: true);
    }

    public void EnableReset(Action onReset, string tooltip = "Reset Section")
    {
        AddHeaderAction("Reset", tooltip, onReset, iconName: "rotate_left");
    }

    public void EnablePresets(Action onPresets, string tooltip = "Presets")
    {
        AddHeaderAction("Presets", tooltip, onPresets, iconName: "dots");
    }

    public void AddHeaderAction(string label, string tooltip, Action onClick, bool isAccentHover = false, string? iconName = null)
    {
        var btn = new VisualElement
        {
            Name = $"{Name}_Action_{label}",
            Text = string.IsNullOrEmpty(iconName) ? label : "",
            Cursor = StandardCursor.Hand,
            BackgroundImageScale = ImageScaleMode.Contain,
            BackgroundImageTintBlendMode = SKBlendMode.SrcIn,
            BackgroundImageTintColor = Theme.TextSecondary,
            Overflow = OverflowMode.Visible,
            Style = new ElementStyle
            {
                BackColor = Theme.Well,
                Border = new BorderStyle
                {
                    Width = 1,
                    Color = Theme.Hairline,
                    Roundness = Theme.RadiusSm
                },
                Shadow = new ShadowStyle(0, 1, 1, 1, new SKColor(0, 0, 0, 35)),
                Text = new TextStyle
                {
                    Color = Theme.TextSecondary,
                    Size = 12,
                    Weight = 600,
                    Alignment = TextAlign.Center,
                    Padding = 0,
                    Overflow = TextOverflow.Visible
                }
            }
        };

        if (!string.IsNullOrEmpty(iconName))
        {
            btn.BackgroundSvg = IconStore.LoadSvg(iconName);
        }

        btn.Events.OnMouseEnter += _ =>
        {
            if (btn.Style != null)
            {
                btn.Style.BackColor = isAccentHover ? Theme.AccentSoft : Theme.ButtonHover;
                btn.Style.Border = new BorderStyle
                {
                    Width = 1,
                    Color = isAccentHover ? Theme.Accent : Theme.HairlineStrong,
                    Roundness = Theme.RadiusSm
                };
                if (btn.Style.Text != null)
                    btn.Style.Text.Color = isAccentHover ? Theme.Accent : Theme.Text;
            }
            btn.BackgroundImageTintColor = isAccentHover ? Theme.Accent : Theme.Text;
            InvalidatePaint();
        };

        btn.Events.OnMouseLeave += _ =>
        {
            if (btn.Style != null)
            {
                btn.Style.BackColor = Theme.Well;
                btn.Style.Border = new BorderStyle
                {
                    Width = 1,
                    Color = Theme.Hairline,
                    Roundness = Theme.RadiusSm
                };
                if (btn.Style.Text != null)
                    btn.Style.Text.Color = Theme.TextSecondary;
            }
            btn.BackgroundImageTintColor = Theme.TextSecondary;
            InvalidatePaint();
        };

        btn.Events.OnClick += (_, args) =>
        {
            if (args.Button != (int)MouseButton.Left) return;
            onClick?.Invoke();
            args.Handled = true; // Prevent header collapse/expand
        };

        _actions.Add(btn);
        _header.AddChild(btn);
        InvalidateLayout();
    }

    public void AddBody(VisualElement child)
    {
        if (child == null) throw new ArgumentNullException(nameof(child));
        _body.Add(child);
        child.Visible = _expanded;
        AddChild(child);
        InvalidateLayout();
    }

    public override SKSize GetPreferredSize(float maxWidth, float maxHeight)
    {
        float w = maxWidth > 0 ? maxWidth : (Transform.Width > 0 ? Transform.Width : Theme.RightW);
        float h = Theme.GroupHeadH;
        if (_expanded)
        {
            float inner = Math.Max(0, w - Padding.Horizontal);
            for (int i = 0; i < _body.Count; i++)
            {
                var child = _body[i];
                if (child == null || !child.Visible) continue;
                var size = child.GetPreferredSize(inner, 0);
                h += size.Height + Theme.SliderGap;
            }
            h += Padding.Vertical;
        }

        if (maxHeight > 0) h = Math.Min(h, maxHeight);
        return new SKSize(w, h);
    }

    protected override void LayoutChildren()
    {
        float originX = Transform.Computed.X;
        float originY = Transform.Computed.Y;
        float w = Math.Max(1f, Transform.Width);

        _header.Transform.SetAbsoluteFrame(originX, originY, w, Theme.GroupHeadH);

        // Layout checkbox toggle on the far left
        float toggleSize = 18f;
        float toggleX = originX + 8f;
        float toggleY = originY + (Theme.GroupHeadH - toggleSize) * 0.5f;
        _toggle.Transform.SetAbsoluteFrame(toggleX, toggleY, toggleSize, toggleSize);

        // Layout chevron next to toggle
        float chevSize = 12f;
        float chevX = toggleX + toggleSize + 6f;
        float chevY = originY + (Theme.GroupHeadH - chevSize) * 0.5f;
        _chevronEl.Transform.SetAbsoluteFrame(chevX, chevY, chevSize, chevSize);

        // Layout action buttons (from right to left)
        float ax = originX + w - 8f;
        for (int i = _actions.Count - 1; i >= 0; i--)
        {
            float btnW = 24f;
            float btnH = 22f;
            ax -= btnW;
            _actions[i].Transform.SetAbsoluteFrame(ax, originY + (Theme.GroupHeadH - btnH) * 0.5f, btnW, btnH);
            ax -= 4f;
        }

        // Layout title after the chevron
        float titleLeft = chevX + chevSize + 6f;
        float titleW = Math.Max(1f, ax - titleLeft);
        _titleEl.Transform.SetAbsoluteFrame(titleLeft, originY + (Theme.GroupHeadH - 20f) * 0.5f, titleW, 20f);

        if (!_expanded)
            return;

        float y = Theme.GroupHeadH + Padding.Top;
        float innerW = Math.Max(0, w - Padding.Horizontal);
        float childX = originX + Padding.Left;

        for (int i = 0; i < _body.Count; i++)
        {
            var child = _body[i];
            if (child == null || !child.Visible) continue;

            var preferred = child.GetPreferredSize(innerW, 0);
            float childH = preferred.Height > 0 ? preferred.Height : child.Transform.Height;
            float childW = innerW;
            child.Transform.SetAbsoluteFrame(childX, originY + y, childW, childH);
            y += childH + Theme.SliderGap;
        }
    }

    private void ApplyEnabledVisuals()
    {
        if (_titleEl.Style?.Text != null)
        {
            _titleEl.Style.Text.Color = _sectionEnabled ? Theme.Text : Theme.TextDisabled;
        }
        _chevronEl.BackgroundImageTintColor = _sectionEnabled ? Theme.TextSecondary : Theme.TextDisabled;
        InvalidatePaint();
    }

    private void ApplyExpanded()
    {
        _chevronEl.BackgroundSvg = IconStore.LoadSvg(_expanded ? "chevron_down" : "chevron_right");
        _titleEl.Text = _title.ToUpperInvariant();
        for (int i = 0; i < _body.Count; i++)
        {
            if (_body[i] != null)
                _body[i].Visible = _expanded;
        }
        InvalidateLayout();
        Parent?.InvalidateLayout();
        InvalidatePaint();
    }
}
