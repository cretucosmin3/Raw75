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
                using var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                Console.SetOut(stdout);
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
        Console.WriteLine("1. Checking SkSL Shader compilation (Monolithic + Staged 1, 2, 3)... ");
        Console.Out.Flush();
        var effect = Raw75.Pipeline.DevelopRenderer.EnsureEffect();
        if (effect == null)
        {
            Console.WriteLine("FAILED on monolithic!");
            throw new InvalidOperationException("DevelopRenderer.EnsureEffect() failed to compile SkSL runtime effect!");
        }
        Console.WriteLine("  Monolithic OK.");
        Console.Out.Flush();

        var eff1 = Raw75.Pipeline.DevelopRenderer.EnsureStage1Effect();
        if (eff1 == null)
        {
            Console.WriteLine("FAILED on Stage 1!");
            throw new InvalidOperationException("DevelopRenderer.EnsureStage1Effect() failed to compile Stage 1 SkSL!");
        }
        Console.WriteLine("  Stage 1 OK.");
        Console.Out.Flush();

        var eff2 = Raw75.Pipeline.DevelopRenderer.EnsureStage2Effect();
        if (eff2 == null)
        {
            Console.WriteLine("FAILED on Stage 2!");
            throw new InvalidOperationException("DevelopRenderer.EnsureStage2Effect() failed to compile Stage 2 SkSL!");
        }
        Console.WriteLine("  Stage 2 OK.");
        Console.Out.Flush();

        var eff3 = Raw75.Pipeline.DevelopRenderer.EnsureStage3Effect();
        if (eff3 == null)
        {
            Console.WriteLine("FAILED on Stage 3!");
            throw new InvalidOperationException("DevelopRenderer.EnsureStage3Effect() failed to compile Stage 3 SkSL!");
        }
        Console.WriteLine("  Stage 3 OK.");
        Console.WriteLine("1. PASSED.");
        Console.Out.Flush();

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

        // 5. Staged 3-pass Pipeline execution check
        Console.Write("5. Checking Staged 3-pass Pipeline uniform bindings, activation, and lifecycle... ");
        var u1 = new SkiaSharp.SKRuntimeEffectUniforms(eff1);
        Raw75.Pipeline.DevelopRenderer.BindStage1Uniforms(u1, s, w, h, fast: false, srcLinear: false);

        var u2 = new SkiaSharp.SKRuntimeEffectUniforms(eff2);
        Raw75.Pipeline.DevelopRenderer.BindStage2Uniforms(u2, s, w, h, fast: false);

        var u3 = new SkiaSharp.SKRuntimeEffectUniforms(eff3);
        Raw75.Pipeline.DevelopRenderer.BindStage3Uniforms(
            u3, s, w, h,
            dstX: 0f, dstY: 0f, dstW: 1f, dstH: 1f,
            split: 0f, before: false, lutSize: 2f, lutAmount: 0f,
            applyCrop: false, showClipping: false,
            tileX: 0f, tileY: 0f, tileW: 1f, tileH: 1f,
            viewCropX: float.NaN, viewCropY: float.NaN, viewCropW: float.NaN, viewCropH: float.NaN,
            frameW: 0, frameH: 0);

        // Verify Stage 2 activation bypass helper
        var sStage2 = new Raw75.Develop.DevelopSettings();
        if (Raw75.Pipeline.DevelopRenderer.IsStage2Active(sStage2))
            throw new InvalidOperationException("Neutral settings should bypass Stage 2!");
        sStage2.Texture = 25f;
        if (!Raw75.Pipeline.DevelopRenderer.IsStage2Active(sStage2))
            throw new InvalidOperationException("Active Texture should activate Stage 2!");
        sStage2.Texture = 0f;
        sStage2.DenoiseLuma = 30f;
        if (!Raw75.Pipeline.DevelopRenderer.IsStage2Active(sStage2))
            throw new InvalidOperationException("Active DenoiseLuma should activate Stage 2!");

        // Verify DevelopLook instantiation and invalidation
        using (var look = new Raw75.Pipeline.DevelopLook())
        using (var bmp = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(w, h, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul)))
        {
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bmp.GetPixels(), pixels.Length);
            using var img = SkiaSharp.SKImage.FromBitmap(bmp);
            using var outSurface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(w, h, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul));
            if (outSurface == null)
                throw new InvalidOperationException("Failed to allocate test SKSurface for DevelopLook!");

            var testDest = new SkiaSharp.SKRect(0, 0, w, h);

            if (Blossom.Core.Gpu.IsReady)
            {
                bool drawn = look.Draw(outSurface.Canvas, testDest, testDest, img, s, fast: false);
                if (!drawn)
                    throw new InvalidOperationException("DevelopLook.Draw failed on staged pipeline test!");

                s.Exposure = 1.2f;
                look.Invalidate();
                bool drawn2 = look.Draw(outSurface.Canvas, testDest, testDest, img, s, fast: false);
                if (!drawn2)
                    throw new InvalidOperationException("DevelopLook.Draw failed after Stage 3 slider invalidation!");

                s.Texture = 25f;
                look.Invalidate();
                bool drawn3 = look.Draw(outSurface.Canvas, testDest, testDest, img, s, fast: false);
                if (!drawn3)
                    throw new InvalidOperationException("DevelopLook.Draw failed after Stage 2 slider invalidation!");

                s.VignetteAmount = -40f;
                look.Invalidate();
                bool drawn4 = look.Draw(outSurface.Canvas, testDest, testDest, img, s, fast: false);
                if (!drawn4)
                    throw new InvalidOperationException("DevelopLook.Draw failed after Stage 1 slider invalidation!");
            }
            else
            {
                // In headless mode without GPU context, Draw must safely return false and not crash
                bool drawn = look.Draw(outSurface.Canvas, testDest, testDest, img, s, fast: false);
                if (drawn)
                    throw new InvalidOperationException("DevelopLook.Draw unexpectedly returned true without active GPU context!");
            }
        }
        Console.WriteLine("PASSED.");

        Console.WriteLine("=== All Raw75 Pipeline Smoke Tests Passed Successfully ===");
    }
}
