using System;
using System.IO;
using System.Text.Json;

namespace Raw75.Io;

/// <summary>App-wide UI prefs (theme id), stored next to workspace.json.</summary>
public static class UiPrefs
{
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Raw75",
        "ui.json");

    public static string LoadThemeId()
    {
        try
        {
            if (!File.Exists(Path))
                return Theme.Midnight.Id;
            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(Path));
            return string.IsNullOrWhiteSpace(dto?.Theme) ? Theme.Midnight.Id : dto.Theme;
        }
        catch
        {
            return Theme.Midnight.Id;
        }
    }

    public static void SaveThemeId(string id)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(new Dto { Theme = id ?? Theme.Midnight.Id }));
        }
        catch
        {
            // Prefs are optional; a failed write must not block the UI.
        }
    }

    private sealed class Dto
    {
        public string? Theme { get; set; }
    }
}
