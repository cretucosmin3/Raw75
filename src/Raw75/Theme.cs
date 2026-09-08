using SkiaSharp;

namespace Raw75;

/// <summary>Develop chrome: dark panels, one accent, rounded cards.</summary>
public static class Theme
{
    public static readonly SKColor Window = new(22, 22, 24);
    public static readonly SKColor Panel = new(32, 32, 36);
    public static readonly SKColor PanelAlt = new(40, 40, 45);
    public static readonly SKColor TopBar = new(28, 28, 32);
    public static readonly SKColor Filmstrip = new(24, 24, 26);
    public static readonly SKColor Hairline = new(255, 255, 255, 22);
    public static readonly SKColor Text = new(244, 244, 247);
    public static readonly SKColor TextDim = new(158, 158, 166);
    public static readonly SKColor Accent = new(255, 153, 51);
    public static readonly SKColor AccentSoft = new(255, 153, 51, 36);
    public static readonly SKColor Track = new(18, 18, 20);
    public static readonly SKColor TrackFill = new(255, 153, 51);
    public static readonly SKColor Handle = new(255, 255, 255);
    public static readonly SKColor PhotoWell = new(10, 10, 12);
    public static readonly SKColor Selected = new(255, 153, 51, 40);
    public static readonly SKColor Button = new(48, 48, 54);

    public const float Radius = 8f;
    public const float RadiusSm = 6f;
    public const float TopBarH = 48f;
    public const float LeftW = 260f;
    public const float RightW = 312f;
    public const float FilmH = 108f;
    public const float ToolH = 40f;
    public const float StatusH = 24f;
    public const float RowH = 44f;
    public const float SliderGap = 10f;
    public const float GroupHeadH = 30f;
    public const float Pad = 10f;
}
