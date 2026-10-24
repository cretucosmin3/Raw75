using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Numerics;
using Blossom;
using Blossom.Core;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Raw75.Controls;
using Raw75.Develop;
using Raw75.Imaging;
using Raw75.Io;
using Raw75.Pipeline;
using Raw75.Presets;
using Raw75.Update;
using SkiaSharp;
using Blossom.Hosting;
using Blossom.Core.Input;
using Blossom.Desktop;
using Blossom.Primitives;
using Blossom.Reactive;

namespace Raw75.Views;

public sealed partial class WorkspaceView : View
{
    private readonly Session _session = new();
    private readonly WorkspaceState _state = new();
    private DevelopEngine _engine = null!;
    private bool _sync;
    private bool _sliderDrag;
    private bool _restoringView;

    private PhotoPane _photo = null!;
    private VisualElement _status = null!;
    private HistogramView _histogram = null!;
    private Filmstrip _film = null!;
    private PresetList _presets = null!;
    private ExportDialog _export = null!;
    private NameDialog _nameDialog = null!;
    private HdrMergeDialog _hdrMergeDialog = null!;
    private IconButton _btnHdrTop = null!;
    private IconButton _btnHdrBottom = null!;
    private int _selectionPivotIndex = -1;
    private SettingsDialog _settings = null!;
    private UpdateDialog _updateDialog = null!;
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(6);
    private WorkspaceGate _gate = null!;
    private PhotoViewOverlay _viewOverlay = null!;
    private IconButton _btnBefore => _viewOverlay.BtnBefore;
    private IconButton _btnSplit => _viewOverlay.BtnSplit;
    private IconButton _btnClip => _viewOverlay.BtnClip;
    private IconButton _btnInfo => _viewOverlay.BtnInfo;
    private DevelopSettings? _copiedSettings;
    private VisualElement _zoomLabel = null!;
    private Stack _markBar = null!;
    private const float MarkBarH = 32f;
    private GalleryView _gallery = null!;
    private PhotoMarkMenu _markMenu = null!;
    private IconButton _btnViewerMode = null!;
    private IconButton _btnGalleryMode = null!;
    private bool IsGallery => _state.GalleryMode.Peek();
    private VisualElement _leftDockBar = null!;
    private VisualElement _rightDockBar = null!;
    private RightColumn _rightColumn = null!;
    private Stack _inspectorTabs = null!;
    private readonly Dictionary<InspectorTab, float> _inspectorScroll = new();
    private const float HistogramH = 132f;
    private const float InspectorTabsH = 38f;
    private float GetGalleryColWidth(float w) => Math.Clamp(w * 0.40f, 320f, Math.Max(320f, w - 200f));

    private PanelGroup _wbGroup = null!;
    private PanelGroup _exposureGroup = null!;
    private PanelGroup _hdrGroup = null!;
    private PanelGroup _localGroup = null!;
    private PanelGroup _toneGroup = null!;
    private PanelGroup _reconGroup = null!;
    private PanelGroup _hslGroup = null!;
    private PanelGroup _gradingGroup = null!;
    private PanelGroup _detailGroup = null!;
    private PanelGroup _geomGroup = null!;
    private PanelGroup _spotGroup = null!;
    private IconButton _btnSpot = null!;
    private SliderRow _spotSize = null!, _spotFeather = null!;

    private SliderRow _temp = null!, _tint = null!, _ev = null!, _con = null!, _sat = null!;
    private ExposureCurveControl _exposureCurveControl = null!;
    private VisualElement _expCurveRule = null!;
    private SliderRow _hi = null!, _sh = null!, _wh = null!, _bk = null!, _vib = null!;
    private SliderRow _sharp = null!, _noise = null!;

    // HSL dynamic
    private HslBandSelector _hslSelector = null!;
    private SliderRow _hslActiveHue = null!;
    private SliderRow _hslActiveSat = null!;
    private SliderRow _hslActiveLuma = null!;

    // Tone
    private CurveGraphControl _curveGraph = null!;
    private SliderRow _sigContrast = null!;
    private SliderRow _sigSkew = null!;

    // Reconstruction
    private SliderRow _reconThreshold = null!;
    private SliderRow _reconColor = null!;
    private SliderRow _reconSpatial = null!;

    // Local contrast & Atmosphere
    private SliderRow _localDetail = null!;
    private SliderRow _texture = null!;
    private SliderRow _dehaze = null!;
    private SliderRow _dehazeDistance = null!;

    private ColorGradingPanel _grading = null!;

    // Lens
    private SliderRow _vignette = null!;
    private SliderRow _vignetteMidpoint = null!;

    private IconButton _matchGray = null!;
    private IconButton _btnCropGeom = null!;
    private SKImage? _filmThumb;

    private static readonly string[] HslNames = { "Red", "Orange", "Yellow", "Green", "Aqua", "Blue" };

    public WorkspaceView() : base("Workspace")
    {
        BackColor = Theme.Window;
        Events.OnFilesDropped += OnFilesDropped;
        Events.OnKeyDown += OnKey;
    }

    public override void Init()
    {
        _engine = new DevelopEngine(t => Shell.Post(() => SetStatus(t)));
        _engine.Updated += OnDeveloped;
        _engine.LiveUpdated += OnLivePreview;
        _engine.HistogramUpdated += OnHistogram;
        _engine.ViewportUpdated += OnViewportTile;
        _engine.BusyChanged += busy => { if (_photo != null) _photo.Busy = busy; };
        _engine.ThumbProgress += (done, total) =>
        {
            if (done < total)
                SetStatus($"Loading previews: {done} of {total} ({done * 100 / Math.Max(1, total)}%)");
            else
                SetStatus($"Previews ready · {_session.Documents.Count} photos");
        };
        _engine.ThumbLoaded += doc =>
        {
            _gallery.RefreshThumbs();
            _film.RefreshThumbs();
        };
        _session.Changed += RefreshSession;
        _session.SelectionChanged += OnSessionSelectionChanged;
        // Controls re-skin themselves through their Theme bindings; the view only owns its backdrop.
        Theme.Changed += () =>
        {
            BackColor = Theme.Window;
            ForceLayoutEvaluation();
        };

        BuildChrome();
        _presets.SetItems(PresetStore.Names());
        ForceLayoutEvaluation();

        if (WorkspaceStore.LoadSession(out var root, out var lastActive, out var recentPhotos) && recentPhotos.Count > 0)
        {
            if (!string.IsNullOrEmpty(root))
                WorkspaceStore.Bind(root);

            int activeIdx = 0;
            for (int i = 0; i < recentPhotos.Count; i++)
            {
                var doc = _session.Add(recentPhotos[i], select: false);
                WorkspaceStore.Hydrate(doc);
                if (lastActive != null && string.Equals(doc.Path, lastActive, StringComparison.OrdinalIgnoreCase))
                    activeIdx = i;
            }

            _gate.Hide();
            _session.Select(activeIdx);
            SetStatus($"Restored {recentPhotos.Count} photos");
            _engine.FillMissingThumbs(_session.Documents);
        }
        else if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
        {
            LoadWorkspace(root, rememberActive: false);
        }
        else
        {
            SetStatus("Choose a folder to start.");
            _gate.Show();
        }

        StartUpdateChecks();
    }

