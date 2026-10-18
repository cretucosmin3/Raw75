using SkiaSharp;

namespace Raw75.Controls;

/// <summary>Image sampling presets (SkiaSharp 3+ moved filtering from SKPaint to draw calls).</summary>
internal static class Sampling
{
    /// <summary>Bilinear with mip blending, for downscaled thumbnails.</summary>
    public static readonly SKSamplingOptions Smooth = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    /// <summary>Bilinear without mips.</summary>
    public static readonly SKSamplingOptions Linear = new(SKFilterMode.Linear, SKMipmapMode.None);
}
