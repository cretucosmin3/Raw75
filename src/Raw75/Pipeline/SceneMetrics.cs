namespace Raw75.Pipeline;

/// <summary>
/// Scene stats from an undeveloped proxy, or from that proxy after the luma tone stack.
/// MetricVersion 2 adds highlight / shadow percentiles for slider inversion.
/// </summary>
public sealed class SceneMetrics
{
    public const int PercentileKnots = 11;
    public const int CurrentVersion = 2;

    public int MetricVersion { get; set; }

    /// <summary>log2(geometric-mean luma / 0.18).</summary>
    public float LogMeanEv { get; set; }

    public float P1Ev { get; set; }
    public float P5Ev { get; set; }
    public float P50Ev { get; set; }
    public float P95Ev { get; set; }
    public float P99Ev { get; set; }

    /// <summary>p95 − p5 of luma, in EV.</summary>
    public float ContrastEv { get; set; }

    /// <summary>Fraction of samples with luma &gt; 0.92 (soft clip).</summary>
    public float ClipHigh { get; set; }

    /// <summary>Fraction of samples with luma &gt; 1.0 (open highlight).</summary>
    public float ClipOpen { get; set; }

    /// <summary>
    /// Exposure-curve X (0–255) at percentiles 0, 10, … 100.
    /// X domain is 255 * luma^(1/2.2).
    /// </summary>
    public float[] GammaX { get; set; } = IdentityGammaX();

    /// <summary>Luma EV at percentiles 0, 10, … 100.</summary>
    public float[] EvAtP { get; set; } = new float[PercentileKnots];

    public static float[] IdentityGammaX()
    {
        var x = new float[PercentileKnots];
        for (int i = 0; i < PercentileKnots; i++)
            x[i] = i * 25.5f;
        return x;
    }

    public bool HasData => GammaX != null && GammaX.Length == PercentileKnots;

    public bool HasToneStats => MetricVersion >= CurrentVersion && HasData;
}