    private void BuildChrome()
    {
        float W = Width;
        float H = Height;
        float L = Theme.LeftW;
        float R = Theme.RightW;
        float top = Theme.TopBarH;
        float film = Theme.FilmH;

        BuildTopBar();

        // Left dock background + cards
        _leftDockBar = Bar("LeftDock", () => Theme.Window, 0, top, L, H - top - film, Anchor.Left | Anchor.Top | Anchor.Bottom);

        float leftInset = 10f;
        float presetTop = top + leftInset;
        _presets = new PresetList
        {
            Transform = new Transform(leftInset, presetTop, L - leftInset * 2, H - presetTop - leftInset - film)
            {
                Anchor = Anchor.Left | Anchor.Top | Anchor.Bottom
            }
        };
        _presets.Applied += ApplyPreset;
        _presets.SaveClicked += SavePreset;
        _presets.Deleted += DeletePreset;
        _presets.CopyClicked += CopyAdjustments;
        _presets.PasteClicked += PasteAdjustments;
        AddElement(_presets);
        BuildMaskPanel(leftInset, H - film - leftInset - 200f, L - leftInset * 2, 200f);

        // Right dock background + cards
        _rightDockBar = Bar("RightDock", () => Theme.Window, W - R, top, R, H - top - film, Anchor.Right | Anchor.Top | Anchor.Bottom);

        float rightInset = 10f;
        _histogram = new HistogramView
        {
            Transform = new Transform(W - R + rightInset, top + rightInset, R - rightInset * 2, HistogramH)
            {
                Anchor = Anchor.Right | Anchor.Top
            }
        };
        AddElement(_histogram);

        // Tab bar stays put above the scroller; each section shows on one tab.
        float tabsY = top + rightInset + HistogramH + 10f;
        _inspectorTabs = new Stack
        {
            Name = "InspectorTabs",
            Orientation = Orientation.Horizontal,
            Align = LayoutAlign.Stretch,
            Transform = new Transform(W - R + rightInset, tabsY, R - rightInset * 2, InspectorTabsH)
            {
                Anchor = Anchor.Right | Anchor.Top
            }
        };
        _inspectorTabs.AddChild(InspectorTabButton("Light", "light", InspectorTab.Light));
        _inspectorTabs.AddChild(InspectorTabButton("Color", "color", InspectorTab.Color));
        _inspectorTabs.AddChild(InspectorTabButton("Tools", "tools", InspectorTab.Tools));
        AddElement(_inspectorTabs);
        BuildMaskHeader(W - R + rightInset, tabsY, R - rightInset * 2, InspectorTabsH);

        float rightDockTop = tabsY + InspectorTabsH + 4f;
        _rightColumn = new RightColumn
        {
            Transform = new Transform(W - R, rightDockTop, R, H - rightDockTop - film)
            {
                Anchor = Anchor.Right | Anchor.Top | Anchor.Bottom
            }
        };
        AddElement(_rightColumn);
        BuildRightPanels(_rightColumn);

        float photoX = L;
        float photoY = top;
        float photoW = W - L - R;
        float photoH = H - top - film;
        _photo = new PhotoPane
        {
            Transform = new Transform(photoX, photoY, photoW, photoH)
            {
                Anchor = Anchor.Left | Anchor.Right | Anchor.Top | Anchor.Bottom
            }
        };
        _photo.ViewChanged += () =>
        {
            UpdateZoomLabel();
            var d = _session.Active;
            if (d == null) return;
            if (!_restoringView && !IsGallery)
                _photo.CaptureView(d);
        };
        _photo.ViewSettled += () =>
        {
            var d = _session.Active;
            if (d == null || IsGallery) return;
            RequestViewport(d);
        };
        _photo.Rotate90Clicked += Rotate;
        _photo.ClippingChanged += v => _state.ShowClipping.Value = v;
        _photo.InfoOverlayChanged += v => _state.ShowInfo.Value = v;
        _photo.ShowBeforeChanged += v => _state.ShowBefore.Value = v;
        _photo.SplitBeforeChanged += v => _state.SplitBefore.Value = v;
        AddElement(_photo);

        _gallery = new GalleryView
        {
            Transform = new Transform(0, top, W - GetGalleryColWidth(W), H - top)
            {
                Anchor = Anchor.Left | Anchor.Right | Anchor.Top | Anchor.Bottom
            },
        };
        _gallery.PhotoSelected += idx => _session.Select(idx);
        _gallery.SelectionRequested += OnItemSelectionRequested;
        _gallery.PhotoDoubleClicked += idx =>
        {
            _session.Select(idx);
            SetViewMode(false);
        };
        _gallery.ReadyToggled += OnReadyToggled;
        _gallery.FavoriteToggled += OnFavoriteToggled;
        _gallery.FilterChanged += f => _film.SetFilter(f);
        _gallery.ContextMenuRequested += OpenMarkMenu;
        _gallery.BatchReadyChanged += () =>
        {
            for (int i = 0; i < _session.Documents.Count; i++)
            {
                _film.SetReady(i, _session.Documents[i].IsReady);
                WorkspaceStore.SaveReadyState(_session.Documents[i]);
            }
            SyncActiveMarks();
        };
        AddElement(_gallery);

        _photo.CropCommitted += () =>
        {
            _state.CropTool.Value = false;
            var d = _session.Active;
            if (d != null)
            {
                d.Undo.Push(d.Settings);
                CopyCropInto(d);
                WorkspaceStore.SaveSettings(d);
            }
            PushLook(fast: false, settle: true);
        };
        _photo.CropCancelled += () =>
        {
            _state.CropTool.Value = false;
            PushLook(fast: false, settle: true);
        };
        _photo.SpotsChanged += () =>
        {
            if (_sync) return;
            PushLook(fast: true, settle: false);
            SyncSpotSliders();
        };
        _photo.SpotEditBegan += () =>
        {
            _sliderDrag = true;
            _session.Active?.Undo.BeginDrag(_session.Active.Settings);
        };
        _photo.SpotEditEnded += () =>
        {
            _sliderDrag = false;
            var d = _session.Active;
            if (d != null)
            {
                d.Undo.EndDrag(d.Settings);
                WorkspaceStore.SaveSettings(d);
            }
            PushLook(fast: false, settle: true);
        };
        _photo.SpotSelectionChanged += SyncSpotSliders;
        _photo.HistogramSampled += OnGpuHistogram;
        WireMaskPhotoEvents();

        // Floating photo view overlay: top-right corner of photo viewport
        float overlayW = 88f;
        float overlayH = 138f;
        float overlayMargin = 14f;
        float overlayX = W - R - overlayMargin - overlayW;
        float overlayY = top + overlayMargin;

        _viewOverlay = new PhotoViewOverlay
        {
            Transform = new Transform(overlayX, overlayY, overlayW, overlayH)
            {
                Anchor = Anchor.Right | Anchor.Top,
                FixedWidth = true
            }
        };
        _btnInfo.Clicked += ToggleInfoOverlay;
        _btnBefore.Clicked += ToggleBefore;
        _btnSplit.Clicked += ToggleSplit;
        _btnClip.Clicked += ToggleClipping;
        _btnInfo.Bind(b => b.Toggled = _state.ShowInfo.Value);
        _btnBefore.Bind(b => b.Toggled = _state.ShowBefore.Value);
        _btnSplit.Bind(b => b.Toggled = _state.SplitBefore.Value);
        _btnClip.Bind(b => b.Toggled = _state.ShowClipping.Value);
        AddElement(_viewOverlay);

        float zoomW = 56f;
        float zoomH = 22f;
        float zoomMargin = 14f;
        _zoomLabel = new VisualElement
        {
            Name = "ZoomPct",
            Cursor = UiCursor.Hand,
            ZIndex = 110,
            Overflow = OverflowMode.Visible,
            Style = new ElementStyle
            {
                Border = new BorderStyle { Width = 1 },
                Text = new TextStyle
                {
                    Size = 11,
                    Weight = 600,
                    Alignment = TextAlign.Center,
                    Overflow = TextOverflow.Visible
                }
            },
            Transform = new Transform(W - R - zoomMargin - zoomW, H - film - zoomMargin - zoomH, zoomW, zoomH)
            {
                Anchor = Anchor.Right | Anchor.Bottom,
                FixedWidth = true,
                FixedHeight = true
            }
        };
        _zoomLabel.BindText(() => _state.ZoomText.Value);
        _zoomLabel.Bind(z =>
        {
            z.Style.BackColor = Theme.Window.WithAlpha(200);
            z.Style.Border.Color = Theme.Hairline;
            z.Style.Border.Roundness = zoomH * 0.5f;
            z.Style.Text.Color = Theme.TextSecondary;
        });
        _zoomLabel.Events.OnMouseDown += (_, _) => _photo.ToggleZoom();
        AddElement(_zoomLabel);

        // Ready / Favorite for the active photo, bottom-left over the photo (big enough to hit, unlike thumbs).
        _markBar = new Stack
        {
            Name = "MarkBar",
            Orientation = Orientation.Horizontal,
            Gap = 6f,
            ZIndex = 110,
            Transform = new Transform(L + 14f, H - film - 14f - MarkBarH, 200f, MarkBarH)
            {
                Anchor = Anchor.Left | Anchor.Bottom
            }
        };
        var ready = new IconButton("Ready", "check") { IconColor = Theme.Success, MinWidth = 92f };
        ready.Bind(b =>
        {
            b.Toggled = _state.ActiveReady.Value;
            b.ToggledBackColor = Theme.Success.WithAlpha(40);
            b.ToggledBorderColor = Theme.SuccessSoft;
            b.ToggledIconColor = Theme.Success;
        });
        ready.Clicked += () => ToggleReady();
        var heart = new IconButton("Favorite", "heart") { IconColor = Theme.Favorite, MinWidth = 104f };
        heart.Bind(b =>
        {
            bool on = _state.ActiveFavorite.Value;
            b.Toggled = on;
            b.SetIcon(on ? "heart_filled" : "heart");
            b.ToggledBackColor = Theme.Favorite.WithAlpha(34);
            b.ToggledBorderColor = Theme.FavoriteSoft;
            b.ToggledIconColor = Theme.Favorite;
        });
        heart.Clicked += ToggleFavorite;
        _markBar.AddChild(ready);
        _markBar.AddChild(heart);
        _markBar.BindVisible(() => _state.HasActive.Value && !_state.GalleryMode.Value);
        AddElement(_markBar);

        _btnHdrBottom = Chip("Merge HDR…", W - R - 14f - 56f - 12f - 148f, H - film - 14f - 34f, 148f, 34f, primary: true, iconName: "stack");
        _btnHdrBottom.Transform.Anchor = Anchor.Right | Anchor.Bottom;
        _btnHdrBottom.ZIndex = 120;
        _btnHdrBottom.Clicked += RequestHdrMerge;
        _btnHdrBottom.Bind(b =>
        {
            b.Visible = _state.CanMergeHdr;
            if (b.Visible)
                b.Caption = $"Merge HDR ({_state.SelectionCount.Value})…";
        });

        _film = new Filmstrip
        {
            Transform = new Transform(0, H - film, W, film)
            {
                Anchor = Anchor.Left | Anchor.Right | Anchor.Bottom
            }
        };
        _film.Selected += i => _session.Select(i);
        _film.SelectionRequested += OnItemSelectionRequested;
        _film.FilterChanged += f => _gallery.SetFilter(f);
        _film.ContextMenuRequested += OpenMarkMenu;
        AddElement(_film);

        // Status pill: floats over the photo bottom edge while there is a message.
        _status = new VisualElement
        {
            Name = "StatusBadge",
            IsClickthrough = true,
            ZIndex = 100,
            Transform = new Transform(photoX + (photoW - 320f) * 0.5f, H - film - 38f, 320f, 26f)
            {
                Anchor = Anchor.Bottom,
                FixedWidth = true,
                FixedHeight = true
            },
            Style = new ElementStyle
            {
                Border = new BorderStyle { Width = 1f, Roundness = 13f },
                Shadow = new ShadowStyle(0, 3f, 6, 6, new SKColor(0, 0, 0, 120)),
                Text = new TextStyle
                {
                    Size = 11.5f,
                    Weight = 500,
                    Alignment = TextAlign.Center,
                    Overflow = TextOverflow.Ellipsis,
                    MaxLines = 1,
                    Padding = 6
                }
            }
        };
        _status.Bind(e =>
        {
            string? text = _state.Status.Value;
            e.Text = text ?? string.Empty;
            e.Visible = !string.IsNullOrWhiteSpace(text);
            e.Style.BackColor = Theme.Overlay.WithAlpha(235);
            e.Style.Border.Color = Theme.HairlineStrong;
            e.Style.Text.Color = Theme.Text;
            UpdateStatusPosition();
        });
        AddElement(_status);

        _export = new ExportDialog();
        _export.Confirmed += OnExportConfirmed;
        AddElement(_export);

        _nameDialog = new NameDialog();
        _nameDialog.Confirmed += OnPresetNamed;
        AddElement(_nameDialog);

        _hdrMergeDialog = new HdrMergeDialog();
        _hdrMergeDialog.MergeSucceeded += OnHdrMergeSucceeded;
        AddElement(_hdrMergeDialog);

        _updateDialog = new UpdateDialog();
        _updateDialog.RestartRequested += RestartForUpdate;
        AddElement(_updateDialog);

        _settings = new SettingsDialog();
        _settings.CacheCleared += OnCacheCleared;
        AddElement(_settings);

        _gate = new WorkspaceGate();
        _gate.FolderPicked += folder => LoadWorkspace(folder, rememberActive: false);
        AddElement(_gate);

        _markMenu = new PhotoMarkMenu
        {
            Transform = new Transform(0, 0, W, H)
            {
                Anchor = Anchor.Left | Anchor.Right | Anchor.Top | Anchor.Bottom
            }
        };
        _markMenu.ReadyToggled += OnReadyToggled;
        _markMenu.FavoriteToggled += OnFavoriteToggled;
        _markMenu.HdrMergeRequested += RequestHdrMerge;
        AddElement(_markMenu);

        var root = new WorkspaceRoot(this)
        {
            Transform = new Transform(0, 0, W, H)
            {
                Anchor = Anchor.Left | Anchor.Right | Anchor.Top | Anchor.Bottom
            }
        };
        AddElement(root);

        // Develop-only chrome follows the view mode; the gallery grid is its inverse.
        foreach (var e in new VisualElement[] { _leftDockBar, _masks, _histogram, _rightColumn, _film, _viewOverlay, _zoomLabel })
            e.BindVisible(() => !_state.GalleryMode.Value);
        _inspectorTabs.BindVisible(() => !_state.GalleryMode.Value && _state.ActiveMask.Value < 0);
        _gallery.BindVisible(() => _state.GalleryMode.Value);

        SetViewMode(false);
    }

    /// <summary>
    /// Top bar: a three-column <see cref="Grid"/> (file actions | view tabs | settings + export)
    /// whose equal star columns keep the tabs centred at any window width.
    /// </summary>
    private void BuildTopBar()
    {
        var bar = new Grid
        {
            Name = "Top",
            Columns = "*, Auto, *",
            Rows = "*",
            ZIndex = 1,
            Transform = new Transform(0, 0, Width, Theme.TopBarH) { Anchor = Anchor.Left | Anchor.Right | Anchor.Top }
        };
        bar.Bind(b => b.Style.BackColor = Theme.TopBar);

        var files = new Stack
        {
            Orientation = Orientation.Horizontal,
            Align = LayoutAlign.Center,
            Gap = 6f,
            Padding = new Thickness(16, 0, 0, 0)
        };
        var brand = new BrandMark { MinWidth = 100f, MaxWidth = 100f, MinHeight = 26f, MaxHeight = 26f };
        brand.Transform.Width = 100f;
        brand.Transform.Height = 26f;
        files.AddChild(brand);
        files.AddChild(TopButton("Open", "open", OpenFiles, 80f));
        files.AddChild(TopButton("Folder", "folder", OpenWorkspace, 88f));
        _btnHdrTop = TopButton("Merge HDR…", "stack", RequestHdrMerge, 118f);
        _btnHdrTop.Bind(b => b.Caption = _state.CanMergeHdr ? $"Merge HDR ({_state.SelectionCount.Value})…" : "Merge HDR…");
        files.AddChild(_btnHdrTop);

        var tabs = new Stack { Orientation = Orientation.Horizontal, Align = LayoutAlign.Stretch };
        _btnGalleryMode = new IconButton("Gallery", "gallery") { Variant = ButtonVariant.Tab, FontSize = 13.5f, MinWidth = 112f };
        _btnGalleryMode.Clicked += () => SetViewMode(true);
        _btnGalleryMode.Bind(b => b.Toggled = _state.GalleryMode.Value);
        _btnViewerMode = new IconButton("Develop", "viewer") { Variant = ButtonVariant.Tab, FontSize = 13.5f, MinWidth = 120f };
        _btnViewerMode.Clicked += () => SetViewMode(false);
        _btnViewerMode.Bind(b => b.Toggled = !_state.GalleryMode.Value);
        tabs.AddChild(_btnGalleryMode);
        tabs.AddChild(_btnViewerMode);

        var actions = new Stack
        {
            Orientation = Orientation.Horizontal,
            Align = LayoutAlign.Center,
            Gap = 8f,
            Padding = new Thickness(0, 0, 16, 0)
        };
        var push = new VisualElement { Name = "TopSpacer", IsClickthrough = true };
        actions.AddChild(push);
        Stack.SetGrow(push, 1f);
        // Shown only when GitHub has a newer release; lit so it reads as news, not as another tool.
        var update = TopButton("Update", "download", OpenUpdate, 96f);
        update.Toggled = true;
        update.BindVisible(() => _state.AvailableUpdate.Value != null);
        actions.AddChild(update);
        actions.AddChild(TopButton("Settings", "settings", OpenSettings, 100f));
        var export = TopButton("Export", "export", StartExport, 92f);
        export.Primary = true;
        actions.AddChild(export);

        bar.AddChild(files);
        bar.AddChild(tabs);
        bar.AddChild(actions);
        AddElement(bar);

        var rule = Bar("TopRule", () => Theme.HairlineSubtle, 0, Theme.TopBarH - 1f, Width, 1f, Anchor.Left | Anchor.Right | Anchor.Top);
        rule.ZIndex = 2;
        rule.IsClickthrough = true;
    }

    /// <summary>Inspector tab in the top-navigation style: accent underline and bold caption when active.</summary>
    private IconButton InspectorTabButton(string caption, string icon, InspectorTab tab)
    {
        var b = new IconButton(caption, icon) { Variant = ButtonVariant.Tab, FontSize = 12.5f, IconSize = 15f };
        b.Clicked += () => SelectInspectorTab(tab);
        b.Bind(x => x.Toggled = _state.Inspector.Value == tab);
        Stack.SetGrow(b, 1f);
        return b;
    }

    private static IconButton TopButton(string caption, string icon, Action onClick, float width)
    {
        var b = new IconButton(caption, icon) { MinWidth = width };
        b.Clicked += onClick;
        return b;
    }

