using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Raw75.Develop;

namespace Raw75.Presets;

public static class PresetStore
{
    public static string UserDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Raw75",
        "presets");

    private static string BuiltinDir => Path.Combine(AppContext.BaseDirectory, "Presets", "builtin");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static IReadOnlyList<string> Names()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        Collect(BuiltinDir, names);
        Collect(UserDir, names);
        return [.. names];
    }

    public static DevelopSettings? Load(string name)
    {
        if (!TryFileName(name, out var file))
            return null;

        foreach (var dir in new[] { UserDir, BuiltinDir })
        {
            var path = Path.Combine(dir, file);
            if (!File.Exists(path))
                continue;

            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<DevelopSettings>(json, JsonOptions);
            if (settings == null)
                continue;
            if (settings.Hsl == null || settings.Hsl.Length != 6)
                settings.Hsl = DevelopSettings.CreateHsl();
            return settings;
        }

        return null;
    }

    public static void Save(string name, DevelopSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (!TryFileName(name, out var file))
            throw new ArgumentException("Invalid preset name.", nameof(name));

        Directory.CreateDirectory(UserDir);
        var json = JsonSerializer.Serialize(s, JsonOptions);
        File.WriteAllText(Path.Combine(UserDir, file), json);
    }

    private static void Collect(string dir, ISet<string> names)
    {
        if (!Directory.Exists(dir))
            return;
        foreach (var path in Directory.EnumerateFiles(dir, "*.json"))
            names.Add(Path.GetFileNameWithoutExtension(path));
    }

    private static bool TryFileName(string name, out string file)
    {
        file = "";
        if (string.IsNullOrWhiteSpace(name))
            return false;
        var trimmed = name.Trim();
        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return false;
        file = trimmed + ".json";
        return true;
    }
}
