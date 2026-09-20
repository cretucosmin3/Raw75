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
using Silk.NET.Input;
using SkiaSharp;

namespace Raw75.Views;

public sealed class WorkspaceView : View
{
    private readonly Session _session = new();
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
    private SettingsDialog _settings = null!;
    private WorkspaceGate _gate = null!;
    private PhotoViewOverlay _viewOverlay = null!;
    private IconButton _btnBefore => _viewOverlay.BtnBefore;
    private IconButton _btnSplit => _viewOverlay.BtnSplit;
    private IconButton _btnClip => _viewOverlay.BtnClip;
    private IconButton _btnInfo => _viewOverlay.BtnInfo;
    private DevelopSettings? _copiedSettings;
    private VisualElement _zoomLabel = null!;
    private GalleryView _gallery = null!;
    private PhotoMarkMenu _markMenu = null!;
    private IconButton _btnViewerMode = null!;
    private IconButton _btnGalleryMode = null!;
    private bool _isGalleryMode;
    private VisualElement _leftDockBar = null!;
    private VisualElement _rightDockBar = null!;
    private RightColumn _rightColumn = null!;
    private float GetGalleryColWidth(float w) => Math.Clamp(w * 0.40f, 320f, Math.Max(320f, w - 200f));

    private PanelGroup _wbGroup = null!;
    private PanelGroup _exposureGroup = null!;
    private PanelGroup _hdrGroup = null!;
    private PanelGroup _localGroup = null!;
    private PanelGroup _toneGroup = null!;
    private PanelGroup _reconGroup = null!;
    private PanelGroup _hslGroup = null!;
    private PanelGroup _detailGroup = null!;
    private PanelGroup _geomGroup = null!;

    private SliderRow _temp = null!, _tint = null!, _ev = null!, _con = null!, _sat = null!;
    private IconButton _btnExpSlider = null!;
    private IconButton _btnExpCurve = null!;
    private ExposureCurveControl _exposureCurveControl = null!;
    private VisualElement _expCurveRule = null!;
    private SliderRow _hi = null!, _sh = null!, _wh = null!, _bk = null!, _vib = null!;
    private SliderRow _sharp = null!, _noise = null!;

    // HSL dynamic
    private HslBandSelector _hslSelector = null!;
    private SliderRow _hslActiveHue = null!;
    private SliderRow _hslActiveSat = null!;
    private SliderRow _hslActiveLuma = null!;
    private int _activeHslBand = 0;

    // Tone
    private CurveGraphControl _curveGraph = null!;
    private IconButton _btnToneSigmoid = null!;
    private IconButton _btnToneFilmic = null!;
    private SliderRow _sigContrast = null!;
    private SliderRow _sigSkew = null!;

    // Reconstruction
    private IconButton _btnReconOff = null!;
    private IconButton _btnReconOpposed = null!;
    private IconButton _btnReconLCh = null!;
    private SliderRow _reconThreshold = null!;
    private SliderRow _reconColor = null!;
    private SliderRow _reconSpatial = null!;

    // Local contrast & Atmosphere
    private SliderRow _localDetail = null!;
    private SliderRow _texture = null!;
    private SliderRow _dehaze = null!;
    private SliderRow _dehazeDistance = null!;