    private void BuildRightPanels(RightColumn host)
    {
        // 1. White Balance
        _wbGroup = new PanelGroup("White Balance");
        _temp = BindSlider(_wbGroup, "Kelvin", -100, 100, "0", (s, v) => s.Temperature = v, s => s.Temperature);
        _temp.GradientMode = SliderGradientMode.Kelvin;
        _tint = BindSlider(_wbGroup, "Tint", -100, 100, "0", (s, v) => s.Tint = v, s => s.Tint);
        _tint.GradientMode = SliderGradientMode.Tint;
        _wbGroup.EnableAuto(AutoWhiteBalance, "Auto White Balance");
        _wbGroup.EnableReset(ResetWhiteBalance, "Reset White Balance");
        _wbGroup.EnabledChanged += en => OnSectionToggled(s => s.EnableWhiteBalance = en);

        // 2. Exposure
        _exposureGroup = new PanelGroup("Exposure");
        var expMode = new SegmentedControl("Slider", "Curve");
        expMode.SelectionChanged += i => SetExposureCurveMode(i == 1);
        expMode.Bind(c => c.SelectedIndex = _state.ExposureCurve.Value ? 1 : 0);
        _exposureGroup.AddBody(expMode);

        _ev = BindSlider(_exposureGroup, "Exposure", -5f, 5f, "0.00", (s, v) => s.Exposure = v, s => s.Exposure);
        _ev.BindVisible(() => !_state.ExposureCurve.Value);

        _exposureCurveControl = new ExposureCurveControl();
        _exposureCurveControl.CurveChanged += () =>
        {
            if (_sync) return;
            var d = _session.Active;
            if (d == null) return;
            _exposureCurveControl.SaveToSettings(d.Settings);
            PushLook(fast: true, settle: false);
        };
        _exposureCurveControl.DragEnded += () =>
        {
            if (_sync) return;
            var d = _session.Active;
            if (d == null) return;
            d.Undo.Push(d.Settings);
            _exposureCurveControl.SaveToSettings(d.Settings);
            PushLook(fast: false, settle: true);
        };
        _exposureGroup.AddBody(_exposureCurveControl);
        _exposureCurveControl.BindVisible(() => _state.ExposureCurve.Value);

        _expCurveRule = new VisualElement
        {
            Name = "ExposureCurveRule",
            IsClickthrough = true,
            Transform = new Transform(0, 0, 0, 1f)
        };
        _exposureGroup.AddBody(_expCurveRule);
        _expCurveRule.Bind(r =>
        {
            r.Style.BackColor = Theme.HairlineSubtle;
            r.Visible = _state.ExposureCurve.Value;
        });

        _con = BindSlider(_exposureGroup, "Contrast", -100, 100, "0", (s, v) => s.Contrast = v, s => s.Contrast);
        _sat = BindSlider(_exposureGroup, "Saturation", -100, 100, "0", (s, v) => s.Saturation = v, s => s.Saturation);
        BindRange(_con, RangedControl.Contrast);
        _exposureGroup.EnableAuto(AutoExposure, "Auto Exposure");
        _exposureGroup.EnableReset(ResetExposure, "Reset Exposure");
        _exposureGroup.EnabledChanged += en => OnSectionToggled(s => s.EnableExposure = en);

        // 3. High Dynamic Range
        _hdrGroup = new PanelGroup("High Dynamic Range");
        _hi = BindSlider(_hdrGroup, "Highlight", -100, 100, "0", (s, v) => s.Highlights = v, s => s.Highlights);
        _sh = BindSlider(_hdrGroup, "Shadow", -100, 100, "0", (s, v) => s.Shadows = v, s => s.Shadows);
        _wh = BindSlider(_hdrGroup, "Whites", -100, 100, "0", (s, v) => s.Whites = v, s => s.Whites);
        _bk = BindSlider(_hdrGroup, "Blacks", -100, 100, "0", (s, v) => s.Blacks = v, s => s.Blacks);
        BindRange(_hi, RangedControl.Highlights);
        BindRange(_sh, RangedControl.Shadows);
        BindRange(_wh, RangedControl.Whites);
        BindRange(_bk, RangedControl.Blacks);
        _hdrGroup.EnableAuto(AutoHdr, "Auto HDR");
        _hdrGroup.EnableReset(ResetHdr, "Reset High Dynamic Range");
        _hdrGroup.EnabledChanged += en => OnSectionToggled(s => s.EnableHdr = en);

        // 4. Clarity & Atmosphere
        _localGroup = new PanelGroup("Clarity & Atmosphere");
        _localDetail = BindSlider(_localGroup, "Detail", -1.0f, 4.0f, "0.00",
            (s, v) => s.LocalContrastDetail = v, s => s.LocalContrastDetail, 0.0f);
        _texture = BindSlider(_localGroup, "Texture", -100f, 100f, "0",
            (s, v) => s.Texture = v, s => s.Texture, 0f);
        _dehaze = BindSlider(_localGroup, "Dehaze", -100f, 100f, "0",
            (s, v) => s.Dehaze = v, s => s.Dehaze, 0f);
        _dehazeDistance = BindSlider(_localGroup, "Distance", 0f, 100f, "0",
            (s, v) => s.DehazeDistance = v, s => s.DehazeDistance, 20f);
        _localGroup.EnableReset(ResetLocalContrast, "Reset Clarity & Atmosphere");
        _localGroup.EnabledChanged += en => OnSectionToggled(s => s.EnableLocalContrast = en);

        // 5. Tone & Curve
        _toneGroup = new PanelGroup("Tone & Curve");
        _curveGraph = new CurveGraphControl();
        _curveGraph.CurveChanged += () =>
        {
            if (_sync) return;
            var d = _session.Active;
            if (d == null) return;
            _curveGraph.SaveToSettings(d.Settings);
            PushLook(fast: true, settle: false);
        };
        _curveGraph.DragEnded += () =>
        {
            if (_sync) return;
            var d = _session.Active;
            if (d == null) return;
            d.Undo.Push(d.Settings);
            _curveGraph.SaveToSettings(d.Settings);
            PushLook(fast: false, settle: true);
        };
        _toneGroup.AddBody(_curveGraph);

        var toneMode = new SegmentedControl("Sigmoid", "Filmic");
        toneMode.SelectionChanged += i => SetToneMode(i == 0 ? ToneMode.Sigmoid : ToneMode.Filmic);
        toneMode.Bind(c => c.SelectedIndex = _state.Tone.Value == ToneMode.Sigmoid ? 0 : 1);
        _toneGroup.AddBody(toneMode);
        _sigContrast = BindSlider(_toneGroup, "Contrast", 0.1f, 10.0f, "0.00",
            (s, v) => s.SigmoidContrast = v, s => s.SigmoidContrast, 1.5f);
        _sigSkew = BindSlider(_toneGroup, "Skew", -1.0f, 1.0f, "0.00",
            (s, v) => s.SigmoidSkew = v, s => s.SigmoidSkew, 0.0f);
        _sigContrast.BindVisible(() => _state.Tone.Value == ToneMode.Sigmoid);
        _sigSkew.BindVisible(() => _state.Tone.Value == ToneMode.Sigmoid);
        _toneGroup.EnableReset(ResetTone, "Reset Tone");
        _toneGroup.EnabledChanged += en => OnSectionToggled(s => s.EnableTone = en);

        // 6. Highlight Reconstruction
        _reconGroup = new PanelGroup("Highlight Reconstruction");
        HighlightMode[] reconModes = { HighlightMode.Off, HighlightMode.Opposed, HighlightMode.LCh };
        var reconMode = new SegmentedControl("Off", "Opposed", "LCh");
        reconMode.SelectionChanged += i => SetReconMode(reconModes[i]);
        reconMode.Bind(c => c.SelectedIndex = Array.IndexOf(reconModes, _state.Reconstruction.Value));
        _reconGroup.AddBody(reconMode);
        _reconThreshold = BindSlider(_reconGroup, "Threshold", 0.20f, 1.00f, "0.00",
            (s, v) => s.HighlightThreshold = v, s => s.HighlightThreshold, 0.95f);
        _reconColor = BindSlider(_reconGroup, "Color amount", 0f, 100f, "0",
            (s, v) => s.ColorReconstructionAmount = v, s => s.ColorReconstructionAmount, 0f);
        _reconSpatial = BindSlider(_reconGroup, "Spatial", 0f, 100f, "0",
            (s, v) => s.ColorReconstructionSpatial = v, s => s.ColorReconstructionSpatial, 0f);
        _reconColor.BindVisible(() => _state.Reconstruction.Value != HighlightMode.Off);
        _reconSpatial.BindVisible(() => _state.Reconstruction.Value != HighlightMode.Off);
        _reconGroup.EnableReset(ResetReconstruction, "Reset Reconstruction");
        _reconGroup.EnabledChanged += en => OnSectionToggled(s => s.EnableReconstruction = en);

        // 7. Color Editor (HSL mixer)
        _hslGroup = new PanelGroup("Color Editor");
        _hslSelector = new HslBandSelector();
        _hslSelector.BandSelected += OnHslBandSelected;
        _hslSelector.Bind(c => c.SelectedIndex = _state.HslBand.Value);
        _hslGroup.AddBody(_hslSelector);

        _hslActiveHue = BindSlider(_hslGroup, "Hue", -100, 100, "0",
            (s, v) => SetActiveHsl(0, v), s => GetActiveHsl(0));
        _hslActiveSat = BindSlider(_hslGroup, "Saturation", -100, 100, "0",
            (s, v) => SetActiveHsl(1, v), s => GetActiveHsl(1));
        _hslActiveLuma = BindSlider(_hslGroup, "Lightness", -100, 100, "0",
            (s, v) => SetActiveHsl(2, v), s => GetActiveHsl(2));

        _vib = BindSlider(_hslGroup, "Vibrance", -100, 100, "0",
            (s, v) => s.Vibrance = v, s => s.Vibrance);

        _matchGray = new IconButton("Match mid-gray", "scale");
        _matchGray.Clicked += () =>
        {
            var d = _session.Active;
            if (d == null) return;
            d.Undo.Push(d.Settings);
            d.Settings.MatchGray = !d.Settings.MatchGray;
            _state.MatchGray.Value = d.Settings.MatchGray;
            PushLook(fast: false, settle: true);
        };
        _matchGray.Bind(b => b.Toggled = _state.MatchGray.Value);
        _hslGroup.AddBody(_matchGray);
        _hslGroup.EnableReset(ResetHsl, "Reset Color Editor");
        _hslGroup.EnabledChanged += en => OnSectionToggled(s => s.EnableHsl = en);

        // 8. Color Grading
        _gradingGroup = new PanelGroup("Color Grading");
        _grading = new ColorGradingPanel();
        _grading.Changed += () =>
        {
            if (_sync) return;
            var d = _session.Active;
            if (d == null) return;
            _grading.SaveToSettings(d.Settings);
            CopyCropInto(d);
            PushLook(fast: _sliderDrag, settle: !_sliderDrag);
        };
        _grading.DragStarted += () =>
        {
            _sliderDrag = true;
            _session.Active?.Undo.BeginDrag(_session.Active.Settings);
        };
        _grading.DragEnded += () =>
        {
            _sliderDrag = false;
            var d = _session.Active;
            if (d == null) return;
            d.Undo.EndDrag(d.Settings);
            PushLook(fast: false, settle: true);
        };
        _gradingGroup.AddBody(_grading);
        _gradingGroup.EnableReset(ResetColorGrading, "Reset Color Grading");
        _gradingGroup.EnabledChanged += en => OnSectionToggled(s => s.EnableColorGrading = en);

        // 9. Detail & Noise Reduction
        _detailGroup = new PanelGroup("Detail & Noise Reduction");
        _sharp = BindSlider(_detailGroup, "Sharpen", 0, 150, "0",
            (s, v) => s.Sharpen = v, s => s.Sharpen, 0f);
        _noise = BindSlider(_detailGroup, "Noise", -100, 100, "0",
            (s, v) =>
            {
                s.Noise = v;
                s.DenoiseLuma = v < 0 ? -v : 0;
                s.DenoiseChroma = v < 0 ? -v : 0;
            }, s => s.Noise, 0f);
        _detailGroup.EnableReset(ResetDetail, "Reset Detail");
        _detailGroup.EnabledChanged += en => OnSectionToggled(s => s.EnableDetail = en);

        // 9. Rotation, Geometry & Lens
        _geomGroup = new PanelGroup("Rotation, Geometry & Lens");

        var rotL = new IconButton("-90°", "rotate_left");
        rotL.Clicked += () => Rotate(-1);
        var rotR = new IconButton("+90°", "rotate_right");
        rotR.Clicked += () => Rotate(1);
        var flH = new IconButton("Flip H", "flip_h");
        flH.Clicked += () => Flip(true);
        var flV = new IconButton("Flip V", "flip_v");
        flV.Clicked += () => Flip(false);
        _geomGroup.AddBody(ButtonGrid(rotL, rotR, flH, flV));

        _btnCropGeom = new IconButton("Crop Tool (C)", "crop");
        _btnCropGeom.Clicked += ToggleCrop;
        _btnCropGeom.Bind(b => b.Toggled = _state.CropTool.Value);
        _geomGroup.AddBody(_btnCropGeom);

        _vignette = BindSlider(_geomGroup, "Vignette", -100f, 100f, "0",
            (s, v) => s.VignetteAmount = v, s => s.VignetteAmount, 0f);
        _vignetteMidpoint = BindSlider(_geomGroup, "Midpoint", 0f, 100f, "0",
            (s, v) => s.VignetteMidpoint = v, s => s.VignetteMidpoint, 50f);

        _geomGroup.EnableAuto(AutoStraighten, "Auto Straighten");
        _geomGroup.EnableReset(ResetGeometry, "Reset Rotation, Geometry & Lens");
        _geomGroup.EnabledChanged += en => OnSectionToggled(s =>
        {
            s.EnableGeometry = en;
            _photo.StraightenPreview = en ? s.Straighten : 0f;
        });

        _spotGroup = new PanelGroup("Spot Removal");
        _btnSpot = new IconButton("Add Spot", "spot");
        _btnSpot.Clicked += ToggleSpotTool;
        _btnSpot.Bind(b => b.Toggled = _state.SpotTool.Value);
        _spotGroup.AddBody(_btnSpot);
        _spotSize = BindSlider(_spotGroup, "Size", 1f, 100f, "0",
            (s, v) =>
            {
                int i = _photo.SelectedSpotIndex;
                if (s.Spots == null || i < 0 || i >= s.Spots.Count) return;
                s.Spots[i].Radius = SpotPatch.SizeToRadius(v);
                _photo.InvalidatePaint();
            },
            s =>
            {
                int i = _photo.SelectedSpotIndex;
                if (s.Spots == null || i < 0 || i >= s.Spots.Count) return 30f;
                return SpotPatch.RadiusToSize(s.Spots[i].Radius);
            }, 30f);
        _spotFeather = BindSlider(_spotGroup, "Diffusion", 0f, 100f, "0",
            (s, v) =>
            {
                int i = _photo.SelectedSpotIndex;
                if (s.Spots == null || i < 0 || i >= s.Spots.Count) return;
                s.Spots[i].Feather = Math.Clamp(v / 100f, 0f, 1f);
                _photo.InvalidatePaint();
            },
            s =>
            {
                int i = _photo.SelectedSpotIndex;
                if (s.Spots == null || i < 0 || i >= s.Spots.Count) return 40f;
                return s.Spots[i].Feather * 100f;
            }, 40f);
        _spotGroup.EnableReset(ResetSpots, "Reset Spot Removal");
        _spotGroup.EnabledChanged += en => OnSectionToggled(s => s.EnableSpotRemoval = en);

        _state.PullModes(new DevelopSettings());

        AddSection(host, InspectorTab.Light, _exposureGroup);
        AddSection(host, InspectorTab.Light, _hdrGroup);
        AddSection(host, InspectorTab.Light, _localGroup);
        AddSection(host, InspectorTab.Light, _toneGroup);
        AddSection(host, InspectorTab.Light, _reconGroup);
        AddSection(host, InspectorTab.Light, _detailGroup);
        AddSection(host, InspectorTab.Color, _wbGroup);
        AddSection(host, InspectorTab.Color, _hslGroup);
        AddSection(host, InspectorTab.Color, _gradingGroup);
        AddSection(host, InspectorTab.Tools, _geomGroup);
        AddSection(host, InspectorTab.Tools, _spotGroup);
        BuildMaskInspector(host);
    }

