using System;
using Blossom.Core.Visual;
using Blossom.Reactive;
using SkiaSharp;

namespace Raw75;

/// <summary>A complete chrome palette: surfaces, type, accent, and corner radii.</summary>
public sealed class UiThemePack
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public bool IsLight { get; init; }

    public required SKColor Canvas { get; init; }
    public required SKColor Window { get; init; }
    public required SKColor TopBar { get; init; }
    public required SKColor BottomBar { get; init; }
    public required SKColor Panel { get; init; }
    public required SKColor PanelAlt { get; init; }
    public required SKColor Filmstrip { get; init; }
    public required SKColor Section { get; init; }
    public required SKColor SectionHeader { get; init; }
    public required SKColor SectionHeaderHover { get; init; }
    public required SKColor Well { get; init; }
    public required SKColor WellHover { get; init; }
    public required SKColor Overlay { get; init; }
    public required SKColor PhotoWell { get; init; }

    public required SKColor Hairline { get; init; }
    public required SKColor HairlineSubtle { get; init; }
    public required SKColor HairlineStrong { get; init; }

    public required SKColor Text { get; init; }
    public required SKColor TextSecondary { get; init; }
    public required SKColor TextDim { get; init; }
    public required SKColor TextDisabled { get; init; }

    public required SKColor Accent { get; init; }
    public required SKColor AccentHover { get; init; }
    public required SKColor AccentPressed { get; init; }
    public required SKColor Button { get; init; }
    public required SKColor ButtonHover { get; init; }
    public required SKColor ButtonTextOnAccent { get; init; }

    public required SKColor Handle { get; init; }
    public required SKColor HandleHover { get; init; }
    public required SKColor HandleActive { get; init; }

    public required float Radius { get; init; }
    public required float RadiusSm { get; init; }

    /// <summary>When true, selected/primary chrome uses an accent stroke instead of a filled accent pill.</summary>
    public bool OutlineAccent { get; init; }

    /// <summary>Drop shadows on buttons and elevated cards.</summary>
    public bool UseShadows { get; init; }
    public float ShadowY { get; init; } = 2f;
    public byte ShadowAlpha { get; init; } = 70;
}

/// <summary>
/// Develop chrome tokens. Every token reads the active <see cref="UiThemePack"/> through the
/// <see cref="Pack"/> signal, so a <c>Bind</c> effect that paints with tokens re-runs when the
/// theme changes; reads outside an effect are plain reads.
/// </summary>
public static class Theme
{
    public static event Action? Changed;

    /// <summary>The active pack. Reading it inside an effect subscribes that effect to theme switches.</summary>
    public static Signal<UiThemePack> Pack => _pack;

    private static UiThemePack P => _pack.Value;

    /// <summary>Subscribes the running effect to theme switches without reading a token.</summary>
    public static void Observe() => _ = _pack.Value;

    public static string Id => P.Id;
    public static string DisplayName => P.Name;
    public static bool IsLight => P.IsLight;
    public static bool OutlineAccent => P.OutlineAccent;
    public static bool UseShadows => P.UseShadows;
    public static float ShadowY => P.ShadowY > 0 ? P.ShadowY : 2f;
    public static byte ShadowAlpha => P.ShadowAlpha > 0 ? P.ShadowAlpha : (byte)70;

    public static SKColor Canvas => P.Canvas;
    public static SKColor Window => P.Window;
    public static SKColor TopBar => P.TopBar;
    public static SKColor BottomBar => P.BottomBar;
    public static SKColor Panel => P.Panel;
    public static SKColor PanelAlt => P.PanelAlt;
    public static SKColor Filmstrip => P.Filmstrip;
    public static SKColor Section => P.Section;
    public static SKColor SectionHeader => P.SectionHeader;
    public static SKColor SectionHeaderHover => P.SectionHeaderHover;
    public static SKColor Well => P.Well;
    public static SKColor WellHover => P.WellHover;
    public static SKColor Overlay => P.Overlay;
    public static SKColor PhotoWell => P.PhotoWell;

    public static SKColor Hairline => P.Hairline;
    public static SKColor HairlineSubtle => P.HairlineSubtle;
    public static SKColor HairlineStrong => P.HairlineStrong;

