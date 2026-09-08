using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Blossom;

namespace Raw75.Imaging;

/// <summary>
/// Sdcb.LibRaw registers its own DllImport resolver (libraw_r.so.23 / raw_r.dll).
/// We only preload those files from the NuGet runtime folder so their loader can find them.
/// Do not call NativeLibrary.SetDllImportResolver on Sdcb.LibRaw — one resolver per assembly.
/// </summary>
internal static class LibRawNative
{
    private static bool _bound;

    public static void EnsureLoaded()
    {
        if (_bound)
            return;
        _bound = true;

        // LibRaw's OpenMP pool + a later UI blit was aborting the process
        // after dcraw_make_mem_image returned.
        Environment.SetEnvironmentVariable("OMP_NUM_THREADS", "1");
        Environment.SetEnvironmentVariable("OMP_WAIT_POLICY", "PASSIVE");

        PreloadDeps();
    }

    private static void PreloadDeps()
    {
        string[] deps =
        {
            "libjpeg.so.8", "liblcms2.so", "libgomp.so.1", "libraw_r.so.23",
            "jpeg8.dll", "lcms2.dll", "raw_r.dll"
        };

        foreach (string dir in NativeDirs())
        {
            if (!Directory.Exists(dir))
                continue;

            foreach (string name in deps)
            {
                string path = Path.Combine(dir, name);
                if (!File.Exists(path))
                    continue;
                if (NativeLibrary.TryLoad(path, out IntPtr handle) && handle != IntPtr.Zero)
                    Log.Info("Preloaded " + path);
            }
        }
    }

    private static IEnumerable<string> NativeDirs()
    {
        string baseDir = AppContext.BaseDirectory;
        yield return baseDir;
        yield return Path.Combine(baseDir, "runtimes", RuntimeInformation.RuntimeIdentifier, "native");
        yield return Path.Combine(baseDir, "runtimes", "linux-x64", "native");
        yield return Path.Combine(baseDir, "runtimes", "win-x64", "native");
    }

    public static string Flatten(Exception ex)
    {
        var sb = new StringBuilder();
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            sb.Append(e.GetType().Name);
            sb.Append(": ");
            sb.Append(e.Message);
            if (e.InnerException != null)
                sb.Append(" → ");
        }
        return sb.ToString();
    }
}