    private void AddSection(RightColumn host, InspectorTab tab, PanelGroup group)
    {
        host.AddBody(group);
        group.BindVisible(() => _state.Inspector.Value == tab && _state.ActiveMask.Value < 0);
    }

    /// <summary>Switches the inspector tab, keeping each tab's own scroll position.</summary>
    private void SelectInspectorTab(InspectorTab tab)
    {
        var current = _state.Inspector.Peek();
        if (current == tab) return;
        _inspectorScroll[current] = _rightColumn.ScrollY;
        _state.Inspector.Value = tab;
        // Lay out the newly visible sections first so the restored offset isn't clamped to the old height.
        ForceLayoutEvaluation();
        _rightColumn.ScrollY = _inspectorScroll.TryGetValue(tab, out float y) ? y : 0f;
    }

    private void OnSectionToggled(Action<DevelopSettings> apply)
    {
        if (_sync) return;
        var d = _session.Active;
        if (d == null) return;
        d.Undo.Push(d.Settings);
        apply(d.Settings);
        WorkspaceStore.SaveSettings(d);
        PushLook(fast: false, settle: true);
    }

    private void SetExposureCurveMode(bool enableCurve)
    {
        var d = _session.Active;
        if (d == null) return;
        if (d.Settings.EnableExposureCurve == enableCurve) return;
        d.Undo.Push(d.Settings);
        d.Settings.EnableExposureCurve = enableCurve;
        UpdateExposureModeUi(d.Settings);
        PushLook(fast: false, settle: true);
    }

    private void UpdateExposureModeUi(DevelopSettings s) => _state.ExposureCurve.Value = s.EnableExposureCurve;

    private void SetToneMode(ToneMode mode)
    {
        var d = _session.Active;
        if (d == null) return;
        d.Undo.Push(d.Settings);
        d.Settings.ToneMode = mode;
        UpdateToneModeUi(d.Settings);
        PushLook(fast: false, settle: true);
    }

    private void UpdateToneModeUi(DevelopSettings s) => _state.Tone.Value = s.ToneMode;

    private void SetReconMode(HighlightMode mode)
    {
        var d = _session.Active;
        if (d == null) return;
        d.Undo.Push(d.Settings);
        d.Settings.ReconstructionMode = mode;
        UpdateReconModeUi(d.Settings);
        PushLook(fast: false, settle: true);
    }

    private void UpdateReconModeUi(DevelopSettings s) => _state.Reconstruction.Value = s.ReconstructionMode;

    /// <summary>Equal-width two-column grid of tool buttons.</summary>
    private static Grid ButtonGrid(params IconButton[] buttons)
    {
        const float gap = 6f;
        int rows = (buttons.Length + 1) / 2;
        // Grid sizes tracks but does not measure content; give the host stack its height.
        var grid = new Grid
        {
            Name = "ButtonGrid",
            Columns = "*, *",
            ColumnGap = gap,
            RowGap = gap,
            MinHeight = rows * Theme.ToolH + (rows - 1) * gap
        };
        foreach (var b in buttons)
            grid.AddChild(b);
        return grid;
    }

    /// <summary>Keeps <paramref name="row"/> at ±the reach chosen in Settings › Editor.</summary>
    private static void BindRange(SliderRow row, RangedControl control) =>
        row.Bind(r =>
        {
            int reach = EditorRanges.Of(control).Value;
            r.SetRange(-reach, reach);
        });

    private SliderRow BindSlider(PanelGroup group, string label, float min, float max, string fmt,
        Action<DevelopSettings, float> set, Func<DevelopSettings, float> get, float def = 0f)
    {
        var row = new SliderRow(label, min, max, fmt, inline: false);
        row.DefaultValue = def;
        row.Value = def;
        row.Changed += v =>
        {
            if (_sync) return;
            var d = _session.Active;
            if (d == null) return;
            set(d.Settings, v);
            CopyCropInto(d);
            PushLook(fast: _sliderDrag, settle: !_sliderDrag);
        };
        row.DragStarted += () =>
        {
            _sliderDrag = true;
            _session.Active?.Undo.BeginDrag(_session.Active.Settings);
        };
        row.DragEnded += () =>
        {
            _sliderDrag = false;
            var d = _session.Active;
            if (d == null) return;
            d.Undo.EndDrag(d.Settings);
            PushLook(fast: false, settle: true);
        };
        group.AddBody(row);
        return row;
    }

    private void CopyCropInto(PhotoDocument d)
    {
        d.Settings.CropX = _photo.CropX;
        d.Settings.CropY = _photo.CropY;
        d.Settings.CropW = _photo.CropW;
        d.Settings.CropH = _photo.CropH;
        d.Settings.Straighten = _photo.StraightenPreview;
    }

    private void RefreshSession()
    {
        _film.Bind(_session.Documents, _session.ActiveIndex, _session.SelectedIndices);
        if (IsGallery) _gallery.Bind(_session.Documents, _session.ActiveIndex, _session.SelectedIndices);
        _filmThumb = _session.Active?.Thumb;
        var d = _session.Active;
        if (!ReferenceEquals(_maskDoc, d))
        {
            _maskDoc = d;
            ResetMaskEditing();
        }
        SyncActiveMarks();
        UpdateHdrButton();
        if (d == null)
        {
            _photo.SetDeveloped(null, owns: false);
            SetStatus("Drop a RAW, Open, or pick a Folder.");
            return;
        }

        _photo.SetDroppedPath(d.Path);
        WorkspaceStore.HydrateLook(d);
        if (!d.SourceRgba.HasPixels)
            _engine.Open(d);
        _restoringView = true;
        BindPhoto(d);
        if (IsGallery)
        {
            _photo.SetZoomMode(ZoomMode.Fit);
            _photo.RefreshGeometry();
        }
        else
        {
            _photo.RestoreView(d);
        }
        _restoringView = false;
        if (!IsGallery)
            RequestViewport(d);
        SaveCurrentSession();
        _histogram.SetBins(d.HistogramR, d.HistogramG, d.HistogramB, d.HistogramY);
        _curveGraph.SetHistogramBins(d.HistogramR, d.HistogramG, d.HistogramB, d.HistogramY);
        _exposureCurveControl.SetHistogramBins(d.HistogramR, d.HistogramG, d.HistogramB, d.HistogramY);
        PullSliders(d);
        UpdateZoomLabel();
    }

    private void SaveCurrentSession()
    {
        if (_session.Documents.Count == 0)
            return;
        WorkspaceStore.SaveSession(
            WorkspaceStore.Root,
            _session.Active?.Path,
            _session.Documents.Select(doc => doc.Path));
    }

    private void SetViewMode(bool gallery)
    {
        if (gallery && _photo.CropTool)
            _photo.CancelCrop();
        if (gallery && _photo.SpotTool)
        {
            _photo.EndSpotTool();
            _state.SpotTool.Value = false;
        }
        if (gallery)
            ResetMaskEditing();

        _state.GalleryMode.Value = gallery;

        if (gallery)
        {
            _photo.Style.Border = new BorderStyle
            {
                Width = 1f,
                Color = Theme.Hairline,
                Roundness = Theme.Radius
            };
            _photo.SetZoomMode(ZoomMode.Fit);

            ApplyLayout();

            _gallery.Bind(_session.Documents, _session.ActiveIndex, _session.SelectedIndices);
            _gallery.RefreshThumbs();
            if (_session.Active != null)
            {
                BindPhoto(_session.Active);
                _photo.SetZoomMode(ZoomMode.Fit);
                _photo.RefreshGeometry();
            }
        }
        else
        {
            _photo.Style.Border = new BorderStyle { Width = 0 };

            ApplyLayout();

            RefreshSession();
            _film.RefreshThumbs();
        }

        SyncActiveMarks();
    }

