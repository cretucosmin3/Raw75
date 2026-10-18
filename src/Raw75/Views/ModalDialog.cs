using System;
using Blossom.Core;
using Blossom.Core.Input;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Blossom.Primitives;
using Blossom.Reactive;
using Raw75.Controls;
using SkiaSharp;

namespace Raw75.Views;

/// <summary>
/// Full-view modal: a scrim over the workspace and a centred card. The card is a vertical
/// <see cref="Stack"/> that subclasses fill with <see cref="Header"/>, <see cref="Body"/> and
/// <see cref="Footer"/> rows split by hairlines; it sizes to its content unless a fixed height is given.
/// Escape closes; a click on the scrim closes when <see cref="DismissOnScrim"/> allows it.
/// </summary>
public abstract class ModalDialog : VisualElement
{
    protected const float CardPad = 20f;
    private const float ViewMargin = 24f;

    /// <summary>File-name characters: printable and none of the reserved path characters.</summary>
    protected static bool IsFileNameChar(char ch) => !char.IsControl(ch) && "<>:\"/\\|?*".IndexOf(ch) < 0;

    private readonly float _cardWidth;
    private readonly float? _cardHeight;

    protected Stack Card { get; }

    protected ModalDialog(string name, int zIndex, float cardWidth, float? cardHeight = null)
    {
        Name = name;
        Visible = false;
        ZIndex = zIndex;
        ReceivesKeyboard = true;
        Transform = new Transform(0, 0, 800, 600)
        {
            Anchor = Anchor.Left | Anchor.Right | Anchor.Top | Anchor.Bottom
        };
        Style = new ElementStyle();
        _cardWidth = cardWidth;
        _cardHeight = cardHeight;

        Card = new Stack
        {
            Name = $"{name}_Card",
            Orientation = Orientation.Vertical,
            Padding = new Thickness(1f),
            Style = new ElementStyle
            {
                Border = new BorderStyle { Width = 1 },
                Shadow = new ShadowStyle(0, 18f, 36f, 36f, SKColors.Transparent)
            }
        };
        AddChild(Card);

        Events.OnClick += (_, e) =>
        {
            if (e.Button != PointerButton.Left || !DismissOnScrim) return;
            if (Card.Transform.Computed.RectF.Contains(e.Global.X, e.Global.Y)) return;
            e.Handled = true;
            Close();
        };
        Events.OnKeyDown += e =>
        {
            if (Visible && HandleKey(e.Key))
                e.Handled = true;
        };

        this.Bind(_ => ApplyModalTheme());
    }

    /// <summary>Whether a click on the scrim closes the dialog.</summary>
    protected virtual bool DismissOnScrim => true;

    protected virtual SKColor ScrimColor => Theme.Scrim;

    public virtual void Close() => HideModal();

    /// <summary>Keys that reach the dialog itself (the focused field handles its own). True = handled.</summary>
    protected virtual bool HandleKey(UiKey key)
    {
        if (key != UiKey.Escape) return false;
        Close();
        return true;
    }

    /// <summary>Covers the view, lays the card out, and gives keyboard focus to <paramref name="focus"/> (or the dialog).</summary>
    protected void ShowModal(VisualElement? focus = null)
    {
        float w = ParentView?.Width ?? Transform.Width;
        float h = ParentView?.Height ?? Transform.Height;
        Transform.SetAbsoluteFrame(0, 0, Math.Max(1f, w), Math.Max(1f, h));
        Transform.Anchor = Anchor.Left | Anchor.Right | Anchor.Top | Anchor.Bottom;
        Visible = true;
        InvalidateLayout();
        ForceLayoutSubtree();
        ParentView?.SetActiveKeyboardElement(focus ?? this);
        InvalidatePaint();
    }

    protected void HideModal()
    {
        if (!Visible) return;
        var active = ParentView?.ActiveKeyboardElement;
        if (active != null && ContainsElement(active))
            ParentView!.SetActiveKeyboardElement(null);
        Visible = false;
        InvalidatePaint();
    }

    protected override void LayoutChildren()
    {
        float ox = Transform.Computed.X;
        float oy = Transform.Computed.Y;
        float w = Math.Max(1f, Transform.Computed.Width);
        float h = Math.Max(1f, Transform.Computed.Height);
        float cw = Math.Min(_cardWidth, Math.Max(1f, w - ViewMargin * 2f));
        float ch = _cardHeight ?? Card.GetPreferredSize(cw, 0f).Height;
        ch = Math.Min(ch, Math.Max(1f, h - ViewMargin * 2f));
        Card.Transform.SetAbsoluteFrame(MathF.Round(ox + (w - cw) * 0.5f), MathF.Round(oy + (h - ch) * 0.5f), cw, ch);
    }

    private void ApplyModalTheme()
    {
        Style.BackColor = ScrimColor;
        Card.Style.BackColor = Theme.Panel;
        Card.Style.Border.Color = Theme.HairlineStrong;
        // Square themes stay square; rounded ones get a softer modal corner.
        Card.Style.Border.Roundness = Theme.Radius > 0f ? Theme.Radius + 4f : 0f;
        Card.Style.Shadow.Color = Theme.UseShadows ? SKColors.Black.WithAlpha(115) : SKColors.Transparent;
        InvalidatePaint();
    }

    protected enum TextRole
    {
        /// <summary>Dialog title: 15px semibold.</summary>
        Title,
        /// <summary>Running copy: 12px secondary.</summary>
        Body,
        /// <summary>Quiet copy: paths, status, hints.</summary>
        Dim,
        /// <summary>Uppercase tracked section label.</summary>
        Section,
        /// <summary>Right-aligned readout beside a section label.</summary>
        Value
    }

