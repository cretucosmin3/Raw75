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
            if (settings.CurveRgb == null || settings.CurveRgb.Length < 2)
                settings.CurveRgb = Raw75.Pipeline.CurveMath.DefaultCurve();
            if (settings.CurveRed == null || settings.CurveRed.Length < 2)
                settings.CurveRed = Raw75.Pipeline.CurveMath.DefaultCurve();
            if (settings.CurveGreen == null || settings.CurveGreen.Length < 2)
                settings.CurveGreen = Raw75.Pipeline.CurveMath.DefaultCurve();
            if (settings.CurveBlue == null || settings.CurveBlue.Length < 2)
                settings.CurveBlue = Raw75.Pipeline.CurveMath.DefaultCurve();
            return settings;
        }

        return null;
    }

    public static bool IsUser(string name)
    {
        if (!TryFileName(name, out var file))
            return false;
        return File.Exists(Path.Combine(UserDir, file));
    }

    public static bool Delete(string name)
    {
        if (!TryFileName(name, out var file))
            return false;
        var path = Path.Combine(UserDir, file);
        if (!File.Exists(path))
            return false;
        File.Delete(path);
        return true;
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
