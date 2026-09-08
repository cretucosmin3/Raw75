using System;
using System.Text.Json.Serialization;

namespace Raw75.Develop;

/// <summary>
/// All develop parameters. Cloneable. JSON = preset shape.
/// Normalized slider ranges.
/// </summary>
public sealed class DevelopSettings
{
    // White balance. Temp/Tint 0 = as shot (camera multipliers).
    public float Temperature { get; set; } // -100..100 (amber/blue offset)
    public float Tint { get; set; }        // -100..100 (green/magenta)

    public float Exposure { get; set; }    // -5..5 EV
    public float Contrast { get; set; }    // -100..100
    public float Highlights { get; set; }  // -100..100
    public float Shadows { get; set; }     // -100..100
    public float Whites { get; set; }      // -100..100
    public float Blacks { get; set; }      // -100..100
    public float Vibrance { get; set; }    // -100..100
    public float Saturation { get; set; }  // -100..100

    public bool MatchGray { get; set; }

    // Reds, Oranges, Yellows, Greens, Aquas, Blues
    public HslBand[] Hsl { get; set; } = CreateHsl();

    public float Sharpen { get; set; }       // 0..150
    public float DenoiseLuma { get; set; }   // 0..100
    public float DenoiseChroma { get; set; } // 0..100

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

    [JsonIgnore]
    public bool HasCrop => CropW > 0.001f && CropH > 0.001f;

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

    public void CopyFrom(DevelopSettings src)
    {
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
        Sharpen = src.Sharpen;
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
        if (src.Hsl != null && src.Hsl.Length == 6)
            Array.Copy(src.Hsl, Hsl, 6);
    }

    public bool LooksLike(DevelopSettings o)
    {
        if (o == null) return false;
        return Temperature == o.Temperature && Tint == o.Tint
            && Exposure == o.Exposure && Contrast == o.Contrast
            && Highlights == o.Highlights && Shadows == o.Shadows
            && Whites == o.Whites && Blacks == o.Blacks
            && Vibrance == o.Vibrance && Saturation == o.Saturation
            && MatchGray == o.MatchGray
            && Sharpen == o.Sharpen && DenoiseLuma == o.DenoiseLuma && DenoiseChroma == o.DenoiseChroma
            && LutPath == o.LutPath && LutAmount == o.LutAmount
            && CropX == o.CropX && CropY == o.CropY && CropW == o.CropW && CropH == o.CropH
            && Straighten == o.Straighten && Rotate90 == o.Rotate90 && FlipH == o.FlipH && FlipV == o.FlipV
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
