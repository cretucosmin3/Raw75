using Blossom.Reactive;
using Raw75.Develop;

namespace Raw75.Views;

/// <summary>Inspector tabs in the right dock.</summary>
internal enum InspectorTab
{
    Light,
    Color,
    Tools
}

/// <summary>
/// UI state of the workspace as signals. Chrome binds to these (toggled buttons, visible rows,
/// status pill, zoom readout) so a state change is one write instead of a fan-out of setters.
/// The develop model itself (<see cref="DevelopSettings"/>) stays imperative; the mode signals
/// below mirror the active photo's settings whenever they are pulled into the panels.
/// UI thread only.
/// </summary>
internal sealed class WorkspaceState
{
    public readonly Signal<bool> GalleryMode = new(false);
    public readonly Signal<InspectorTab> Inspector = new(InspectorTab.Light);

    /// <summary>Transient message for the floating status pill; null or empty hides it.</summary>
    public readonly Signal<string?> Status = new(null);
    public readonly Signal<string> ZoomText = new("Fit");

    public readonly Signal<bool> ShowBefore = new(false);
    public readonly Signal<bool> SplitBefore = new(false);
    public readonly Signal<bool> ShowClipping = new(false);
    public readonly Signal<bool> ShowInfo = new(false);
    public readonly Signal<bool> CropTool = new(false);
    public readonly Signal<bool> SpotTool = new(false);

    public readonly Signal<bool> ExposureCurve = new(false);
    public readonly Signal<ToneMode> Tone = new(ToneMode.Sigmoid);
    public readonly Signal<HighlightMode> Reconstruction = new(HighlightMode.Off);
    public readonly Signal<bool> MatchGray = new(false);
    public readonly Signal<int> HslBand = new(0);

    /// <summary>Marks of the active photo, for the photo-view Ready / Favorite buttons.</summary>
    public readonly Signal<bool> HasActive = new(false);
    public readonly Signal<bool> ActiveReady = new(false);
    public readonly Signal<bool> ActiveFavorite = new(false);

    /// <summary>A newer release found on GitHub, or null; shows the Update button.</summary>
    public readonly Signal<Raw75.Update.UpdateInfo?> AvailableUpdate = new(null);

    /// <summary>Number of photos in the current multi-selection.</summary>
    public readonly Signal<int> SelectionCount = new(0);

    public bool CanMergeHdr => SelectionCount.Value is >= 2 and <= 7;

    /// <summary>Copies the per-photo modes from <paramref name="s"/> in one batch.</summary>
    public void PullModes(DevelopSettings s)
    {
        ReactiveEngine.Batch(() =>
        {
            ExposureCurve.Value = s.EnableExposureCurve;
            Tone.Value = s.ToneMode;
            Reconstruction.Value = s.ReconstructionMode;
            MatchGray.Value = s.MatchGray;
        });
    }
}
