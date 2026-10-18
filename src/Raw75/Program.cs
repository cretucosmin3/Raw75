using System;
using System.IO;
using System.Threading.Tasks;
using Blossom;
using Raw75.Imaging;
using SkiaSharp;
using Blossom.Desktop;

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
        Raw75.Update.SelfUpdate.CleanUpInBackground();

        Theme.Apply(Theme.Find(Raw75.Io.UiPrefs.LoadThemeId()), persist: false, notify: false);

        Shell.MaxFps = 120;

        bool showStats = false;
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
                showStats = true;
            }
        }

        // F12 always toggles Blossom's performance overlay; --fps starts with it shown.
        Shell.ShowDebugOverlay = showStats;
        Shell.Initialize(new Raw75Application { EnableStatsOverlay = true });

        // The window is gone: end the process even if a worker (decode, export, HDR merge) or a
        // native library thread is still busy, instead of lingering headless in the background.
        Log.Info("Window closed");
        Environment.Exit(0);
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

        Console.Write("8. Checking adaptive look presets... ");
        var dark = GrayLinear(48, 48, 0.04f);
        var bright = GrayLinear(48, 48, 0.45f);
        var darkM = Raw75.Pipeline.SceneAnalyzer.Measure(dark);
        var brightM = Raw75.Pipeline.SceneAnalyzer.Measure(bright);
        if (darkM.LogMeanEv >= brightM.LogMeanEv)
            throw new InvalidOperationException("Dark buffer should measure lower LogMeanEv than bright.");

        var authored = new Raw75.Develop.DevelopSettings { Exposure = 2.2f, Contrast = 40f };
        var lookM = Raw75.Pipeline.SceneAnalyzer.MeasureLook(dark, authored);
        var preset = new Raw75.Presets.LookPreset
        {
            Source = darkM,
            Look = lookM,
            Settings = authored
        };
        var dest = new Raw75.Develop.DevelopSettings { BaseExposure = 0.8f, CropW = 0.5f, CropH = 0.5f };
        Raw75.Presets.AdaptiveLook.Apply(preset, dest, brightM);
        if (dest.Exposure > 1.0f)
            throw new InvalidOperationException($"Bright dest should not keep +2.2 EV (got {dest.Exposure}).");
        if (MathF.Abs(dest.BaseExposure - 0.8f) > 0.001f || dest.CropW < 0.4f)
            throw new InvalidOperationException("Adaptive apply must keep dest BaseExposure and crop.");

        var curvePreset = new Raw75.Presets.LookPreset
        {
            Source = darkM,
            Look = lookM,
            Settings = new Raw75.Develop.DevelopSettings
            {
                EnableExposureCurve = true,
                ExposureCurve = new[]
                {
                    new Raw75.Pipeline.ExposureCurvePoint(0f, 128f, 1f),
                    new Raw75.Pipeline.ExposureCurvePoint(48f, 200f, 1f),
                    new Raw75.Pipeline.ExposureCurvePoint(255f, 128f, 1f)
                }
            }
        };
        var destCurve = new Raw75.Develop.DevelopSettings();
        Raw75.Presets.AdaptiveLook.Apply(curvePreset, destCurve, brightM);
        if (!destCurve.EnableExposureCurve || destCurve.ExposureCurve == null || destCurve.ExposureCurve.Length != 3)
            throw new InvalidOperationException("Curve look must stay in curve mode.");
        if (destCurve.ExposureCurve[1].X <= 48f + 1f)
            throw new InvalidOperationException("Exposure curve X should remap toward brighter dest percentiles.");
        if (destCurve.ExposureCurve[1].Y < 160f)
            throw new InvalidOperationException($"Curve style lift should survive apply (Y={destCurve.ExposureCurve[1].Y}).");

        var legacy = Raw75.Presets.PresetStore.Deserialize("{ \"Contrast\": 30 }");
        if (legacy.Settings == null || Math.Abs(legacy.Settings.Contrast - 30f) > 0.01f || legacy.CanAdapt)
            throw new InvalidOperationException("Legacy preset JSON should load as exact Contrast 30.");

        if (Math.Abs(Raw75.Presets.AdaptiveLook.NeedScale(1f, 0f)) > 0.001f)
            throw new InvalidOperationException("Zero dest need must scale to 0.");
        if (Math.Abs(Raw75.Presets.AdaptiveLook.NeedScale(0.05f, 1f) - 1f) > 0.001f)
            throw new InvalidOperationException("Tiny author need must keep scale 1.");

        var held = MixedLinear(48, 48, 0.12f, 1.6f);
        var heldSettings = new Raw75.Develop.DevelopSettings { Exposure = 0.4f, Highlights = -55f, Whites = -30f };
        var heldSrc = Raw75.Pipeline.SceneAnalyzer.Measure(held);
        var heldLook = Raw75.Pipeline.SceneAnalyzer.MeasureLook(held, heldSettings);
        var blown = GrayLinear(48, 48, 1.4f);
        var blownM = Raw75.Pipeline.SceneAnalyzer.Measure(blown);
        var heldPreset = new Raw75.Presets.LookPreset
        {
            Source = heldSrc,
            Look = heldLook,
            Settings = heldSettings
        };
        var blownDest = new Raw75.Develop.DevelopSettings();
        Raw75.Presets.AdaptiveLook.Apply(heldPreset, blownDest, blownM);
        if (blownDest.Highlights > -10f && blownDest.Whites > -10f)
            throw new InvalidOperationException($"Overexposed dest should invert to hold highlights (H={blownDest.Highlights}, W={blownDest.Whites}).");
        Console.WriteLine("PASSED.");

        Console.Write("9. Checking spot heal CPU clone... ");
        int sw = 32, sh = 32;
        float[] linSpot = new float[sw * sh * 4];
        for (int y = 0; y < sh; y++)
        {
            for (int x = 0; x < sw; x++)
            {
                int o = (y * sw + x) * 4;
                float v = x < 16 ? 0.05f : 0.8f;
                linSpot[o] = v;
                linSpot[o + 1] = v;
                linSpot[o + 2] = v;
                linSpot[o + 3] = 1f;
            }
        }
        var spotRaster = new Raw75.Imaging.RasterBuffer(linSpot, sw, sh);
        var sSpot = new Raw75.Develop.DevelopSettings
        {
            EnableSpotRemoval = true,
            Spots = { new Raw75.Develop.SpotPatch { DestX = 0.25f, DestY = 0.5f, SrcX = 0.75f, SrcY = 0.5f, Radius = 0.2f, Feather = 0.1f } }
        };
        var rSpot = Raw75.Pipeline.DevelopCpu.Apply(spotRaster, sSpot, fast: true);
        int left = ((sh / 2) * sw + 4) * 4;
        int right = ((sh / 2) * sw + 24) * 4;
        if (rSpot.Rgba![left] < rSpot.Rgba[right] - 40)
            throw new InvalidOperationException("Spot heal should copy bright source into the dark destination.");
        Console.WriteLine("PASSED.");

        // 10. HDR EV calculation from fake metadata (1 stop apart)
        Console.Write("10. Checking HDR EV calculation from metadata... ");
        var meta1 = new Raw75.Develop.PhotoMetadata { ShutterSpeed = 1f / 100f, Aperture = 4.0f, Iso = 100 };
        var meta2 = new Raw75.Develop.PhotoMetadata { ShutterSpeed = 1f / 50f, Aperture = 4.0f, Iso = 100 };
        var meta3 = new Raw75.Develop.PhotoMetadata { ShutterSpeed = 1f / 100f, Aperture = 2.828427f, Iso = 100 };
        var meta4 = new Raw75.Develop.PhotoMetadata { ShutterSpeed = 1f / 100f, Aperture = 4.0f, Iso = 200 };

        float ev1 = Raw75.Pipeline.HdrMerge.ComputeEv(meta1);
        float ev2 = Raw75.Pipeline.HdrMerge.ComputeEv(meta2);
        float ev3 = Raw75.Pipeline.HdrMerge.ComputeEv(meta3);
        float ev4 = Raw75.Pipeline.HdrMerge.ComputeEv(meta4);

        if (MathF.Abs((ev2 - ev1) - 1.0f) > 0.05f)
            throw new InvalidOperationException($"1 stop shutter should increase EV by 1.0 (diff={ev2 - ev1})");
        if (MathF.Abs((ev3 - ev1) - 1.0f) > 0.05f)
            throw new InvalidOperationException($"1 stop aperture should increase EV by 1.0 (diff={ev3 - ev1})");
        if (MathF.Abs((ev4 - ev1) - 1.0f) > 0.05f)
            throw new InvalidOperationException($"1 stop ISO should increase EV by 1.0 (diff={ev4 - ev1})");
        Console.WriteLine("PASSED.");

        // 11. 32-bit linear Float TIFF round-trip
        Console.Write("11. Checking 32-bit linear Float TIFF round-trip... ");
        int tw = 32, th = 32;
        float[] origBuf = new float[tw * th * 4];
        for (int i = 0; i < tw * th; i++)
        {
            origBuf[i * 4 + 0] = (i % tw) / 32f;
            origBuf[i * 4 + 1] = ((i / tw) / 32f) * 2.5f;
            origBuf[i * 4 + 2] = 0.42f;
            origBuf[i * 4 + 3] = 1.0f;
        }
        var origRaster = new Raw75.Imaging.RasterBuffer(origBuf, tw, th);
        string tempTiff = Path.Combine(Path.GetTempPath(), $"raw75_hdr_test_{Guid.NewGuid():N}.tif");
        try
        {
            Raw75.Io.HdrTiff.Write(tempTiff, origRaster, new Raw75.Develop.PhotoMetadata { ShutterSpeed = 0.01f, Aperture = 2.8f, Iso = 100 });
            if (!Raw75.Io.HdrTiff.IsHdrTiff(tempTiff))
                throw new InvalidOperationException("IsHdrTiff returned false for newly written HDR TIFF!");

            if (!Raw75.Io.HdrTiff.TryRead(tempTiff, out var readRaster, out var readMeta))
                throw new InvalidOperationException("TryRead failed on HDR TIFF!");

            if (readRaster.Width != tw || readRaster.Height != th || !readRaster.HasLinear)
                throw new InvalidOperationException($"Raster dimensions or HasLinear mismatch! {readRaster.Width}x{readRaster.Height}, HasLinear={readRaster.HasLinear}");

            for (int i = 0; i < tw * th * 4; i++)
            {
                float diff = MathF.Abs(origRaster.Linear![i] - readRaster.Linear![i]);
                if (diff > 1e-4f)
                    throw new InvalidOperationException($"TIFF round-trip value mismatch at {i}: orig={origRaster.Linear[i]}, read={readRaster.Linear[i]}, diff={diff}");
            }
        }
        finally
        {
            if (File.Exists(tempTiff)) File.Delete(tempTiff);
        }
        Console.WriteLine("PASSED.");

        // 12. Homography identity
        Console.Write("12. Checking Homography alignment identity... ");
        var hIdentity = Raw75.Pipeline.HdrAlign.Homography.Identity;
        var (tx, ty) = hIdentity.Transform(100f, 200f);
        if (MathF.Abs(tx - 100f) > 1e-4f || MathF.Abs(ty - 200f) > 1e-4f)
            throw new InvalidOperationException($"Homography Identity transformed point incorrectly: ({tx}, {ty})");

        int aw = 64, ah = 64;
        float[] f1Buf = new float[aw * ah * 4];
        for (int y = 0; y < ah; y++)
        {
            for (int x = 0; x < aw; x++)
            {
                int idx = (y * aw + x) * 4;
                float p = ((x / 8) % 2 == (y / 8) % 2) ? 0.8f : 0.2f;
                f1Buf[idx] = p;
                f1Buf[idx + 1] = p;
                f1Buf[idx + 2] = p;
                f1Buf[idx + 3] = 1f;
            }
        }
        var f1Raster = new Raw75.Imaging.RasterBuffer(f1Buf, aw, ah);
        var alignResult = Raw75.Pipeline.HdrAlign.Align(f1Raster, f1Raster, 0f, 0f);
        if (!alignResult.Transform.IsIdentity())
            throw new InvalidOperationException("Homography of identical frames should be identity.");

        // Check shifted + exposure-scaled pattern: must either recover shift or fall back to identity if weak
        float[] f2Buf = new float[aw * ah * 4];
        for (int y = 0; y < ah; y++)
        {
            for (int x = 0; x < aw; x++)
            {
                int idx = (y * aw + x) * 4;
                int sx = x + 2;
                int sy = y + 1;
                float p = (((sx / 8) % 2 == (sy / 8) % 2) ? 0.8f : 0.2f) * 4.0f; // 2 stops brighter
                f2Buf[idx] = p;
                f2Buf[idx + 1] = p;
                f2Buf[idx + 2] = p;
                f2Buf[idx + 3] = 1f;
            }
        }
        var f2Raster = new Raw75.Imaging.RasterBuffer(f2Buf, aw, ah);
        var shiftedResult = Raw75.Pipeline.HdrAlign.Align(f2Raster, f1Raster, 2f, 0f);
        if (shiftedResult.IsWeak && !shiftedResult.Transform.IsIdentity())
            throw new InvalidOperationException("Weak alignment must return Identity transform.");
        Console.WriteLine("PASSED.");

        // 13. Two synthetic ramps merge
        Console.Write("13. Checking two synthetic ramps merge... ");
        int rw = 128, rh = 8;
        float[] r1 = new float[rw * rh * 4];
        float[] r2 = new float[rw * rh * 4];
        for (int y = 0; y < rh; y++)
        {
            for (int x = 0; x < rw; x++)
            {
                float scene = (x + 0.5f) / rw * 2.0f;
                float val1 = Math.Clamp(scene * 0.5f, 0f, 1f);
                float val2 = Math.Clamp(scene * 2.0f, 0f, 1f);

                int o = (y * rw + x) * 4;
                r1[o] = val1; r1[o + 1] = val1; r1[o + 2] = val1; r1[o + 3] = 1f;
                r2[o] = val2; r2[o + 1] = val2; r2[o + 2] = val2; r2[o + 3] = 1f;
            }
        }
        var buf1 = new Raw75.Imaging.RasterBuffer(r1, rw, rh);
        var buf2 = new Raw75.Imaging.RasterBuffer(r2, rw, rh);
        var mergedRamp = Raw75.Pipeline.HdrMerge.Merge(new[] { buf1, buf2 }, new[] { 0f, 2f }, refIndex: 0);
        float prevVal = -1f;
        for (int x = 0; x < rw; x++)
        {
            float val = mergedRamp.Linear![x * 4];
            if (val < prevVal - 1e-4f)
                throw new InvalidOperationException($"Merged ramp is not monotonically increasing at x={x}: val={val} < prev={prevVal}");
            prevVal = val;
        }
        if (prevVal < 0.95f)
            throw new InvalidOperationException($"Merged ramp failed to reconstruct highlights: max={prevVal}");
        Console.WriteLine("PASSED.");

        // 14. Deghosting (moving ghost suppression)
        Console.Write("14. Checking Deghosting (moving ghost suppression)... ");
        int gw = 64, gh = 64;
        float[] g1 = new float[gw * gh * 4];
        float[] g2 = new float[gw * gh * 4];
        for (int y = 0; y < gh; y++)
        {
            for (int x = 0; x < gw; x++)
            {
                int o = (y * gw + x) * 4;
                g1[o] = 0.1f; g1[o + 1] = 0.1f; g1[o + 2] = 0.1f; g1[o + 3] = 1f;

                bool inGhost = x >= 24 && x <= 40 && y >= 24 && y <= 40;
                float v2 = inGhost ? 0.9f : 0.1f;
                g2[o] = v2; g2[o + 1] = v2; g2[o + 2] = v2; g2[o + 3] = 1f;
            }
        }
        var gBuf1 = new Raw75.Imaging.RasterBuffer(g1, gw, gh);
        var gBuf2 = new Raw75.Imaging.RasterBuffer(g2, gw, gh);
        var gFrames = new[] { gBuf1, gBuf2 };
        var gEvs = new[] { 0f, 0f };

        // Deghost Off
        var mOff = Raw75.Pipeline.HdrMerge.Merge(gFrames, gEvs, refIndex: 0);
        int centerIdx = ((gh / 2) * gw + (gw / 2)) * 4;
        if (mOff.Linear![centerIdx] < 0.4f)
            throw new InvalidOperationException($"Deghost Off should include ghost: val={mOff.Linear[centerIdx]}");

        // Deghost Low
        var mLow = Raw75.Pipeline.HdrMerge.Merge(gFrames, gEvs, refIndex: 0,
            deghostMaskProvider: (yStart, tileH, w, totalH) =>
                Raw75.Pipeline.HdrDeghost.ComputeTileMask(gFrames, gEvs, 0, Raw75.Pipeline.HdrDeghostMode.Low, yStart, tileH, w, totalH));
        if (mLow.Linear![centerIdx] > 0.25f)
            throw new InvalidOperationException($"Deghost Low should suppress ghost: val={mLow.Linear[centerIdx]}");

        // Deghost High
        var mHigh = Raw75.Pipeline.HdrMerge.Merge(gFrames, gEvs, refIndex: 0,
            deghostMaskProvider: (yStart, tileH, w, totalH) =>
                Raw75.Pipeline.HdrDeghost.ComputeTileMask(gFrames, gEvs, 0, Raw75.Pipeline.HdrDeghostMode.High, yStart, tileH, w, totalH));
        if (MathF.Abs(mHigh.Linear![centerIdx] - 0.1f) > 0.05f)
            throw new InvalidOperationException($"Deghost High should lock to reference: val={mHigh.Linear[centerIdx]}");
        Console.WriteLine("PASSED.");

        // 15. File count limits (reject 1 file, cap at 7)
        Console.Write("15. Checking file count limits (min 2, max 7)... ");
        bool threwUnder = false;
        try
        {
            Raw75.Pipeline.HdrMerge.Merge(new[] { gBuf1 }, new[] { 0f }, 0);
        }
        catch (ArgumentException)
        {
            threwUnder = true;
        }
        if (!threwUnder)
            throw new InvalidOperationException("HdrMerge should reject less than 2 frames!");

        bool threwOver = false;
        try
        {
            var f8 = new Raw75.Imaging.RasterBuffer[8];
            var e8 = new float[8];
            for (int i = 0; i < 8; i++) { f8[i] = gBuf1; e8[i] = 0f; }
            Raw75.Pipeline.HdrMerge.Merge(f8, e8, 0);
        }
        catch (ArgumentException)
        {
            threwOver = true;
        }
        if (!threwOver)
            throw new InvalidOperationException("HdrMerge should reject more than 7 frames!");
        Console.WriteLine("PASSED.");

        Console.WriteLine("=== All Raw75 Pipeline Smoke Tests Passed Successfully ===");
    }

    private static Raw75.Imaging.RasterBuffer GrayLinear(int w, int h, float luma)
    {
        float[] lin = new float[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            int o = i * 4;
            lin[o] = luma;
            lin[o + 1] = luma;
            lin[o + 2] = luma;
            lin[o + 3] = 1f;
        }
        return new Raw75.Imaging.RasterBuffer(lin, w, h);
    }

    private static Raw75.Imaging.RasterBuffer MixedLinear(int w, int h, float body, float spec)
    {
        float[] lin = new float[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            int x = i % w;
            float luma = x > w * 3 / 4 ? spec : body;
            int o = i * 4;
            lin[o] = luma;
            lin[o + 1] = luma;
            lin[o + 2] = luma;
            lin[o + 3] = 1f;
        }
        return new Raw75.Imaging.RasterBuffer(lin, w, h);
    }
}
