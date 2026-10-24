using System;
using System.Collections.Generic;
using Blossom.Animations;
using Blossom.Core;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Blossom.Primitives;
using Blossom.Reactive;
using Raw75.Develop;
using SkiaSharp;

namespace Raw75.Controls;

/// <summary>
/// Masks card for the left dock, a <see cref="PanelGroup"/>: shape tools (Radial, Linear, Rectangle, Custom),
/// the photo's masks in a recessed list (eye = enabled, hover cross = delete, double-click = rename), and the selected mask's
/// edge settings (Feather, Amount, Invert, Overlay). The header checkbox bypasses every mask.
/// The owner wires the events and calls <see cref="SetMasks"/> whenever the list or selection changes.
/// </summary>
public sealed class MaskPanel : PanelGroup
{
    private const float RowH = 28f;
    private static readonly TimeSpan DoubleClickThreshold = TimeSpan.FromMilliseconds(300);

    private static readonly (MaskShape Shape, string Caption, string Icon)[] Tools =
    {
        (MaskShape.Radial, "Radial", "mask_radial"),
        (MaskShape.Linear, "Linear", "mask_linear"),
        (MaskShape.Rectangle, "Rectangle", "mask_rect"),
        (MaskShape.Spline, "Custom", "mask_pen"),
    };

    private readonly Dictionary<MaskShape, IconButton> _tools = new();
    private readonly VisualElement _empty;
    private readonly Stack _list;
    private readonly List<MaskRow> _rows = new();
    private readonly Stack _selection;
    private readonly Signal<int> _selected = new(-1);
    private int _lastClickRow = -1;
    private DateTime _lastClickTime = DateTime.MinValue;

    public SliderRow Feather { get; }
    public SliderRow Amount { get; }
    public IconButton InvertButton { get; }
    public IconButton OverlayButton { get; }

    public event Action<MaskShape>? AddRequested;
    public event Action<int>? RowClicked;
    /// <summary>A row was double-clicked: select that mask and edit its name.</summary>
    public event Action<int>? RenameRequested;
    public event Action<int>? EnableToggled;
    public event Action<int>? DeleteRequested;

    public MaskPanel() : base("Masks")
    {
        Name = "MaskPanel";

        var grid = new Grid
        {
            Name = "MaskTools",
            Columns = "*, *",
            ColumnGap = 6f,
            RowGap = 6f,
            // Grid sizes tracks but does not measure content; give the body stack its height.
            MinHeight = Theme.ToolH * 2f + 6f
        };
        foreach (var (shape, caption, icon) in Tools)
        {
            var b = new IconButton(caption, icon) { FontSize = 12f, IconSize = 15f };
            b.Clicked += () => AddRequested?.Invoke(shape);
            _tools[shape] = b;
            grid.AddChild(b);
        }
        AddBody(grid);

        _empty = new RichBox
        {
            Name = "MaskPanel_Empty",
            Text = "Pick a shape, then drag on the photo. Custom: click points around the area.",
            IsClickthrough = true,
            Overflow = OverflowMode.Visible,
            Style = new ElementStyle
            {
                BackColor = SKColors.Transparent,
                Text = new TextStyle { Size = 11.5f, Weight = 400, Alignment = TextAlign.Left, Padding = 2, MaxLines = 3 }
            }
        };
        _empty.Transform.Height = 46f;
        _empty.Bind(e => e.Style.Text.Color = Theme.TextDim);
        AddBody(_empty);

        _list = new Stack
        {
            Name = "MaskPanel_List",
            Orientation = Orientation.Vertical,
            Gap = 0f,
            Padding = new Thickness(2f),
            Style = new ElementStyle { Border = new BorderStyle { Width = 1 } }
        };
        _list.Bind(l =>
        {
            l.Style.BackColor = Theme.Well;
            l.Style.Border.Color = Theme.HairlineSubtle;
            l.Style.Border.Roundness = Theme.RadiusSm;
        });
        AddBody(_list);

        _selection = new Stack
        {
            Name = "MaskPanel_Selection",
            Orientation = Orientation.Vertical,
            Gap = Theme.SliderGap,
            Padding = new Thickness(0, 4, 0, 0)
        };
        Feather = new SliderRow("Feather", 0f, 100f, "0", inline: false) { DefaultValue = 50f };
        Amount = new SliderRow("Amount", 0f, 100f, "0", inline: false) { DefaultValue = 100f };
        _selection.AddChild(Feather);
        _selection.AddChild(Amount);

        InvertButton = new IconButton("Invert", "invert") { FontSize = 12f, IconSize = 14f };
        OverlayButton = new IconButton("Overlay", "overlay") { FontSize = 12f, IconSize = 14f };
        var toggles = new Grid
        {
            Name = "MaskToggles",
            Columns = "*, *",
            ColumnGap = 6f,
            MinHeight = Theme.ToolH
        };
        toggles.AddChild(InvertButton);
        toggles.AddChild(OverlayButton);
        _selection.AddChild(toggles);
        AddBody(_selection);

        SetMasks(Array.Empty<MaskLayer>(), -1, null);
    }

