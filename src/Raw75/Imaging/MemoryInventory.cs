using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime;
using System.Text;
using Blossom;
using Raw75.Develop;
using Raw75.Io;
using Raw75.Pipeline;
using Raw75.Views;
using SkiaSharp;

namespace Raw75.Imaging;

internal sealed class MemoryDumpResult
{
    public string FilePath { get; init; } = "";
    public string Summary { get; init; } = "";
    public long ProcessBytes { get; init; }
    public long NamedBytes { get; init; }
    public int DocumentCount { get; init; }
}

internal static class MemoryInventory
{
    public static MemoryDumpResult Write(Session? session, PhotoPane? pane)
    {
        var sb = new StringBuilder(16 * 1024);
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        long named = 0;

        sb.AppendLine("======== Raw75 memory dump ========");
        sb.AppendLine($"Time        {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
        sb.AppendLine($"Runtime     {Environment.Version}  {RuntimeInformationOs()}");
        sb.AppendLine($"GC          server={GCSettings.IsServerGC}  latency={GCSettings.LatencyMode}");
        sb.AppendLine();

        AppendProcess(sb);
        sb.AppendLine();
        AppendGc(sb);
        sb.AppendLine();

        sb.AppendLine($"Workspace   {(WorkspaceStore.Root ?? "(none — LocalAppData cache)")}");
        sb.AppendLine($"CacheLimit  {session?.CacheLimit ?? 0}");
        int docs = session?.Documents.Count ?? 0;
        int active = session?.ActiveIndex ?? -1;
        sb.AppendLine($"Session     {docs} photos  active={active}");
        sb.AppendLine();

        if (session != null)
        {
            var ranks = new List<(int Index, string Name, long Bytes)>();
            for (int i = 0; i < session.Documents.Count; i++)
            {
                var doc = session.Documents[i];
                sb.AppendLine($"--- [{i}]{(i == active ? " ACTIVE" : "")} {doc.Name} ---");
                sb.AppendLine($"  path  {doc.Path}");
                long sub = 0;
                sub += AppendImage(sb, seen, "Display", doc.Display);
                sub += AppendImage(sb, seen, "Proxy  ", doc.Proxy);
                sub += AppendImage(sb, seen, "Thumb  ", doc.Thumb);
                sub += AppendImage(sb, seen, "Preview", doc.Preview);
                sub += AppendImage(sb, seen, "Look   ", doc.Look);
                sub += AppendImage(sb, seen, "ViewTile", doc.ViewportTile);
                if (doc.TileW > 0)
                    sb.AppendLine($"  tile norm  {doc.TileX:0.000},{doc.TileY:0.000}  {doc.TileW:0.000}x{doc.TileH:0.000}  native {doc.NativeWidth}x{doc.NativeHeight}");
                sub += AppendBitmap(sb, seen, "RasterKeep", doc.RasterKeep);
                sub += AppendRaster(sb, seen, "SourceRgba", doc.SourceRgba);
                sub += AppendRaster(sb, seen, "LiveRgba  ", doc.LiveRgba);
                sub += AppendRaster(sb, seen, "HiResRgba ", doc.HiResRgba);
                sub += AppendArray(sb, seen, "HistR", doc.HistogramR);
                sub += AppendArray(sb, seen, "HistG", doc.HistogramG);
                sub += AppendArray(sb, seen, "HistB", doc.HistogramB);
                sub += AppendArray(sb, seen, "HistY", doc.HistogramY);
                named += sub;
                sb.AppendLine($"  subtotal unique  {MemSize.Bytes(sub)}");
                sb.AppendLine();
                ranks.Add((i, doc.Name, sub));
            }

            ranks.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
            sb.AppendLine("Top documents by unique named bytes");
            int show = Math.Min(12, ranks.Count);
            for (int i = 0; i < show; i++)
                sb.AppendLine($"  {i + 1,2}. [{ranks[i].Index}] {ranks[i].Name}  {MemSize.Bytes(ranks[i].Bytes)}");
            sb.AppendLine();
        }

        sb.AppendLine("--- UI / GPU caches ---");
        pane?.DescribeMemory(sb);
        sb.AppendLine($"DevelopRenderer LUT  {MemSize.ImageLabel(DevelopRenderer.LastLut)}");
        GpuRetain.Describe(sb);
        sb.AppendLine();
        sb.AppendLine($"Named unique objects (images+rasters+hist, aliases counted once)  {MemSize.Bytes(named)}");
        sb.AppendLine("SKImage sizes are Info/CPU estimates. GPU textures and native LibRaw heaps may sit extra in RSS.");
        sb.AppendLine("======== end dump ========");

        string dir = Path.GetDirectoryName(Log.LogFilePath) ?? AppContext.BaseDirectory;
        string path = Path.Combine(dir, "memory-dump.log");
        File.WriteAllText(path, sb.ToString());

        string full = sb.ToString();
        using (var reader = new StringReader(full))
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
                Log.Info("MEM  " + line);
        }

        long proc = CurrentRss();
        string summary =
            $"Process {MemSize.Bytes(proc)}   GC heap {MemSize.Bytes(GC.GetTotalMemory(false))}\n" +
            $"Session {docs} photos   named buffers {MemSize.Bytes(named)}\n" +
            $"Wrote {path}";

        return new MemoryDumpResult
        {
            FilePath = path,
            Summary = summary,
            ProcessBytes = proc,
            NamedBytes = named,
            DocumentCount = docs
        };
    }