    /// <summary>
    /// Text label whose colour follows the theme. Blossom measures wrapped text against the box's
    /// current height, so a multi-line label reserves <paramref name="maxLines"/> lines up front.
    /// </summary>
    protected static RichBox Label(string text, TextRole role, int maxLines = 1)
    {
        var (size, weight, spacing) = role switch
        {
            TextRole.Title => (15f, 600, 0),
            TextRole.Section => (10.5f, 600, 1),
            TextRole.Value => (11.5f, 500, 0),
            _ => (12f, 400, 0)
        };
        var label = new RichBox
        {
            Name = $"Label_{role}",
            Text = role == TextRole.Section ? text.ToUpperInvariant() : text,
            IsClickthrough = true,
            Style = new ElementStyle
            {
                BackColor = SKColors.Transparent,
                Text = new TextStyle
                {
                    Size = size,
                    Weight = weight,
                    Spacing = spacing,
                    Alignment = role == TextRole.Value ? TextAlign.Right : TextAlign.Left,
                    Padding = 0,
                    // A readout sizes to its text; everything else ellipsizes in the width it is given.
                    Overflow = role == TextRole.Value ? TextOverflow.Visible : TextOverflow.Ellipsis,
                    MaxLines = maxLines
                }
            }
        };
        label.MinHeight = MathF.Ceiling(size * 1.4f) * maxLines;
        label.Bind(l => l.Style.Text.Color = RoleColor(role));
        return label;
    }

    protected static SKColor RoleColor(TextRole role) => role switch
    {
        TextRole.Title => Theme.Text,
        TextRole.Body or TextRole.Value => Theme.TextSecondary,
        _ => Theme.TextDim
    };

    /// <summary>Title block; <paramref name="subtitle"/> is returned so callers can rewrite it per open.</summary>
    protected static Stack Header(string title, string? subtitle, out RichBox subtitleLabel, int subtitleLines = 1)
    {
        var header = new Stack
        {
            Name = "ModalHeader",
            Orientation = Orientation.Vertical,
            Gap = 4f,
            Padding = new Thickness(CardPad, 16f, CardPad, 14f)
        };
        header.AddChild(Label(title, TextRole.Title));
        subtitleLabel = Label(subtitle ?? "", TextRole.Body, subtitleLines);
        subtitleLabel.Visible = subtitle != null;
        header.AddChild(subtitleLabel);
        return header;
    }

    protected static Stack Header(string title, string? subtitle = null) => Header(title, subtitle, out _);

    protected static Stack Body(float gap = 16f) => new()
    {
        Name = "ModalBody",
        Orientation = Orientation.Vertical,
        Gap = gap,
        Padding = new Thickness(CardPad, 16f, CardPad, 18f)
    };

    /// <summary>Action row: <paramref name="leading"/> (status text) takes the slack, actions sit right.</summary>
    protected static Stack Footer(VisualElement? leading, params IconButton[] actions)
    {
        var footer = new Stack
        {
            Name = "ModalFooter",
            Orientation = Orientation.Horizontal,
            Align = LayoutAlign.Center,
            Gap = 8f,
            Padding = new Thickness(CardPad, 12f, CardPad, 14f)
        };
        var slack = leading ?? new VisualElement { Name = "FooterSpacer", IsClickthrough = true };
        footer.AddChild(slack);
        Stack.SetGrow(slack, 1f);
        foreach (var action in actions)
        {
            action.MinWidth = 84f;
            footer.AddChild(action);
        }
        return footer;
    }

    protected static VisualElement Separator()
    {
        var line = new VisualElement { Name = "ModalSeparator", IsClickthrough = true };
        line.Transform.Height = 1f;
        line.BindBackColor(() => Theme.HairlineSubtle);
        return line;
    }

    /// <summary>Section label over one or more controls.</summary>
    protected static Stack Field(string label, params VisualElement[] controls)
    {
        var field = new Stack { Name = $"Field_{label}", Orientation = Orientation.Vertical, Gap = 7f };
        field.AddChild(Label(label, TextRole.Section));
        foreach (var c in controls)
            field.AddChild(c);
        return field;
    }

    /// <summary>Section label with a readout on the right, over one or more controls.</summary>
    protected static Stack ReadoutField(string label, RichBox value, params VisualElement[] controls)
    {
        var head = new Stack { Name = $"FieldHead_{label}", Orientation = Orientation.Horizontal, Align = LayoutAlign.Center, Gap = 8f };
        var title = Label(label, TextRole.Section);
        head.AddChild(title);
        head.AddChild(value);
        Stack.SetGrow(title, 1f);

        var field = new Stack { Name = $"Field_{label}", Orientation = Orientation.Vertical, Gap = 7f };
        field.AddChild(head);
        foreach (var c in controls)
            field.AddChild(c);
        return field;
    }

    /// <summary>Segmented control kept in sync with <paramref name="signal"/> both ways.</summary>
    protected static SegmentedControl Segmented<T>(Signal<T> signal, T[] values, params string[] labels)
    {
        var seg = new SegmentedControl(labels);
        seg.SelectionChanged += i => signal.Value = values[i];
        seg.Bind(s =>
        {
            int i = Array.IndexOf(values, signal.Value);
            if (i >= 0 && s.SelectedIndex != i)
                s.SelectedIndex = i;
        });
        return seg;
    }
}
