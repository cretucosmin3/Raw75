using System;
using System.Text.Json.Serialization;

namespace Raw75.Develop;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ToneMode
{
    Sigmoid = 0,
    Filmic = 1
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HighlightMode
{
    Off = 0,
    Opposed = 1,
    LCh = 2
}

/// <summary>
/// All develop parameters. Cloneable. JSON = preset shape.
/// Normalized slider ranges.
/// </summary>
public sealed class DevelopSettings
{
    // Section bypass / enable switches (true = module active, false = module bypassed)
    public bool EnableWhiteBalance { get; set; } = true;
    public bool EnableExposure { get; set; } = true;
    public bool EnableHdr { get; set; } = true;
    public bool EnableLocalContrast { get; set; } = true;
    public bool EnableTone { get; set; } = true;
    public bool EnableReconstruction { get; set; } = true;
    public bool EnableHsl { get; set; } = true;
    public bool EnableDetail { get; set; } = true;
    public bool EnableGeometry { get; set; } = true;

    // White balance. Temp/Tint 0 = as shot (camera multipliers).
    public float Temperature { get; set; } // -100..100 (amber/blue offset)
    public float Tint { get; set; }        // -100..100 (green/magenta)

    public float Exposure { get; set; }    // -2.5..2.5 EV
    public float Contrast { get; set; }    // -50..50
    public float Highlights { get; set; }  // -100..100
    public float Shadows { get; set; }     // -100..100
    public float Whites { get; set; }      // -100..100
    public float Blacks { get; set; }      // -100..100
    public float Vibrance { get; set; }    // -100..100
    public float Saturation { get; set; }  // -100..100

    public bool MatchGray { get; set; }

    // --- Tone (display transform) ---
    public ToneMode ToneMode { get; set; } = ToneMode.Sigmoid;
    public float SigmoidContrast { get; set; } = 1.5f; // 0.1..10, default 1.5
    public float SigmoidSkew { get; set; } = 0.0f;     // -1..1, default 0.0

    // --- Reconstruction ---
    public HighlightMode ReconstructionMode { get; set; } = HighlightMode.Off;
    public float HighlightThreshold { get; set; } = 1.0f;         // clip relative to white
    public float ColorReconstructionAmount { get; set; } = 0.0f;  // 0..100
    public float ColorReconstructionSpatial { get; set; } = 0.0f; // 0..100

    // --- Local contrast & Atmosphere ---
    public float LocalContrastDetail { get; set; } = 0.0f;     // -1..4, 0 = off
    public float Dehaze { get; set; } = 0.0f;                  // -100..100
    public float Texture { get; set; } = 0.0f;                 // -100..100
    public float LocalContrastHighlights { get; set; } = 0.0f; // -100..100
    public float LocalContrastShadows { get; set; } = 0.0f;    // -100..100
    public float LocalContrastMidtones { get; set; } = 50.0f;  // 0..100

    // --- Color Grading (Split Toning) ---
    public float GradingShadowHue { get; set; } = 220.0f;      // 0..360 (default teal/blue)
    public float GradingShadowSat { get; set; } = 0.0f;        // 0..100
    public float GradingHighlightHue { get; set; } = 40.0f;    // 0..360 (default warm amber)
    public float GradingHighlightSat { get; set; } = 0.0f;     // 0..100
    public float GradingBalance { get; set; } = 0.0f;          // -100..100

    // Reds, Oranges, Yellows, Greens, Aquas, Blues
    public HslBand[] Hsl { get; set; } = CreateHsl();

    public float Sharpen { get; set; }       // 0..150
    public float Noise { get; set; }         // -100..100 (-100 = denoise, +100 = film grain, 0 = off)
    public float DenoiseLuma { get; set; }   // legacy fallback 0..100
    public float DenoiseChroma { get; set; } // legacy fallback 0..100

    public string? LutPath { get; set; }
    public float LutAmount { get; set; } = 1f; // 0..1

    // Crop in normalized source coords (0..1). Width/Height 0 = full frame.
    public float CropX { get; set; }
    public float CropY { get; set; }
    public float CropW { get; set; }
    public float CropH { get; set; }
    public float Straighten { get; set; } // degrees, typically -45..45
    public int Rotate90 { get; set; }     // 0..3 quarter turns CW
    public bool FlipH { get; set; }
    public bool FlipV { get; set; }
    public float VignetteAmount { get; set; } = 0.0f;    // -100..100 (optical falloff correction or creative vignette)
    public float VignetteMidpoint { get; set; } = 50.0f; // 0..100

    [JsonIgnore]
    public bool HasCrop =>
        CropW > 0.001f && CropH > 0.001f
        && (CropW < 0.999f || CropH < 0.999f || CropX > 0.001f || CropY > 0.001f);

    public static HslBand[] CreateHsl()
    {
        var a = new HslBand[6];
        return a;
    }

    public DevelopSettings Clone()
    {
        var c = (DevelopSettings)MemberwiseClone();
        c.Hsl = new HslBand[6];
        Array.Copy(Hsl, c.Hsl, 6);
        return c;
    }

    public void Reset()
    {
        CopyFrom(new DevelopSettings());
    }

