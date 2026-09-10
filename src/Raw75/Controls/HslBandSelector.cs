using System;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Silk.NET.Input;
using SkiaSharp;

namespace Raw75.Controls;

/// <summary>
/// 6-color swatch tab selector for HSL editing.
/// Clicking a chip switches active color band without vertical scrolling.
/// </summary>
public sealed class HslBandSelector : VisualElement
{
    private static readonly string[] Names = { "Red", "Orange", "Yellow", "Green", "Aqua", "Blue" };
    private static readonly SKColor[] SwatchColors =
    {
        new(239, 68, 68),   // Red
        new(249, 115, 22),  // Orange
        new(234, 179, 8),   // Yellow
        new(34, 197, 94),   // Green
        new(6, 182, 212),   // Aqua
        new(59, 130, 246)   // Blue
    };

    private readonly VisualElement[] _chips = new VisualElement[6];
    private int _selectedIndex = 0;

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            int clamped = Math.Clamp(value, 0, 5);
            if (_selectedIndex == clamped) return;
            _selectedIndex = clamped;
            UpdateChipStyles();
            BandSelected?.Invoke(_selectedIndex);
        }
    }

    public event Action<int>? BandSelected;

    public HslBandSelector()
    {
        Name = "HslBandSelector";
        Transform.Height = 24f;
        Style = new ElementStyle { BackColor = SKColors.Transparent };

        for (int i = 0; i < 6; i++)
        {
            int idx = i;
            var chip = new VisualElement
            {
                Name = $"HslChip_{Names[i]}",
                Text = Names[i],
                Cursor = StandardCursor.Hand,
                Style = new ElementStyle
                {
                    BackColor = Theme.Well,
                    Border = new BorderStyle
                    {
                        Width = 1,
                        Color = Theme.Hairline,
                        Roundness = Theme.RadiusSm
                    },
                    Text = new TextStyle
                    {
                        Color = Theme.TextSecondary,
                        Size = 12,
                        Weight = 500,
                        Alignment = TextAlign.Center,
                        Padding = 0
                    }
                }
            };

            chip.Events.OnClick += (_, args) =>
            {
                if (args.Button != (int)MouseButton.Left) return;
                SelectedIndex = idx;
                args.Handled = true;
            };

            _chips[i] = chip;
            AddChild(chip);
        }

        UpdateChipStyles();
    }

    public override SKSize GetPreferredSize(float maxWidth, float maxHeight)
    {
        float w = maxWidth > 0 ? maxWidth : Theme.RightW;
        return new SKSize(w, 28f);
    }

    protected override void LayoutChildren()
    {
        float ox = Transform.Computed.X;
        float oy = Transform.Computed.Y;
        float w = Math.Max(1f, Transform.Width);
        float h = Transform.Height;
        float gap = 4f;
        float cellW = Math.Max(1f, (w - gap * 5f) / 6f);

        for (int i = 0; i < 6; i++)
        {
            _chips[i].Transform.SetAbsoluteFrame(ox + i * (cellW + gap), oy, cellW, h);
        }
    }

    private void UpdateChipStyles()
    {
        for (int i = 0; i < 6; i++)
        {
            bool selected = i == _selectedIndex;
            var chip = _chips[i];
            if (chip.Style == null) continue;

            chip.Style.BackColor = selected ? Theme.Lighten(Theme.SectionHeaderHover, 18) : Theme.Well;
            chip.Style.Border = new BorderStyle
            {
                Width = 1,
                Color = selected ? SwatchColors[i] : Theme.Hairline,
                Roundness = Theme.RadiusSm
            };

            if (chip.Style.Text != null)
            {
                chip.Style.Text.Color = selected ? Theme.Text : Theme.TextDim;
                chip.Style.Text.Weight = selected ? 600 : 500;
            }
            chip.InvalidatePaint();
        }
        InvalidatePaint();
    }
}
