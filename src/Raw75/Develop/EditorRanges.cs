using System;
using System.Collections.Generic;
using Blossom.Reactive;
using Raw75.Io;

namespace Raw75.Develop;

/// <summary>Develop controls whose slider reach (±100 / ±150 / ±200) is a user setting.</summary>
public enum RangedControl
{
    Contrast,
    Saturation,
    Highlights,
    Shadows,
    Whites,
    Blacks
}

/// <summary>
/// How far each <see cref="RangedControl"/> slider goes, as a signal per control (sliders bind to it, the
/// Settings › Editor tab writes it). Persisted in <see cref="UiPrefs"/>. UI thread only.
/// </summary>
public static class EditorRanges
{
    public const int Default = 100;

    /// <summary>The reach choices offered in Settings.</summary>
    public static readonly int[] Choices = { 100, 150, 200 };

    /// <summary>The largest reach any setting allows; develop code may clamp to this, never tighter.</summary>
    public const int Max = 200;

    private static readonly Dictionary<RangedControl, Signal<int>> Ranges = Load();

    /// <summary>The reach of <paramref name="control"/>; read it in a binding to follow changes.</summary>
    public static Signal<int> Of(RangedControl control) => Ranges[control];

    private static Dictionary<RangedControl, Signal<int>> Load()
    {
        var saved = UiPrefs.LoadSliderRanges();
        var ranges = new Dictionary<RangedControl, Signal<int>>();
        foreach (RangedControl control in Enum.GetValues<RangedControl>())
        {
            int range = saved.TryGetValue(control.ToString(), out int r) && Array.IndexOf(Choices, r) >= 0 ? r : Default;
            var signal = new Signal<int>(range);
            ranges[control] = signal;
        }
        return ranges;
    }

    /// <summary>Sets and saves the reach of <paramref name="control"/>.</summary>
    public static void Set(RangedControl control, int range)
    {
        if (Array.IndexOf(Choices, range) < 0)
            return;
        var signal = Ranges[control];
        if (signal.Peek() == range)
            return;
        signal.Value = range;
        UiPrefs.SaveSliderRange(control.ToString(), range);
    }
}