    public const string FontFamily = "Liberation Sans, Noto Sans, sans-serif";
    public const string MonospaceFontFamily = "Liberation Mono, DejaVu Sans Mono, monospace";
    public static SKTypeface GetTypeface(int weight = 400, int width = 5, SKFontStyleSlant slant = SKFontStyleSlant.Upright)
        => Blossom.Utils.Fonts.GetTypeface(FontFamily, weight, width, slant);
    public static SKTypeface MonospaceTypeface => Blossom.Utils.Fonts.GetTypeface(MonospaceFontFamily, 400);

    public static SKColor Text => P.Text;
    public static SKColor TextSecondary => P.TextSecondary;
    public static SKColor TextDim => P.TextDim;
    public static SKColor TextDisabled => P.TextDisabled;

    public static SKColor Accent => P.Accent;
    public static SKColor AccentHover => P.AccentHover;
    public static SKColor AccentPressed => P.AccentPressed;
    public static SKColor AccentSoft => P.Accent.WithAlpha(34);
    /// <summary>Faint accent tint for selected rows and active segments.</summary>
    public static SKColor AccentWash => P.Accent.WithAlpha(20);
    public static SKColor Selected => P.Accent.WithAlpha(44);
    /// <summary>Readout colour for a parameter that differs from its default.</summary>
    public static SKColor Modified => P.OutlineAccent ? P.Text : P.Accent;
    public static SKColor Button => P.Button;
    public static SKColor ButtonHover => P.ButtonHover;
    public static SKColor Hover => P.ButtonHover;
    public static SKColor ButtonTextOnAccent => P.ButtonTextOnAccent;
    public static SKColor Success { get; } = new(46, 180, 80);
    public static SKColor SuccessSoft { get; } = new(46, 180, 80, 140);
    public static SKColor Favorite { get; } = new(255, 214, 50);
    public static SKColor FavoriteSoft { get; } = new(255, 214, 50, 140);

    /// <summary>Translucent scrim behind modal dialogs.</summary>
    public static SKColor Scrim => P.IsLight ? new SKColor(20, 20, 24, 110) : new SKColor(0, 0, 0, 150);

    public static SKColor Track => P.Well;
    public static SKColor TrackHover => P.WellHover;
    public static SKColor TrackFill => P.Accent;
    public static SKColor Handle => P.Handle;
    public static SKColor HandleHover => P.HandleHover;
    public static SKColor HandleActive => P.HandleActive;

    public static SKColor RedChannel { get; } = new(239, 68, 68);
    public static SKColor GreenChannel { get; } = new(34, 197, 94);
    public static SKColor BlueChannel { get; } = new(59, 130, 246);
    public static SKColor KelvinCold { get; } = new(59, 130, 246);
    public static SKColor KelvinWarm { get; } = new(245, 158, 11);
    public static SKColor TintGreen { get; } = new(34, 197, 94);
    public static SKColor TintMagenta { get; } = new(217, 70, 239);

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

    public static float Radius => P.Radius;
    public static float RadiusSm => P.RadiusSm;
    public const float TopBarH = 48f;
    public const float LeftW = 310f;
    public const float RightW = 356f;
    public const float FilmH = 108f;
    public const float ToolH = 30f;
    public const float StatusH = 26f;
    public const float GroupHeadH = 32f;
    public const float Pad = 10f;
    public const float SliderGap = 4f;

    public const float RowH = 26f;
    public const float RowHStacked = 40f;
    /// <summary>Breathing room above a stacked slider's label row.</summary>
    public const float StackedTopGap = 6f;
    public const float SliderLabelW = 84f;
    public const float SliderValueW = 48f;
    public const float TrackH = 10f;
    public const float FillH = 4f;
    public const float ThumbW = 9f;
    public const float ThumbH = 18f;
    public const float ThumbRadius = 4.5f;
    public const float InsetX = 2f;

    public static void ApplyButtonShadow(ElementStyle style)
    {
        ApplyShadow(style, ShadowY * 0.7f, (byte)Math.Max(0, ShadowAlpha - 20));
    }

    public static void ApplyCardShadow(ElementStyle style)
    {
        ApplyShadow(style, ShadowY, ShadowAlpha);
    }

    private static void ApplyShadow(ElementStyle style, float y, byte alpha)
    {
        if (style == null) return;
        if (!UseShadows)
        {
            if (style.Shadow != null)
                style.Shadow.Color = SKColors.Transparent;
            return;
        }

        style.Shadow ??= new ShadowStyle();
        style.Shadow.OffsetX = 0f;
        style.Shadow.OffsetY = y;
        style.Shadow.SpreadX = y + 0.5f;
        style.Shadow.SpreadY = y + 0.5f;
        style.Shadow.Color = new SKColor(0, 0, 0, alpha);
    }

