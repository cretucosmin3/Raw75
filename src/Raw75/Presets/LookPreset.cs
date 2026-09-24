using Raw75.Develop;
using Raw75.Pipeline;

namespace Raw75.Presets;

/// <summary>
/// Saved look: exact develop recipe plus optional scene metrics for adaptive apply.
/// </summary>
public sealed class LookPreset
{
    public SceneMetrics? Source { get; set; }
    public SceneMetrics? Look { get; set; }
    public DevelopSettings Settings { get; set; } = new();

    public bool CanAdapt =>
        Source != null && Look != null && Source.HasData && Look.HasData;
}
