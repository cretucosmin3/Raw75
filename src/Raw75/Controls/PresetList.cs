using System;
using System.Collections.Generic;
using Blossom.Animations;
using Blossom.Core;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Blossom.Primitives;
using Blossom.Reactive;
using Raw75.Presets;
using SkiaSharp;

namespace Raw75.Controls;

/// <summary>
/// Presets card, a vertical <see cref="Stack"/> like <see cref="PanelGroup"/>: uppercase header strip,
/// hairline divider, then a body with the Copy / Paste / Save Preset grid over a recessed list well.
/// The last applied preset stays highlighted with an accent wash and bar.
/// </summary>
public class PresetList : Stack
{
    private const float RowH = 26f;
    private const float BodyPad = 8f;
    private const float BodyGap = 8f;
    private const float ActionGap = 6f;

    private readonly Stack _header;
    private readonly VisualElement _divider;
    private readonly VisualElement _headerTitle;
    private readonly Grid _actions;
    private readonly PresetScrollList _list;
    private readonly List<PresetRow> _rows = new();
    private readonly Signal<string?> _applied = new(null);

    public event Action<string>? Applied;
    public event Action? CopyClicked;
    public event Action? PasteClicked;
    public event Action? SaveClicked;
    public event Action<string>? Deleted;

    public PresetList()
    {
        Name = "PresetList";
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
            Name = "PresetList_Header",
            Orientation = Orientation.Horizontal,
            Align = LayoutAlign.Center,
            Padding = new Thickness(10, 0, 8, 0),
            Style = new ElementStyle { Border = new BorderStyle { Width = 0 } }
        };
        _header.MinHeight = Theme.GroupHeadH - 2f;

        _headerTitle = new RichBox
        {
            Name = "PresetList_Title",
            Text = "PRESETS & LOOKS",
            IsClickthrough = true,
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
        _headerTitle.Transform.Height = 18f;
        _header.AddChild(_headerTitle);
        SetGrow(_headerTitle, 1f);
        AddChild(_header);

        _divider = new VisualElement { Name = "PresetList_Divider", IsClickthrough = true };
        _divider.Transform.Height = 1f;
        AddChild(_divider);

        var body = new Stack
        {
            Name = "PresetList_Body",
            Orientation = Orientation.Vertical,
            Gap = BodyGap,
            Padding = new Thickness(BodyPad),
            Style = new ElementStyle { BackColor = SKColors.Transparent }
        };
        AddChild(body);
        SetGrow(body, 1f);

        var copy = new IconButton("Copy", "copy");
        copy.Clicked += () => CopyClicked?.Invoke();
        var paste = new IconButton("Paste", "paste");
        paste.Clicked += () => PasteClicked?.Invoke();
        var save = new IconButton("Save Preset", "plus");
        save.Clicked += () => SaveClicked?.Invoke();

        _actions = new Grid
        {
            Name = "PresetList_Actions",
            Columns = "*, *",
            Rows = "Auto, Auto",
            ColumnGap = ActionGap,
            RowGap = ActionGap
        };
        // Grid does not measure its content; pin the two button rows' height for the body stack.
        _actions.MinHeight = Theme.ToolH * 2f + ActionGap;
        _actions.AddChild(copy);
        _actions.AddChild(paste);
        _actions.AddChild(save);
        Grid.SetCell(save, column: 0, row: 1, columnSpan: 2);
        body.AddChild(_actions);

        _list = new PresetScrollList();
        body.AddChild(_list);
        SetGrow(_list, 1f);

        this.Bind(_ => ApplyTheme());
    }

    private void ApplyTheme()
    {
        Style.BackColor = Theme.Section;
        Style.Border.Color = Theme.Hairline;
        Style.Border.Roundness = Theme.Radius;
        if (Style.Shadow != null)
            Style.Shadow.Color = Theme.UseShadows ? new SKColor(0, 0, 0, Theme.ShadowAlpha) : SKColors.Transparent;

        float innerR = Math.Max(0, Theme.Radius - 1f);
        _header.Style.BackColor = Theme.SectionHeader;
        var b = _header.Style.Border;
        b.RoundnessTopLeft = innerR;
        b.RoundnessTopRight = innerR;
        b.RoundnessBottomLeft = 0f;
        b.RoundnessBottomRight = 0f;

        _headerTitle.Style.Text.Color = Theme.Text;
        _divider.Style.BackColor = Theme.HairlineSubtle;
        InvalidatePaint();
    }

    public void SetItems(IReadOnlyList<string> names)
    {
        names ??= Array.Empty<string>();

        var previous = _rows.ToArray();
        _rows.Clear();
        _list.ClearItems();
        for (int i = 0; i < previous.Length; i++)
            previous[i].Dispose();

        for (int i = 0; i < names.Count; i++)
        {
            string name = names[i] ?? "";
            // Built outside any caller effect so each row's bindings live exactly as long as the row.
            var row = EffectScope.Record(() => new PresetRow(this, name, PresetStore.IsUser(name))).Result;
            row.Clicked += () =>
            {
                _applied.Value = name;
                Applied?.Invoke(name);
            };
            row.DeleteClicked += () => Deleted?.Invoke(name);
            _rows.Add(row);
            _list.AddItem(row);
        }

        InvalidateLayout();
        InvalidatePaint();
    }

    public override SKSize GetPreferredSize(float maxWidth, float maxHeight)
    {
        float w = maxWidth > 0 ? maxWidth : (Transform.Width > 0 ? Transform.Width : Theme.LeftW);
        float actionsH = Theme.ToolH * 2f + ActionGap;
        float h = 2f + (Theme.GroupHeadH - 2f) + 1f + BodyPad * 2f + actionsH + BodyGap + _rows.Count * RowH + 4f;
        if (maxHeight > 0) h = Math.Min(h, maxHeight);
        return new SKSize(w, h);
    }

