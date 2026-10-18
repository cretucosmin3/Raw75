using System;
using SkiaSharp;

namespace Raw75.Controls;

/// <summary>Six colour-band segments for the HSL editor; picking one swaps the three active sliders.</summary>
public sealed class HslBandSelector : SegmentedControl
{
    private static readonly (string, SKColor?)[] Bands =
    {
        ("Red", new SKColor(239, 68, 68)),
        ("Orange", new SKColor(249, 115, 22)),
        ("Yellow", new SKColor(234, 179, 8)),
        ("Green", new SKColor(34, 197, 94)),
        ("Aqua", new SKColor(6, 182, 212)),
        ("Blue", new SKColor(59, 130, 246))
    };

    public event Action<int>? BandSelected;

    public HslBandSelector() : base(Bands)
    {
        Name = "HslBandSelector";
        SelectionChanged += i => BandSelected?.Invoke(i);
    }
}
