using System;
using System.Collections.Generic;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Silk.NET.Input;
using SkiaSharp;

namespace Raw75.Controls;

/// <summary>Named looks: save row plus a clipped vertical list of presets.</summary>
public class PresetList : VisualElement
{
    private const float RowH = 28f;
    private readonly VisualElement _save;
    private readonly List<NameRow> _rows = new();

    public event Action<string>? Applied;
    public event Action? SaveClicked;

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

        _save = new VisualElement
        {
            Name = "PresetList_Save",
            Text = "Save preset",
            Cursor = StandardCursor.Hand,
            Style = new ElementStyle
            {
                BackColor = Theme.PanelAlt,
                Border = new BorderStyle
                {
                    Width = 0,
                    Color = Theme.Hairline,
                    Roundness = 0
                },
                Text = new TextStyle
                {
                    Color = Theme.Text,
                    Size = 13,
                    Weight = 600,
                    Alignment = TextAlign.Left,
                    Padding = 8
                }
            }
        };
        _save.Events.OnClick += (_, args) =>
        {
            if (args.Button != (int)MouseButton.Left) return;
            SaveClicked?.Invoke();
            args.Handled = true;
        };
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
            var row = new NameRow(name);
            row.Events.OnClick += (_, args) =>
            {
                if (args.Button != (int)MouseButton.Left) return;
                Applied?.Invoke(name);
                args.Handled = true;
            };
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
        public NameRow(string name)
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
    }
}
