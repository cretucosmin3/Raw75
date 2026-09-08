using System;
using System.Collections.Generic;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Raw75.Presets;
using Silk.NET.Input;
using SkiaSharp;

namespace Raw75.Controls;

/// <summary>Named looks: save row plus a clipped vertical list of presets.</summary>
public class PresetList : VisualElement
{
    private const float RowH = 28f;
    private readonly IconButton _save;
    private readonly List<NameRow> _rows = new();

    public event Action<string>? Applied;
    public event Action? SaveClicked;
    public event Action<string>? Deleted;

    public PresetList()
    {
        Name = "PresetList";
        Overflow = OverflowMode.Clip;
        Padding = new Thickness(0, 0, 0, 4);
        Style = new ElementStyle
        {
            BackColor = Theme.Panel,
            Border = new BorderStyle
            {
                Width = 1,
                Color = Theme.Hairline,
                Roundness = Theme.Radius
            }
        };

        _save = new IconButton("Save preset");
        _save.Clicked += () => SaveClicked?.Invoke();
        AddChild(_save);
    }

    public void SetItems(IReadOnlyList<string> names)
    {
        names ??= Array.Empty<string>();

        var previous = _rows.ToArray();
        _rows.Clear();
        for (int i = 0; i < previous.Length; i++)
        {
            RemoveChild(previous[i]);
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
            AddChild(row);
        }

        InvalidateLayout();
        InvalidatePaint();
    }

    public override SKSize GetPreferredSize(float maxWidth, float maxHeight)
    {
        float w = maxWidth > 0 ? maxWidth : (Transform.Width > 0 ? Transform.Width : Theme.LeftW);
        float h = RowH + Padding.Vertical + _rows.Count * RowH;
        if (maxHeight > 0) h = Math.Min(h, maxHeight);
        return new SKSize(w, h);
    }

    protected override void LayoutChildren()
    {
        float originX = Transform.Computed.X;
        float originY = Transform.Computed.Y;
        float w = Math.Max(1f, Transform.Width);

        _save.Transform.SetAbsoluteFrame(originX, originY, w, RowH);

        float y = RowH + Padding.Top;
        for (int i = 0; i < _rows.Count; i++)
        {
            _rows[i].Transform.SetAbsoluteFrame(originX, originY + y, w, RowH);
            y += RowH;
        }
    }

    private sealed class NameRow : VisualElement
    {
        public event Action? DeleteClicked;

        public NameRow(string name, bool canDelete)
        {
            Name = $"Preset_{name}";
            Text = name;
            Cursor = StandardCursor.Hand;
            Style = new ElementStyle
            {
                BackColor = SKColors.Transparent,
                Text = new TextStyle
                {
                    Color = Theme.TextDim,
                    Size = 11,
                    Weight = 400,
                    Alignment = TextAlign.Left,
                    Padding = 10
                }
            };

            if (canDelete)
            {
                var x = new VisualElement
                {
                    Name = $"{Name}_Del",
                    Text = "×",
                    Cursor = StandardCursor.Hand,
                    Style = new ElementStyle
                    {
                        BackColor = SKColors.Transparent,
                        Text = new TextStyle
                        {
                            Color = Theme.TextDim,
                            Size = 14,
                            Weight = 600,
                            Alignment = TextAlign.Center
                        }
                    }
                };
                x.Events.OnClick += (_, args) =>
                {
                    args.Handled = true;
                    DeleteClicked?.Invoke();
                };
                AddChild(x);
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
            if (Children.Count == 0)
                return;
            float ox = Transform.Computed.X;
            float oy = Transform.Computed.Y;
            float w = Math.Max(1f, Transform.Width);
            Children[0].Transform.SetAbsoluteFrame(ox + w - 22, oy + 4, 18, 20);
        }
    }
}