    private void ApplyLayout()
    {
        if (_photo == null || _presets == null || _gallery == null || _rightDockBar == null ||
            _leftDockBar == null || _film == null || _rightColumn == null || _histogram == null || _inspectorTabs == null ||
            _viewOverlay == null || _zoomLabel == null || _masks == null || _maskHeader == null)
            return;

        float W = Width;
        float H = Height;
        if (W <= 1 || H <= 1) return;

        float L = Theme.LeftW;
        float R = Theme.RightW;
        float top = Theme.TopBarH;
        float film = Theme.FilmH;
        float leftInset = 10f;
        float rightInset = 10f;
        float gap = 10f;

        if (IsGallery)
        {
            // --- GALLERY VIEW LAYOUT ---
            // 1. Right column takes 40% of the view width
            float galleryColW = GetGalleryColWidth(W);
            float galleryW = Math.Max(200f, W - galleryColW);

            // Left side: photo grid view (~60% width)
            _gallery.Transform.SetAbsoluteFrame(0, top, galleryW, H - top);
            _gallery.Transform.Anchor = Anchor.Left | Anchor.Right | Anchor.Top | Anchor.Bottom;

            // Right dock background (40% width)
            _rightDockBar.Transform.SetAbsoluteFrame(W - galleryColW, top, galleryColW, H - top);
            _rightDockBar.Transform.Anchor = Anchor.Right | Anchor.Top | Anchor.Bottom;

            // In that right column:
            // Photo preview dominates (~65% height of the column)
            float availH = Math.Max(200f, H - top - rightInset * 2f - gap);
            float previewH = (float)Math.Round(availH * 0.65f);
            float presetsH = Math.Max(120f, availH - previewH);
            float previewW = galleryColW - rightInset * 2f;
            float previewX = W - galleryColW + rightInset;
            float previewY = top + rightInset;

            _photo.Transform.SetAbsoluteFrame(previewX, previewY, previewW, previewH);
            _photo.Transform.Anchor = Anchor.Right | Anchor.Top;
            _photo.RefreshGeometry();

            // Presets under photo preview (~35% height)
            float presetsY = previewY + previewH + gap;
            _presets.Transform.SetAbsoluteFrame(previewX, presetsY, previewW, presetsH);
            _presets.Transform.Anchor = Anchor.Right | Anchor.Top | Anchor.Bottom;
        }
        else
        {
            // --- DEVELOP VIEW LAYOUT ---
            // 1. Left dock bar
            _leftDockBar.Transform.SetAbsoluteFrame(0, top, L, H - top - film);
            _leftDockBar.Transform.Anchor = Anchor.Left | Anchor.Top | Anchor.Bottom;

            // 2. Presets on left dock, masks below them (as tall as their content, up to 70%)
            float presetTop = top + leftInset;
            float leftH = H - presetTop - leftInset - film;
            float leftW = L - leftInset * 2f;
            float maskH = Math.Min(_masks.GetPreferredSize(leftW, 0f).Height, Math.Max(120f, leftH * 0.7f));
            float presetH = Math.Max(120f, leftH - maskH - gap);
            maskH = Math.Max(0f, leftH - presetH - gap);
            _presets.Transform.SetAbsoluteFrame(leftInset, presetTop, leftW, presetH);
            _presets.Transform.Anchor = Anchor.Left | Anchor.Top | Anchor.Bottom;
            _masks.Transform.SetAbsoluteFrame(leftInset, presetTop + presetH + gap, leftW, maskH);
            _masks.Transform.Anchor = Anchor.Left | Anchor.Bottom;

            // 3. Central photo pane
            float photoX = L;
            float photoY = top;
            float photoW = W - L - R;
            float photoH = H - top - film;
            _photo.Transform.SetAbsoluteFrame(photoX, photoY, photoW, photoH);
            _photo.Transform.Anchor = Anchor.Left | Anchor.Right | Anchor.Top | Anchor.Bottom;
            _photo.RefreshGeometry();

            // 4. Right dock bar
            _rightDockBar.Transform.SetAbsoluteFrame(W - R, top, R, H - top - film);
            _rightDockBar.Transform.Anchor = Anchor.Right | Anchor.Top | Anchor.Bottom;

            // 5. Histogram on right dock
            _histogram.Transform.SetAbsoluteFrame(W - R + rightInset, top + rightInset, R - rightInset * 2f, HistogramH);
            _histogram.Transform.Anchor = Anchor.Right | Anchor.Top;

            // 6. Inspector tabs, then the scrolling sections
            float tabsY = top + rightInset + HistogramH + 10f;
            _inspectorTabs.Transform.SetAbsoluteFrame(W - R + rightInset, tabsY, R - rightInset * 2f, InspectorTabsH);
            _inspectorTabs.Transform.Anchor = Anchor.Right | Anchor.Top;
            _maskHeader.Transform.SetAbsoluteFrame(W - R + rightInset, tabsY + 2f, R - rightInset * 2f, InspectorTabsH - 4f);
            _maskHeader.Transform.Anchor = Anchor.Right | Anchor.Top;

            float rightDockTop = tabsY + InspectorTabsH + 4f;
            _rightColumn.Transform.SetAbsoluteFrame(W - R, rightDockTop, R, H - rightDockTop - film);
            _rightColumn.Transform.Anchor = Anchor.Right | Anchor.Top | Anchor.Bottom;

            // 7. Filmstrip at bottom
            _film.Transform.SetAbsoluteFrame(0, H - film, W, film);
            _film.Transform.Anchor = Anchor.Left | Anchor.Right | Anchor.Bottom;

            // 8. Floating overlays
            float overlayW = 88f;
            float overlayH = 138f;
            float overlayMargin = 14f;
            _viewOverlay.Transform.SetAbsoluteFrame(W - R - overlayMargin - overlayW, top + overlayMargin, overlayW, overlayH);
            _viewOverlay.Transform.Anchor = Anchor.Right | Anchor.Top;

            float zoomW = 56f;
            float zoomH = 22f;
            float zoomMargin = 14f;
            _zoomLabel.Transform.SetAbsoluteFrame(W - R - zoomMargin - zoomW, H - film - zoomMargin - zoomH, zoomW, zoomH);
            _markBar.Transform.SetAbsoluteFrame(L + zoomMargin, H - film - zoomMargin - MarkBarH, 200f, MarkBarH);
            _markBar.Transform.Anchor = Anchor.Left | Anchor.Bottom;
            _zoomLabel.Transform.Anchor = Anchor.Right | Anchor.Bottom;
        }

        if (_btnHdrBottom != null)
        {
            float btnW = 148f;
            float btnH = 34f;
            if (IsGallery)
            {
                float galleryColW = GetGalleryColWidth(W);
                _btnHdrBottom.Transform.SetAbsoluteFrame(W - galleryColW - 16f - btnW, H - 16f - btnH, btnW, btnH);
                _btnHdrBottom.Transform.Anchor = Anchor.Right | Anchor.Bottom;
            }
            else
            {
                _btnHdrBottom.Transform.SetAbsoluteFrame(W - R - 14f - 56f - 12f - btnW, H - film - 14f - btnH, btnW, btnH);
                _btnHdrBottom.Transform.Anchor = Anchor.Right | Anchor.Bottom;
            }
        }

        UpdateStatusPosition();
    }

    private sealed class WorkspaceRoot : VisualElement
    {
        private readonly WorkspaceView _view;
        public WorkspaceRoot(WorkspaceView view)
        {
            _view = view;
            Name = "WorkspaceRoot";
            IsClickthrough = true;
            ZIndex = -100;
        }

        protected override void LayoutChildren()
        {
            _view.ApplyLayout();
        }
    }

    private void SyncActiveMarks()
    {
        var d = _session.Active;
        ReactiveEngine.Batch(() =>
        {
            _state.HasActive.Value = d != null;
            _state.ActiveReady.Value = d?.IsReady ?? false;
            _state.ActiveFavorite.Value = d?.IsFavorite ?? false;
        });
    }

    private void ToggleReady(bool? force = null)
    {
        var d = _session.Active;
        if (d == null) return;
        OnReadyToggled(_session.ActiveIndex, force ?? !d.IsReady);
    }

    private void ToggleFavorite()
    {
        var d = _session.Active;
        if (d == null) return;
        OnFavoriteToggled(_session.ActiveIndex, !d.IsFavorite);
    }

    private void OnReadyToggled(int idx, bool ready)
    {
        if (idx < 0 || idx >= _session.Documents.Count) return;
        var doc = _session.Documents[idx];
        doc.IsReady = ready;
        _film.SetReady(idx, ready);
        _gallery.SetReady(idx, ready);
        WorkspaceStore.SaveReadyState(doc);
        SyncActiveMarks();
        SetStatus(ready ? $"Marked ready: {doc.Name}" : $"Unmarked ready: {doc.Name}");
    }

    private void OnFavoriteToggled(int idx, bool favorite)
    {
        if (idx < 0 || idx >= _session.Documents.Count) return;
        var doc = _session.Documents[idx];
        doc.IsFavorite = favorite;
        _film.SetFavorite(idx, favorite);
        _gallery.SetFavorite(idx, favorite);
        WorkspaceStore.SaveReadyState(doc);
        SyncActiveMarks();
        SetStatus(favorite ? $"Added to favorites: {doc.Name}" : $"Removed from favorites: {doc.Name}");
    }

    private void OpenMarkMenu(int idx, float x, float y)
    {
        if (idx < 0 || idx >= _session.Documents.Count) return;
        var doc = _session.Documents[idx];
        bool canMerge = _session.SelectedIndices.Count >= 2 && _session.SelectedIndices.Count <= 7;
        _markMenu.Open(idx, doc.IsReady, doc.IsFavorite, x, y, Width, Height, showHdrMerge: canMerge);
    }

    private void RequestHdrMerge()
    {
        var selected = _session.GetSelectedDocuments();
        if (selected.Count < 2)
        {
            SetStatus("Select at least 2 photos (up to 7) to merge HDR.");
            return;
        }
        if (selected.Count > 7)
        {
            SetStatus("Maximum 7 photos supported for HDR merge.");
            return;
        }
        _hdrMergeDialog.Open(selected);
    }

    private void OnHdrMergeSucceeded(string path)
    {
        var doc = _session.Add(path, select: true);
        WorkspaceStore.SaveSession(WorkspaceStore.Root, path, _session.Documents.Select(d => d.Path));
        SetStatus($"HDR merge complete: {Path.GetFileName(path)}");
    }

    private void OnItemSelectionRequested(int index, bool ctrl, bool shift)
    {
        if (ctrl)
        {
            _session.ToggleSelection(index);
            _selectionPivotIndex = index;
        }
        else if (shift)
        {
            int start = _selectionPivotIndex >= 0 ? _selectionPivotIndex : _session.ActiveIndex;
            if (start < 0) start = index;
            _session.SelectRange(start, index);
        }
        else
        {
            _session.Select(index);
            _selectionPivotIndex = index;
        }
    }

    private void OnSessionSelectionChanged()
    {
        _film.SetSelection(_session.SelectedIndices, _session.ActiveIndex);
        if (IsGallery) _gallery.SetSelection(_session.SelectedIndices, _session.ActiveIndex);
        UpdateHdrButton();
    }

    private void UpdateHdrButton() => _state.SelectionCount.Value = _session.SelectedIndices.Count;

    private void OnLivePreview(PhotoDocument doc)
    {
        if (!ReferenceEquals(doc, _session.Active))
            return;
        _photo.SetDeveloped(doc.Display, owns: false, live: true);
    }

    private void OnDeveloped(PhotoDocument doc)
    {
        _film.Bind(_session.Documents, _session.ActiveIndex);
        _film.RefreshThumbs();
        if (IsGallery) _gallery.RefreshThumbs();
        if (!ReferenceEquals(doc, _session.Active))
            return;

        _photo.Metadata = doc.Metadata;

        if (doc.Proxy != null)
        {
            _restoringView = true;
            BindPhoto(doc);
            if (IsGallery)
            {
                _photo.SetZoomMode(ZoomMode.Fit);
                _photo.RefreshGeometry();
            }
            else
            {
                _photo.RestoreView(doc);
            }
            _restoringView = false;
            _photo.SetBefore(doc.Proxy);
            if (!IsGallery)
                RequestViewport(doc);
        }
        else
        {
            SKImage? best = doc.Display ?? doc.Look ?? doc.Preview ?? doc.Thumb;
            if (best != null)
                _photo.SetDeveloped(best, owns: false);
        }

        _histogram.SetBins(doc.HistogramR, doc.HistogramG, doc.HistogramB, doc.HistogramY);
        _curveGraph.SetHistogramBins(doc.HistogramR, doc.HistogramG, doc.HistogramB, doc.HistogramY);
        _exposureCurveControl.SetHistogramBins(doc.HistogramR, doc.HistogramG, doc.HistogramB, doc.HistogramY);
        _filmThumb = doc.Preview ?? doc.Thumb;
        PullSliders(doc);
        UpdateZoomLabel();
        SyncActiveMarks();
    }

    /// <summary>
    /// GPU-look the undeveloped proxy. If it was unloaded, blit the stored
    /// developed look so rotation is already correct while LibRaw reloads.
    /// </summary>
    private void BindPhoto(PhotoDocument d)
    {
        if (d.Settings.AtmosphereDepthMax <= 0.1f || d.Settings.AtmosphereR <= 0.001f)
        {
            RasterBuffer r = d.SourceRgba.HasPixels ? d.SourceRgba : d.LiveRgba;
            if (r.HasPixels)
            {
                AtmosphereEstimator.Estimate(r, out float ar, out float ag, out float ab, out float dm);
                d.Settings.AtmosphereR = ar;
                d.Settings.AtmosphereG = ag;
                d.Settings.AtmosphereB = ab;
                d.Settings.AtmosphereDepthMax = dm;
            }
        }
        _photo.PrepareBind(d.Settings);
        _photo.SetNativeSize(d.NativeWidth, d.NativeHeight);
        _photo.Metadata = d.Metadata;
        SKImage? working = d.Proxy;
        if (working != null && working.Handle != IntPtr.Zero)
        {
            _photo.SetLook(working, d.Settings, fast: false);
            _photo.SetDeveloped(d.Display ?? working, owns: false);
            _photo.RefreshGeometry();
            if (d.ViewportTile != null)
                _photo.SetViewportTile(d.ViewportTile, d.TileX, d.TileY, d.TileW, d.TileH);
            else
                _photo.ClearViewportTile();
            return;
        }

        SKImage? placeholder = d.Look ?? d.Preview ?? d.Display ?? d.Thumb;
        _photo.SetLook(null, d.Settings, fast: false);
        _photo.ClearViewportTile();
        if (placeholder != null)
            _photo.SetDeveloped(placeholder, owns: false);
        else
            _photo.SetDeveloped(null, owns: false);
        _photo.RefreshGeometry();
    }

    private void PullSliders(PhotoDocument d)
    {
        _sync = true;
        var s = d.Settings;
        _wbGroup.SectionEnabled = s.EnableWhiteBalance;
        _exposureGroup.SectionEnabled = s.EnableExposure;
        _hdrGroup.SectionEnabled = s.EnableHdr;
        _localGroup.SectionEnabled = s.EnableLocalContrast;
        _toneGroup.SectionEnabled = s.EnableTone;
        _reconGroup.SectionEnabled = s.EnableReconstruction;
        _hslGroup.SectionEnabled = s.EnableHsl;
        _gradingGroup.SectionEnabled = s.EnableColorGrading;
        _detailGroup.SectionEnabled = s.EnableDetail;
        _geomGroup.SectionEnabled = s.EnableGeometry;
        _spotGroup.SectionEnabled = s.EnableSpotRemoval;
        SyncSpotSliders();

        _temp.Value = s.Temperature;
        _tint.Value = s.Tint;
        _ev.Value = s.Exposure;
        _state.PullModes(s);
        _exposureCurveControl.LoadFromSettings(s);
        _con.Value = s.Contrast;
        _hi.Value = s.Highlights;
        _sh.Value = s.Shadows;
        _wh.Value = s.Whites;
        _bk.Value = s.Blacks;
        _vib.Value = s.Vibrance;
        _sat.Value = s.Saturation;
        _sigContrast.Value = s.SigmoidContrast;
        _sigSkew.Value = s.SigmoidSkew;
        _curveGraph.LoadFromSettings(s);
        _reconThreshold.Value = s.HighlightThreshold;
        _reconColor.Value = s.ColorReconstructionAmount;
        _reconSpatial.Value = s.ColorReconstructionSpatial;
        _localDetail.Value = s.LocalContrastDetail;
        _texture.Value = s.Texture;
        _dehaze.Value = s.Dehaze;
        _dehazeDistance.Value = s.DehazeDistance;
        _grading.LoadFromSettings(s);
        _sharp.Value = s.Sharpen;
        _noise.Value = s.Noise != 0 ? s.Noise : (s.DenoiseLuma > 0 ? -s.DenoiseLuma : 0);
        _photo.StraightenPreview = s.Straighten;
        _vignette.Value = s.VignetteAmount;
        _vignetteMidpoint.Value = s.VignetteMidpoint;
        SyncActiveHslSliders();
        _sync = false;
        SyncMaskUi();
    }