    public void CopyFrom(DevelopSettings src)
    {
        EnableWhiteBalance = src.EnableWhiteBalance;
        EnableExposure = src.EnableExposure;
        EnableHdr = src.EnableHdr;
        EnableLocalContrast = src.EnableLocalContrast;
        EnableTone = src.EnableTone;
        EnableReconstruction = src.EnableReconstruction;
        EnableHsl = src.EnableHsl;
        EnableDetail = src.EnableDetail;
        EnableGeometry = src.EnableGeometry;

        Temperature = src.Temperature;
        Tint = src.Tint;
        Exposure = src.Exposure;
        Contrast = src.Contrast;
        Highlights = src.Highlights;
        Shadows = src.Shadows;
        Whites = src.Whites;
        Blacks = src.Blacks;
        Vibrance = src.Vibrance;
        Saturation = src.Saturation;
        MatchGray = src.MatchGray;
        ToneMode = src.ToneMode;
        SigmoidContrast = src.SigmoidContrast;
        SigmoidSkew = src.SigmoidSkew;
        ReconstructionMode = src.ReconstructionMode;
        HighlightThreshold = src.HighlightThreshold;
        ColorReconstructionAmount = src.ColorReconstructionAmount;
        ColorReconstructionSpatial = src.ColorReconstructionSpatial;
        LocalContrastDetail = src.LocalContrastDetail;
        Dehaze = src.Dehaze;
        Texture = src.Texture;
        LocalContrastHighlights = src.LocalContrastHighlights;
        LocalContrastShadows = src.LocalContrastShadows;
        LocalContrastMidtones = src.LocalContrastMidtones;
        GradingShadowHue = src.GradingShadowHue;
        GradingShadowSat = src.GradingShadowSat;
        GradingHighlightHue = src.GradingHighlightHue;
        GradingHighlightSat = src.GradingHighlightSat;
        GradingBalance = src.GradingBalance;
        Sharpen = src.Sharpen;
        Noise = src.Noise;
        DenoiseLuma = src.DenoiseLuma;
        DenoiseChroma = src.DenoiseChroma;
        LutPath = src.LutPath;
        LutAmount = src.LutAmount;
        CropX = src.CropX;
        CropY = src.CropY;
        CropW = src.CropW;
        CropH = src.CropH;
        Straighten = src.Straighten;
        Rotate90 = src.Rotate90;
        FlipH = src.FlipH;
        FlipV = src.FlipV;
        VignetteAmount = src.VignetteAmount;
        VignetteMidpoint = src.VignetteMidpoint;
        if (src.Hsl != null && src.Hsl.Length == 6)
            Array.Copy(src.Hsl, Hsl, 6);
    }

    public bool LooksLike(DevelopSettings o)
    {
        if (o == null) return false;
        return EnableWhiteBalance == o.EnableWhiteBalance
            && EnableExposure == o.EnableExposure
            && EnableHdr == o.EnableHdr
            && EnableLocalContrast == o.EnableLocalContrast
            && EnableTone == o.EnableTone
            && EnableReconstruction == o.EnableReconstruction
            && EnableHsl == o.EnableHsl
            && EnableDetail == o.EnableDetail
            && EnableGeometry == o.EnableGeometry
            && Temperature == o.Temperature && Tint == o.Tint
            && Exposure == o.Exposure && Contrast == o.Contrast
            && Highlights == o.Highlights && Shadows == o.Shadows
            && Whites == o.Whites && Blacks == o.Blacks
            && Vibrance == o.Vibrance && Saturation == o.Saturation
            && MatchGray == o.MatchGray
            && ToneMode == o.ToneMode
            && SigmoidContrast == o.SigmoidContrast && SigmoidSkew == o.SigmoidSkew
            && ReconstructionMode == o.ReconstructionMode && HighlightThreshold == o.HighlightThreshold
            && ColorReconstructionAmount == o.ColorReconstructionAmount && ColorReconstructionSpatial == o.ColorReconstructionSpatial
            && LocalContrastDetail == o.LocalContrastDetail && Dehaze == o.Dehaze && Texture == o.Texture
            && LocalContrastHighlights == o.LocalContrastHighlights
            && LocalContrastShadows == o.LocalContrastShadows && LocalContrastMidtones == o.LocalContrastMidtones
            && GradingShadowHue == o.GradingShadowHue && GradingShadowSat == o.GradingShadowSat
            && GradingHighlightHue == o.GradingHighlightHue && GradingHighlightSat == o.GradingHighlightSat && GradingBalance == o.GradingBalance
            && Sharpen == o.Sharpen && Noise == o.Noise && DenoiseLuma == o.DenoiseLuma && DenoiseChroma == o.DenoiseChroma
            && LutPath == o.LutPath && LutAmount == o.LutAmount
            && CropX == o.CropX && CropY == o.CropY && CropW == o.CropW && CropH == o.CropH
            && Straighten == o.Straighten && Rotate90 == o.Rotate90 && FlipH == o.FlipH && FlipV == o.FlipV
            && VignetteAmount == o.VignetteAmount && VignetteMidpoint == o.VignetteMidpoint
            && HslEqual(o);
    }

    private bool HslEqual(DevelopSettings o)
    {
        for (int i = 0; i < 6; i++)
        {
            if (Hsl[i].Hue != o.Hsl[i].Hue || Hsl[i].Sat != o.Hsl[i].Sat || Hsl[i].Luma != o.Hsl[i].Luma)
                return false;
        }
        return true;
    }
}