    private static void AppendProcess(StringBuilder sb)
    {
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        sb.AppendLine("Process");
        sb.AppendLine($"  WorkingSet     {MemSize.Bytes(p.WorkingSet64)}");
        sb.AppendLine($"  Private        {MemSize.Bytes(p.PrivateMemorySize64)}");
        sb.AppendLine($"  Virtual        {MemSize.Bytes(p.VirtualMemorySize64)}");
        sb.AppendLine($"  PeakWorkingSet {MemSize.Bytes(p.PeakWorkingSet64)}");
        sb.AppendLine($"  GC TotalMemory {MemSize.Bytes(GC.GetTotalMemory(false))}  (false = no collect)");
        sb.AppendLine($"  Allocated      {MemSize.Bytes(GC.GetTotalAllocatedBytes(precise: false))}");
        sb.AppendLine($"  Threads        {p.Threads.Count}");

        if (OperatingSystem.IsLinux())
        {
            try
            {
                foreach (string line in File.ReadLines("/proc/self/status"))
                {
                    if (line.StartsWith("VmPeak", StringComparison.Ordinal)
                        || line.StartsWith("VmSize", StringComparison.Ordinal)
                        || line.StartsWith("VmHWM", StringComparison.Ordinal)
                        || line.StartsWith("VmRSS", StringComparison.Ordinal)
                        || line.StartsWith("RssAnon", StringComparison.Ordinal)
                        || line.StartsWith("RssFile", StringComparison.Ordinal)
                        || line.StartsWith("VmData", StringComparison.Ordinal)
                        || line.StartsWith("VmStk", StringComparison.Ordinal))
                    {
                        sb.AppendLine("  " + line.Trim());
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("  /proc/self/status  " + ex.Message);
            }
        }
    }

    private static void AppendGc(StringBuilder sb)
    {
        var info = GC.GetGCMemoryInfo();
        sb.AppendLine("GC heap");
        sb.AppendLine($"  HeapSize        {MemSize.Bytes(info.HeapSizeBytes)}");
        sb.AppendLine($"  Fragmented      {MemSize.Bytes(info.FragmentedBytes)}");
        sb.AppendLine($"  MemoryLoad      {MemSize.Bytes(info.MemoryLoadBytes)}");
        sb.AppendLine($"  TotalAvailable  {MemSize.Bytes(info.TotalAvailableMemoryBytes)}");
        sb.AppendLine($"  HighMemoryLoad  {MemSize.Bytes(info.HighMemoryLoadThresholdBytes)}");
        sb.AppendLine($"  Collections     gen0={GC.CollectionCount(0)}  gen1={GC.CollectionCount(1)}  gen2={GC.CollectionCount(2)}");
    }

    private static long AppendImage(StringBuilder sb, HashSet<object> seen, string name, SKImage? image)
    {
        if (image == null || image.Handle == IntPtr.Zero)
        {
            sb.AppendLine($"  {name}  null");
            return 0;
        }

        bool unique = seen.Add(image);
        string alias = unique ? "" : "  ALIAS";
        long n = unique ? MemSize.Image(image) : 0;
        sb.AppendLine($"  {name}  {MemSize.ImageLabel(image)}{alias}");
        return n;
    }

    private static long AppendBitmap(StringBuilder sb, HashSet<object> seen, string name, SKBitmap? bitmap)
    {
        if (bitmap == null || bitmap.Handle == IntPtr.Zero)
        {
            sb.AppendLine($"  {name}  null");
            return 0;
        }

        bool unique = seen.Add(bitmap);
        long n = unique ? MemSize.Bitmap(bitmap) : 0;
        sb.AppendLine($"  {name}  {bitmap.Width}x{bitmap.Height} {bitmap.ColorType}  {MemSize.Bytes(MemSize.Bitmap(bitmap))}{(unique ? "" : "  ALIAS")}");
        return n;
    }

    private static long AppendRaster(StringBuilder sb, HashSet<object> seen, string name, RasterBuffer buf)
    {
        if (!buf.HasPixels)
        {
            sb.AppendLine($"  {name}  empty");
            return 0;
        }

        long n = 0;
        if (buf.Linear != null && seen.Add(buf.Linear))
            n += (long)buf.Linear.Length * sizeof(float);
        if (buf.Rgba != null && seen.Add(buf.Rgba))
            n += buf.Rgba.Length;
        sb.AppendLine($"  {name}  {buf.Width}x{buf.Height} {buf.PixelKind}  {MemSize.Bytes(buf.ByteLength)}  unique {MemSize.Bytes(n)}");
        return n;
    }

    private static long AppendArray(StringBuilder sb, HashSet<object> seen, string name, Array? arr)
    {
        if (arr == null)
            return 0;
        if (!seen.Add(arr))
            return 0;
        long n = (long)arr.Length * (arr is float[] ? sizeof(float) : 4);
        if (n >= 64 * 1024)
            sb.AppendLine($"  {name}  {MemSize.Bytes(n)}");
        return n;
    }

    private static long CurrentRss()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            p.Refresh();
            return p.WorkingSet64;
        }
        catch
        {
            return 0;
        }
    }

    private static string RuntimeInformationOs() =>
        $"{Environment.OSVersion}  {(Environment.Is64BitProcess ? "x64" : "x86")}  processors={Environment.ProcessorCount}";
}