    private sealed class PresetScrollList : ScrollContainer
    {
        private readonly List<VisualElement> _items = new();

        public PresetScrollList()
        {
            Name = "PresetList_ScrollContainer";
            OverflowX = OverflowMode.Clip;
            OverflowY = OverflowMode.Scroll;
            ScrollbarVisibilityX = ScrollbarVisibility.Hidden;
            ScrollbarVisibilityY = ScrollbarVisibility.Auto;
            ScrollbarThickness = 6f;
            ScrollbarRadius = 3f;
            ScrollbarPadding = 1.5f;
            ScrollbarTrackColor = SKColors.Transparent;
            Style = new ElementStyle { Border = new BorderStyle { Width = 1 } };

            this.Bind(_ =>
            {
                Style.BackColor = Theme.Well;
                Style.Border.Color = Theme.HairlineSubtle;
                Style.Border.Roundness = Theme.RadiusSm;
                ThumbTile.StyleScrollbars(this);
            });
        }

        public void AddItem(VisualElement item)
        {
            _items.Add(item);
            AddChild(item);
        }

        public void ClearItems()
        {
            for (int i = 0; i < _items.Count; i++)
                RemoveChild(_items[i]);
            _items.Clear();
        }

        protected override void LayoutChildren()
        {
            float ox = Transform.Computed.X;
            float oy = Transform.Computed.Y;
            float w = Math.Max(1f, Transform.Width);
            float y = 2f;

            for (int i = 0; i < _items.Count; i++)
            {
                _items[i].Transform.SetAbsoluteFrame(ox + 2f, oy + y, w - 4f, RowH);
                y += RowH;
            }

            SetContentSize(w, Math.Max(y + 2f, Transform.Computed.Height));
            base.LayoutChildren();
        }
    }

    /// <summary>
    /// One preset: 12px name that brightens on hover over a soft wash; the applied preset gets an
    /// accent wash and a 2px accent bar. User presets reveal a ghost delete button on hover.
    /// </summary>
    private sealed class PresetRow : ButtonBase
    {
        private const float BarW = 2f;
        private const float TextX = 12f;
        private const float DeleteSize = 20f;

        private static readonly TextPaint Ink = new() { TextSize = 12f };
        private static SKTypeface? _regular;
        private static SKTypeface? _medium;
        private static SKTypeface Regular => _regular ??= Theme.GetTypeface(400);
        private static SKTypeface Medium => _medium ??= Theme.GetTypeface(500);

        private readonly string _label;
        private readonly IconButton? _delete;
        private float _hover;
        private bool _applied;
        private float _fitW = -1f;
        private string _fit = "";

        public event Action? DeleteClicked;

        public PresetRow(PresetList owner, string name, bool canDelete)
        {
            _label = name;
            Name = $"Preset_{name}";
            ReceivesKeyboard = false;
            Style = new ElementStyle { BackColor = SKColors.Transparent };

            if (canDelete)
            {
                _delete = IconButton.Icon("cross");
                _delete.Name = $"{Name}_Del";
                _delete.Variant = ButtonVariant.Ghost;
                _delete.IconSize = 10f;
                _delete.MinWidth = _delete.MaxWidth = DeleteSize;
                _delete.MinHeight = _delete.MaxHeight = DeleteSize;
                _delete.Visible = false;
                _delete.Clicked += () => DeleteClicked?.Invoke();
                AddChild(_delete);
            }

            // A row owns its click: it must not bubble to the card.
            Events.OnClick += (_, e) => { if (e.Button == PointerButton.Left) e.Handled = true; };

            this.Bind(_ =>
            {
                Theme.Observe();
                _applied = owner._applied.Value == _label;
                InvalidatePaint();
            });
        }

        protected override void OnHoverChanged()
        {
            _hover = IsHovered ? 1f : 0f;
            InvalidatePaint();
            if (_delete != null) _delete.Visible = IsHovered;
        }

        protected override void LayoutChildren()
        {
            if (_delete == null) return;
            float ox = Transform.Computed.X;
            float oy = Transform.Computed.Y;
            float w = Math.Max(1f, Transform.Width);
            float h = Math.Max(1f, Transform.Height);
            _delete.Transform.SetAbsoluteFrame(ox + w - DeleteSize - 3f, oy + (h - DeleteSize) * 0.5f, DeleteSize, DeleteSize);
        }

        protected override void Paint(SKCanvas c)
        {
            float hover = _hover;
            float r = Math.Max(0f, Theme.RadiusSm - 1f);
            var rect = SKRect.Create(0, 0, W, H);

            if (_applied)
            {
                ThumbInk.FillRound(c, rect, r, Theme.AccentWash);
                ThumbInk.FillRound(c, SKRect.Create(0, 4f, BarW, H - 8f), BarW * 0.5f, Theme.Accent);
            }
            if (hover > 0f)
                ThumbInk.FillRound(c, rect, r, Theme.Hover.WithAlpha((byte)(140 * hover)));

            float maxW = W - TextX - (_delete != null ? DeleteSize + 8f : 8f);
            if (Math.Abs(maxW - _fitW) > 0.5f)
            {
                // Fit against the wider weight so the name never reflows when it becomes applied.
                _fitW = maxW;
                Ink.Typeface = Medium;
                _fit = Blossom.Utils.Text.Ellipsize(_label, Ink.Font, maxW);
            }

            Ink.Typeface = _applied ? Medium : Regular;
            Ink.Color = _applied ? Theme.Text : Anim.Lerp(Theme.TextSecondary, Theme.Text, hover);
            c.DrawText(_fit, TextX, Blossom.Utils.Text.Baseline(Ink.Font, H * 0.5f), Ink);
        }
    }
}