    public static readonly UiThemePack Midnight = new()
    {
        Id = "midnight",
        Name = "Midnight",
        Description = "Neutral dark studio chrome, warm amber accent.",
        UseShadows = true,
        ShadowY = 2.4f,
        ShadowAlpha = 80,
        Canvas = new(12, 12, 14),
        Window = new(20, 20, 23),
        TopBar = new(26, 26, 30),
        BottomBar = new(22, 22, 26),
        Panel = new(22, 22, 26),
        PanelAlt = new(28, 28, 33),
        Filmstrip = new(18, 18, 22),
        Section = new(32, 32, 38),
        SectionHeader = new(26, 26, 32),
        SectionHeaderHover = new(38, 38, 46),
        Well = new(16, 16, 19),
        WellHover = new(24, 24, 29),
        Overlay = new(46, 46, 54),
        PhotoWell = new(10, 10, 12),
        Hairline = new(255, 255, 255, 26),
        HairlineSubtle = new(255, 255, 255, 14),
        HairlineStrong = new(255, 255, 255, 52),
        Text = new(248, 250, 252),
        TextSecondary = new(205, 210, 222),
        TextDim = new(165, 170, 184),
        TextDisabled = new(105, 110, 122),
        Accent = new(255, 153, 51),
        AccentHover = new(255, 174, 82),
        AccentPressed = new(230, 130, 30),
        Button = new(36, 36, 43),
        ButtonHover = new(46, 46, 55),
        ButtonTextOnAccent = new(20, 20, 23),
        Handle = new(255, 255, 255),
        HandleHover = new(255, 225, 185),
        HandleActive = new(255, 240, 215),
        Radius = 6f,
        RadiusSm = 4f
    };

    public static readonly UiThemePack Carbon = new()
    {
        Id = "carbon",
        Name = "Carbon",
        Description = "Neutral dark chrome, steel accent, tight corners.",
        UseShadows = true,
        ShadowY = 1.6f,
        ShadowAlpha = 55,
        Canvas = new(11, 12, 13),
        Window = new(19, 20, 22),
        TopBar = new(25, 26, 28),
        BottomBar = new(21, 22, 24),
        Panel = new(21, 22, 24),
        PanelAlt = new(27, 28, 31),
        Filmstrip = new(17, 18, 20),
        Section = new(31, 32, 35),
        SectionHeader = new(25, 26, 28),
        SectionHeaderHover = new(37, 38, 42),
        Well = new(15, 16, 18),
        WellHover = new(23, 24, 27),
        Overlay = new(45, 46, 50),
        PhotoWell = new(9, 10, 11),
        Hairline = new(255, 255, 255, 26),
        HairlineSubtle = new(255, 255, 255, 14),
        HairlineStrong = new(255, 255, 255, 52),
        Text = new(236, 242, 248),
        TextSecondary = new(176, 190, 206),
        TextDim = new(132, 146, 162),
        TextDisabled = new(90, 102, 116),
        Accent = new(94, 184, 255),
        AccentHover = new(130, 200, 255),
        AccentPressed = new(64, 150, 220),
        Button = new(30, 36, 44),
        ButtonHover = new(40, 48, 58),
        ButtonTextOnAccent = new(10, 16, 22),
        Handle = new(240, 246, 252),
        HandleHover = new(200, 226, 255),
        HandleActive = new(220, 236, 255),
        Radius = 2f,
        RadiusSm = 2f
    };

