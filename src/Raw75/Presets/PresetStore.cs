using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Raw75.Develop;
using Raw75.Pipeline;

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

    public static LookPreset? Load(string name)
    {
        if (!TryFileName(name, out var file))
            return null;

        foreach (var dir in new[] { UserDir, BuiltinDir })
        {
            var path = Path.Combine(dir, file);
            if (!File.Exists(path))
                continue;

            var json = File.ReadAllText(path);
            var preset = Deserialize(json);
            if (preset?.Settings == null)
                continue;
            NormalizeSettings(preset.Settings);
            StripTransforms(preset.Settings);
            return preset;
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

    public static void Save(string name, DevelopSettings s, SceneMetrics? source = null, SceneMetrics? look = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (!TryFileName(name, out var file))
            throw new ArgumentException("Invalid preset name.", nameof(name));

        var clean = s.Clone();
        StripTransforms(clean);
        clean.EnableGeometry = true;
        clean.BaseExposure = 0f;

        var preset = new LookPreset
        {
            Settings = clean,
            Source = source,
            Look = look
        };

        Directory.CreateDirectory(UserDir);
        var json = JsonSerializer.Serialize(preset, JsonOptions);
        File.WriteAllText(Path.Combine(UserDir, file), json);
    }

    internal static LookPreset Deserialize(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("Settings", out _))
        {
            return JsonSerializer.Deserialize<LookPreset>(json, JsonOptions) ?? new LookPreset();
        }

        var settings = JsonSerializer.Deserialize<DevelopSettings>(json, JsonOptions) ?? new DevelopSettings();
        return new LookPreset { Settings = settings };
    }

    private static void NormalizeSettings(DevelopSettings settings)
    {
        if (settings.Hsl == null || settings.Hsl.Length != 6)
            settings.Hsl = DevelopSettings.CreateHsl();
        if (settings.CurveRgb == null || settings.CurveRgb.Length < 2)
            settings.CurveRgb = CurveMath.DefaultCurve();
        if (settings.CurveRed == null || settings.CurveRed.Length < 2)
            settings.CurveRed = CurveMath.DefaultCurve();
        if (settings.CurveGreen == null || settings.CurveGreen.Length < 2)
            settings.CurveGreen = CurveMath.DefaultCurve();
        if (settings.CurveBlue == null || settings.CurveBlue.Length < 2)
            settings.CurveBlue = CurveMath.DefaultCurve();
        if (settings.ExposureCurve == null || settings.ExposureCurve.Length < 2)
            settings.ExposureCurve = CurveMath.DefaultExposureCurve();
    }

    private static void StripTransforms(DevelopSettings settings)
    {
        settings.CropX = 0;
        settings.CropY = 0;
        settings.CropW = 0;
        settings.CropH = 0;
        settings.Straighten = 0;
        settings.Rotate90 = 0;
        settings.FlipH = false;
        settings.FlipV = false;
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
