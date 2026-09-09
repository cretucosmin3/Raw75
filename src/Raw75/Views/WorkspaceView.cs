using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Blossom;
using Blossom.Core;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Raw75.Controls;
using Raw75.Develop;
using Raw75.Imaging;
using Raw75.Io;
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
    private NavigatorBox _nav = null!;
    private PresetList _presets = null!;
    private ExportDialog _export = null!;
    private NameDialog _nameDialog = null!;
    private SettingsDialog _settings = null!;
    private WorkspaceGate _gate = null!;
    private IconButton _btnCrop = null!;
    private IconButton _btnBefore = null!;
    private IconButton _btnSplit = null!;
    private VisualElement _zoomLabel = null!;

    private SliderRow _temp = null!, _tint = null!, _ev = null!, _con = null!, _hi = null!, _sh = null!;
    private SliderRow _wh = null!, _bk = null!, _vib = null!, _sat = null!;
    private SliderRow _sharp = null!, _dnY = null!, _dnC = null!, _straight = null!;

    // Tone
    private PanelGroup _toneGroup = null!;
    private IconButton _btnToneSigmoid = null!;
    private IconButton _btnToneFilmic = null!;
    private SliderRow _sigContrast = null!;
    private SliderRow _sigSkew = null!;

    // Reconstruction
    private PanelGroup _reconGroup = null!;
    private IconButton _btnReconOff = null!;
    private IconButton _btnReconOpposed = null!;
    private IconButton _btnReconLCh = null!;
    private SliderRow _reconThreshold = null!;
    private SliderRow _reconColor = null!;
    private SliderRow _reconSpatial = null!;

    // Local contrast
    private PanelGroup _localGroup = null!;
    private SliderRow _localDetail = null!;
    private SliderRow _localHighlights = null!;
    private SliderRow _localShadows = null!;
    private SliderRow _localMidtones = null!;

    private readonly SliderRow[] _hslH = new SliderRow[6];
    private readonly SliderRow[] _hslS = new SliderRow[6];
    private readonly SliderRow[] _hslL = new SliderRow[6];
    private IconButton _matchGray = null!;
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
        _engine = new DevelopEngine(t => { if (_status != null) _status.Text = t; });
        _engine.Updated += OnDeveloped;
        _engine.LiveUpdated += OnLivePreview;
        _engine.HistogramUpdated += OnHistogram;
        _engine.ViewportUpdated += OnViewportTile;
        _engine.BusyChanged += busy => { if (_photo != null) _photo.Busy = busy; };
        _session.Changed += RefreshSession;

        BuildChrome();
        _presets.SetItems(PresetStore.Names());
        SetStatus("Choose a folder to start.");
        ForceLayoutEvaluation();
        _gate.Show();
    }

    private void BuildChrome()
    {
        float W = Width;
        float H = Height;
        float L = Theme.LeftW;
        float R = Theme.RightW;
        float top = Theme.TopBarH;
        float film = Theme.FilmH;
        float tool = Theme.ToolH;
        float st = Theme.StatusH;

        AddElement(Bar("Top", Theme.TopBar, 0, 0, W, top, Anchor.Left | Anchor.Right | Anchor.Top));
        var brand = new BrandMark
        {
            Transform = new Transform(16, 12, 92, 24)
            {
                Anchor = Anchor.Left | Anchor.Top
            }
        };
        AddElement(brand);
        var open = Chip("Open", 118, 10, 72, 28);
        open.Clicked += OpenFiles;
        var folder = Chip("Folder", 198, 10, 72, 28);
        folder.Clicked += OpenWorkspace;
        var settings = Chip("Settings", W - 196, 10, 88, 28);
        settings.Clicked += OpenSettings;
        var exp = Chip("Export", W - 100, 10, 80, 28, primary: true);
        exp.Clicked += StartExport;

        _nav = new NavigatorBox
        {
            Transform = new Transform(0, top, L, 148)
            {
                Anchor = Anchor.Left | Anchor.Top
            }
        };
        _nav.ModePicked += m =>
        {
            _photo.SetZoomMode(m == "Fill" ? ZoomMode.Fill : m == "1:1" ? ZoomMode.OneToOne : ZoomMode.Fit);
            UpdateZoomLabel();
        };
        AddElement(_nav);

        _presets = new PresetList
        {
            Transform = new Transform(0, top + 148, L, H - top - 148 - film - st)
            {
                Anchor = Anchor.Left | Anchor.Top | Anchor.Bottom
            }
        };
        _presets.Applied += ApplyPreset;
        _presets.SaveClicked += SavePreset;
        _presets.Deleted += DeletePreset;
        AddElement(_presets);

        _histogram = new HistogramView
        {
            Transform = new Transform(W - R, top, R, 88)
            {
                Anchor = Anchor.Right | Anchor.Top
            }
        };
        AddElement(_histogram);

        var rightDock = new RightColumn
        {
            Transform = new Transform(W - R, top + 88, R, H - top - 88 - film - st)
            {
                Anchor = Anchor.Right | Anchor.Top | Anchor.Bottom
            }
        };
        AddElement(rightDock);
        BuildRightPanels(rightDock);

        float photoX = L;
        float photoY = top;
        float photoW = W - L - R;
        float photoH = H - top - tool - film - st;
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
            if (!_restoringView)
                _photo.CaptureView(d);
            RequestViewport(d);
        };
        _photo.Rotate90Clicked += Rotate;
        _photo.CropChanged += () =>
        {
            if (_sync) return;
            _sync = true;
            _straight.Value = _photo.StraightenPreview;
            _sync = false;
        };
        AddElement(_photo);

        var tools = Bar("Tools", Theme.PanelAlt, photoX, photoY + photoH, photoW, tool,
            Anchor.Left | Anchor.Right | Anchor.Bottom);
        float tx = photoX + 8;
        float ty = photoY + photoH + 2;
        _btnCrop = Tool("Crop", tx, ty); tx += 60;
        _btnCrop.Clicked += ToggleCrop;
        _photo.CropCommitted += () =>
        {
            _btnCrop.Toggled = false;
            var d = _session.Active;
            if (d != null)
            {
                d.Undo.Push(d.Settings);
                CopyCropInto(d);
                _straight.Value = d.Settings.Straighten;
                WorkspaceStore.SaveSettings(d);
            }
            PushLook(fast: false, settle: true);
        };
        _photo.CropCancelled += () =>
        {
            _btnCrop.Toggled = false;
            var d = _session.Active;
            if (d != null)
            {
                _straight.Value = d.Settings.Straighten;
            }
            PushLook(fast: false, settle: true);
        };
        _btnBefore = Tool("Before", tx, ty); tx += 68;
        _btnBefore.Clicked += ToggleBefore;
        _btnSplit = Tool("Split", tx, ty); tx += 60;
        _btnSplit.Clicked += () =>
        {
            _btnSplit.Toggled = !_btnSplit.Toggled;
            _photo.SplitBefore = _btnSplit.Toggled;
            var d = _session.Active;
            _photo.SetBefore(d?.Proxy);
        };
        var rotL = Tool("Rot L", tx, ty); tx += 60;
        rotL.Clicked += () => Rotate(-1);
        var rotR = Tool("Rot R", tx, ty); tx += 60;
        rotR.Clicked += () => Rotate(1);
        var flipH = Tool("Flip H", tx, ty); tx += 64;
        flipH.Clicked += () => Flip(h: true);
        var flipV = Tool("Flip V", tx, ty); tx += 64;
        flipV.Clicked += () => Flip(h: false);
        var hdr = Tool("HDR", tx, ty);
        hdr.Clicked += MergeHdr;
        _zoomLabel = Label("ZoomPct", "Fit", photoX + photoW - 70, photoY + photoH + 6, 64, 20, Theme.TextDim, 11, 400);

        _film = new Filmstrip
        {
            Transform = new Transform(0, H - film - st, W, film)
            {
                Anchor = Anchor.Left | Anchor.Right | Anchor.Bottom
            }
        };
        _film.Selected += i => _session.Select(i);
        _film.CloseRequested += i => _session.Close(i);
        AddElement(_film);

        _status = Bar("Status", Theme.Filmstrip, 0, H - st, W, st, Anchor.Left | Anchor.Right | Anchor.Bottom);
        _status.Style.Text = new TextStyle { Color = Theme.TextDim, Size = 11, Padding = 8, Alignment = TextAlign.Left };
        _status.Text = "Drop a RAW or Open.";

        _export = new ExportDialog();
        _export.Confirmed += OnExportConfirmed;
        AddElement(_export);

        _nameDialog = new NameDialog();
        _nameDialog.Confirmed += OnPresetNamed;
        AddElement(_nameDialog);

        _settings = new SettingsDialog();
        AddElement(_settings);

        _gate = new WorkspaceGate();
        _gate.FolderPicked += folder => LoadWorkspace(folder, rememberActive: false);
        AddElement(_gate);
    }

    private void BuildRightPanels(RightColumn host)
    {
        var basic = new PanelGroup("Basic");
        _temp = BindSlider(basic, "Temp", -100, 100, "0", (s, v) => s.Temperature = v, s => s.Temperature);
        _tint = BindSlider(basic, "Tint", -100, 100, "0", (s, v) => s.Tint = v, s => s.Tint);
        _ev = BindSlider(basic, "Exposure", -5, 5, "0.00", (s, v) => s.Exposure = v, s => s.Exposure);
        _con = BindSlider(basic, "Contrast", -100, 100, "0", (s, v) => s.Contrast = v, s => s.Contrast);
        _hi = BindSlider(basic, "Hi recovery", -100, 100, "0", (s, v) => s.Highlights = v, s => s.Highlights);
        _sh = BindSlider(basic, "Shadow recovery", -100, 100, "0", (s, v) => s.Shadows = v, s => s.Shadows);
        _wh = BindSlider(basic, "Whites", -100, 100, "0", (s, v) => s.Whites = v, s => s.Whites);
        _bk = BindSlider(basic, "Blacks", -100, 100, "0", (s, v) => s.Blacks = v, s => s.Blacks);
        _vib = BindSlider(basic, "Vibrance", -100, 100, "0", (s, v) => s.Vibrance = v, s => s.Vibrance);
        _sat = BindSlider(basic, "Saturation", -100, 100, "0", (s, v) => s.Saturation = v, s => s.Saturation);
        _matchGray = new IconButton("Match mid-gray");
        _matchGray.Clicked += () =>
        {
            var d = _session.Active;
            if (d == null) return;
            d.Undo.Push(d.Settings);
            d.Settings.MatchGray = !d.Settings.MatchGray;
            _matchGray.Toggled = d.Settings.MatchGray;
            PushLook(fast: false, settle: true);
        };
        basic.AddBody(_matchGray);

        _toneGroup = new PanelGroup("Tone");
        _btnToneSigmoid = new IconButton("Sigmoid");
        _btnToneFilmic = new IconButton("Filmic");
        _btnToneSigmoid.Clicked += () => SetToneMode(ToneMode.Sigmoid);
        _btnToneFilmic.Clicked += () => SetToneMode(ToneMode.Filmic);
        _toneGroup.AddBody(new ModeChipRow([_btnToneSigmoid, _btnToneFilmic]));
        _sigContrast = BindSlider(_toneGroup, "Tone Contrast", 0.1f, 10.0f, "0.00",
            (s, v) => s.SigmoidContrast = v, s => s.SigmoidContrast, 1.5f);
        _sigSkew = BindSlider(_toneGroup, "Skew", -1.0f, 1.0f, "0.00",
            (s, v) => s.SigmoidSkew = v, s => s.SigmoidSkew, 0.0f);
        _toneGroup.ExpandedChanged += exp =>
        {
            if (exp && _session.Active != null)
                UpdateToneModeUi(_session.Active.Settings);
        };

        _reconGroup = new PanelGroup("Reconstruction");
        _btnReconOff = new IconButton("Off");
        _btnReconOpposed = new IconButton("Opposed");
        _btnReconLCh = new IconButton("LCh");
        _btnReconOff.Clicked += () => SetReconMode(HighlightMode.Off);
        _btnReconOpposed.Clicked += () => SetReconMode(HighlightMode.Opposed);
        _btnReconLCh.Clicked += () => SetReconMode(HighlightMode.LCh);
        _reconGroup.AddBody(new ModeChipRow([_btnReconOff, _btnReconOpposed, _btnReconLCh]));
        _reconThreshold = BindSlider(_reconGroup, "Threshold", 0.5f, 2.0f, "0.00",
            (s, v) => s.HighlightThreshold = v, s => s.HighlightThreshold, 1.0f);
        _reconColor = BindSlider(_reconGroup, "Color amount", 0f, 100f, "0",
            (s, v) => s.ColorReconstructionAmount = v, s => s.ColorReconstructionAmount, 0f);
        _reconSpatial = BindSlider(_reconGroup, "Spatial", 0f, 100f, "0",
            (s, v) => s.ColorReconstructionSpatial = v, s => s.ColorReconstructionSpatial, 0f);
        _reconGroup.ExpandedChanged += exp =>
        {
            if (exp && _session.Active != null)
                UpdateReconModeUi(_session.Active.Settings);
        };

        _localGroup = new PanelGroup("Local contrast");
        _localDetail = BindSlider(_localGroup, "Detail", -1.0f, 4.0f, "0.00",
            (s, v) =>
            {
                s.LocalContrastDetail = v;
                UpdateLocalContrastUi(s);
            }, s => s.LocalContrastDetail, 0.0f);
        _localHighlights = BindSlider(_localGroup, "Highlights", -100f, 100f, "0",
            (s, v) => s.LocalContrastHighlights = v, s => s.LocalContrastHighlights, 0f);
        _localShadows = BindSlider(_localGroup, "Shadows", -100f, 100f, "0",
            (s, v) => s.LocalContrastShadows = v, s => s.LocalContrastShadows, 0f);
        _localMidtones = BindSlider(_localGroup, "Midtone range", 0f, 100f, "0",
            (s, v) => s.LocalContrastMidtones = v, s => s.LocalContrastMidtones, 50f);
        _localGroup.ExpandedChanged += exp =>
        {
            if (exp && _session.Active != null)
                UpdateLocalContrastUi(_session.Active.Settings);
        };

        UpdateReconModeUi(new DevelopSettings());
        UpdateLocalContrastUi(new DevelopSettings());

        var hsl = new PanelGroup("HSL");
        for (int i = 0; i < 6; i++)
        {
            int idx = i;
            _hslH[i] = BindSlider(hsl, HslNames[i] + " H", -100, 100, "0",
                (s, v) => { var b = s.Hsl[idx]; b.Hue = v; s.Hsl[idx] = b; },
                s => s.Hsl[idx].Hue);
            _hslS[i] = BindSlider(hsl, HslNames[i] + " S", -100, 100, "0",
                (s, v) => { var b = s.Hsl[idx]; b.Sat = v; s.Hsl[idx] = b; },
                s => s.Hsl[idx].Sat);
            _hslL[i] = BindSlider(hsl, HslNames[i] + " L", -100, 100, "0",
                (s, v) => { var b = s.Hsl[idx]; b.Luma = v; s.Hsl[idx] = b; },
                s => s.Hsl[idx].Luma);
        }

        var detail = new PanelGroup("Detail");
        _sharp = BindSlider(detail, "Sharpen", 0, 150, "0", (s, v) => s.Sharpen = v, s => s.Sharpen);
        _dnY = BindSlider(detail, "Luma NR", 0, 100, "0", (s, v) => s.DenoiseLuma = v, s => s.DenoiseLuma);
        _dnC = BindSlider(detail, "Chroma NR", 0, 100, "0", (s, v) => s.DenoiseChroma = v, s => s.DenoiseChroma);

        var xform = new PanelGroup("Transform");
        _straight = BindSlider(xform, "Straighten", -45, 45, "0.0", (s, v) =>
        {
            s.Straighten = v;
            _photo.StraightenPreview = v;
        }, s => s.Straighten);

        host.AddBody(basic);
        host.AddBody(_toneGroup);
        host.AddBody(_reconGroup);
        host.AddBody(_localGroup);
        host.AddBody(hsl);
        host.AddBody(detail);
        host.AddBody(xform);
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

    private void UpdateLocalContrastUi(DevelopSettings s)
    {
        bool active = Math.Abs(s.LocalContrastDetail) > 0.001f;
        _localHighlights.Visible = active;
        _localShadows.Visible = active;
        _localMidtones.Visible = active;
        _localGroup.InvalidateLayout();
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
        _straight.Value = _photo.StraightenPreview;
    }

    private void RefreshSession()
    {
        _film.Bind(_session.Documents, _session.ActiveIndex);
        _filmThumb = _session.Active?.Thumb;
        var d = _session.Active;
        if (d == null)
        {
            _photo.SetDeveloped(null, owns: false);
            _nav.Image = null;
            SetStatus("Drop a RAW, Open, or pick a Folder.");
            return;
        }

        _photo.SetDroppedPath(d.Path);
        WorkspaceStore.HydrateLook(d);
        if (!d.SourceRgba.HasPixels)
            _engine.Open(d);
        _restoringView = true;
        BindPhoto(d);
        _photo.RestoreView(d);
        _restoringView = false;
        _nav.Image = d.Preview ?? d.Look ?? d.Thumb ?? d.Display;
        WorkspaceStore.RememberActive(d.Path);
        _histogram.SetBins(d.HistogramR, d.HistogramG, d.HistogramB, d.HistogramY);
        PullSliders(d);
        _matchGray.Toggled = d.Settings.MatchGray;
        UpdateZoomLabel();
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
        if (!ReferenceEquals(doc, _session.Active))
            return;

        _restoringView = true;
        BindPhoto(doc);
        _photo.RestoreView(doc);
        _restoringView = false;
        _photo.SetBefore(doc.Proxy);
        _nav.Image = doc.Preview ?? doc.Look ?? doc.Display ?? doc.Thumb;
        _histogram.SetBins(doc.HistogramR, doc.HistogramG, doc.HistogramB, doc.HistogramY);
        _filmThumb = doc.Preview ?? doc.Thumb;
        UpdateZoomLabel();
    }

    /// <summary>
    /// GPU-look the undeveloped proxy. If it was unloaded, blit the stored
    /// developed look so rotation is already correct while LibRaw reloads.
    /// </summary>
    private void BindPhoto(PhotoDocument d)
    {
        _photo.PrepareBind(d.Settings);
        _photo.SetNativeSize(d.NativeWidth, d.NativeHeight);
        SKImage? working = d.Proxy;
        if (working != null && working.Handle != IntPtr.Zero)
        {
            _photo.SetLook(working, d.Settings, fast: false);
            _photo.SetDeveloped(d.Display, owns: false);
            _photo.RefreshGeometry();
            if (d.ViewportTile != null)
                _photo.SetViewportTile(d.ViewportTile, d.TileX, d.TileY, d.TileW, d.TileH);
            else
                _photo.ClearViewportTile();
            RequestViewport(d);
            return;
        }

        _photo.SetLook(null, d.Settings, fast: false);
        _photo.SetDeveloped(d.Look ?? d.Preview ?? d.Display ?? d.Thumb, owns: false);
        _photo.RefreshGeometry();
    }

    private void PullSliders(PhotoDocument d)
    {
        _sync = true;
        var s = d.Settings;
        _temp.Value = s.Temperature;
        _tint.Value = s.Tint;
        _ev.Value = s.Exposure;
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
        _reconThreshold.Value = s.HighlightThreshold;
        _reconColor.Value = s.ColorReconstructionAmount;
        _reconSpatial.Value = s.ColorReconstructionSpatial;
        UpdateReconModeUi(s);
        _localDetail.Value = s.LocalContrastDetail;
        _localHighlights.Value = s.LocalContrastHighlights;
        _localShadows.Value = s.LocalContrastShadows;
        _localMidtones.Value = s.LocalContrastMidtones;
        UpdateLocalContrastUi(s);
        _sharp.Value = s.Sharpen;
        _dnY.Value = s.DenoiseLuma;
        _dnC.Value = s.DenoiseChroma;
        _straight.Value = s.Straighten;
        _photo.StraightenPreview = s.Straighten;
        for (int i = 0; i < 6; i++)
        {
            _hslH[i].Value = s.Hsl[i].Hue;
            _hslS[i].Value = s.Hsl[i].Sat;
            _hslL[i].Value = s.Hsl[i].Luma;
        }
        _sync = false;
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
        if (d == null || _photo == null)
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
    }

    private void ToggleBefore()
    {
        _btnBefore.Toggled = !_btnBefore.Toggled;
        _photo.ShowBefore = _btnBefore.Toggled;
    }

    private void ToggleCrop()
    {
        if (_photo.CropTool)
        {
            _photo.CancelCrop();
            return;
        }
        _btnCrop.Toggled = true;
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

    private void MergeHdr()
    {
        if (_session.Documents.Count < 2)
        {
            SetStatus("Open 2+ photos, then HDR.");
            return;
        }

        var bmp = HdrMerge.Average(_session.Documents.ToArray());
        if (bmp == null)
        {
            SetStatus("HDR merge needs matching sizes.");
            return;
        }

        string path = Path.Combine(Path.GetTempPath(), "raw75-hdr.png");
        using (var data = bmp.Encode(SKEncodedImageFormat.Png, 90))
        using (var fs = File.OpenWrite(path))
            data.SaveTo(fs);

        var doc = _session.Add(path);
        doc.SourceWidth = bmp.Width;
        doc.SourceHeight = bmp.Height;
        var img = RawDecoder.Upload(bmp);
        bmp.Dispose();
        doc.Proxy = img;
        doc.Display = img;
        doc.Thumb = img;
        _engine.Invalidate(doc);
        RefreshSession();
        SetStatus("HDR merge (average). Align/de-ghost still to come.");
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
        d.Settings.CopyFrom(p);
        if (keepWb) { d.Settings.Temperature = t; d.Settings.Tint = ti; }
        if (keepEv) d.Settings.Exposure = ev;
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
    }

    private void OnFilesDropped(string[] paths)
    {
        if (paths == null || paths.Length == 0)
            return;

        string first = paths[0];
        string? dir = FileDialogs.NormalizeDir(first);
        if (dir == null && File.Exists(first))
            dir = FileDialogs.NormalizeDir(Path.GetDirectoryName(first));

        if (_gate.Visible && dir != null)
        {
            LoadWorkspace(dir, rememberActive: true);
            return;
        }

        OpenPaths(paths);
    }

    private void OpenPaths(string[] paths)
    {
        PhotoDocument? last = null;
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
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
    }

    private void OpenSettings()
    {
        _settings.Open(_session, _photo);
    }

    private int _exporting;

    private void StartExport()
    {
        var d = _session.Active;
        if (d == null) { SetStatus("Nothing to export."); return; }
        _export.Open(Path.ChangeExtension(d.Name, ".jpg"), d.NativeWidth, d.NativeHeight);
    }

    private void OnExportConfirmed(ExportRequest req)
    {
        var d = _session.Active;
        if (d == null) { SetStatus("Nothing to export."); return; }
        if (Interlocked.CompareExchange(ref _exporting, 1, 0) != 0)
        {
            SetStatus("Export already running.");
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
                    msg => Browser.Post(() => SetStatus(msg)));
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
        if (ctrl && key == Key.Z && Events.IsShiftDown) { Redo(); return; }
        if (ctrl && key == Key.Z) { Undo(); return; }
        if (ctrl && key == Key.O) { OpenFiles(); return; }
        if (ctrl && key == Key.E) { StartExport(); return; }
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
            _photo.SetZoomMode(_photo.ZoomMode == ZoomMode.OneToOne ? ZoomMode.Fit : ZoomMode.OneToOne);
        if (key == Key.BackSlash || key == Key.Slash) ToggleBefore();
        if (key == Key.LeftBracket) Rotate(-1);
        if (key == Key.RightBracket) Rotate(1);
        UpdateZoomLabel();
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
        _zoomLabel.Text = _photo.ZoomMode == ZoomMode.Fit ? "Fit" :
            _photo.ZoomMode == ZoomMode.Fill ? "Fill" :
            $"{_photo.Zoom * 100:0}%";
        _nav.SetMode(_photo.ZoomMode == ZoomMode.Fill ? "Fill" :
            _photo.ZoomMode == ZoomMode.OneToOne ? "1:1" : "Fit");
    }

    private void SetStatus(string t)
    {
        if (_status != null) _status.Text = t;
    }

    private VisualElement Bar(string name, SKColor color, float x, float y, float w, float h, Anchor a)
    {
        var e = new VisualElement
        {
            Name = name,
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

    private IconButton Chip(string caption, float x, float y, float w, float h, bool primary = false)
    {
        var e = new IconButton(caption, primary);
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

    private IconButton Tool(string caption, float x, float y)
    {
        var b = new IconButton(caption);
        b.Transform = new Transform(x, y, 62, Theme.ToolH - 4)
        {
            Anchor = Anchor.Left | Anchor.Bottom
        };
        AddElement(b);
        return b;
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
        float inset = 8f;
        float w = Math.Max(1f, Transform.Width - inset * 2f);
        float y = inset;
        foreach (var c in _items)
        {
            if (c == null || !c.Visible) continue;
            float ch = c.GetPreferredSize(w, 0).Height;
            c.Transform.SetAbsoluteFrame(ox + inset, oy + y, w, ch);
            y += ch + 8f;
        }
        SetContentSize(Transform.Width, Math.Max(y + inset, Transform.Computed.Height));
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
