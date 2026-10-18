using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Raw75.Io;

/// <summary>App-wide UI prefs (theme id, editor slider ranges), stored next to workspace.json.</summary>
public static class UiPrefs
{
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Raw75",
        "ui.json");

    public static string LoadThemeId()
    {
        string? theme = Load().Theme;
        return string.IsNullOrWhiteSpace(theme) ? Theme.Midnight.Id : theme;
    }

    public static void SaveThemeId(string id) => Update(dto => dto.Theme = id ?? Theme.Midnight.Id);

    /// <summary>Saved slider ranges by control name; empty when none were chosen.</summary>
    public static IReadOnlyDictionary<string, int> LoadSliderRanges() =>
        Load().SliderRanges ?? new Dictionary<string, int>();

    public static void SaveSliderRange(string control, int range) => Update(dto =>
    {
        dto.SliderRanges ??= new Dictionary<string, int>();
        dto.SliderRanges[control] = range;
    });

    private static Dto Load()
    {
        try
        {
            return File.Exists(Path) ? JsonSerializer.Deserialize<Dto>(File.ReadAllText(Path)) ?? new Dto() : new Dto();
        }
        catch
        {
            return new Dto();
        }
    }

    // Read-modify-write so each setting keeps the others.
    private static void Update(Action<Dto> change)
    {
        try
        {
            var dto = Load();
            change(dto);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(dto));
        }
        catch
        {
            // Prefs are optional; a failed write must not block the UI.
        }
    }

    private sealed class Dto
    {
        public string? Theme { get; set; }
        public Dictionary<string, int>? SliderRanges { get; set; }
    }
}