    // Color grading (split toning)
    private SliderRow _gradeShadowHue = null!;
    private SliderRow _gradeShadowSat = null!;
    private SliderRow _gradeHighlightHue = null!;
    private SliderRow _gradeHighlightSat = null!;
    private SliderRow _gradeBalance = null!;

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
        _engine = new DevelopEngine(t => Browser.Post(() => SetStatus(t)));
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
        Theme.Changed += () => ThemeApplier.Refresh(this);

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
    }

    private void BuildChrome()
    {
        float W = Width;
        float H = Height;
        float L = Theme.LeftW;
        float R = Theme.RightW;
        float top = Theme.TopBarH;
        float film = Theme.FilmH;

        AddElement(Bar("Top", Theme.TopBar, 0, 0, W, top, Anchor.Left | Anchor.Right | Anchor.Top));
        var brand = new BrandMark
        {
            Transform = new Transform(16, (top - 26) * 0.5f, 100, 26)
            {
                Anchor = Anchor.Left | Anchor.Top
            }
        };
        AddElement(brand);
        var open = Chip("Open", 124, (top - 32) * 0.5f, 84, 32, iconName: "open");
        open.Transform.Anchor = Anchor.Left | Anchor.Top;
        open.Clicked += OpenFiles;
        var folder = Chip("Folder", 214, (top - 32) * 0.5f, 92, 32, iconName: "folder");
        folder.Transform.Anchor = Anchor.Left | Anchor.Top;
        folder.Clicked += OpenWorkspace;

        float navY = (top - 32) * 0.5f;
        AddElement(new VisualElement
        {
            Name = "NavSplit",
            IsClickthrough = true,
            Style = new ElementStyle { BackColor = Theme.Hairline },
            Transform = new Transform(318, navY + 6, 1, 20)
            {
                Anchor = Anchor.Left | Anchor.Top
            }
        });

        _btnGalleryMode = Chip("Gallery", 331, navY, 96, 32, iconName: "gallery");
        _btnGalleryMode.ShowUnderscore = true;
        _btnGalleryMode.Transform.Anchor = Anchor.Left | Anchor.Top;
        _btnGalleryMode.Clicked += () => SetViewMode(true);

        _btnViewerMode = Chip("Develop", 433, navY, 110, 32, iconName: "viewer");
        _btnViewerMode.ShowUnderscore = true;
        _btnViewerMode.Transform.Anchor = Anchor.Left | Anchor.Top;
        _btnViewerMode.Toggled = true;
        _btnViewerMode.Clicked += () => SetViewMode(false);

        var settings = Chip("Settings", W - 222, (top - 32) * 0.5f, 104, 32, iconName: "settings");
        settings.Transform.Anchor = Anchor.Right | Anchor.Top;
        settings.Clicked += OpenSettings;
        var exp = Chip("Export", W - 112, (top - 32) * 0.5f, 96, 32, primary: true, iconName: "export");
        exp.Transform.Anchor = Anchor.Right | Anchor.Top;
        exp.Clicked += StartExport;

        // Left dock background + cards
        _leftDockBar = Bar("LeftDock", Theme.Window, 0, top, L, H - top - film, Anchor.Left | Anchor.Top | Anchor.Bottom);

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

        // Right dock background + cards
        _rightDockBar = Bar("RightDock", Theme.Window, W - R, top, R, H - top - film, Anchor.Right | Anchor.Top | Anchor.Bottom);

        float rightInset = 10f;
        _histogram = new HistogramView
        {
            Transform = new Transform(W - R + rightInset, top + rightInset, R - rightInset * 2, 96)
            {
                Anchor = Anchor.Right | Anchor.Top
            }
        };
        AddElement(_histogram);

        float rightDockTop = top + rightInset + 96 + 14f;
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
            if (!_restoringView && !_isGalleryMode)
                _photo.CaptureView(d);
        };
        _photo.ViewSettled += () =>
        {
            var d = _session.Active;
            if (d == null || _isGalleryMode) return;
            RequestViewport(d);
        };
        _photo.Rotate90Clicked += Rotate;
        _photo.ClippingChanged += v => { if (_btnClip != null) _btnClip.Toggled = v; };
        _photo.InfoOverlayChanged += v => { if (_btnInfo != null) _btnInfo.Toggled = v; };
        _photo.ShowBeforeChanged += v => { if (_btnBefore != null) _btnBefore.Toggled = v; };
        _photo.SplitBeforeChanged += v => { if (_btnSplit != null) _btnSplit.Toggled = v; };
        AddElement(_photo);

        _gallery = new GalleryView
        {
            Transform = new Transform(0, top, W - GetGalleryColWidth(W), H - top)
            {
                Anchor = Anchor.Left | Anchor.Right | Anchor.Top | Anchor.Bottom
            },
            Visible = false
        };
        _gallery.PhotoSelected += idx => _session.Select(idx);
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
            UpdateTitleBadge();
        };
        AddElement(_gallery);

        _photo.CropCommitted += () =>
        {
            if (_btnCropGeom != null) _btnCropGeom.Toggled = false;
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
            if (_btnCropGeom != null) _btnCropGeom.Toggled = false;
            PushLook(fast: false, settle: true);
        };

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
        _btnSplit.Clicked += () =>
        {
            _btnSplit.Toggled = !_btnSplit.Toggled;
            if (_btnSplit.Toggled && _btnBefore.Toggled)
            {
                _btnBefore.Toggled = false;
                _photo.ShowBefore = false;
            }
            _photo.SplitBefore = _btnSplit.Toggled;
            var d = _session.Active;
            _photo.SetBefore(d?.Proxy);
        };
        _btnClip.Clicked += ToggleClipping;
        AddElement(_viewOverlay);

        float zoomW = 56f;
        float zoomH = 22f;
        float zoomMargin = 14f;
        _zoomLabel = new VisualElement
        {
            Name = "ZoomPct",
            Text = "Fit",
            Cursor = StandardCursor.Hand,
            ZIndex = 110,
            Overflow = OverflowMode.Visible,
            Style = new ElementStyle
            {
                BackColor = new SKColor(20, 20, 24, 191),
                Border = new BorderStyle
                {
                    Width = 1,
                    Color = Theme.Hairline,
                    Roundness = Theme.RadiusSm
                },
                Text = new TextStyle
                {
                    Color = Theme.Text,
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
        _zoomLabel.Events.OnMouseDown += (_, _) => _photo.ToggleZoom();
        AddElement(_zoomLabel);

        _film = new Filmstrip
        {
            Transform = new Transform(0, H - film, W, film)
            {
                Anchor = Anchor.Left | Anchor.Right | Anchor.Bottom
            }
        };
        _film.Selected += i => _session.Select(i);
        _film.ReadyToggled += OnReadyToggled;
        _film.FavoriteToggled += OnFavoriteToggled;
        _film.FilterChanged += f => _gallery.SetFilter(f);
        _film.ContextMenuRequested += OpenMarkMenu;
        AddElement(_film);

        // Status badge: floating pill overlaying the image at the bottom, visible only when showing a message
        _status = new VisualElement
        {
            Name = "StatusBadge",
            Visible = false,
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
                BackColor = new SKColor(20, 20, 24, 220),
                Border = new BorderStyle
                {
                    Width = 1f,
                    Color = new SKColor(255, 255, 255, 30),
                    Roundness = 13f
                },
                Shadow = new ShadowStyle(0, 2f, 4, 4, new SKColor(0, 0, 0, 140)),
                Text = new TextStyle
                {
                    Color = Theme.Text,
                    Size = 11.5f,
                    Weight = 400,
                    Alignment = TextAlign.Center,
                    Overflow = TextOverflow.Ellipsis,
                    MaxLines = 1,
                    Padding = 6
                }
            }
        };
        AddElement(_status);

        _export = new ExportDialog();
        _export.Confirmed += OnExportConfirmed;
        AddElement(_export);

        _nameDialog = new NameDialog();
        _nameDialog.Confirmed += OnPresetNamed;
        AddElement(_nameDialog);

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
        AddElement(_markMenu);

        var root = new WorkspaceRoot(this)
        {
            Transform = new Transform(0, 0, W, H)
            {
                Anchor = Anchor.Left | Anchor.Right | Anchor.Top | Anchor.Bottom
            }
        };
        AddElement(root);

        SetViewMode(false);
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
        _btnExpSlider = new IconButton("Slider");
        _btnExpCurve = new IconButton("Curve");
        _btnExpSlider.Clicked += () => SetExposureCurveMode(false);
        _btnExpCurve.Clicked += () => SetExposureCurveMode(true);
        _exposureGroup.AddBody(new ModeChipRow([_btnExpSlider, _btnExpCurve]));

        _ev = BindSlider(_exposureGroup, "Exposure", -5f, 5f, "0.00", (s, v) => s.Exposure = v, s => s.Exposure);

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

        _expCurveRule = new VisualElement
        {
            Name = "ExposureCurveRule",
            IsClickthrough = true,
            Style = new ElementStyle { BackColor = Theme.HairlineStrong },
            Transform = new Transform(0, 0, 0, 1f)
        };
        _exposureGroup.AddBody(_expCurveRule);
        _expCurveRule.Visible = false;

        _con = BindSlider(_exposureGroup, "Contrast", -100, 100, "0", (s, v) => s.Contrast = v, s => s.Contrast);
        _sat = BindSlider(_exposureGroup, "Saturation", -100, 100, "0", (s, v) => s.Saturation = v, s => s.Saturation);
        _exposureGroup.ExpandedChanged += exp =>
        {
            if (exp && _session.Active != null)
                UpdateExposureModeUi(_session.Active.Settings);
        };
        _exposureGroup.EnableAuto(AutoExposure, "Auto Exposure");
        _exposureGroup.EnableReset(ResetExposure, "Reset Exposure");
        _exposureGroup.EnabledChanged += en => OnSectionToggled(s => s.EnableExposure = en);

        // 3. High Dynamic Range
        _hdrGroup = new PanelGroup("High Dynamic Range");
        _hi = BindSlider(_hdrGroup, "Highlight", -100, 100, "0", (s, v) => s.Highlights = v, s => s.Highlights);
        _sh = BindSlider(_hdrGroup, "Shadow", -100, 100, "0", (s, v) => s.Shadows = v, s => s.Shadows);
        _wh = BindSlider(_hdrGroup, "Whites", -100, 100, "0", (s, v) => s.Whites = v, s => s.Whites);
        _bk = BindSlider(_hdrGroup, "Blacks", -100, 100, "0", (s, v) => s.Blacks = v, s => s.Blacks);
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

        _btnToneSigmoid = new IconButton("Sigmoid");
        _btnToneFilmic = new IconButton("Filmic");
        _btnToneSigmoid.Clicked += () => SetToneMode(ToneMode.Sigmoid);
        _btnToneFilmic.Clicked += () => SetToneMode(ToneMode.Filmic);
        _toneGroup.AddBody(new ModeChipRow([_btnToneSigmoid, _btnToneFilmic]));
        _sigContrast = BindSlider(_toneGroup, "Contrast", 0.1f, 10.0f, "0.00",
            (s, v) => s.SigmoidContrast = v, s => s.SigmoidContrast, 1.5f);
        _sigSkew = BindSlider(_toneGroup, "Skew", -1.0f, 1.0f, "0.00",
            (s, v) => s.SigmoidSkew = v, s => s.SigmoidSkew, 0.0f);
        _toneGroup.ExpandedChanged += exp =>
        {
            if (exp && _session.Active != null)
                UpdateToneModeUi(_session.Active.Settings);
        };
        _toneGroup.EnableReset(ResetTone, "Reset Tone");
        _toneGroup.EnabledChanged += en => OnSectionToggled(s => s.EnableTone = en);

        // 6. Highlight Reconstruction
        _reconGroup = new PanelGroup("Highlight Reconstruction");
        _btnReconOff = new IconButton("Off");
        _btnReconOpposed = new IconButton("Opposed");
        _btnReconLCh = new IconButton("LCh");
        _btnReconOff.Clicked += () => SetReconMode(HighlightMode.Off);
        _btnReconOpposed.Clicked += () => SetReconMode(HighlightMode.Opposed);
        _btnReconLCh.Clicked += () => SetReconMode(HighlightMode.LCh);
        _reconGroup.AddBody(new ModeChipRow([_btnReconOff, _btnReconOpposed, _btnReconLCh]));
        _reconThreshold = BindSlider(_reconGroup, "Threshold", 0.20f, 1.00f, "0.00",
            (s, v) => s.HighlightThreshold = v, s => s.HighlightThreshold, 0.95f);
        _reconColor = BindSlider(_reconGroup, "Color amount", 0f, 100f, "0",
            (s, v) => s.ColorReconstructionAmount = v, s => s.ColorReconstructionAmount, 0f);
        _reconSpatial = BindSlider(_reconGroup, "Spatial", 0f, 100f, "0",
            (s, v) => s.ColorReconstructionSpatial = v, s => s.ColorReconstructionSpatial, 0f);
        _reconGroup.ExpandedChanged += exp =>
        {
            if (exp && _session.Active != null)
                UpdateReconModeUi(_session.Active.Settings);
        };
        _reconGroup.EnableReset(ResetReconstruction, "Reset Reconstruction");
        _reconGroup.EnabledChanged += en => OnSectionToggled(s => s.EnableReconstruction = en);

        // 7. Color Editor & Grading
        _hslGroup = new PanelGroup("Color Editor & Grading");
        _hslSelector = new HslBandSelector();
        _hslSelector.BandSelected += OnHslBandSelected;
        _hslGroup.AddBody(_hslSelector);

        _hslActiveHue = BindSlider(_hslGroup, "Hue", -100, 100, "0",
            (s, v) => SetActiveHsl(0, v), s => GetActiveHsl(0));
        _hslActiveSat = BindSlider(_hslGroup, "Saturation", -100, 100, "0",
            (s, v) => SetActiveHsl(1, v), s => GetActiveHsl(1));
        _hslActiveLuma = BindSlider(_hslGroup, "Lightness", -100, 100, "0",
            (s, v) => SetActiveHsl(2, v), s => GetActiveHsl(2));

        _vib = BindSlider(_hslGroup, "Vibrance", -100, 100, "0",
            (s, v) => s.Vibrance = v, s => s.Vibrance);

        _gradeShadowHue = BindSlider(_hslGroup, "Shadow Hue", 0f, 360f, "0°",
            (s, v) => s.GradingShadowHue = v, s => s.GradingShadowHue, 220f);
        _gradeShadowSat = BindSlider(_hslGroup, "Shadow Sat", 0f, 100f, "0",
            (s, v) => s.GradingShadowSat = v, s => s.GradingShadowSat, 0f);
        _gradeHighlightHue = BindSlider(_hslGroup, "Highlight Hue", 0f, 360f, "0°",
            (s, v) => s.GradingHighlightHue = v, s => s.GradingHighlightHue, 40f);
        _gradeHighlightSat = BindSlider(_hslGroup, "Highlight Sat", 0f, 100f, "0",
            (s, v) => s.GradingHighlightSat = v, s => s.GradingHighlightSat, 0f);
        _gradeBalance = BindSlider(_hslGroup, "Balance", -100f, 100f, "0",
            (s, v) => s.GradingBalance = v, s => s.GradingBalance, 0f);

        _matchGray = new IconButton("Match mid-gray", "scale");
        _matchGray.Clicked += () =>
        {
            var d = _session.Active;
            if (d == null) return;
            d.Undo.Push(d.Settings);
            d.Settings.MatchGray = !d.Settings.MatchGray;
            _matchGray.Toggled = d.Settings.MatchGray;
            PushLook(fast: false, settle: true);
        };
        _hslGroup.AddBody(_matchGray);
        _hslGroup.EnableReset(ResetHsl, "Reset Color Editor & Grading");
        _hslGroup.EnabledChanged += en => OnSectionToggled(s => s.EnableHsl = en);

        // 8. Detail & Noise Reduction
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
        _geomGroup.AddBody(new ModeChipRow([rotL, rotR]));

        var flH = new IconButton("Flip H", "flip_h");
        flH.Clicked += () => Flip(true);
        var flV = new IconButton("Flip V", "flip_v");
        flV.Clicked += () => Flip(false);
        _geomGroup.AddBody(new ModeChipRow([flH, flV]));

        _btnCropGeom = new IconButton("Crop Tool (C)", "crop");
        _btnCropGeom.Clicked += ToggleCrop;
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

        UpdateReconModeUi(new DevelopSettings());

        host.AddBody(_wbGroup);
        host.AddBody(_exposureGroup);
        host.AddBody(_hdrGroup);
        host.AddBody(_localGroup);
        host.AddBody(_toneGroup);
        host.AddBody(_reconGroup);
        host.AddBody(_hslGroup);
        host.AddBody(_detailGroup);
        host.AddBody(_geomGroup);
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

    private void UpdateExposureModeUi(DevelopSettings s)
    {
        bool useCurve = s.EnableExposureCurve;
        _btnExpSlider.Toggled = !useCurve;
        _btnExpCurve.Toggled = useCurve;
        _ev.Visible = !useCurve;
        _exposureCurveControl.Visible = useCurve;
        if (_expCurveRule != null)
            _expCurveRule.Visible = useCurve;
        _exposureGroup.InvalidateLayout();
    }

    private void SetToneMode(ToneMode mode)
    {
        var d = _session.Active;
        if (d == null) return;
        d.Undo.Push(d.Settings);
        d.Settings.ToneMode = mode;
        UpdateToneModeUi(d.Settings);
        PushLook(fast: false, settle: true);
    }

    private void UpdateToneModeUi(DevelopSettings s)
    {
        bool isSigmoid = s.ToneMode == ToneMode.Sigmoid;
        _btnToneSigmoid.Toggled = isSigmoid;
        _btnToneFilmic.Toggled = !isSigmoid;
        _sigContrast.Visible = isSigmoid;
        _sigSkew.Visible = isSigmoid;
        _toneGroup.InvalidateLayout();
    }

    private void SetReconMode(HighlightMode mode)
    {
        var d = _session.Active;
        if (d == null) return;
        d.Undo.Push(d.Settings);
        d.Settings.ReconstructionMode = mode;
        UpdateReconModeUi(d.Settings);
        PushLook(fast: false, settle: true);
    }

    private void UpdateReconModeUi(DevelopSettings s)
    {
        bool active = s.ReconstructionMode != HighlightMode.Off;
        _btnReconOff.Toggled = s.ReconstructionMode == HighlightMode.Off;
        _btnReconOpposed.Toggled = s.ReconstructionMode == HighlightMode.Opposed;
        _btnReconLCh.Toggled = s.ReconstructionMode == HighlightMode.LCh;
        _reconColor.Visible = active;
        _reconSpatial.Visible = active;
        _reconGroup.InvalidateLayout();
    }



    private SliderRow BindSlider(PanelGroup group, string label, float min, float max, string fmt,
        Action<DevelopSettings, float> set, Func<DevelopSettings, float> get, float def = 0f)
    {
        var row = new SliderRow(label, min, max, fmt);
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
        _film.Bind(_session.Documents, _session.ActiveIndex);
        if (_isGalleryMode) _gallery.Bind(_session.Documents, _session.ActiveIndex);
        _filmThumb = _session.Active?.Thumb;
        var d = _session.Active;
        UpdateTitleBadge();
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
        if (_isGalleryMode)
        {
            _photo.SetZoomMode(ZoomMode.Fit);
            _photo.RefreshGeometry();
        }
        else
        {
            _photo.RestoreView(d);
        }
        _restoringView = false;
        if (!_isGalleryMode)
            RequestViewport(d);
        SaveCurrentSession();
        _histogram.SetBins(d.HistogramR, d.HistogramG, d.HistogramB, d.HistogramY);
        _curveGraph.SetHistogramBins(d.HistogramR, d.HistogramG, d.HistogramB, d.HistogramY);
        _exposureCurveControl.SetHistogramBins(d.HistogramR, d.HistogramG, d.HistogramB, d.HistogramY);
        PullSliders(d);
        _matchGray.Toggled = d.Settings.MatchGray;
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
        {
            _photo.CancelCrop();
        }

        _isGalleryMode = gallery;
        _btnViewerMode.Toggled = !gallery;
        _btnGalleryMode.Toggled = gallery;

        if (gallery)
        {
            _photo.Style.Border = new BorderStyle
            {
                Width = 1f,
                Color = Theme.Hairline,
                Roundness = Theme.Radius
            };
            _photo.SetZoomMode(ZoomMode.Fit);

            // Visibility
            _gallery.Visible = true;
            _rightDockBar.Visible = true;
            _photo.Visible = true;
            _presets.Visible = true;

            // Hide develop-only elements: editing sliders, histogram, left dock, filmstrip, overlay, zoom
            _leftDockBar.Visible = false;
            _histogram.Visible = false;
            _rightColumn.Visible = false;
            _film.Visible = false;
            _viewOverlay.Visible = false;
            _zoomLabel.Visible = false;

            ApplyLayout();

            _gallery.Bind(_session.Documents, _session.ActiveIndex);
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

            _leftDockBar.Visible = true;
            _presets.Visible = true;
            _photo.Visible = true;
            _rightDockBar.Visible = true;
            _histogram.Visible = true;
            _rightColumn.Visible = true;
            _film.Visible = true;
            _viewOverlay.Visible = true;
            _zoomLabel.Visible = true;

            _gallery.Visible = false;

            ApplyLayout();

            RefreshSession();
            _film.RefreshThumbs();
        }

        UpdateTitleBadge();
    }

    private void ApplyLayout()
    {
        if (_photo == null || _presets == null || _gallery == null || _rightDockBar == null ||
            _leftDockBar == null || _film == null || _rightColumn == null || _histogram == null ||
            _viewOverlay == null || _zoomLabel == null)
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

        if (_isGalleryMode)
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

            // 2. Presets on left dock
            float presetTop = top + leftInset;
            float presetH = H - presetTop - leftInset - film;
            _presets.Transform.SetAbsoluteFrame(leftInset, presetTop, L - leftInset * 2f, presetH);
            _presets.Transform.Anchor = Anchor.Left | Anchor.Top | Anchor.Bottom;

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
            _histogram.Transform.SetAbsoluteFrame(W - R + rightInset, top + rightInset, R - rightInset * 2f, 96f);
            _histogram.Transform.Anchor = Anchor.Right | Anchor.Top;

            // 6. Right column (editing sliders)
            float rightDockTop = top + rightInset + 96f + 14f;
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
            _zoomLabel.Transform.Anchor = Anchor.Right | Anchor.Bottom;
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

    private void UpdateTitleBadge()
    {
    }

    private void ToggleReady(bool? force = null)
    {
        var d = _session.Active;
        if (d == null) return;
        OnReadyToggled(_session.ActiveIndex, force ?? !d.IsReady);
    }

    private void OnReadyToggled(int idx, bool ready)
    {
        if (idx < 0 || idx >= _session.Documents.Count) return;
        var doc = _session.Documents[idx];
        doc.IsReady = ready;
        _film.SetReady(idx, ready);
        _gallery.SetReady(idx, ready);
        WorkspaceStore.SaveReadyState(doc);
        UpdateTitleBadge();
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
        UpdateTitleBadge();
        SetStatus(favorite ? $"Starred: {doc.Name}" : $"Unstarred: {doc.Name}");
    }

    private void OpenMarkMenu(int idx, float x, float y)
    {
        if (idx < 0 || idx >= _session.Documents.Count) return;
        var doc = _session.Documents[idx];
        _markMenu.Open(idx, doc.IsReady, doc.IsFavorite, x, y, Width, Height);
    }

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
        if (_isGalleryMode) _gallery.RefreshThumbs();
        if (!ReferenceEquals(doc, _session.Active))
            return;

        _photo.Metadata = doc.Metadata;

        if (doc.Proxy != null)
        {
            _restoringView = true;
            BindPhoto(doc);
            if (_isGalleryMode)
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
            if (!_isGalleryMode)
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
        UpdateTitleBadge();
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
        _detailGroup.SectionEnabled = s.EnableDetail;
        _geomGroup.SectionEnabled = s.EnableGeometry;

        _temp.Value = s.Temperature;
        _tint.Value = s.Tint;
        _ev.Value = s.Exposure;
        UpdateExposureModeUi(s);
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
        UpdateToneModeUi(s);
        _curveGraph.LoadFromSettings(s);
        _reconThreshold.Value = s.HighlightThreshold;
        _reconColor.Value = s.ColorReconstructionAmount;
        _reconSpatial.Value = s.ColorReconstructionSpatial;
        UpdateReconModeUi(s);
        _localDetail.Value = s.LocalContrastDetail;
        _texture.Value = s.Texture;
        _dehaze.Value = s.Dehaze;
        _dehazeDistance.Value = s.DehazeDistance;
        _gradeShadowHue.Value = s.GradingShadowHue;
        _gradeShadowSat.Value = s.GradingShadowSat;
        _gradeHighlightHue.Value = s.GradingHighlightHue;
        _gradeHighlightSat.Value = s.GradingHighlightSat;
        _gradeBalance.Value = s.GradingBalance;
        _sharp.Value = s.Sharpen;
        _noise.Value = s.Noise != 0 ? s.Noise : (s.DenoiseLuma > 0 ? -s.DenoiseLuma : 0);
        _photo.StraightenPreview = s.Straighten;
        _vignette.Value = s.VignetteAmount;
        _vignetteMidpoint.Value = s.VignetteMidpoint;
        SyncActiveHslSliders();
        _sync = false;
    }

    private void SyncActiveHslSliders()
    {
        var d = _session.Active;
        var s = d?.Settings ?? new DevelopSettings();
        int idx = Math.Clamp(_activeHslBand, 0, 5);
        _sync = true;
        _hslActiveHue.Value = s.Hsl[idx].Hue;
        _hslActiveSat.Value = s.Hsl[idx].Sat;
        _hslActiveLuma.Value = s.Hsl[idx].Luma;
        _sync = false;
    }

    private void OnHslBandSelected(int idx)
    {
        _activeHslBand = idx;
        SyncActiveHslSliders();
    }

    private void SetActiveHsl(int component, float val)
    {
        var d = _session.Active;
        if (d == null) return;
        int idx = Math.Clamp(_activeHslBand, 0, 5);
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
        int idx = Math.Clamp(_activeHslBand, 0, 5);
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
        d.Settings.GradingShadowHue = 220f;
        d.Settings.GradingShadowSat = 0f;
        d.Settings.GradingHighlightHue = 40f;
        d.Settings.GradingHighlightSat = 0f;
        d.Settings.GradingBalance = 0f;
        PullSliders(d);
        PushLook(fast: false, settle: true);
        SetStatus("Reset Color Editor & Grading");
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
        if (_isGalleryMode || d == null || _photo == null)
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
        _btnBefore.Toggled = !_btnBefore.Toggled;
        if (_btnBefore.Toggled && _btnSplit.Toggled)
        {
            _btnSplit.Toggled = false;
            _photo.SplitBefore = false;
        }
        _photo.ShowBefore = _btnBefore.Toggled;
    }

    private void ToggleCrop()
    {
        if (_isGalleryMode || _session.Active == null)
            return;

        if (_photo.CropTool)
        {
            _photo.CancelCrop();
            return;
        }
        if (_btnCropGeom != null) _btnCropGeom.Toggled = true;
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
        bool keepWb = Math.Abs(p.Temperature) < 0.01f && Math.Abs(p.Tint) < 0.01f;
        bool keepEv = Math.Abs(p.Exposure) < 0.001f;
        float t = d.Settings.Temperature, ti = d.Settings.Tint, ev = d.Settings.Exposure;

        // Preserve active photo's geometry and crop (presets never modify transforms)
        float cropX = d.Settings.CropX;
        float cropY = d.Settings.CropY;
        float cropW = d.Settings.CropW;
        float cropH = d.Settings.CropH;
        float straighten = d.Settings.Straighten;
        int rotate90 = d.Settings.Rotate90;
        bool flipH = d.Settings.FlipH;
        bool flipV = d.Settings.FlipV;
        bool enableGeom = d.Settings.EnableGeometry;

        d.Settings.CopyFrom(p);
        if (keepWb) { d.Settings.Temperature = t; d.Settings.Tint = ti; }
        if (keepEv) d.Settings.Exposure = ev;

        d.Settings.CropX = cropX;
        d.Settings.CropY = cropY;
        d.Settings.CropW = cropW;
        d.Settings.CropH = cropH;
        d.Settings.Straighten = straighten;
        d.Settings.Rotate90 = rotate90;
        d.Settings.FlipH = flipH;
        d.Settings.FlipV = flipV;
        d.Settings.EnableGeometry = enableGeom;

        PullSliders(d);
        PushLook(fast: false, settle: true);
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
            PresetStore.Save(name, d.Settings);
            _presets.SetItems(PresetStore.Names());
            SetStatus("Saved preset " + name);
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
                        msg => Browser.Post(() => SetStatus(msg)), d.Metadata);
                    Browser.Post(() => SetStatus(
                        $"Exported {size.Width}×{size.Height}  {Path.GetFileName(dest)}"));
                }
                catch (Exception ex)
                {
                    Browser.Post(() => SetStatus("Export failed: " + ex.Message));
                }
                finally
                {
                    Volatile.Write(ref _exporting, 0);
                    Browser.Post(() =>
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
                    Browser.Post(() => SetStatus($"Exporting {i + 1} of {list.Count}: {doc.Name}…"));

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

                Browser.Post(() =>
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

    private void OnKey(int code)
    {
        var key = (Key)code;
        bool ctrl = Events.IsControlDown;

        // 1. Gated by modal dialogs and active keyboard element (e.g. text inputs):
        // Single-key shortcuts and view hotkeys MUST NEVER trigger while typing or interacting with modals.
        bool isModalOrText = ActiveKeyboardElement != null
            || (_nameDialog != null && _nameDialog.Visible)
            || (_export != null && _export.Visible)
            || (_settings != null && _settings.Visible)
            || (_gate != null && _gate.Visible)
            || (_markMenu != null && _markMenu.Visible);

        if (isModalOrText)
        {
            if (_nameDialog != null && _nameDialog.Visible)
            {
                if (key == Key.Escape) { _nameDialog.Close(); return; }
                if (key == Key.Enter || key == Key.KeypadEnter) { _nameDialog.Confirm(); return; }
                if (key == Key.Backspace || (int)key == 259 || (int)key == 8 || (int)key == 127) { _nameDialog.Backspace(); return; }
                if (key == Key.Delete || (int)key == 261) { _nameDialog.Delete(); return; }
                if (key == Key.Left) { _nameDialog.MoveLeft(); return; }
                if (key == Key.Right) { _nameDialog.MoveRight(); return; }
                if (key == Key.Home) { _nameDialog.MoveHome(); return; }
                if (key == Key.End) { _nameDialog.MoveEnd(); return; }
            }
            else if (_export != null && _export.Visible)
            {
                if (key == Key.Escape) { _export.Close(); return; }
                if (key == Key.Enter) { _export.Confirm(); return; }
            }
            else if (_settings != null && _settings.Visible)
            {
                if (key == Key.Escape) { _settings.Close(); return; }
            }
            else if (_gate != null && _gate.Visible)
            {
                if (key == Key.Enter || key == Key.O) { _gate.Pick(); return; }
            }
            else if (_markMenu != null && _markMenu.Visible)
            {
                if (key == Key.Escape) { _markMenu.Close(); return; }
            }
            return;
        }

        // 2. Global command shortcuts:
        if (ctrl && key == Key.C) { CopyAdjustments(); return; }
        if (ctrl && key == Key.V) { PasteAdjustments(); return; }
        if (ctrl && key == Key.Z && Events.IsShiftDown) { Redo(); return; }
        if (ctrl && key == Key.Z) { Undo(); return; }
        if (ctrl && key == Key.O) { OpenFiles(); return; }
        if (ctrl && key == Key.E) { StartExport(); return; }

        // 3. Mode-specific navigation:
        if (_isGalleryMode)
        {
            if (key == Key.Enter || key == Key.E) { SetViewMode(false); return; }
            if (key == Key.P) { ToggleReady(true); return; }
            if (key == Key.X) { ToggleReady(false); return; }
            if (key == Key.Left && _session.ActiveIndex > 0)
            {
                _session.Select(_session.ActiveIndex - 1);
                return;
            }
            if (key == Key.Right && _session.ActiveIndex < _session.Documents.Count - 1)
            {
                _session.Select(_session.ActiveIndex + 1);
                return;
            }
            // In gallery mode, NEVER fall through to develop view shortcuts (crop, zoom, rotate, etc.)!
            return;
        }

        // 4. Develop view navigation & overlays:
        if (key == Key.G) { SetViewMode(true); return; }
        if (key == Key.P) { ToggleReady(true); return; }
        if (key == Key.X) { ToggleReady(false); return; }
        if (key == Key.I) { ToggleInfoOverlay(); return; }
        if (key == Key.O && !ctrl) { ToggleClipping(); return; }

        // 5. Develop view photo editing tools (only if active photo is present):
        if (_session.Active == null) return;

        if (_photo.CropTool)
        {
            if (key == Key.Enter || key == Key.KeypadEnter)
            {
                _photo.CommitCrop();
                return;
            }
            if (key == Key.Escape)
            {
                _photo.CancelCrop();
                return;
            }
        }
        if (key == Key.C) { ToggleCrop(); return; }
        if (key == Key.F) _photo.SetZoomMode(ZoomMode.Fit);
        if (key == Key.Z || key == Key.Space)
            _photo.ToggleZoom();
        if (key == Key.BackSlash || key == Key.Slash) ToggleBefore();
        if (key == Key.LeftBracket) Rotate(-1);
        if (key == Key.RightBracket) Rotate(1);
        UpdateZoomLabel();
    }

    private void ToggleClipping()
    {
        _photo.ToggleClipping();
        _btnClip.Toggled = _photo.ShowClipping;
        SetStatus(_photo.ShowClipping ? "Clipping warnings enabled (Red = Highlights, Blue = Shadows)" : "Clipping warnings disabled");
    }

    private void ToggleInfoOverlay()
    {
        _photo.ToggleInfoOverlay();
        _btnInfo.Toggled = _photo.ShowInfoOverlay;
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

        d.Settings.CopyFrom(_copiedSettings);

        d.Settings.CropX = targetCropX;
        d.Settings.CropY = targetCropY;
        d.Settings.CropW = targetCropW;
        d.Settings.CropH = targetCropH;
        d.Settings.Straighten = targetStraight;
        d.Settings.Rotate90 = targetRot;
        d.Settings.FlipH = targetFlipH;
        d.Settings.FlipV = targetFlipV;

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
        if (_zoomLabel == null || _photo == null) return;
        if (_photo.IsFreeZoom)
        {
            _zoomLabel.Text = $"{_photo.Zoom * 100:0}%";
        }
        else
        {
            _zoomLabel.Text = _photo.ZoomMode == ZoomMode.Fit ? "Fit" :
                _photo.ZoomMode == ZoomMode.Fill ? "Fill" :
                $"{_photo.Zoom * 100:0}%";
        }
    }

    private CancellationTokenSource? _statusDismissCts;

    private void SetStatus(string? t)
    {
        if (_status == null) return;
        _statusDismissCts?.Cancel();
        _statusDismissCts?.Dispose();
        _statusDismissCts = null;

        bool hasText = !string.IsNullOrWhiteSpace(t);
        _status.Text = t ?? string.Empty;
        _status.Visible = hasText;
        if (hasText)
        {
            UpdateStatusPosition();
            _status.InvalidatePaint();

            var cts = new CancellationTokenSource();
            _statusDismissCts = cts;
            var token = cts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(3500, token);
                    Browser.Post(() =>
                    {
                        if (!token.IsCancellationRequested && _status != null)
                        {
                            _status.Text = string.Empty;
                            _status.Visible = false;
                            _status.InvalidatePaint();
                        }
                    });
                }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
            });
        }
        else
        {
            _status.InvalidatePaint();
        }
    }

    private void UpdateStatusPosition()
    {
        if (_status == null) return;
        float L = Theme.LeftW;
        float R = Theme.RightW;
        float film = Theme.FilmH;
        float photoW = Math.Max(200f, Width - L - R);
        string text = _status.Text ?? string.Empty;

        float textW = Math.Max(60f, text.Length * 7.2f);
        float pillW = Math.Clamp(textW + 40f, 130f, photoW - 40f);
        float pillH = 26f;
        float pillX = L + (photoW - pillW) * 0.5f;
        float pillY = Height - film - pillH - 12f;

        if (_isGalleryMode)
        {
            float galleryColW = GetGalleryColWidth(Width);
            float galleryW = Math.Max(200f, Width - galleryColW);
            pillW = Math.Clamp(textW + 40f, 130f, galleryW - 40f);
            pillX = (galleryW - pillW) * 0.5f;
            pillY = Height - pillH - 16f;
        }

        _status.Transform.SetAbsoluteFrame(pillX, pillY, pillW, pillH);
    }

    private VisualElement Bar(string name, SKColor color, float x, float y, float w, float h, Anchor a)
    {
        var e = new VisualElement
        {
            Name = name,
            ZIndex = -1,
            Style = new ElementStyle { BackColor = color },
            Transform = new Transform(x, y, w, h) { Anchor = a }
        };
        AddElement(e);
        return e;
    }

    private void AddLabel(string name, string text, float x, float y, float w, float h, SKColor c, float size, int weight)
    {
        AddElement(new VisualElement
        {
            Name = name,
            Text = text,
            Style = new ElementStyle
            {
                Text = new TextStyle { Color = c, Size = size, Weight = weight, Alignment = TextAlign.Left }
            },
            Transform = new Transform(x, y, w, h) { Anchor = Anchor.Left | Anchor.Top }
        });
    }

    private IconButton Chip(string caption, float x, float y, float w, float h, bool primary = false, string? iconName = null)
    {
        var e = new IconButton(caption, iconName, primary);
        e.ZIndex = 1;
        e.Transform = new Transform(x, y, w, h) { Anchor = Anchor.Top };
        AddElement(e);
        return e;
    }

    private VisualElement Label(string name, string text, float x, float y, float w, float h, SKColor c, float size, int weight)
    {
        var e = new VisualElement
        {
            Name = name,
            Text = text,
            Style = new ElementStyle
            {
                Text = new TextStyle { Color = c, Size = size, Weight = weight, Alignment = TextAlign.Right }
            },
            Transform = new Transform(x, y, w, h) { Anchor = Anchor.Right | Anchor.Bottom }
        };
        AddElement(e);
        return e;
    }

}

internal sealed class RightColumn : ScrollContainer
{
    private readonly List<VisualElement> _items = new();

    public RightColumn()
    {
        Name = "RightColumn";
        OverflowX = OverflowMode.Clip;
        OverflowY = OverflowMode.Scroll;
        ScrollbarVisibilityX = ScrollbarVisibility.Hidden;
        ScrollbarVisibilityY = ScrollbarVisibility.Auto;
        Style = new ElementStyle { BackColor = Theme.Window };
    }

    public void AddBody(VisualElement e)
    {
        _items.Add(e);
        AddChild(e);
    }

    public override SKSize GetPreferredSize(float maxWidth, float maxHeight)
    {
        float h = 0;
        float w = maxWidth > 0 ? maxWidth : Theme.RightW;
        foreach (var c in _items)
        {
            if (c == null || !c.Visible) continue;
            h += c.GetPreferredSize(w, 0).Height;
        }
        return new SKSize(w, h);
    }

    protected override void LayoutChildren()
    {
        float ox = Transform.Computed.X;
        float oy = Transform.Computed.Y;
        float inset = 10f;
        float w = Math.Max(1f, Transform.Width - inset * 2f);
        float y = inset;
        foreach (var c in _items)
        {
            if (c == null || !c.Visible) continue;
            float ch = c.GetPreferredSize(w, 0).Height;
            c.Transform.SetAbsoluteFrame(ox + inset, oy + y, w, ch);
            y += ch + 14f;
        }
        float newH = Math.Max(y + inset, Transform.Computed.Height);
        float newW = Transform.Width;
        if (CustomContentHeight != newH || CustomContentWidth != newW)
        {
            SetContentSize(newW, newH);
        }
        base.LayoutChildren();
    }
}

internal sealed class ModeChipRow : VisualElement
{
    private readonly IconButton[] _buttons;
    private readonly float _gap;

    public ModeChipRow(IconButton[] buttons, float gap = 6f)
    {
        _buttons = buttons;
        _gap = gap;
        Transform.Height = Theme.ToolH;
        foreach (var b in buttons)
            AddChild(b);
    }

    public override SKSize GetPreferredSize(float maxWidth, float maxHeight)
    {
        float w = maxWidth > 0 ? maxWidth : Theme.RightW;
        return new SKSize(w, Theme.ToolH);
    }

    protected override void LayoutChildren()
    {
        float ox = Transform.Computed.X;
        float oy = Transform.Computed.Y;
        float w = Math.Max(1f, Transform.Width);
        float h = Math.Max(1f, Transform.Height);
        int n = _buttons.Length;
        float cellW = n > 0 ? (w - _gap * (n - 1)) / n : w;
        for (int i = 0; i < n; i++)
        {
            _buttons[i].Transform.SetAbsoluteFrame(ox + i * (cellW + _gap), oy, cellW, h);
        }
    }
}
