using Blossom.Core;
using Blossom.Core.Visual;
using Raw75.Views;
using SkiaSharp;

namespace Raw75.Controls;

/// <summary>Pushes the active <see cref="Theme"/> onto already-built chrome.</summary>
public static class ThemeApplier
{
    public static void Refresh(View view)
    {
        if (view == null) return;
        view.BackColor = Theme.Window;
        foreach (var e in view.Elements.Items)
            RefreshOne(e);
        view.ForceLayoutEvaluation();
    }

    private static void RefreshOne(VisualElement e)
    {
        if (e == null || e.IsDisposed) return;

        switch (e)
        {
            case IconButton b:
                b.RefreshTheme();
                break;
            case PanelGroup g:
                g.RefreshTheme();
                break;
            case HistogramView h:
                h.RefreshTheme();
                break;
            case PresetList p:
                p.RefreshTheme();
                break;
            case Filmstrip f:
                f.RefreshTheme();
                break;
            case GalleryView g:
                g.RefreshTheme();
                break;
            case PhotoViewOverlay o:
                o.RefreshTheme();
                break;
            case HslBandSelector hsl:
                hsl.RefreshTheme();
                break;
            case SettingsDialog s:
                s.RefreshTheme();
                break;
            case PhotoMarkMenu m:
                m.RefreshTheme();
                break;
            case SliderRow sl:
                sl.RefreshTheme();
                break;
            case ExportDialog ex:
                ex.RefreshTheme();
                break;
            case NameDialog nd:
                nd.RefreshTheme();
                break;
            case WorkspaceGate gate:
                gate.RefreshTheme();
                break;
            case PhotoPane pane:
                pane.RefreshTheme();
                break;
            default:
                ApplyNamed(e);
                break;
        }

        e.InvalidateLayout();
        e.InvalidatePaint();
    }

    private static void ApplyNamed(VisualElement e)
    {
        switch (e.Name)
        {
            case "Top":
                e.Style.BackColor = Theme.TopBar;
                break;
            case "LeftDock":
            case "RightDock":
            case "RightColumn":
                e.Style.BackColor = Theme.Window;
                break;
            case "NavSplit":
                e.Style.BackColor = Theme.Hairline;
                break;
            case "ExposureCurveRule":
                e.Style.BackColor = Theme.HairlineStrong;
                break;
            case "ZoomPct":
                e.Style.BackColor = new SKColor(Theme.Window.Red, Theme.Window.Green, Theme.Window.Blue, 191);
                if (e.Style.Border != null)
                {
                    e.Style.Border.Color = Theme.Hairline;
                    e.Style.Border.Roundness = Theme.RadiusSm;
                }
                if (e.Style.Text != null)
                    e.Style.Text.Color = Theme.Text;
                break;
            case "StatusBadge":
                e.Style.BackColor = new SKColor(Theme.Window.Red, Theme.Window.Green, Theme.Window.Blue, 220);
                if (e.Style.Border != null)
                {
                    e.Style.Border.Color = Theme.Hairline;
                    e.Style.Border.Roundness = 13f;
                }
                if (e.Style.Text != null)
                    e.Style.Text.Color = Theme.Text;
                break;
        }
    }
}