    private void SyncActiveHslSliders()
    {
        var d = _session.Active;
        var s = d?.Settings ?? new DevelopSettings();
        int idx = Math.Clamp(_state.HslBand.Peek(), 0, 5);
        _sync = true;
        _hslActiveHue.Value = s.Hsl[idx].Hue;
        _hslActiveSat.Value = s.Hsl[idx].Sat;
        _hslActiveLuma.Value = s.Hsl[idx].Luma;
        _sync = false;
    }

    private void OnHslBandSelected(int idx)
    {
        _state.HslBand.Value = idx;
        SyncActiveHslSliders();
    }

    private void SetActiveHsl(int component, float val)
    {
        var d = _session.Active;
        if (d == null) return;
        int idx = Math.Clamp(_state.HslBand.Peek(), 0, 5);
        var b = d.Settings.Hsl[idx];
        if (component == 0) b.Hue = val;
        else if (component == 1) b.Sat = val;
        else if (component == 2) b.Luma = val;
        d.Settings.Hsl[idx] = b;
    }

    private float GetActiveHsl(int component)
    {
        var d = _session.Active;
        if (d == null) return 0f;
        int idx = Math.Clamp(_state.HslBand.Peek(), 0, 5);
        var b = d.Settings.Hsl[idx];
        return component == 0 ? b.Hue : component == 1 ? b.Sat : b.Luma;
    }

    private void ResetWhiteBalance()
    {
        var d = _session.Active;
        if (d == null) return;
        d.Undo.Push(d.Settings);
        d.Settings.Temperature = 0;
        d.Settings.Tint = 0;
        PullSliders(d);
        PushLook(fast: false, settle: true);
        SetStatus("Reset White Balance");
    }

    private void ResetExposure()
    {
        var d = _session.Active;
        if (d == null) return;
        d.Undo.Push(d.Settings);
        d.Settings.Exposure = 0;
        d.Settings.ExposureCurve = CurveMath.DefaultExposureCurve();
        d.Settings.Contrast = 0;
        d.Settings.Saturation = 0;
        PullSliders(d);
        PushLook(fast: false, settle: true);
        SetStatus("Reset Exposure");
    }

    private void ResetHdr()
    {
        var d = _session.Active;
        if (d == null) return;
        d.Undo.Push(d.Settings);
        d.Settings.Highlights = 0;
        d.Settings.Shadows = 0;
        d.Settings.Whites = 0;
        d.Settings.Blacks = 0;
        PullSliders(d);
        PushLook(fast: false, settle: true);
        SetStatus("Reset High Dynamic Range");
    }

    private void ResetLocalContrast()
    {
        var d = _session.Active;
        if (d == null) return;
        d.Undo.Push(d.Settings);
        d.Settings.LocalContrastDetail = 0;
        d.Settings.Texture = 0;
        d.Settings.Dehaze = 0;
        d.Settings.DehazeDistance = 20;
        d.Settings.LocalContrastHighlights = 0;
        d.Settings.LocalContrastShadows = 0;
        d.Settings.LocalContrastMidtones = 50;
        PullSliders(d);
        PushLook(fast: false, settle: true);
        SetStatus("Reset Clarity & Atmosphere");
    }

    private void ResetTone()
    {
        var d = _session.Active;
        if (d == null) return;
        d.Undo.Push(d.Settings);
        d.Settings.ToneMode = ToneMode.Sigmoid;
        d.Settings.SigmoidContrast = 1.5f;
        d.Settings.SigmoidSkew = 0.0f;
        d.Settings.CurveRgb = CurveMath.DefaultCurve();
        d.Settings.CurveRed = CurveMath.DefaultCurve();
        d.Settings.CurveGreen = CurveMath.DefaultCurve();
        d.Settings.CurveBlue = CurveMath.DefaultCurve();
        _curveGraph.LoadFromSettings(d.Settings);
        PullSliders(d);
        PushLook(fast: false, settle: true);
        SetStatus("Reset Tone & Curve");
    }

    private void ResetReconstruction()
    {
        var d = _session.Active;
        if (d == null) return;
        d.Undo.Push(d.Settings);
        d.Settings.ReconstructionMode = HighlightMode.Off;
        d.Settings.HighlightThreshold = 0.95f;
        d.Settings.ColorReconstructionAmount = 0;
        d.Settings.ColorReconstructionSpatial = 0;
        PullSliders(d);
        PushLook(fast: false, settle: true);
        SetStatus("Reset Highlight Reconstruction");
    }

    private void ResetHsl()
    {
        var d = _session.Active;
        if (d == null) return;
        d.Undo.Push(d.Settings);
        for (int i = 0; i < 6; i++)
        {
            d.Settings.Hsl[i].Hue = 0;
            d.Settings.Hsl[i].Sat = 0;
            d.Settings.Hsl[i].Luma = 0;
        }
        d.Settings.Vibrance = 0;
        d.Settings.MatchGray = false;
        PullSliders(d);
        PushLook(fast: false, settle: true);
        SetStatus("Reset Color Editor");
    }

    private void ResetColorGrading()
    {
        var d = _session.Active;
        if (d == null) return;
        d.Undo.Push(d.Settings);
        d.Settings.GradingShadowHue = 0f;
        d.Settings.GradingShadowSat = 0f;
        d.Settings.GradingShadowLum = 0f;
        d.Settings.GradingMidHue = 0f;
        d.Settings.GradingMidSat = 0f;
        d.Settings.GradingMidLum = 0f;
        d.Settings.GradingHighlightHue = 0f;
        d.Settings.GradingHighlightSat = 0f;
        d.Settings.GradingHighlightLum = 0f;
        d.Settings.GradingBlending = 50f;
        d.Settings.GradingBalance = 0f;
        PullSliders(d);
        PushLook(fast: false, settle: true);
        SetStatus("Reset Color Grading");
    }

    private void ResetDetail()
    {
        var d = _session.Active;
        if (d == null) return;
        d.Undo.Push(d.Settings);
        d.Settings.Sharpen = 0;
        d.Settings.Noise = 0;
        d.Settings.DenoiseLuma = 0;
        d.Settings.DenoiseChroma = 0;
        PullSliders(d);
        PushLook(fast: false, settle: true);
        SetStatus("Reset Detail & Noise Reduction");
    }

    private void ResetGeometry()
    {
        var d = _session.Active;
        if (d == null) return;
        d.Undo.Push(d.Settings);
        d.Settings.Straighten = 0;
        d.Settings.Rotate90 = 0;
        d.Settings.FlipH = false;
        d.Settings.FlipV = false;
        d.Settings.VignetteAmount = 0;
        d.Settings.VignetteMidpoint = 50;
        _photo.NotifyOrientation(0, flipH: false, flipV: false);
        PullSliders(d);
        PushLook(fast: false, settle: true);
        SetStatus("Reset Rotation, Geometry & Lens");
    }

    private void ResetSpots()
    {
        var d = _session.Active;
        if (d == null) return;
        d.Undo.Push(d.Settings);
        d.Settings.Spots = new List<SpotPatch>();
        _photo.SelectSpot(-1);
        PullSliders(d);
        PushLook(fast: false, settle: true);
        SetStatus("Reset Spot Removal");
    }

    private void SyncSpotSliders()
    {
        var d = _session.Active;
        if (d == null || _spotSize == null) return;
        bool had = _sync;
        _sync = true;
        int i = _photo.SelectedSpotIndex;
        if (d.Settings.Spots != null && i >= 0 && i < d.Settings.Spots.Count)
        {
            _spotSize.Value = SpotPatch.RadiusToSize(d.Settings.Spots[i].Radius);
            _spotFeather.Value = d.Settings.Spots[i].Feather * 100f;
        }
        _sync = had;
        _state.SpotTool.Value = _photo.SpotTool;
        _photo.InvalidatePaint();
    }

    private void ToggleSpotTool()
    {
        if (IsGallery || _session.Active == null)
            return;
        if (_photo.SpotTool)
        {
            _photo.EndSpotTool();
            _state.SpotTool.Value = false;
            return;
        }
        if (_photo.CropTool)
            _photo.CancelCrop();
        if (MaskSelected)
            SelectMask(-1);
        _state.SpotTool.Value = true;
        SelectInspectorTab(InspectorTab.Tools);
        _photo.BeginSpotTool();
        SetStatus("Click the photo to add a spot. Drag the dashed circle to set the source. Shift+scroll = size, Ctrl+scroll = diffusion.");
    }

    private void AutoWhiteBalance()
    {
        var d = _session.Active;
        if (d == null) return;
        d.Undo.Push(d.Settings);
        d.Settings.Temperature = Math.Clamp(d.Settings.Temperature * 0.5f, -100, 100);
        d.Settings.Tint = Math.Clamp(d.Settings.Tint * 0.5f, -100, 100);
        PullSliders(d);
        PushLook(fast: false, settle: true);
        SetStatus("Auto White Balance applied");
    }

    private void AutoExposure()
    {
        var d = _session.Active;
        if (d == null) return;
        RasterBuffer r = d.LiveRgba.HasPixels ? d.LiveRgba : d.SourceRgba;
        if (!r.HasPixels) return;

        d.Undo.Push(d.Settings);
        float autoEv = AutoExposureEstimator.Estimate(r);
        d.Settings.Exposure = autoEv;
        if (d.Settings.BaseExposure == 0f)
            d.Settings.BaseExposure = autoEv;
        if (d.Settings.EnableExposureCurve)
        {
            d.Settings.EnableExposureCurve = false;
            UpdateExposureModeUi(d.Settings);
        }
        PullSliders(d);
        PushLook(fast: false, settle: true);
        SetStatus($"Auto Exposure applied ({(autoEv >= 0 ? "+" : "")}{autoEv:0.00} EV)");
    }

    private void AutoHdr()
    {
        var d = _session.Active;
        if (d == null) return;
        d.Undo.Push(d.Settings);
        d.Settings.Highlights = -25f;
        d.Settings.Shadows = 20f;
        PullSliders(d);
        PushLook(fast: false, settle: true);
        SetStatus("Auto HDR applied");
    }

    private void AutoStraighten()
    {
        var d = _session.Active;
        if (d == null) return;
        d.Undo.Push(d.Settings);
        d.Settings.Straighten = 0f;
        PullSliders(d);
        PushLook(fast: false, settle: true);
        SetStatus("Auto Straighten leveled");
    }

    private void InvalidateActive()
    {
        PushLook(fast: false, settle: true);
    }

    private void PushLook(bool fast, bool settle)
    {
        var d = _session.Active;
        if (d == null) return;
        CopyCropInto(d);
        if (d.Proxy != null)
            _photo.SetLook(d.Proxy, d.Settings, fast);
        else
            BindPhoto(d);
        // The pane samples the histogram from the GPU look; the CPU estimate only covers a failed look.
        _engine.CpuHistogram = _photo.LookFailed;
        _photo.RequestHistogram();
        _engine.RequestHistogram(d);
        if (_photo.LookFailed)
            _engine.Invalidate(d, preview: !settle);
        else if (settle)
            _engine.Invalidate(d, preview: false);
        if (settle)
            WorkspaceStore.SaveSettings(d);
    }

    private void RequestViewport(PhotoDocument d)
    {
        if (IsGallery || d == null || _photo == null)
            return;
        if (!_photo.TryGetViewportRequest(out SKRect aabb, out int sw, out int sh))
        {
            if (d.ViewportTile != null)
                _engine.ClearViewport(d);
            else
                _photo.ClearViewportTile();
            return;
        }

        _engine.RequestViewport(d, aabb, sw, sh);
    }

    private void OnViewportTile(PhotoDocument doc)
    {
        if (!ReferenceEquals(doc, _session.Active))
            return;
        _photo.SetNativeSize(doc.NativeWidth, doc.NativeHeight);
        if (doc.ViewportTile != null && doc.TileW > 0)
            _photo.SetViewportTile(doc.ViewportTile, doc.TileX, doc.TileY, doc.TileW, doc.TileH);
        else
            _photo.ClearViewportTile();
    }

    private void OnGpuHistogram(float[] r, float[] g, float[] b, float[] y)
    {
        var d = _session.Active;
        if (d == null) return;
        _engine.CpuHistogram = false;
        Array.Copy(r, d.HistogramR, Math.Min(r.Length, d.HistogramR.Length));
        Array.Copy(g, d.HistogramG, Math.Min(g.Length, d.HistogramG.Length));
        Array.Copy(b, d.HistogramB, Math.Min(b.Length, d.HistogramB.Length));
        Array.Copy(y, d.HistogramY, Math.Min(y.Length, d.HistogramY.Length));
        OnHistogram(d);
    }