    public static readonly UiThemePack Copper = new()
    {
        Id = "copper",
        Name = "Copper",
        Description = "Neutral dark chrome, copper accent, soft corners.",
        UseShadows = true,
        ShadowY = 3.2f,
        ShadowAlpha = 110,
        Canvas = new(12, 12, 13),
        Window = new(20, 20, 22),
        TopBar = new(26, 26, 28),
        BottomBar = new(22, 22, 24),
        Panel = new(22, 22, 24),
        PanelAlt = new(28, 28, 31),
        Filmstrip = new(18, 18, 19),
        Section = new(32, 32, 35),
        SectionHeader = new(26, 26, 28),
        SectionHeaderHover = new(38, 38, 41),
        Well = new(16, 16, 17),
        WellHover = new(24, 24, 26),
        Overlay = new(46, 46, 50),
        PhotoWell = new(10, 10, 11),
        Hairline = new(255, 255, 255, 26),
        HairlineSubtle = new(255, 255, 255, 14),
        HairlineStrong = new(255, 255, 255, 52),
        Text = new(246, 246, 248),
        TextSecondary = new(196, 196, 202),
        TextDim = new(150, 150, 156),
        TextDisabled = new(104, 104, 110),
        Accent = new(196, 118, 64),
        AccentHover = new(214, 138, 84),
        AccentPressed = new(168, 96, 48),
        Button = new(36, 36, 40),
        ButtonHover = new(46, 46, 50),
        ButtonTextOnAccent = new(20, 16, 12),
        Handle = new(255, 255, 255),
        HandleHover = new(255, 220, 190),
        HandleActive = new(255, 232, 210),
        Radius = 12f,
        RadiusSm = 8f
    };

    public static readonly UiThemePack Noir = new()
    {
        Id = "noir",
        Name = "Noir",
        Description = "OLED black, gold accent, square edges.",
        Canvas = new(4, 4, 4),
        Window = new(10, 10, 10),
        TopBar = new(16, 16, 16),
        BottomBar = new(12, 12, 12),
        Panel = new(14, 14, 14),
        PanelAlt = new(22, 22, 22),
        Filmstrip = new(8, 8, 8),
        Section = new(26, 26, 26),
        SectionHeader = new(18, 18, 18),
        SectionHeaderHover = new(34, 34, 34),
        Well = new(6, 6, 6),
        WellHover = new(16, 16, 16),
        Overlay = new(32, 32, 32),
        PhotoWell = new(0, 0, 0),
        Hairline = new(255, 255, 255, 36),
        HairlineSubtle = new(255, 255, 255, 16),
        HairlineStrong = new(255, 255, 255, 72),
        Text = new(250, 250, 250),
        TextSecondary = new(196, 196, 196),
        TextDim = new(140, 140, 140),
        TextDisabled = new(88, 88, 88),
        Accent = new(212, 175, 55),
        AccentHover = new(232, 196, 80),
        AccentPressed = new(176, 140, 36),
        Button = new(28, 28, 28),
        ButtonHover = new(40, 40, 40),
        ButtonTextOnAccent = new(12, 12, 12),
        Handle = new(255, 255, 255),
        HandleHover = new(255, 232, 160),
        HandleActive = new(255, 244, 196),
        Radius = 0f,
        RadiusSm = 0f
    };

    public static readonly UiThemePack Moss = new()
    {
        Id = "moss",
        Name = "Moss",
        Description = "Neutral dark chrome, sage accent.",
        UseShadows = true,
        ShadowY = 2.2f,
        ShadowAlpha = 75,
        Canvas = new(12, 12, 13),
        Window = new(20, 21, 20),
        TopBar = new(26, 27, 26),
        BottomBar = new(22, 23, 22),
        Panel = new(22, 23, 22),
        PanelAlt = new(28, 30, 28),
        Filmstrip = new(18, 19, 18),
        Section = new(32, 34, 32),
        SectionHeader = new(26, 27, 26),
        SectionHeaderHover = new(38, 40, 38),
        Well = new(16, 17, 16),
        WellHover = new(24, 26, 24),
        Overlay = new(46, 48, 46),
        PhotoWell = new(10, 11, 10),
        Hairline = new(255, 255, 255, 26),
        HairlineSubtle = new(255, 255, 255, 14),
        HairlineStrong = new(255, 255, 255, 52),
        Text = new(246, 248, 246),
        TextSecondary = new(196, 200, 196),
        TextDim = new(150, 154, 150),
        TextDisabled = new(104, 108, 104),
        Accent = new(122, 158, 96),
        AccentHover = new(142, 176, 114),
        AccentPressed = new(96, 130, 76),
        Button = new(36, 38, 36),
        ButtonHover = new(46, 48, 46),
        ButtonTextOnAccent = new(16, 20, 14),
        Handle = new(255, 255, 255),
        HandleHover = new(210, 230, 190),
        HandleActive = new(226, 238, 210),
        Radius = 5f,
        RadiusSm = 3f
    };

