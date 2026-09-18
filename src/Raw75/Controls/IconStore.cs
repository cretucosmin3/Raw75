using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Blossom;
using Blossom.Core;
using SkiaSharp.Extended.Svg;

namespace Raw75.Controls;

/// <summary>
/// Provides resolution and loading of SVG icons for Blossom VisualElements.
/// </summary>
public static class IconStore
{
    private static readonly Dictionary<string, string> FallbackSvgXml = new(StringComparer.OrdinalIgnoreCase)
    {
        ["viewer"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 24 24"" fill=""none"" stroke=""#FFFFFF"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><rect x=""3"" y=""3"" width=""18"" height=""18"" rx=""2""/><circle cx=""8.5"" cy=""8.5"" r=""1.5""/><path d=""M21 15l-5-5L5 21""/></svg>",
        ["gallery"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 24 24"" fill=""none"" stroke=""#FFFFFF"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><rect x=""3"" y=""3"" width=""7"" height=""7"" rx=""1.5""/><rect x=""14"" y=""3"" width=""7"" height=""7"" rx=""1.5""/><rect x=""14"" y=""14"" width=""7"" height=""7"" rx=""1.5""/><rect x=""3"" y=""14"" width=""7"" height=""7"" rx=""1.5""/></svg>",
        ["gallery_size_s"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 24 24"" fill=""none"" stroke=""#FFFFFF"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><rect x=""3"" y=""3"" width=""5"" height=""5"" rx=""1""/><rect x=""9.5"" y=""3"" width=""5"" height=""5"" rx=""1""/><rect x=""16"" y=""3"" width=""5"" height=""5"" rx=""1""/><rect x=""3"" y=""9.5"" width=""5"" height=""5"" rx=""1""/><rect x=""9.5"" y=""9.5"" width=""5"" height=""5"" rx=""1""/><rect x=""16"" y=""9.5"" width=""5"" height=""5"" rx=""1""/><rect x=""3"" y=""16"" width=""5"" height=""5"" rx=""1""/><rect x=""9.5"" y=""16"" width=""5"" height=""5"" rx=""1""/><rect x=""16"" y=""16"" width=""5"" height=""5"" rx=""1""/></svg>",
        ["gallery_size_m"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 24 24"" fill=""none"" stroke=""#FFFFFF"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><rect x=""3"" y=""3"" width=""8"" height=""8"" rx=""1.5""/><rect x=""13"" y=""3"" width=""8"" height=""8"" rx=""1.5""/><rect x=""3"" y=""13"" width=""8"" height=""8"" rx=""1.5""/><rect x=""13"" y=""13"" width=""8"" height=""8"" rx=""1.5""/></svg>",
        ["gallery_size_l"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 24 24"" fill=""none"" stroke=""#FFFFFF"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><rect x=""4"" y=""4"" width=""16"" height=""16"" rx=""2""/></svg>",
        ["open"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 24 24"" fill=""none"" stroke=""#FFFFFF"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><path d=""M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z""/><polyline points=""14 2 14 8 20 8""/><line x1=""12"" y1=""18"" x2=""12"" y2=""12""/><polyline points=""9 15 12 12 15 15""/></svg>",
        ["folder"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 24 24"" fill=""none"" stroke=""#FFFFFF"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><path d=""M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z""/></svg>",
        ["settings"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 24 24"" fill=""none"" stroke=""#FFFFFF"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><circle cx=""12"" cy=""12"" r=""3""/><path d=""M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1 0 2.83 2 2 0 0 1-2.83 0l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-2 2 2 2 0 0 1-2-2v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83 0 2 2 0 0 1 0-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1-2-2 2 2 0 0 1 2-2h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 0-2.83 2 2 0 0 1 2.83 0l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 2-2 2 2 0 0 1 2 2v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 0 2 2 0 0 1 0 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 2 2 2 2 0 0 1-2 2h-.09a1.65 1.65 0 0 0-1.51 1z""/></svg>",
        ["export"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 24 24"" fill=""none"" stroke=""#FFFFFF"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><path d=""M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4""/><polyline points=""17 8 12 3 7 8""/><line x1=""12"" y1=""3"" x2=""12"" y2=""15""/></svg>",
        ["crop"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 24 24"" fill=""none"" stroke=""#FFFFFF"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><path d=""M6.13 1L6 16a2 2 0 0 0 2 2h15""/><path d=""M1 6.13L16 6a2 2 0 0 1 2 2v15""/></svg>",
        ["before"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 24 24"" fill=""none"" stroke=""#FFFFFF"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><rect x=""3"" y=""5"" width=""14"" height=""14"" rx=""2""/><path d=""M7 5V4a1 1 0 0 1 1-1h12a1 1 0 0 1 1 1v12a1 1 0 0 1-1 1h-1""/></svg>",
        ["split"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 24 24"" fill=""none"" stroke=""#FFFFFF"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><rect x=""3"" y=""3"" width=""18"" height=""18"" rx=""2""/><line x1=""12"" y1=""3"" x2=""12"" y2=""21""/><path d=""M3 3h9v18H3z"" fill=""#FFFFFF"" fill-opacity=""0.35""/></svg>",
        ["rotate_left"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 24 24"" fill=""none"" stroke=""#FFFFFF"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><path d=""M3 12a9 9 0 1 0 9-9 9.75 9.75 0 0 0-6.74 2.74L3 8""/><polyline points=""3 3 3 8 8 8""/></svg>",
        ["rotate_right"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 24 24"" fill=""none"" stroke=""#FFFFFF"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><path d=""M21 12a9 9 0 1 1-9-9 9.75 9.75 0 0 1 6.74 2.74L21 8""/><polyline points=""21 3 21 8 16 8""/></svg>",
        ["flip_h"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 24 24"" fill=""none"" stroke=""#FFFFFF"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><line x1=""12"" y1=""2"" x2=""12"" y2=""22"" stroke-dasharray=""2 3""/><polyline points=""7 8 3 12 7 16""/><polyline points=""17 8 21 12 17 16""/><line x1=""3"" y1=""12"" x2=""9"" y2=""12""/><line x1=""15"" y1=""12"" x2=""21"" y2=""12""/></svg>",
        ["flip_v"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 24 24"" fill=""none"" stroke=""#FFFFFF"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><line x1=""2"" y1=""12"" x2=""22"" y2=""12"" stroke-dasharray=""2 3""/><polyline points=""8 7 12 3 16 7""/><polyline points=""8 17 12 21 16 17""/><line x1=""12"" y1=""3"" x2=""12"" y2=""9""/><line x1=""12"" y1=""15"" x2=""12"" y2=""21""/></svg>",
        ["plus"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 24 24"" fill=""none"" stroke=""#FFFFFF"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><line x1=""12"" y1=""5"" x2=""12"" y2=""19""/><line x1=""5"" y1=""12"" x2=""19"" y2=""12""/></svg>",
        ["check"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 24 24"" fill=""none"" stroke=""#FFFFFF"" stroke-width=""2.5"" stroke-linecap=""round"" stroke-linejoin=""round""><polyline points=""20 6 9 17 4 12""/></svg>",
        ["star"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 24 24"" fill=""none"" stroke=""#FFFFFF"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><polygon points=""12 2 15.09 8.26 22 9.27 17 14.14 18.18 21.02 12 17.77 5.82 21.02 7 14.14 2 9.27 8.91 8.26 12 2""/></svg>",
        ["star_filled"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 24 24"" fill=""#FFFFFF"" stroke=""#FFFFFF"" stroke-width=""1.2"" stroke-linejoin=""round""><polygon points=""12 2 15.09 8.26 22 9.27 17 14.14 18.18 21.02 12 17.77 5.82 21.02 7 14.14 2 9.27 8.91 8.26 12 2""/></svg>",
        ["cross"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 24 24"" fill=""none"" stroke=""#FFFFFF"" stroke-width=""2.5"" stroke-linecap=""round"" stroke-linejoin=""round""><line x1=""18"" y1=""6"" x2=""6"" y2=""18""/><line x1=""6"" y1=""6"" x2=""18"" y2=""18""/></svg>",
        ["scale"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 24 24"" fill=""none"" stroke=""#FFFFFF"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><path d=""M12 3v18""/><path d=""M5 6l7-3 7 3""/><path d=""M2 13l3-7 3 7a3 3 0 0 1-6 0z""/><path d=""M16 13l3-7 3 7a3 3 0 0 1-6 0z""/><path d=""M4 21h16""/></svg>",
        ["dots"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 24 24"" width=""24"" height=""24"" fill=""#FFFFFF""><circle cx=""5"" cy=""12"" r=""2""/><circle cx=""12"" cy=""12"" r=""2""/><circle cx=""19"" cy=""12"" r=""2""/></svg>",
        ["fit"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 24 24"" fill=""none"" stroke=""#FFFFFF"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><path d=""M8 3H5a2 2 0 0 0-2 2v3m18 0V5a2 2 0 0 0-2-2h-3m0 18h3a2 2 0 0 0 2-2v-3M3 16v3a2 2 0 0 0 2 2h3""/></svg>",
        ["fill"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 24 24"" fill=""none"" stroke=""#FFFFFF"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><rect x=""3"" y=""3"" width=""18"" height=""18"" rx=""2""/><path d=""M9 3v18M15 3v18M3 9h18M3 15h18"" stroke-opacity=""0.3""/></svg>",
        ["chevron_down"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 24 24"" fill=""none"" stroke=""#FFFFFF"" stroke-width=""2.5"" stroke-linecap=""round"" stroke-linejoin=""round""><polyline points=""6 9 12 15 18 9""/></svg>",
        ["chevron_right"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 24 24"" fill=""none"" stroke=""#FFFFFF"" stroke-width=""2.5"" stroke-linecap=""round"" stroke-linejoin=""round""><polyline points=""9 18 15 12 9 6""/></svg>",
        ["info"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 24 24"" fill=""none"" stroke=""#FFFFFF"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><circle cx=""12"" cy=""12"" r=""10""/><line x1=""12"" y1=""16"" x2=""12"" y2=""12""/><line x1=""12"" y1=""8"" x2=""12.01"" y2=""8""/></svg>",
        ["clipping"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 24 24"" fill=""none"" stroke=""#FFFFFF"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><circle cx=""12"" cy=""12"" r=""9""/><path d=""M12 3a9 9 0 0 1 0 18z"" fill=""#FFFFFF"" fill-opacity=""0.35""/><line x1=""12"" y1=""3"" x2=""12"" y2=""21""/></svg>",
        ["copy"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 24 24"" fill=""none"" stroke=""#FFFFFF"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><rect x=""9"" y=""9"" width=""13"" height=""13"" rx=""2"" ry=""2""/><path d=""M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1""/></svg>",
        ["paste"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 24 24"" fill=""none"" stroke=""#FFFFFF"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><path d=""M16 4h2a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2h2""/><rect x=""8"" y=""2"" width=""8"" height=""4"" rx=""1"" ry=""1""/></svg>"
    };

    public static string? FindPath(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string file = name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? name : $"{name}.svg";

        // 1. AppContext BaseDirectory
        string p1 = Path.Combine(AppContext.BaseDirectory, "assets", "icons", file);
        if (File.Exists(p1)) return p1;

        // 2. CurrentDirectory
        string p2 = Path.Combine(Directory.GetCurrentDirectory(), "assets", "icons", file);
        if (File.Exists(p2)) return p2;

        // 3. Probing parents
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 4; i++)
        {
            string probe = Path.Combine(dir, "assets", "icons", file);
            if (File.Exists(probe)) return probe;
            string? parent = Path.GetDirectoryName(dir);
            if (parent == null || parent == dir) break;
            dir = parent;
        }

        return null;
    }

    public static SKSvg? LoadSvg(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        string key = name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
            ? name[..^4]
            : name;

        string? path = FindPath(key);
        if (path != null && File.Exists(path))
        {
            try
            {
                var svg = new SKSvg();
                svg.Load(path);
                return svg;
            }
            catch (Exception ex)
            {
                Log.Error($"[IconStore] Failed to load SVG from {path}: {ex.Message}");
            }
        }

        if (FallbackSvgXml.TryGetValue(key, out var xml))
        {
            try
            {
                using var ms = new MemoryStream(Encoding.UTF8.GetBytes(xml));
                var svg = new SKSvg();
                svg.Load(ms);
                return svg;
            }
            catch (Exception ex)
            {
                Log.Error($"[IconStore] Failed to load fallback SVG for '{key}': {ex.Message}");
            }
        }

        return null;
    }
}