    private void OnHistogram(PhotoDocument doc)
    {
        if (!ReferenceEquals(doc, _session.Active) || _histogram == null)
            return;
        _histogram.SetBins(doc.HistogramR, doc.HistogramG, doc.HistogramB, doc.HistogramY);
        _curveGraph.SetHistogramBins(doc.HistogramR, doc.HistogramG, doc.HistogramB, doc.HistogramY);
        _exposureCurveControl.SetHistogramBins(doc.HistogramR, doc.HistogramG, doc.HistogramB, doc.HistogramY);
    }

    private void ToggleBefore()
    {
        bool before = !_state.ShowBefore.Peek();
        ReactiveEngine.Batch(() =>
        {
            _state.ShowBefore.Value = before;
            if (before && _state.SplitBefore.Peek())
            {
                _state.SplitBefore.Value = false;
                _photo.SplitBefore = false;
            }
        });
        _photo.ShowBefore = before;
    }

    private void ToggleSplit()
    {
        bool split = !_state.SplitBefore.Peek();
        ReactiveEngine.Batch(() =>
        {
            _state.SplitBefore.Value = split;
            if (split && _state.ShowBefore.Peek())
            {
                _state.ShowBefore.Value = false;
                _photo.ShowBefore = false;
            }
        });
        _photo.SplitBefore = split;
        _photo.SetBefore(_session.Active?.Proxy);
    }

    private void ToggleCrop()
    {
        if (IsGallery || _session.Active == null)
            return;

        if (_photo.CropTool)
        {
            _photo.CancelCrop();
            return;
        }
        if (MaskSelected)
            SelectMask(-1);
        _state.CropTool.Value = true;
        SelectInspectorTab(InspectorTab.Tools);
        _photo.BeginCrop();
        PushLook(fast: false, settle: false);
    }

    private void Rotate(int dir)
    {
        var d = _session.Active;
        if (d == null) return;
        d.Undo.Push(d.Settings);
        d.Settings.Rotate90 = (d.Settings.Rotate90 + dir) & 3;
        _photo.NotifyOrientation(dir);
        WorkspaceStore.SaveSettings(d);
        PushLook(fast: false, settle: true);
    }

    private void Flip(bool h)
    {
        var d = _session.Active;
        if (d == null) return;
        d.Undo.Push(d.Settings);
        if (h) d.Settings.FlipH = !d.Settings.FlipH;
        else d.Settings.FlipV = !d.Settings.FlipV;
        _photo.NotifyOrientation(flipH: h, flipV: !h);
        WorkspaceStore.SaveSettings(d);
        PushLook(fast: false, settle: true);
    }

    private void ApplyPreset(string name)
    {
        var d = _session.Active;
        var p = PresetStore.Load(name);
        if (d == null || p == null) return;
        d.Undo.Push(d.Settings);
        SceneMetrics? destMetrics = d.SourceMetrics ?? MeasureActiveSource(d);
        AdaptiveLook.Apply(p, d.Settings, destMetrics);
        PullSliders(d);
        PushLook(fast: false, settle: true);
        SetStatus(p.CanAdapt && destMetrics != null ? "Applied look " + name : "Applied preset " + name);
    }

    private void SavePreset()
    {
        if (_session.Active == null) return;
        _nameDialog.Open("");
    }

    private void OnPresetNamed(string name)
    {
        var d = _session.Active;
        if (d == null) return;
        try
        {
            RasterBuffer src = MetricRaster(d);
            SceneMetrics? source = d.SourceMetrics;
            if (source == null && src.HasPixels)
                source = SceneAnalyzer.Measure(src);
            SceneMetrics? look = src.HasPixels ? SceneAnalyzer.MeasureLook(src, d.Settings) : null;
            PresetStore.Save(name, d.Settings, source, look);
            _presets.SetItems(PresetStore.Names());
            SetStatus(look != null ? "Saved look " + name : "Saved preset " + name);
        }
        catch (Exception ex)
        {
            SetStatus("Could not save preset: " + ex.Message);
        }
    }

    private void DeletePreset(string name)
    {
        if (!PresetStore.Delete(name))
        {
            SetStatus("That preset cannot be deleted.");
            return;
        }
        _presets.SetItems(PresetStore.Names());
        SetStatus("Deleted " + name);
    }

    private static RasterBuffer MetricRaster(PhotoDocument d)
    {
        if (d.LiveRgba.HasPixels)
            return d.LiveRgba;
        return d.SourceRgba;
    }

    private static SceneMetrics? MeasureActiveSource(PhotoDocument d)
    {
        RasterBuffer src = MetricRaster(d);
        if (!src.HasPixels)
            return d.SourceMetrics;
        var metrics = SceneAnalyzer.Measure(src);
        d.SourceMetrics = metrics;
        return metrics;
    }

    private void OpenFiles()
    {
        string[]? paths = null;
        try { paths = FileDialogs.OpenPhotos(); }
        catch (Exception ex) { Log.Warning(ex.Message); }
        if (paths == null || paths.Length == 0) return;
        OpenPaths(paths);
    }

    private void OpenWorkspace()
    {
        string? folder = null;
        try { folder = FileDialogs.OpenFolder(); }
        catch (Exception ex) { Log.Warning(ex.Message); }
        if (string.IsNullOrWhiteSpace(folder))
            return;
        LoadWorkspace(folder, rememberActive: false);
    }

    private void LoadWorkspace(string folder, bool rememberActive)
    {
        string? root = FileDialogs.NormalizeDir(folder);
        if (root == null)
        {
            SetStatus("That path is not a folder.");
            Log.Warning("LoadWorkspace rejected: " + folder);
            return;
        }

        SetStatus("Scanning " + root + "…");
        Log.Info("LoadWorkspace " + root);
        string? want = rememberActive ? null : WorkspaceStore.LoadLastActive(root);
        WorkspaceStore.Bind(root);
        IReadOnlyList<string> photos;
        try
        {
            photos = WorkspaceStore.EnumeratePhotos(root);
        }
        catch (Exception ex)
        {
            Log.Error("Workspace scan failed: " + ex);
            SetStatus("Could not read folder: " + ex.Message);
            return;
        }

        _session.Clear();
        int active = 0;
        for (int i = 0; i < photos.Count; i++)
        {
            var doc = _session.Add(photos[i], select: false);
            WorkspaceStore.Hydrate(doc);
            if (want != null && string.Equals(doc.Path, want, StringComparison.OrdinalIgnoreCase))
                active = i;
        }

        _gate.Hide();

        if (_session.Documents.Count == 0)
        {
            SetStatus("Workspace · " + root + "  (no photos found)");
            RefreshSession();
            return;
        }

        _session.Select(active);
        SetStatus("Workspace · " + root + "  ·  " + _session.Documents.Count + " photos");
        _engine.FillMissingThumbs(_session.Documents);
        SaveCurrentSession();
    }

    private void OnFilesDropped(string[] paths)
    {
        if (paths == null || paths.Length == 0)
            return;

        if (paths.Length == 1 && Directory.Exists(paths[0]))
        {
            LoadWorkspace(paths[0], rememberActive: true);
            return;
        }

        _gate.Hide();
        OpenPaths(paths);
    }

    private void OpenPaths(string[] paths)
    {
        _gate.Hide();
        PhotoDocument? last = null;
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (Directory.Exists(path))
            {
                var scanned = WorkspaceStore.EnumeratePhotos(path);
                foreach (var p in scanned)
                {
                    var d = _session.Add(p, select: false);
                    WorkspaceStore.Hydrate(d);
                    last = d;
                }
                continue;
            }
            if (!File.Exists(path)) continue;
            var doc = _session.Add(path, select: false);
            WorkspaceStore.Hydrate(doc);
            last = doc;
        }