    public static readonly UiThemePack Ink = new()
    {
        Id = "ink",
        Name = "Ink",
        Description = "Neutral dark chrome, indigo accent.",
        UseShadows = true,
        ShadowY = 2.6f,
        ShadowAlpha = 90,
        Canvas = new(12, 12, 14),
        Window = new(20, 20, 24),
        TopBar = new(26, 26, 30),
        BottomBar = new(22, 22, 26),
        Panel = new(22, 22, 26),
        PanelAlt = new(28, 28, 34),
        Filmstrip = new(18, 18, 22),
        Section = new(32, 32, 38),
        SectionHeader = new(26, 26, 32),
        SectionHeaderHover = new(38, 38, 46),
        Well = new(16, 16, 20),
        WellHover = new(24, 24, 30),
        Overlay = new(46, 46, 54),
        PhotoWell = new(10, 10, 12),
        Hairline = new(255, 255, 255, 26),
        HairlineSubtle = new(255, 255, 255, 14),
        HairlineStrong = new(255, 255, 255, 52),
        Text = new(246, 246, 252),
        TextSecondary = new(196, 196, 210),
        TextDim = new(150, 150, 164),
        TextDisabled = new(104, 104, 118),
        Accent = new(124, 108, 220),
        AccentHover = new(148, 132, 236),
        AccentPressed = new(100, 86, 188),
        Button = new(36, 36, 44),
        ButtonHover = new(46, 46, 56),
        ButtonTextOnAccent = new(248, 248, 255),
        Handle = new(255, 255, 255),
        HandleHover = new(210, 200, 255),
        HandleActive = new(226, 220, 255),
        Radius = 8f,
        RadiusSm = 5f
    };

    public static readonly UiThemePack Graphite = new()
    {
        Id = "graphite",
        Name = "Graphite",
        Description = "Flat gray chrome, white selected buttons.",
        Canvas = new(12, 12, 12),
        Window = new(18, 18, 18),
        TopBar = new(24, 24, 24),
        BottomBar = new(20, 20, 20),
        Panel = new(20, 20, 20),
        PanelAlt = new(28, 28, 28),
        Filmstrip = new(14, 14, 14),
        Section = new(32, 32, 32),
        SectionHeader = new(24, 24, 24),
        SectionHeaderHover = new(40, 40, 40),
        Well = new(12, 12, 12),
        WellHover = new(22, 22, 22),
        Overlay = new(44, 44, 44),
        PhotoWell = new(8, 8, 8),
        Hairline = new(255, 255, 255, 40),
        HairlineSubtle = new(255, 255, 255, 18),
        HairlineStrong = new(255, 255, 255, 88),
        Text = new(248, 248, 248),
        TextSecondary = new(196, 196, 196),
        TextDim = new(150, 150, 150),
        TextDisabled = new(100, 100, 100),
        Accent = new(236, 236, 236),
        AccentHover = new(255, 255, 255),
        AccentPressed = new(210, 210, 210),
        Button = new(72, 72, 72),
        ButtonHover = new(92, 92, 92),
        ButtonTextOnAccent = new(12, 12, 12),
        Handle = new(255, 255, 255),
        HandleHover = new(236, 236, 236),
        HandleActive = new(255, 255, 255),
        Radius = 2f,
        RadiusSm = 2f
    };

    public static readonly UiThemePack Slate = new()
    {
        Id = "slate",
        Name = "Slate",
        Description = "Flat cool gray, bright cool-white controls.",
        Canvas = new(11, 12, 14),
        Window = new(17, 18, 21),
        TopBar = new(23, 24, 28),
        BottomBar = new(19, 20, 24),
        Panel = new(19, 20, 24),
        PanelAlt = new(27, 28, 32),
        Filmstrip = new(13, 14, 17),
        Section = new(31, 32, 36),
        SectionHeader = new(23, 24, 28),
        SectionHeaderHover = new(39, 40, 46),
        Well = new(11, 12, 15),
        WellHover = new(21, 22, 26),
        Overlay = new(43, 44, 50),
        PhotoWell = new(7, 8, 10),
        Hairline = new(255, 255, 255, 42),
        HairlineSubtle = new(255, 255, 255, 18),
        HairlineStrong = new(255, 255, 255, 92),
        Text = new(244, 246, 252),
        TextSecondary = new(188, 192, 204),
        TextDim = new(144, 148, 160),
        TextDisabled = new(98, 102, 112),
        Accent = new(224, 230, 242),
        AccentHover = new(240, 244, 255),
        AccentPressed = new(196, 204, 220),
        Button = new(68, 72, 82),
        ButtonHover = new(88, 94, 106),
        ButtonTextOnAccent = new(12, 14, 18),
        Handle = new(255, 255, 255),
        HandleHover = new(224, 230, 242),
        HandleActive = new(240, 244, 252),
        Radius = 0f,
        RadiusSm = 0f
    };

