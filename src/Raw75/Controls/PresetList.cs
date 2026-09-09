using System;
using System.Collections.Generic;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Raw75.Presets;
using Silk.NET.Input;
using SkiaSharp;

namespace Raw75.Controls;

/// <summary>
/// Capture One Pro-style Presets card:
/// Crisp header bar, "Save Preset" action button, and a recessed scrollable list well with custom delete buttons.
/// </summary>
public class PresetList : VisualElement
{
    private const float HeaderH = 28f;
    private const float SaveH = 24f;
    private const float RowH = 26f;

    private readonly VisualElement _header;
    private readonly VisualElement _headerTitle;
    private readonly IconButton _save;
    private readonly PresetScrollList _list;
    private readonly List<NameRow> _rows = new();

    public event Action<string>? Applied;
    public event Action? SaveClicked;
    public event Action<string>? Deleted;

    public PresetList()
    {
        Name = "PresetList";
        Overflow = OverflowMode.Clip;
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
            Name = "PresetList_Header",
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

        _headerTitle = new VisualElement
        {
            Name = "PresetList_Title",
            Text = "▾  🎨 PRESETS & LOOKS",
            IsClickthrough = true,
            Style = new ElementStyle
            {
                BackColor = SKColors.Transparent,
                Text = new TextStyle
                {
                    Color = Theme.Text,
                    Size = 11,
                    Weight = 600,
                    Alignment = TextAlign.Left,
                    Padding = 0
                }
            }
        };
        _header.AddChild(_headerTitle);
        AddChild(_header);

        _save = new IconButton("➕ Save Preset");
        _save.Clicked += () => SaveClicked?.Invoke();
        AddChild(_save);

        _list = new PresetScrollList();
        AddChild(_list);
    }

    public void SetItems(IReadOnlyList<string> names)
    {
        names ??= Array.Empty<string>();

        var previous = _rows.ToArray();
        _rows.Clear();
        _list.ClearItems();
        for (int i = 0; i < previous.Length; i++)
        {
            previous[i].Dispose();
        }

        for (int i = 0; i < names.Count; i++)
        {
            string name = names[i] ?? "";
            var row = new NameRow(name, PresetStore.IsUser(name));
            row.Events.OnClick += (_, args) =>
            {
                if (args.Button != (int)MouseButton.Left) return;
                Applied?.Invoke(name);
                args.Handled = true;
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
        float h = HeaderH + SaveH + 16f + _rows.Count * RowH + 16f;
        if (maxHeight > 0) h = Math.Min(h, maxHeight);
        return new SKSize(w, h);
    }

    protected override void LayoutChildren()
    {
        float originX = Transform.Computed.X;
        float originY = Transform.Computed.Y;
        float w = Math.Max(1f, Transform.Width);
        float h = Math.Max(1f, Transform.Height);

        // Header bar
        _header.Transform.SetAbsoluteFrame(originX, originY, w, HeaderH);
        _headerTitle.Transform.SetAbsoluteFrame(originX + 8f, originY + (HeaderH - 18f) * 0.5f, w - 16f, 18f);

        // Save preset action button
        float saveY = originY + HeaderH + 6f;
        _save.Transform.SetAbsoluteFrame(originX + 8f, saveY, w - 16f, SaveH);

        // Recessed presets list well
        float listY = saveY + SaveH + 6f;
        float listH = Math.Max(24f, originY + h - listY - 8f);
        _list.Transform.SetAbsoluteFrame(originX + 8f, listY, w - 16f, listH);
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
            Style = new ElementStyle
            {
                BackColor = Theme.Well,
                Border = new BorderStyle
                {
                    Width = 1,
                    Color = Theme.HairlineSubtle,
                    Roundness = Theme.RadiusSm
                }
            };
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
                var row = _items[i];
                row.Transform.SetAbsoluteFrame(ox + 2f, oy + y, w - 4f, RowH);
                y += RowH;
            }

            SetContentSize(w, Math.Max(y + 2f, Transform.Computed.Height));
            base.LayoutChildren();
        }
    }

    private sealed class NameRow : VisualElement
    {
        public event Action? DeleteClicked;

        public NameRow(string name, bool canDelete)
        {
            Name = $"Preset_{name}";
            Text = $"🎨  {name}";
            Cursor = StandardCursor.Hand;
            Style = new ElementStyle
            {
                BackColor = SKColors.Transparent,
                Border = new BorderStyle
                {
                    Width = 0,
                    Color = SKColors.Transparent,
                    Roundness = Theme.RadiusSm
                },
                Text = new TextStyle
                {
                    Color = Theme.TextDim,
                    Size = 11,
                    Weight = 400,
                    Alignment = TextAlign.Left,
                    Padding = 8
                }
            };

            if (canDelete)
            {
                var del = new VisualElement
                {
                    Name = $"{Name}_Del",
                    Text = "✕",
                    Cursor = StandardCursor.Hand,
                    Style = new ElementStyle
                    {
                        BackColor = SKColors.Transparent,
                        Border = new BorderStyle { Width = 0, Roundness = 2 },
                        Text = new TextStyle
                        {
                            Color = Theme.TextDim,
                            Size = 11,
                            Weight = 600,
                            Alignment = TextAlign.Center
                        }
                    }
                };
                del.Events.OnMouseEnter += _ =>
                {
                    del.Style.BackColor = Theme.Hover;
                    del.Style.Text.Color = Theme.Text;
                    InvalidatePaint();
                };
                del.Events.OnMouseLeave += _ =>
                {
                    del.Style.BackColor = SKColors.Transparent;
                    del.Style.Text.Color = Theme.TextDim;
                    InvalidatePaint();
                };
                del.Events.OnClick += (_, args) =>
                {
                    args.Handled = true;
                    DeleteClicked?.Invoke();
                };
                AddChild(del);
            }

            Events.OnMouseEnter += _ =>
            {
                Style.BackColor = Theme.Selected;
                Style.Text.Color = Theme.Text;
                InvalidatePaint();
            };
            Events.OnMouseLeave += _ =>
            {
                Style.BackColor = SKColors.Transparent;
                Style.Text.Color = Theme.TextDim;
                InvalidatePaint();
            };
        }

        protected override void LayoutChildren()
        {
            if (Children.Count == 0) return;
            float ox = Transform.Computed.X;
            float oy = Transform.Computed.Y;
            float w = Math.Max(1f, Transform.Width);
            float h = Math.Max(1f, Transform.Height);
            Children[0].Transform.SetAbsoluteFrame(ox + w - 22, oy + (h - 18) * 0.5f, 18, 18);
        }
    }
}
