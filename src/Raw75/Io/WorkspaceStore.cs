using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Raw75.Develop;
using Raw75.Imaging;
using SkiaSharp;

namespace Raw75.Io;

/// <summary>
/// A folder the user picks as the catalog. Sidecars and preview JPEGs live in
/// <c>.raw75/cache/</c> so filmstrip/orientation survive click-away and restarts.
/// </summary>
public static class WorkspaceStore
{
    public const string FolderName = ".raw75";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly HashSet<string> PhotoExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".tif", ".tiff",
        ".dng", ".cr2", ".cr3", ".crw", ".nef", ".nrw",
        ".arw", ".srf", ".sr2", ".raf", ".orf", ".rw2",
        ".pef", ".ptx", ".raw", ".rwl", ".3fr", ".fff",
        ".mos", ".kdc", ".dcr", ".mrw", ".erf", ".x3f",
        ".srw", ".iiq", ".heic", ".heif"
    };

    public static string? Root { get; private set; }

    public static string PrefsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Raw75",
        "workspace.json");

    public static void Bind(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            Root = null;
            SavePrefs(null, null);
            return;
        }

        Root = Path.GetFullPath(folder);
        Directory.CreateDirectory(CacheRoot());
        SavePrefs(Root, null);
    }

    public static bool IsPhotoPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        return PhotoExt.Contains(Path.GetExtension(path));
    }

    public static IReadOnlyList<string> EnumeratePhotos(string folder)
    {
        var list = new List<string>();
        string? root = FileDialogs.NormalizeDir(folder);
        if (root == null)
        {
            Blossom.Log.Warning("EnumeratePhotos: not a directory: " + folder);
            return list;
        }

        Walk(root, list, 0);
        list.Sort(StringComparer.OrdinalIgnoreCase);
        Blossom.Log.Info("Workspace scan " + root + " → " + list.Count + " photos");
        return list;
    }

    private static void Walk(string dir, List<string> list, int depth)
    {
        if (depth > 12)
            return;

        try
        {
            foreach (string path in Directory.EnumerateFiles(dir))
            {
                if (InCacheDir(path))
                    continue;
                if (IsPhotoPath(path))
                    list.Add(path);
            }
        }
        catch (Exception ex)
        {
            Blossom.Log.Warning("Workspace scan files in " + dir + ": " + ex.Message);
        }

        try
        {
            foreach (string sub in Directory.EnumerateDirectories(dir))
            {
                string name = Path.GetFileName(sub);
                if (string.Equals(name, FolderName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase))
                    continue;
                Walk(sub, list, depth + 1);
            }
        }
        catch (Exception ex)
        {
            Blossom.Log.Warning("Workspace scan dirs in " + dir + ": " + ex.Message);
        }
    }

    public static bool Hydrate(PhotoDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        string dir = EntryDir(doc.Path);
        string metaPath = Path.Combine(dir, "meta.json");
        if (!File.Exists(metaPath))
            return false;

        try
        {
            var meta = JsonSerializer.Deserialize<CacheMeta>(File.ReadAllText(metaPath), JsonOptions);
            if (meta?.Settings == null || !StampMatches(doc.Path, meta))
                return false;

            if (meta.Settings.Hsl == null || meta.Settings.Hsl.Length != 6)
                meta.Settings.Hsl = DevelopSettings.CreateHsl();
            doc.Settings.CopyFrom(meta.Settings);

            SKImage? preview = LoadJpeg(Path.Combine(dir, "preview.jpg"));
            if (preview != null)
            {
                SKImage? old = doc.Preview;
                doc.Preview = preview;
                if (old != null && !ReferenceEquals(old, doc.Thumb) && !ReferenceEquals(old, doc.Look))
                    GpuRetain.Retire(old);
            }

            return true;
        }
        catch (Exception ex)
        {
            Blossom.Log.Warning("Workspace hydrate: " + ex.Message);
            return false;
        }
    }

    public static void HydrateLook(PhotoDocument doc)
    {
        if (doc == null || doc.Look != null)
            return;
        try
        {
            SKImage? look = LoadJpeg(Path.Combine(EntryDir(doc.Path), "look.jpg"));
            if (look == null)
                return;
            SKImage? old = doc.Look;
            doc.Look = look;
            if (old != null && !ReferenceEquals(old, doc.Thumb) && !ReferenceEquals(old, doc.Preview))
                GpuRetain.Retire(old);
        }
        catch (Exception ex)
        {
            Blossom.Log.Warning("Workspace hydrate look: " + ex.Message);
        }
    }

    public static void SaveSettings(PhotoDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        try
        {
            string dir = EntryDir(doc.Path);
            Directory.CreateDirectory(dir);
            var meta = Stamp(doc.Path);
            meta.Settings = doc.Settings.Clone();
            File.WriteAllText(Path.Combine(dir, "meta.json"), JsonSerializer.Serialize(meta, JsonOptions));
        }
        catch (Exception ex)
        {
            Blossom.Log.Warning("Workspace save settings: " + ex.Message);
        }
    }

    public static void SaveLooks(PhotoDocument doc, DevelopSettings settings, byte[]? previewJpeg, byte[]? lookJpeg)
    {
        ArgumentNullException.ThrowIfNull(doc);
        try
        {
            string dir = EntryDir(doc.Path);
            Directory.CreateDirectory(dir);
            var meta = Stamp(doc.Path);
            meta.Settings = settings.Clone();
            File.WriteAllText(Path.Combine(dir, "meta.json"), JsonSerializer.Serialize(meta, JsonOptions));
            if (previewJpeg is { Length: > 0 })
                File.WriteAllBytes(Path.Combine(dir, "preview.jpg"), previewJpeg);
            if (lookJpeg is { Length: > 0 })
                File.WriteAllBytes(Path.Combine(dir, "look.jpg"), lookJpeg);
        }
        catch (Exception ex)
        {
            Blossom.Log.Warning("Workspace save looks: " + ex.Message);
        }
    }

    internal static byte[]? EncodeJpeg(RasterBuffer buf, int quality = 82)
    {
        if (!buf.HasPixels || buf.Rgba == null || buf.Width < 1 || buf.Height < 1)
            return null;

        var info = new SKImageInfo(buf.Width, buf.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bmp = new SKBitmap(info);
        IntPtr pixels = bmp.GetPixels();
        if (pixels == IntPtr.Zero)
            return null;

        int srcStride = buf.Width * 4;
        int dstStride = bmp.RowBytes;
        if (dstStride == srcStride)
        {
            Marshal.Copy(buf.Rgba, 0, pixels, srcStride * buf.Height);
        }
        else
        {
            for (int y = 0; y < buf.Height; y++)
                Marshal.Copy(buf.Rgba, y * srcStride, pixels + y * dstStride, srcStride);
        }

        using SKImage image = SKImage.FromBitmap(bmp);
        using SKData? data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data?.ToArray();
    }

    public static string? LoadLastRoot()
    {
        try
        {
            if (!File.Exists(PrefsPath))
                return null;
            var prefs = JsonSerializer.Deserialize<WorkspacePrefs>(File.ReadAllText(PrefsPath), JsonOptions);
            if (prefs?.Root == null || !Directory.Exists(prefs.Root))
                return null;
            return prefs.Root;
        }
        catch
        {
            return null;
        }
    }

    public static string? LoadLastActive(string root)
    {
        try
        {
            if (!File.Exists(PrefsPath))
                return null;
            var prefs = JsonSerializer.Deserialize<WorkspacePrefs>(File.ReadAllText(PrefsPath), JsonOptions);
            if (prefs == null || !string.Equals(prefs.Root, root, StringComparison.OrdinalIgnoreCase))
                return null;
            return prefs.ActivePath;
        }
        catch
        {
            return null;
        }
    }

    public static void RememberActive(string? path)
    {
        SavePrefs(Root, path);
    }

    private static void SavePrefs(string? root, string? active)
    {
        try
        {
            string? dir = Path.GetDirectoryName(PrefsPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var prefs = new WorkspacePrefs { Root = root, ActivePath = active };
            File.WriteAllText(PrefsPath, JsonSerializer.Serialize(prefs, JsonOptions));
        }
        catch (Exception ex)
        {
            Blossom.Log.Warning("Workspace prefs: " + ex.Message);
        }
    }

    private static SKImage? LoadJpeg(string path)
    {
        if (!File.Exists(path))
            return null;
        using var bmp = SKBitmap.Decode(path);
        if (bmp == null || bmp.Width < 1)
            return null;
        return SKImage.FromBitmap(bmp);
    }

    private static string CacheRoot()
    {
        if (!string.IsNullOrEmpty(Root))
            return Path.Combine(Root, FolderName, "cache");
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Raw75",
            "cache");
    }

    private static string EntryDir(string photoPath)
    {
        string full = Path.GetFullPath(photoPath);
        byte[] hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(full));
        string id = Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
        return Path.Combine(CacheRoot(), id);
    }

    private static bool InCacheDir(string path)
    {
        return path.Replace('\\', '/').Contains("/" + FolderName + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static CacheMeta Stamp(string path)
    {
        var info = new FileInfo(path);
        return new CacheMeta
        {
            Source = Path.GetFullPath(path),
            Length = info.Exists ? info.Length : 0,
            MtimeUtc = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue
        };
    }

    private static bool StampMatches(string path, CacheMeta meta)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return false;
            return info.Length == meta.Length
                   && Math.Abs((info.LastWriteTimeUtc - meta.MtimeUtc).TotalSeconds) < 2;
        }
        catch
        {
            return false;
        }
    }

    private sealed class CacheMeta
    {
        public string Source { get; set; } = "";
        public long Length { get; set; }
        public DateTime MtimeUtc { get; set; }
        public DevelopSettings? Settings { get; set; }
    }

    private sealed class WorkspacePrefs
    {
        public string? Root { get; set; }
        public string? ActivePath { get; set; }
    }
}