    /// <summary>Rebuilds the rows and shows the selected mask's settings (values are set by the owner).</summary>
    public void SetMasks(IReadOnlyList<MaskLayer> masks, int selected, MaskShape? placing)
    {
        masks ??= Array.Empty<MaskLayer>();
        foreach (var (shape, button) in _tools)
        {
            button.Toggled = placing == shape;
            button.Enabled = masks.Count < MaskLayer.MaxMasks || placing == shape;
        }

        if (!SameRows(masks))
        {
            var previous = _rows.ToArray();
            _rows.Clear();
            foreach (var row in previous)
            {
                _list.RemoveChild(row);
                row.Dispose();
            }
            for (int i = 0; i < masks.Count; i++)
            {
                int index = i;
                // Built outside any caller effect so each row's bindings live exactly as long as the row.
                var row = EffectScope.Record(() => new MaskRow(this, index)).Result;
                row.Clicked += () => OnRowClicked(index);
                row.EyeClicked += () => EnableToggled?.Invoke(index);
                row.DeleteClicked += () => DeleteRequested?.Invoke(index);
                _rows.Add(row);
                _list.AddChild(row);
            }
        }
        for (int i = 0; i < _rows.Count; i++)
            _rows[i].Update(masks[i]);

        _selected.Value = selected >= 0 && selected < masks.Count ? selected : -1;
        _empty.Visible = masks.Count == 0;
        _list.Visible = masks.Count > 0;
        var sel = _selected.Peek() >= 0 ? masks[_selected.Peek()] : null;
        _selection.Visible = sel != null;
        Feather.Visible = sel?.HasFeather ?? false;
        InvertButton.Toggled = sel?.Invert ?? false;

        InvalidateLayout();
        Parent?.InvalidateLayout();
        InvalidatePaint();
    }

    public static string IconFor(MaskShape shape) => shape switch
    {
        MaskShape.Linear => "mask_linear",
        MaskShape.Rectangle => "mask_rect",
        MaskShape.Spline => "mask_pen",
        _ => "mask_radial"
    };

    // The second click of a double-click renames instead of toggling the selection back off.
    private void OnRowClicked(int index)
    {
        var now = DateTime.UtcNow;
        bool second = index == _lastClickRow && now - _lastClickTime < DoubleClickThreshold;
        _lastClickRow = second ? -1 : index;
        _lastClickTime = now;
        if (second)
            RenameRequested?.Invoke(index);
        else
            RowClicked?.Invoke(index);
    }

    private bool SameRows(IReadOnlyList<MaskLayer> masks) => masks.Count == _rows.Count;

    /// <summary>
    /// One mask: shape icon, name, eye toggle; the delete cross shows on hover. The selected mask gets
    /// the accent wash and bar the applied preset uses.
    /// </summary>
    private sealed class MaskRow : ButtonBase
    {
        private const float BarW = 2f;
        private const float IconX = 10f;
        private const float TextX = 32f;
        private const float ButtonSize = 22f;

        private static readonly TextPaint Ink = new() { TextSize = 12f };
        private static SKTypeface? _regular;
        private static SKTypeface? _medium;
        private static SKTypeface Regular => _regular ??= Theme.GetTypeface(400);
        private static SKTypeface Medium => _medium ??= Theme.GetTypeface(500);

        private readonly int _index;
        private readonly VisualElement _icon;
        private readonly IconButton _eye;
        private readonly IconButton _delete;
        private string _label = "";
        private string _iconName = "";
        private bool _enabled = true;
        private bool _isSelected;
        private float _hover;
        private float _fitW = -1f;
        private string _fit = "";

        public event Action? EyeClicked;
        public event Action? DeleteClicked;

