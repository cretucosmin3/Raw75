using System;
using Blossom.Core.Visual;
using SkiaSharp;

namespace Raw75;

/// <summary>Develop chrome: dark panels, one accent, rounded cards, High density and depth.</summary>
public static class Theme
{
    // Surfaces & Wells (Refined multi-tier contrast hierarchy)
    public static readonly SKColor Canvas = new(12, 12, 14);              // Deepest neutral black photo well
    public static readonly SKColor Window = new(20, 20, 23);              // Global app chrome background
    public static readonly SKColor TopBar = new(26, 26, 30);              // Main top navigation & action strip
    public static readonly SKColor BottomBar = new(22, 22, 26);           // Bottom tools strip
    public static readonly SKColor Panel = new(22, 22, 26);               // Sidebar column background
    public static readonly SKColor PanelAlt = new(28, 28, 33);            // Elevated secondary column background
    public static readonly SKColor Filmstrip = new(18, 18, 22);           // Bottom filmstrip backdrop
    public static readonly SKColor Section = new(32, 32, 38);             // Tool cards & accordion body (clearly elevated from panel)
    public static readonly SKColor SectionHeader = new(26, 26, 32);       // Accordion header resting surface
    public static readonly SKColor SectionHeaderHover = new(38, 38, 46);   // Accordion header hover
    public static readonly SKColor Well = new(16, 16, 19);                // Inset wells, slider track backgrounds
    public static readonly SKColor WellHover = new(24, 24, 29);           // Hovered track background
    public static readonly SKColor Overlay = new(46, 46, 54);             // Floating panels & tooltips
    public static readonly SKColor PhotoWell = new(10, 10, 12);           // Histogram and thumbnail wells

    // Borders & Hairlines
    public static readonly SKColor Hairline = new(255, 255, 255, 26);     // 1px crisp card and button boundary
    public static readonly SKColor HairlineSubtle = new(255, 255, 255, 14);// Inner cell dividers & header bottom lines
    public static readonly SKColor HairlineStrong = new(255, 255, 255, 52);// Active boundary / focused frame

    // Typography
    public static readonly SKColor Text = new(248, 250, 252);             // Primary high-contrast labels
    public static readonly SKColor TextSecondary = new(205, 210, 222);     // Module titles, secondary info
    public static readonly SKColor TextDim = new(165, 170, 184);          // Value readouts, units, hints
    public static readonly SKColor TextDisabled = new(105, 110, 122);       // Inactive / disabled states

    // Accents & Selection
    public static readonly SKColor Accent = new(255, 153, 51);            // Signature Raw75 warm amber
    public static readonly SKColor AccentHover = new(255, 174, 82);       // Lighter interactive amber
    public static readonly SKColor AccentPressed = new(230, 130, 30);     // Pressed / active drag amber
    public static readonly SKColor AccentSoft = new(255, 153, 51, 34);    // Soft selection wash (13% alpha)
    public static readonly SKColor Selected = new(255, 153, 51, 44);      // Selected item background
    public static readonly SKColor Button = new(36, 36, 43);              // Standard button surface
    public static readonly SKColor ButtonHover = new(46, 46, 55);         // Button hover surface
    public static readonly SKColor Hover = ButtonHover;                   // Generic item hover surface
    public static readonly SKColor ButtonTextOnAccent = new(20, 20, 23);   // High-contrast text on accent pill
    public static readonly SKColor Success = new(46, 180, 80);           // Ready status green circle (#2EB450)
    public static readonly SKColor SuccessSoft = new(46, 180, 80, 140);   // Ready status cell border accent

    // Slider Specific
    public static readonly SKColor Track = Well;
    public static readonly SKColor TrackHover = WellHover;
    public static readonly SKColor TrackFill = Accent;
    public static readonly SKColor Handle = new(255, 255, 255);           // Pure white pill thumb
    public static readonly SKColor HandleHover = new(255, 225, 185);      // Soft warm glow on hover
    public static readonly SKColor HandleActive = new(255, 240, 215);     // Bright warm pill on active drag

    // Gradients & Channels
    public static readonly SKColor RedChannel = new(239, 68, 68);
    public static readonly SKColor GreenChannel = new(34, 197, 94);
    public static readonly SKColor BlueChannel = new(59, 130, 246);
    public static readonly SKColor KelvinCold = new(59, 130, 246);
    public static readonly SKColor KelvinWarm = new(245, 158, 11);
    public static readonly SKColor TintGreen = new(34, 197, 94);
    public static readonly SKColor TintMagenta = new(217, 70, 239);

    public static SKColor Lighten(SKColor c, int d) => new(
        (byte)Math.Min(255, c.Red + d),
        (byte)Math.Min(255, c.Green + d),
        (byte)Math.Min(255, c.Blue + d),
        c.Alpha);

    public static SKColor Darken(SKColor c, int d) => new(
        (byte)Math.Max(0, c.Red - d),
        (byte)Math.Max(0, c.Green - d),
        (byte)Math.Max(0, c.Blue - d),
        c.Alpha);

    // Geometry & Layout Constants
    public const float Radius = 6f;
    public const float RadiusSm = 4f;
    public const float TopBarH = 48f;
    public const float LeftW = 310f;
    public const float RightW = 356f;
    public const float FilmH = 108f;
    public const float ToolH = 32f;
    public const float StatusH = 26f;
    public const float GroupHeadH = 32f;
    public const float Pad = 10f;
    public const float SliderGap = 6f;

    // Compact Slider Constants
    public const float RowH = 28f;
    public const float RowHStacked = 34f;
    public const float SliderLabelW = 88f;
    public const float SliderValueW = 48f;
    public const float TrackH = 11f;
    public const float FillH = 5f;
    public const float ThumbW = 9f;
    public const float ThumbH = 18f;
    public const float ThumbRadius = 4.5f;
    public const float InsetX = 2f;
}
