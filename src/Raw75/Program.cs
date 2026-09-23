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

        Theme.Apply(Theme.Find(Raw75.Io.UiPrefs.LoadThemeId()), persist: false, notify: false);

        Browser.MaxFps = 120;

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
            GradingShadowLum = -10f,
            GradingMidHue = 30f,
            GradingMidSat = 20f,
            GradingHighlightHue = 42f,
            GradingHighlightSat = 25f,
            GradingHighlightLum = 8f,
            GradingBlending = 55f,
            GradingBalance = 15f
        };
        var clone = s.Clone();
        if (!s.LooksLike(clone))
            throw new InvalidOperationException("Settings.Clone() failed LooksLike check!");
        clone.Dehaze = 44f;
        if (s.LooksLike(clone))
            throw new InvalidOperationException("LooksLike failed to detect Dehaze mismatch!");
        clone.Dehaze = s.Dehaze;
        clone.DehazeDistance = 35f;
        if (s.LooksLike(clone))
            throw new InvalidOperationException("LooksLike failed to detect DehazeDistance mismatch!");
        clone.DehazeDistance = s.DehazeDistance;
        clone.Texture = 10f;
        if (s.LooksLike(clone))
            throw new InvalidOperationException("LooksLike failed to detect Texture mismatch!");
        clone.Texture = s.Texture;
        clone.GradingMidSat = 40f;
        if (s.LooksLike(clone))
            throw new InvalidOperationException("LooksLike failed to detect GradingMidSat mismatch!");
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

        // 6. Checking Curves (Fritsch-Carlson Spline, 2D LUT, and Pipeline evaluation)
        Console.Write("6. Checking Curve engine (Spline, 2D LUT, and CPU/GPU pipeline)... ");
        var defaultPoints = Raw75.Pipeline.CurveMath.DefaultCurve();
        if (!Raw75.Pipeline.CurveMath.IsIdentity(defaultPoints))
            throw new InvalidOperationException("Default curve must be identity!");

        float[] linearSpline = Raw75.Pipeline.CurveMath.EvaluateSpline(defaultPoints, 256);
        if (MathF.Abs(linearSpline[0] - 0f) > 0.001f || MathF.Abs(linearSpline[255] - 1f) > 0.001f || MathF.Abs(linearSpline[128] - (128f / 255f)) > 0.005f)
            throw new InvalidOperationException("Identity spline does not evaluate to linear ramp!");

        // S-Curve test
        var sCurvePoints = new[]
        {
            new Raw75.Pipeline.CurvePoint(0f, 0f),
            new Raw75.Pipeline.CurvePoint(64f, 40f),
            new Raw75.Pipeline.CurvePoint(192f, 215f),
            new Raw75.Pipeline.CurvePoint(255f, 255f)
        };
        if (Raw75.Pipeline.CurveMath.IsIdentity(sCurvePoints))
            throw new InvalidOperationException("S-curve should not be identity!");

        float[] sSpline = Raw75.Pipeline.CurveMath.EvaluateSpline(sCurvePoints, 256);
        if (sSpline[64] >= linearSpline[64])
            throw new InvalidOperationException("S-curve darks should be darker than linear!");
        if (sSpline[192] <= linearSpline[192])
            throw new InvalidOperationException("S-curve lights should be lighter than linear!");

        // 2D LUT generation
        byte[] lutBuf = new byte[2048];
        Raw75.Pipeline.CurveMath.BuildCurveLut2D(sCurvePoints, defaultPoints, defaultPoints, defaultPoints, lutBuf);
        if (lutBuf[3] != 255 || lutBuf[1024 + 3] != 255)
            throw new InvalidOperationException("2D curve LUT alpha must be 255!");

        // CPU evaluation with curve
        var sWithCurve = new Raw75.Develop.DevelopSettings();
        sWithCurve.CurveRgb = sCurvePoints;
        var rCurved = Raw75.Pipeline.DevelopCpu.Apply(srcRaster, sWithCurve, fast: true);
        bool curveAltered = false;
        for (int i = 0; i < resNeutral.Rgba!.Length; i += 4)
        {
            if (Math.Abs(rCurved.Rgba![i] - resNeutral.Rgba[i]) > 2)
            {
                curveAltered = true;
                break;
            }
        }
        if (!curveAltered)
            throw new InvalidOperationException("DevelopCpu with active curve did not alter pixels!");

        // DevelopRenderer curve texture generation
        var curveTex = Raw75.Pipeline.DevelopRenderer.GetCurveTexture(sWithCurve, out float hasCurve, out float curveMode);
        if (curveTex == null || hasCurve < 0.5f || curveMode < 0.5f)
            throw new InvalidOperationException("DevelopRenderer.GetCurveTexture failed to generate active curve texture!");

        Console.WriteLine("PASSED.");

        // 7. Checking Atmospheric Dehaze & Airlight Estimator
        Console.Write("7. Checking Dehaze & AtmosphereEstimator... ");
        Raw75.Pipeline.AtmosphereEstimator.Estimate(srcRaster, out float airR, out float airG, out float airB, out float depthMax);
        if (airR <= 0f || airG <= 0f || airB <= 0f || depthMax <= 0f)
            throw new InvalidOperationException("AtmosphereEstimator failed to calculate valid atmosphere parameters!");

        var sDehazePos = new Raw75.Develop.DevelopSettings
        {
            Dehaze = 50f,
            DehazeDistance = 20f,
            AtmosphereR = airR,
            AtmosphereG = airG,
            AtmosphereB = airB,
            AtmosphereDepthMax = depthMax
        };
        var rDehazePos = Raw75.Pipeline.DevelopCpu.Apply(srcRaster, sDehazePos, fast: true);

        var sDehazeNeg = new Raw75.Develop.DevelopSettings
        {
            Dehaze = -50f,
            DehazeDistance = 20f,
            AtmosphereR = airR,
            AtmosphereG = airG,
            AtmosphereB = airB,
            AtmosphereDepthMax = depthMax
        };
        var rDehazeNeg = Raw75.Pipeline.DevelopCpu.Apply(srcRaster, sDehazeNeg, fast: true);

        bool posAltered = false;
        bool negAltered = false;
        for (int i = 0; i < resNeutral.Rgba!.Length; i += 4)
        {
            if (Math.Abs(rDehazePos.Rgba![i] - resNeutral.Rgba[i]) > 0) posAltered = true;
            if (Math.Abs(rDehazeNeg.Rgba![i] - resNeutral.Rgba[i]) > 0) negAltered = true;
            if (posAltered && negAltered) break;
        }
        if (!posAltered)
            throw new InvalidOperationException("Positive Dehaze did not alter pixels!");
        if (!negAltered)
            throw new InvalidOperationException("Negative Dehaze (haze addition) did not alter pixels!");

        Console.WriteLine("PASSED.");

        Console.WriteLine("=== All Raw75 Pipeline Smoke Tests Passed Successfully ===");
    }
}
