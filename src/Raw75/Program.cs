using System;
using System.IO;
using System.Threading.Tasks;
using Blossom;
using Raw75.Imaging;

namespace Raw75;

internal static class Program
{
    private static readonly string AppName = Path.GetFileNameWithoutExtension(
        Environment.GetCommandLineArgs().Length > 0 ? Environment.GetCommandLineArgs()[0] : "Raw75");

    static void Main()
    {
        Log.Initialize();
        Log.Info($"Starting {AppName}");
        Log.Info($"Application directory: {AppDomain.CurrentDomain.BaseDirectory}");
        LibRawNative.EnsureLoaded();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Fatal($"Unhandled domain exception (IsTerminating={e.IsTerminating}): {e.ExceptionObject}");
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error($"Unobserved task exception: {e.Exception}");
            e.SetObserved();
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Log.Info("Application exiting");
        NativeCrash.Install();

        foreach (var arg in Environment.GetCommandLineArgs())
        {
            if (arg.Equals("--smoke-test", StringComparison.OrdinalIgnoreCase))
            {
                RunSmokeTest();
                return;
            }

            if (arg.Equals("--show-fps", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--fps", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--debug-overlay", StringComparison.OrdinalIgnoreCase))
            {
                Browser.ShowDebugOverlay = true;
            }
        }

        Browser.Initialize(new Raw75Application());
    }

    private static void RunSmokeTest()
    {
        Console.WriteLine("=== Running Raw75 Pipeline Smoke Tests ===");

        // 1. SkSL compilation check
        Console.Write("1. Checking SkSL Shader compilation... ");
        var effect = Raw75.Pipeline.DevelopRenderer.EnsureEffect();
        if (effect == null)
        {
            Console.WriteLine("FAILED!");
            throw new InvalidOperationException("DevelopRenderer.EnsureEffect() failed to compile SkSL runtime effect!");
        }
        Console.WriteLine("PASSED.");

        // 2. DevelopSettings validation
        Console.Write("2. Checking DevelopSettings Clone & LooksLike... ");
        var s = new Raw75.Develop.DevelopSettings
        {
            Dehaze = 45f,
            Texture = -30f,
            VignetteAmount = -25f,
            VignetteMidpoint = 40f,
            GradingShadowHue = 215f,
            GradingShadowSat = 35f,
            GradingHighlightHue = 42f,
            GradingHighlightSat = 25f,
            GradingBalance = 15f
        };
        var clone = s.Clone();
        if (!s.LooksLike(clone))
            throw new InvalidOperationException("Settings.Clone() failed LooksLike check!");
        clone.Dehaze = 44f;
        if (s.LooksLike(clone))
            throw new InvalidOperationException("LooksLike failed to detect Dehaze mismatch!");
        clone.Dehaze = s.Dehaze;
        clone.Texture = 10f;
        if (s.LooksLike(clone))
            throw new InvalidOperationException("LooksLike failed to detect Texture mismatch!");
        Console.WriteLine("PASSED.");

        // 3. CPU Pipeline processing check
        Console.Write("3. Checking DevelopCpu processing with all adjustments active... ");
        int w = 64, h = 64;
        byte[] pixels = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int idx = (y * w + x) * 4;
                byte v = (byte)((x < 32) ? 40 : 200); // step edge
                pixels[idx] = v;
                pixels[idx + 1] = v;
                pixels[idx + 2] = v;
                pixels[idx + 3] = 255;
            }
        }
        var srcRaster = new Raw75.Imaging.RasterBuffer(pixels, w, h);
        var res1 = Raw75.Pipeline.DevelopCpu.Apply(srcRaster, s, fast: false);
        if (res1.Rgba == null || res1.Width != w || res1.Height != h)
            throw new InvalidOperationException("DevelopCpu.Apply failed to return valid buffer!");

        // Verify pixels are valid byte values
        for (int i = 0; i < res1.Rgba.Length; i++)
        {
            if (res1.Rgba[i] < 0 || res1.Rgba[i] > 255)
                throw new InvalidOperationException($"Invalid pixel value at byte {i}: {res1.Rgba[i]}");
        }
        Console.WriteLine("PASSED.");

        // 4. Verify effect deltas
        Console.Write("4. Checking adjustment deltas against neutral baseline... ");
        var neutral = new Raw75.Develop.DevelopSettings();
        var resNeutral = Raw75.Pipeline.DevelopCpu.Apply(srcRaster, neutral, fast: false);
        bool hasDelta = false;
        for (int i = 0; i < res1.Rgba.Length; i += 4)
        {
            if (res1.Rgba[i] != resNeutral.Rgba![i] ||
                res1.Rgba[i + 1] != resNeutral.Rgba[i + 1] ||
                res1.Rgba[i + 2] != resNeutral.Rgba[i + 2])
            {
                hasDelta = true;
                break;
            }
        }
        if (!hasDelta)
            throw new InvalidOperationException("Active adjustments produced zero difference compared to neutral!");
        Console.WriteLine("PASSED.");

        Console.WriteLine("=== All Raw75 Pipeline Smoke Tests Passed Successfully ===");
    }
}