    public static readonly UiThemePack Ash = new()
    {
        Id = "ash",
        Name = "Ash",
        Description = "Flat gray chrome, white outline on active controls.",
        OutlineAccent = true,
        Canvas = new(14, 14, 14),
        Window = new(22, 22, 22),
        TopBar = new(28, 28, 28),
        BottomBar = new(24, 24, 24),
        Panel = new(24, 24, 24),
        PanelAlt = new(32, 32, 32),
        Filmstrip = new(18, 18, 18),
        Section = new(36, 36, 36),
        SectionHeader = new(28, 28, 28),
        SectionHeaderHover = new(44, 44, 44),
        Well = new(16, 16, 16),
        WellHover = new(26, 26, 26),
        Overlay = new(48, 48, 48),
        PhotoWell = new(10, 10, 10),
        Hairline = new(255, 255, 255, 44),
        HairlineSubtle = new(255, 255, 255, 18),
        HairlineStrong = new(255, 255, 255, 96),
        Text = new(246, 246, 246),
        TextSecondary = new(190, 190, 190),
        TextDim = new(146, 146, 146),
        TextDisabled = new(100, 100, 100),
        Accent = new(255, 255, 255),
        AccentHover = new(255, 255, 255),
        AccentPressed = new(210, 210, 210),
        Button = new(64, 64, 64),
        ButtonHover = new(84, 84, 84),
        ButtonTextOnAccent = new(255, 255, 255),
        Handle = new(255, 255, 255),
        HandleHover = new(236, 236, 236),
        HandleActive = new(255, 255, 255),
        Radius = 3f,
        RadiusSm = 2f
    };

    public static readonly UiThemePack Tide = new()
    {
        Id = "tide",
        Name = "Tide",
        Description = "Neutral dark chrome, teal accent, soft corners.",
        UseShadows = true,
        ShadowY = 2.4f,
        ShadowAlpha = 85,
        Canvas = new(11, 12, 13),
        Window = new(19, 20, 22),
        TopBar = new(25, 26, 28),
        BottomBar = new(21, 22, 24),
        Panel = new(21, 22, 24),
        PanelAlt = new(27, 28, 31),
        Filmstrip = new(17, 18, 20),
        Section = new(31, 32, 35),
        SectionHeader = new(25, 26, 28),
        SectionHeaderHover = new(37, 39, 42),
        Well = new(15, 16, 17),
        WellHover = new(23, 24, 26),
        Overlay = new(45, 47, 50),
        PhotoWell = new(9, 10, 11),
        Hairline = new(255, 255, 255, 26),
        HairlineSubtle = new(255, 255, 255, 14),
        HairlineStrong = new(255, 255, 255, 52),
        Text = new(242, 246, 247),
        TextSecondary = new(190, 198, 200),
        TextDim = new(144, 154, 157),
        TextDisabled = new(98, 106, 109),
        Accent = new(45, 200, 180),
        AccentHover = new(80, 220, 200),
        AccentPressed = new(30, 168, 150),
        Button = new(34, 36, 39),
        ButtonHover = new(44, 47, 50),
        ButtonTextOnAccent = new(8, 24, 22),
        Handle = new(255, 255, 255),
        HandleHover = new(200, 245, 238),
        HandleActive = new(220, 250, 245),
        Radius = 7f,
        RadiusSm = 4f
    };

    public static readonly UiThemePack[] All = { Midnight, Carbon, Copper, Noir, Moss, Ink, Tide, Graphite, Slate, Ash };

    // Declared after the packs: static initializers run in textual order.
    private static readonly Signal<UiThemePack> _pack = new(Midnight);

    public static UiThemePack Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return Midnight;
        foreach (var pack in All)
        {
            if (string.Equals(pack.Id, id, StringComparison.OrdinalIgnoreCase))
                return pack;
        }
        return Midnight;
    }

    public static void Apply(string? id) => Apply(Find(id));

    public static void Apply(UiThemePack pack, bool persist = true, bool notify = true)
    {
        ArgumentNullException.ThrowIfNull(pack);
        _pack.Value = pack;

        if (persist)
            Io.UiPrefs.SaveThemeId(pack.Id);
        if (notify)
            Changed?.Invoke();
    }
}