        if (last == null)
            return;
        int idx = _session.Documents.IndexOf(last);
        if (idx >= 0)
            _session.Select(idx);
        else
            RefreshSession();
        _engine.FillMissingThumbs(_session.Documents);
        SaveCurrentSession();
    }

    /// <summary>Asks GitHub for a newer release a few seconds after start, then every <see cref="UpdateCheckInterval"/>.</summary>
    private void StartUpdateChecks()
    {
        Shell.PostDelayed(TimeSpan.FromSeconds(3), () => _ = CheckForUpdateAsync());
        Shell.PostPeriodic(UpdateCheckInterval, () => _ = CheckForUpdateAsync());
    }

    private async Task CheckForUpdateAsync()
    {
        try
        {
            using var checker = new UpdateChecker();
            UpdateInfo? found = await Task.Run(() => checker.CheckAsync(AppVersion.Current, SelfUpdate.PackageName));
            if (found != null)
                Log.Info($"Update available: {found.Version} (running {AppVersion.Current}).");
            _state.AvailableUpdate.Value = found;
        }
        catch (Exception ex)
        {
            Log.Warning($"Update check failed: {ex.Message}");
        }
    }

    private void OpenUpdate()
    {
        if (_state.AvailableUpdate.Peek() is { } update)
            _updateDialog.Open(update);
    }

    /// <summary>The new version is installed: keep the user's work, start it, and leave.</summary>
    private void RestartForUpdate()
    {
        var d = _session.Active;
        if (d != null)
        {
            CopyCropInto(d);
            WorkspaceStore.SaveSettings(d);
        }
        SaveCurrentSession();
        if (!SelfUpdate.Relaunch())
        {
            _updateDialog.RestartFailed();
            return;
        }
        Log.Info("Restarting into the updated version.");
        Environment.Exit(0);
    }

    private void OpenSettings()
    {
        _settings.Open(_session, _photo);
    }

    private void OnCacheCleared()
    {
        var d = _session.Active;
        if (d != null)
        {
            PullSliders(d);
            _engine.Open(d);
            BindPhoto(d);
            RequestViewport(d);
            _histogram.SetBins(d.HistogramR, d.HistogramG, d.HistogramB, d.HistogramY);
            _curveGraph.SetHistogramBins(d.HistogramR, d.HistogramG, d.HistogramB, d.HistogramY);
            _exposureCurveControl.SetHistogramBins(d.HistogramR, d.HistogramG, d.HistogramB, d.HistogramY);
        }

        _engine.FillMissingThumbs(_session.Documents);
        RefreshSession();
        SetStatus("Cleared cache and stored edits. Photo memory reset.");
    }

    private int _exporting;

    private void StartExport()
    {
        var d = _session.Active;
        if (d == null && _session.Documents.Count == 0) { SetStatus("Nothing to export."); return; }
        int ready = 0;
        for (int i = 0; i < _session.Documents.Count; i++)
            if (_session.Documents[i].IsReady) ready++;
        int total = _session.Documents.Count;
        string name = d != null ? Path.ChangeExtension(d.Name, ".jpg") : "export.jpg";
        int w = d?.NativeWidth ?? 0;
        int h = d?.NativeHeight ?? 0;
        _export.Open(name, w, h, ready, total);
    }

    private void OnExportConfirmed(ExportRequest req)
    {
        if (Interlocked.CompareExchange(ref _exporting, 1, 0) != 0)
        {
            SetStatus("Export already running.");
            return;
        }

        if (req.Scope == ExportScope.Active)
        {
            var d = _session.Active;
            if (d == null)
            {
                Volatile.Write(ref _exporting, 0);
                SetStatus("Nothing to export.");
                return;
            }

            string? dest = FileDialogs.SavePhoto(Path.ChangeExtension(d.Path, Ext(req.Format)));
            if (dest == null)
            {
                Volatile.Write(ref _exporting, 0);
                return;
            }

            var settings = d.Settings.Clone();
            string srcPath = d.Path;
            RasterBuffer hiRes = d.HiResRgba;
            _photo.Busy = true;
            SetStatus("Exporting full resolution…");
            Task.Run(() =>
            {
                try
                {
                    var size = DevelopEngine.WriteExport(
                        srcPath, settings, hiRes, dest, req.Format, req.Quality, req.LongEdge,
                        msg => Shell.Post(() => SetStatus(msg)), d.Metadata);
                    Shell.Post(() => SetStatus(
                        $"Exported {size.Width}×{size.Height}  {Path.GetFileName(dest)}"));
                }
                catch (Exception ex)
                {
                    Shell.Post(() => SetStatus("Export failed: " + ex.Message));
                }
                finally
                {
                    Volatile.Write(ref _exporting, 0);
                    Shell.Post(() =>
                    {
                        if (_photo != null)
                            _photo.Busy = false;
                    });
                }
            });
        }
        else
        {
            // Batch export: Ready or All
            var list = new List<PhotoDocument>();
            for (int i = 0; i < _session.Documents.Count; i++)
            {
                var doc = _session.Documents[i];
                if (req.Scope == ExportScope.Ready && !doc.IsReady)
                    continue;
                list.Add(doc);
            }

            if (list.Count == 0)
            {
                Volatile.Write(ref _exporting, 0);
                SetStatus(req.Scope == ExportScope.Ready
                    ? "No photos are marked Ready. Check the corner box on photos to mark them ready."
                    : "No photos in session to export.");
                return;
            }

            string? outDir = FileDialogs.OpenFolder();
            if (string.IsNullOrWhiteSpace(outDir) || !Directory.Exists(outDir))
            {
                Volatile.Write(ref _exporting, 0);
                return;
            }

            _photo.Busy = true;
            SetStatus($"Batch exporting {list.Count} photos…");
            Task.Run(() =>
            {
                int done = 0;
                int failed = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    var doc = list[i];
                    string destName = Path.GetFileNameWithoutExtension(doc.Name) + Ext(req.Format);
                    string destPath = Path.Combine(outDir, destName);
                    Shell.Post(() => SetStatus($"Exporting {i + 1} of {list.Count}: {doc.Name}…"));

                    try
                    {
                        DevelopEngine.WriteExport(
                            doc.Path, doc.Settings.Clone(), doc.HiResRgba, destPath,
                            req.Format, req.Quality, req.LongEdge, null, doc.Metadata);
                        done++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Log.Warning($"Batch export failed for {doc.Name}: {ex.Message}");
                    }
                }

                Shell.Post(() =>
                {
                    Volatile.Write(ref _exporting, 0);
                    if (_photo != null) _photo.Busy = false;
                    SetStatus(failed > 0
                        ? $"Exported {done} photos ({failed} failed) to {Path.GetFileName(outDir)}"
                        : $"Exported all {done} photos to {Path.GetFileName(outDir)}");
                });
            });
        }
    }

    private static string Ext(string format) => format.ToUpperInvariant() switch
    {
        "PNG" => ".png",
        "WEBP" => ".webp",
        "TIFF" => ".tif",
        _ => ".jpg"
    };

    private void OnKey(KeyEvent e)
    {
        var key = e.Key;
        bool ctrl = Events.IsControlDown;

        // 1. Gated by modal dialogs and active keyboard element (e.g. text inputs):
        // Single-key shortcuts and view hotkeys MUST NEVER trigger while typing or interacting with modals.
        bool isModalOrText = ActiveKeyboardElement is TextInputBase
            || (_nameDialog != null && _nameDialog.Visible)
            || (_export != null && _export.Visible)
            || (_hdrMergeDialog != null && _hdrMergeDialog.Visible)
            || (_updateDialog != null && _updateDialog.Visible)
            || (_settings != null && _settings.Visible)
            || (_gate != null && _gate.Visible)
            || (_markMenu != null && _markMenu.Visible);

        if (isModalOrText)
        {
            // Dialogs own their keys (they take focus when shown); only the context menu needs the view.
            if (_markMenu != null && _markMenu.Visible)
            {
                if (key == UiKey.Escape) { _markMenu.Close(); return; }
            }
            return;
        }

        // 2. Global command shortcuts:
        if (ctrl && key == UiKey.C) { CopyAdjustments(); return; }
        if (ctrl && key == UiKey.V) { PasteAdjustments(); return; }
        if (ctrl && key == UiKey.Z && Events.IsShiftDown) { Redo(); return; }
        if (ctrl && key == UiKey.Z) { Undo(); return; }
        if (ctrl && key == UiKey.O) { OpenFiles(); return; }
        if (ctrl && key == UiKey.E) { StartExport(); return; }
        if (ctrl && key == UiKey.M) { RequestHdrMerge(); return; }

        // 3. Mode-specific navigation:
        if (IsGallery)
        {
            if (key == UiKey.Enter || key == UiKey.E) { SetViewMode(false); return; }
            if (key == UiKey.P) { ToggleReady(true); return; }
            if (key == UiKey.X) { ToggleReady(false); return; }
            if (key == UiKey.Left && _session.ActiveIndex > 0)
            {
                _session.Select(_session.ActiveIndex - 1);
                return;
            }
            if (key == UiKey.Right && _session.ActiveIndex < _session.Documents.Count - 1)
            {
                _session.Select(_session.ActiveIndex + 1);
                return;
            }
            // In gallery mode, NEVER fall through to develop view shortcuts (crop, zoom, rotate, etc.)!
            return;
        }

        // 4. Develop view navigation & overlays (mask keys first: O toggles the mask overlay there):
        if (_session.Active != null && OnMaskKey(key)) return;
        if (key == UiKey.G) { SetViewMode(true); return; }
        if (key == UiKey.P) { ToggleReady(true); return; }
        if (key == UiKey.X) { ToggleReady(false); return; }
        if (key == UiKey.I) { ToggleInfoOverlay(); return; }
        if (key == UiKey.O && !ctrl) { ToggleClipping(); return; }

        // 5. Develop view photo editing tools (only if active photo is present):
        if (_session.Active == null) return;

        if (_photo.CropTool)
        {
            if (key == UiKey.Enter || key == UiKey.KeypadEnter)
            {
                _photo.CommitCrop();
                return;
            }
            if (key == UiKey.Escape)
            {
                _photo.CancelCrop();
                return;
            }
        }
        if (_photo.SpotTool)
        {
            if (key == UiKey.Escape)
            {
                _photo.EndSpotTool();
                _state.SpotTool.Value = false;
                return;
            }
            if (key == UiKey.Delete || key == UiKey.Backspace)
            {
                var d = _session.Active;
                if (d != null)
                {
                    d.Undo.Push(d.Settings);
                    if (_photo.DeleteSelectedSpot())
                    {
                        WorkspaceStore.SaveSettings(d);
                        PushLook(fast: false, settle: true);
                    }
                }
                return;
            }
        }
        if (key == UiKey.C) { ToggleCrop(); return; }
        if (key == UiKey.Q) { ToggleSpotTool(); return; }
        if (key == UiKey.F) _photo.SetZoomMode(ZoomMode.Fit);
        if (key == UiKey.Z || key == UiKey.Space)
            _photo.ToggleZoom();
        if (key == UiKey.BackSlash || key == UiKey.Slash) ToggleBefore();
        if (key == UiKey.LeftBracket) Rotate(-1);
        if (key == UiKey.RightBracket) Rotate(1);
        UpdateZoomLabel();
    }

    private void ToggleClipping()
    {
        _photo.ToggleClipping();
        _state.ShowClipping.Value = _photo.ShowClipping;
        SetStatus(_photo.ShowClipping ? "Clipping warnings enabled (Red = Highlights, Blue = Shadows)" : "Clipping warnings disabled");
    }

    private void ToggleInfoOverlay()
    {
        _photo.ToggleInfoOverlay();
        _state.ShowInfo.Value = _photo.ShowInfoOverlay;
    }

    private void CopyAdjustments()
    {
        var d = _session.Active;
        if (d == null) return;
        _copiedSettings = d.Settings.Clone();
        SetStatus($"Copied adjustments from {d.Name}");
    }

    private void PasteAdjustments()
    {
        if (_copiedSettings == null)
        {
            SetStatus("Clipboard empty. Copy adjustments first (Ctrl+C).");
            return;
        }
        var d = _session.Active;
        if (d == null) return;

        d.Undo.Push(d.Settings);

        // Retain target photo's geometry (crop/straighten/rotation/flip)
        float targetCropX = d.Settings.CropX;
        float targetCropY = d.Settings.CropY;
        float targetCropW = d.Settings.CropW;
        float targetCropH = d.Settings.CropH;
        float targetStraight = d.Settings.Straighten;
        int targetRot = d.Settings.Rotate90;
        bool targetFlipH = d.Settings.FlipH;
        bool targetFlipV = d.Settings.FlipV;
        bool targetSpotsEn = d.Settings.EnableSpotRemoval;
        var targetSpots = d.Settings.Spots;
        bool targetMasksEn = d.Settings.EnableMasks;
        var targetMasks = d.Settings.Masks;

        d.Settings.CopyFrom(_copiedSettings);

        d.Settings.CropX = targetCropX;
        d.Settings.CropY = targetCropY;
        d.Settings.CropW = targetCropW;
        d.Settings.CropH = targetCropH;
        d.Settings.Straighten = targetStraight;
        d.Settings.Rotate90 = targetRot;
        d.Settings.FlipH = targetFlipH;
        d.Settings.FlipV = targetFlipV;
        d.Settings.EnableSpotRemoval = targetSpotsEn;
        d.Settings.Spots = targetSpots;
        d.Settings.EnableMasks = targetMasksEn;
        d.Settings.Masks = targetMasks;

        PullSliders(d);
        WorkspaceStore.SaveSettings(d);
        PushLook(fast: false, settle: true);
        SetStatus($"Pasted adjustments to {d.Name}");
    }

    private void Undo()
    {
        var d = _session.Active;
        if (d == null) return;
        var s = d.Undo.Undo(d.Settings);
        if (s == null) return;
        d.Settings.CopyFrom(s);
        PullSliders(d);
        PushLook(fast: false, settle: true);
    }

    private void Redo()
    {
        var d = _session.Active;
        if (d == null) return;
        var s = d.Undo.Redo(d.Settings);
        if (s == null) return;
        d.Settings.CopyFrom(s);
        PullSliders(d);
        PushLook(fast: false, settle: true);
    }

    private void UpdateZoomLabel()
    {
        if (_photo == null) return;
        _state.ZoomText.Value = _photo.IsFreeZoom ? $"{_photo.Zoom * 100:0}%"
            : _photo.ZoomMode == ZoomMode.Fit ? "Fit"
            : _photo.ZoomMode == ZoomMode.Fill ? "Fill"
            : $"{_photo.Zoom * 100:0}%";
    }

    private CancellationTokenSource? _statusDismissCts;

    private void SetStatus(string? t)
    {
        _statusDismissCts?.Cancel();
        _statusDismissCts?.Dispose();
        _statusDismissCts = null;

        _state.Status.Value = t;
        if (string.IsNullOrWhiteSpace(t))
            return;

        var cts = new CancellationTokenSource();
        _statusDismissCts = cts;
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(3500, token);
                Shell.Post(() =>
                {
                    if (!token.IsCancellationRequested)
                        _state.Status.Value = null;
                });
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        });
    }

    private void UpdateStatusPosition()
    {
        if (_status == null) return;
        float L = Theme.LeftW;
        float R = Theme.RightW;
        float film = Theme.FilmH;
        float photoW = Math.Max(200f, Width - L - R);
        string text = _state.Status.Peek() ?? string.Empty;

        using var measure = new TextPaint { TextSize = 11.5f, Typeface = Theme.GetTypeface(500) };
        float textW = Math.Max(60f, measure.MeasureText(text));
        float pillW = Math.Clamp(textW + 40f, 130f, photoW - 40f);
        float pillH = 26f;
        float pillX = L + (photoW - pillW) * 0.5f;
        float pillY = Height - film - pillH - 12f;

        if (IsGallery)
        {
            float galleryColW = GetGalleryColWidth(Width);
            float galleryW = Math.Max(200f, Width - galleryColW);
            pillW = Math.Clamp(textW + 40f, 130f, galleryW - 40f);
            pillX = (galleryW - pillW) * 0.5f;
            pillY = Height - pillH - 16f;
        }

        _status.Transform.SetAbsoluteFrame(pillX, pillY, pillW, pillH);
    }

    private VisualElement Bar(string name, Func<SKColor> color, float x, float y, float w, float h, Anchor a)
    {
        var e = new VisualElement
        {
            Name = name,
            ZIndex = -1,
            Style = new ElementStyle(),
            Transform = new Transform(x, y, w, h) { Anchor = a }
        };
        e.Bind(el => el.Style.BackColor = color());
        AddElement(e);
        return e;
    }

    private IconButton Chip(string caption, float x, float y, float w, float h, bool primary = false, string? iconName = null)
    {
        var e = new IconButton(caption, iconName, primary);
        e.ZIndex = 1;
        e.Transform = new Transform(x, y, w, h) { Anchor = Anchor.Top };
        AddElement(e);
        return e;
    }

}

/// <summary>
/// Inspector column: a vertical scroller whose content is one <see cref="Stack"/> of section cards.
/// </summary>
internal sealed class RightColumn : ScrollContainer
{
    private const float Inset = 10f;
    private readonly Stack _content;

    public RightColumn()
    {
        Name = "RightColumn";
        OverflowX = OverflowMode.Clip;
        OverflowY = OverflowMode.Scroll;
        ScrollbarVisibilityX = ScrollbarVisibility.Hidden;
        ScrollbarVisibilityY = ScrollbarVisibility.Auto;
        ScrollbarThickness = 6f;
        ScrollbarRadius = 3f;
        ScrollbarPadding = 1f;
        Style = new ElementStyle();

        _content = new Stack
        {
            Name = "RightColumnContent",
            Orientation = Orientation.Vertical,
            Gap = 10f,
            Padding = new Thickness(Inset)
        };
        AddChild(_content);

        this.Bind(_ =>
        {
            Style.BackColor = Theme.Window;
            ScrollbarTrackColor = SKColors.Transparent;
            ScrollbarThumbColor = Theme.HairlineStrong;
            ScrollbarThumbHoverColor = Theme.TextDim;
            ScrollbarThumbDragColor = Theme.TextSecondary;
            InvalidatePaint();
        });
    }

    public void AddBody(VisualElement e) => _content.AddChild(e);

    protected override void LayoutChildren()
    {
        float w = Math.Max(1f, Transform.Width);
        float h = _content.GetPreferredSize(w, 0f).Height;
        _content.Transform.SetAbsoluteFrame(Transform.Computed.X, Transform.Computed.Y, w, h);
        float contentH = Math.Max(h, Transform.Computed.Height);
        if (CustomContentHeight != contentH || CustomContentWidth != w)
            SetContentSize(w, contentH);
        base.LayoutChildren();
    }
}
