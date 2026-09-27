using System;

namespace Raw75.Develop;

/// <summary>
/// One heal/clone stamp: destination circle replaced from a source circle.
/// Centers are normalized source-image UVs (0–1). Radius is a fraction of min(width, height).
/// Feather 0 is a hard edge; 1 feathers across the full radius.
/// </summary>
public sealed class SpotPatch
{
    public float DestX { get; set; } = 0.5f;
    public float DestY { get; set; } = 0.5f;
    public float SrcX { get; set; } = 0.58f;
    public float SrcY { get; set; } = 0.5f;
    public float Radius { get; set; } = 0.028f;
    public float Feather { get; set; } = 0.4f;

    public const float MinRadius = 0.004f;
    public const float MaxRadius = 0.16f;

    public SpotPatch Clone() => new()
    {
        DestX = DestX,
        DestY = DestY,
        SrcX = SrcX,
        SrcY = SrcY,
        Radius = Radius,
        Feather = Feather
    };

    public static float SizeToRadius(float size01to100) =>
        MinRadius + Math.Clamp(size01to100, 1f, 100f) / 100f * (MaxRadius - MinRadius);

    public static float RadiusToSize(float radius) =>
        Math.Clamp((radius - MinRadius) / (MaxRadius - MinRadius) * 100f, 1f, 100f);

    public bool LooksLike(SpotPatch o) =>
        o != null
        && MathF.Abs(DestX - o.DestX) < 1e-5f && MathF.Abs(DestY - o.DestY) < 1e-5f
        && MathF.Abs(SrcX - o.SrcX) < 1e-5f && MathF.Abs(SrcY - o.SrcY) < 1e-5f
        && MathF.Abs(Radius - o.Radius) < 1e-5f && MathF.Abs(Feather - o.Feather) < 1e-5f;
}