        public MaskRow(MaskPanel owner, int index)
        {
            _index = index;
            Name = $"MaskRow_{index}";
            ReceivesKeyboard = false;
            Style = new ElementStyle { BackColor = SKColors.Transparent };
            Transform.Height = RowH;

            _icon = new VisualElement
            {
                Name = $"{Name}_Icon",
                IsClickthrough = true,
                BackgroundImageScale = ImageScaleMode.Contain,
                BackgroundImageTintBlendMode = SKBlendMode.SrcIn,
            };
            AddChild(_icon);

            _eye = Ghost("eye", $"{Name}_Eye");
            _eye.Clicked += () => EyeClicked?.Invoke();
            AddChild(_eye);

            _delete = Ghost("cross", $"{Name}_Del");
            _delete.IconSize = 10f;
            _delete.Visible = false;
            _delete.Clicked += () => DeleteClicked?.Invoke();
            AddChild(_delete);

            // A row owns its click: it must not bubble to the card.
            Events.OnClick += (_, e) => { if (e.Button == PointerButton.Left) e.Handled = true; };

            this.Bind(_ =>
            {
                Theme.Observe();
                _isSelected = owner._selected.Value == _index;
                _icon.BackgroundImageTintColor = _isSelected ? Theme.Accent : Theme.TextDim;
                InvalidatePaint();
            });
        }

        private static IconButton Ghost(string icon, string name)
        {
            var b = IconButton.Icon(icon);
            b.Name = name;
            b.Variant = ButtonVariant.Ghost;
            b.IconSize = 13f;
            b.MinWidth = b.MaxWidth = ButtonSize;
            b.MinHeight = b.MaxHeight = ButtonSize;
            return b;
        }

        public void Update(MaskLayer m)
        {
            string icon = IconFor(m.Shape);
            if (icon != _iconName)
            {
                _iconName = icon;
                _icon.BackgroundSvg = IconStore.LoadSvg(icon);
            }
            if (_label != m.Name)
            {
                _label = m.Name ?? "";
                _fitW = -1f;
            }
            if (_enabled != m.Enabled)
            {
                _enabled = m.Enabled;
                _eye.SetIcon(_enabled ? "eye" : "eye_off");
            }
            InvalidatePaint();
        }

        public override SKSize GetPreferredSize(float maxWidth, float maxHeight) =>
            new(maxWidth > 0 ? maxWidth : 200f, RowH);

        protected override void OnHoverChanged()
        {
            _hover = IsHovered ? 1f : 0f;
            _delete.Visible = IsHovered;
            InvalidatePaint();
        }

        protected override void LayoutChildren()
        {
            float ox = Transform.Computed.X;
            float oy = Transform.Computed.Y;
            float w = Math.Max(1f, Transform.Width);
            float h = Math.Max(1f, Transform.Height);
            float by = oy + (h - ButtonSize) * 0.5f;
            _icon.Transform.SetAbsoluteFrame(ox + IconX, oy + (h - 14f) * 0.5f, 14f, 14f);
            _delete.Transform.SetAbsoluteFrame(ox + w - ButtonSize - 3f, by, ButtonSize, ButtonSize);
            _eye.Transform.SetAbsoluteFrame(ox + w - ButtonSize * 2f - 5f, by, ButtonSize, ButtonSize);
        }

        protected override void Paint(SKCanvas c)
        {
            float r = Math.Max(0f, Theme.RadiusSm - 1f);
            var rect = SKRect.Create(0, 0, W, H);

            if (_isSelected)
            {
                ThumbInk.FillRound(c, rect, r, Theme.AccentWash);
                ThumbInk.FillRound(c, SKRect.Create(0, 4f, BarW, H - 8f), BarW * 0.5f, Theme.Accent);
            }
            if (_hover > 0f)
                ThumbInk.FillRound(c, rect, r, Theme.Hover.WithAlpha((byte)(140 * _hover)));

            float maxW = W - TextX - ButtonSize * 2f - 12f;
            if (Math.Abs(maxW - _fitW) > 0.5f)
            {
                _fitW = maxW;
                Ink.Typeface = Medium;
                _fit = Blossom.Utils.Text.Ellipsize(_label, Ink.Font, Math.Max(10f, maxW));
            }

            Ink.Typeface = _isSelected ? Medium : Regular;
            SKColor ink = _isSelected ? Theme.Text : Anim.Lerp(Theme.TextSecondary, Theme.Text, _hover);
            Ink.Color = _enabled ? ink : Theme.TextDisabled;
            c.DrawText(_fit, TextX, Blossom.Utils.Text.Baseline(Ink.Font, H * 0.5f), Ink);
        }
    }
}
