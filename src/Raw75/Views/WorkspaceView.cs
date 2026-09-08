using System;
using System.Collections.Generic;
using System.IO;
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

    private PhotoPane _photo = null!;
    private VisualElement _status = null!;
    private HistogramView _histogram = null!;
    private Filmstrip _film = null!;
    private NavigatorBox _nav = null!;
    private PresetList _presets = null!;
    private ExportDialog _export = null!;
    private IconButton _btnCrop = null!;
    private IconButton _btnBefore = null!;
    private IconButton _btnSplit = null!;
    private VisualElement _zoomLabel = null!;

    private SliderRow _temp = null!, _tint = null!, _ev = null!, _con = null!, _hi = null!, _sh = null!;
    private SliderRow _wh = null!, _bk = null!, _vib = null!, _sat = null!;
    private SliderRow _sharp = null!, _dnY = null!, _dnC = null!, _straight = null!;
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
        _engine.BusyChanged += busy => { if (_photo != null) _photo.Busy = busy; };
        _session.Changed += RefreshSession;

        BuildChrome();
        _presets.SetItems(PresetStore.Names());
        SetStatus("Drop a RAW or Open.");
        ForceLayoutEvaluation();
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
        AddLabel("Brand", "Raw75", 16, 14, 88, 22, Theme.Text, 16, 700);
        var open = Chip("Open", 112, 10, 72, 28);
        open.Events.OnClick += (_, e) => { e.Handled = true; OpenFiles(); };
        var exp = Chip("Export", W - 100, 10, 80, 28);
        exp.Events.OnClick += (_, e) => { e.Handled = true; StartExport(); };

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
            if (_photo.ZoomMode == ZoomMode.OneToOne || _photo.Zoom >= 1f)
                _engine.EnsureHiRes(d);
        };
        _photo.CropChanged += () =>
        {
            var d = _session.Active;
            if (d == null) return;
            d.Settings.CropX = _photo.CropX;
            d.Settings.CropY = _photo.CropY;
            d.Settings.CropW = _photo.CropW;
            d.Settings.CropH = _photo.CropH;
        };
        AddElement(_photo);

        var tools = Bar("Tools", Theme.PanelAlt, photoX, photoY + photoH, photoW, tool,
            Anchor.Left | Anchor.Right | Anchor.Bottom);
        float tx = photoX + 8;
        float ty = photoY + photoH + 2;
        _btnCrop = Tool("Crop", tx, ty); tx += 60;
        _btnCrop.Clicked += () => { _btnCrop.Toggled = !_btnCrop.Toggled; _photo.CropTool = _btnCrop.Toggled; };
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
    }

    private void BuildRightPanels(RightColumn host)
    {
        var basic = new PanelGroup("Basic");
        _temp = BindSlider(basic, "Temp", -100, 100, "0", (s, v) => s.Temperature = v, s => s.Temperature);
        _tint = BindSlider(basic, "Tint", -100, 100, "0", (s, v) => s.Tint = v, s => s.Tint);
        _ev = BindSlider(basic, "Exposure", -5, 5, "0.00", (s, v) => s.Exposure = v, s => s.Exposure);
        _con = BindSlider(basic, "Contrast", -100, 100, "0", (s, v) => s.Contrast = v, s => s.Contrast);
        _hi = BindSlider(basic, "Highlights", -100, 100, "0", (s, v) => s.Highlights = v, s => s.Highlights);
        _sh = BindSlider(basic, "Shadows", -100, 100, "0", (s, v) => s.Shadows = v, s => s.Shadows);
        _wh = BindSlider(basic, "Whites", -100, 100, "0", (s, v) => s.Whites = v, s => s.Whites);
        _bk = BindSlider(basic, "Blacks", -100, 100, "0", (s, v) => s.Blacks = v, s => s.Blacks);
        _vib = BindSlider(basic, "Vibrance", -100, 100, "0", (s, v) => s.Vibrance = v, s => s.Vibrance);
        _sat = BindSlider(basic, "Saturation", -100, 100, "0", (s, v) => s.Saturation = v, s => s.Saturation);
        _matchGray = new IconButton("Match gray");
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
        host.AddBody(hsl);
        host.AddBody(detail);
        host.AddBody(xform);
    }

    private SliderRow BindSlider(PanelGroup group, string label, float min, float max, string fmt,
        Action<DevelopSettings, float> set, Func<DevelopSettings, float> get)
    {
        var row = new SliderRow(label, min, max, fmt);
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
        d.Settings.Straighten = _straight.Value;
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
            SetStatus("Drop a RAW or Open.");
            return;
        }

        _photo.SetDroppedPath(d.Path);
        _photo.SetLook(d.Proxy ?? d.Display, d.Settings, fast: false);
        _photo.SetDeveloped(d.Display, owns: false);
        _nav.Image = d.Thumb ?? d.Display;
        _histogram.SetBins(d.HistogramR, d.HistogramG, d.HistogramB, d.HistogramY);
        PullSliders(d);
        _matchGray.Toggled = d.Settings.MatchGray;
        _photo.CropX = d.Settings.CropX;
        _photo.CropY = d.Settings.CropY;
        _photo.CropW = d.Settings.CropW;
        _photo.CropH = d.Settings.CropH;
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
        if (!ReferenceEquals(doc, _session.Active))
            return;

        _photo.SetLook(doc.Proxy ?? doc.Display, doc.Settings, fast: false);
        _photo.SetDeveloped(doc.Display, owns: false);
        _photo.SetBefore(doc.Proxy);
        _nav.Image = doc.Thumb ?? doc.Display;
        _histogram.SetBins(doc.HistogramR, doc.HistogramG, doc.HistogramB, doc.HistogramY);
        if (!ReferenceEquals(_filmThumb, doc.Thumb))
        {
            _filmThumb = doc.Thumb;
            _film.Bind(_session.Documents, _session.ActiveIndex);
        }
        UpdateZoomLabel();
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
        _sharp.Value = s.Sharpen;
        _dnY.Value = s.DenoiseLuma;
        _dnC.Value = s.DenoiseChroma;
        _straight.Value = s.Straighten;
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
        _photo.SetLook(d.Proxy ?? d.Display, d.Settings, fast);
        _engine.RequestHistogram(d);
        if (!settle && !_photo.LookFailed)
            return;
        _engine.Invalidate(d, preview: !settle);
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

    private void Rotate(int dir)
    {
        var d = _session.Active;
        if (d == null) return;
        d.Undo.Push(d.Settings);
        d.Settings.Rotate90 = (d.Settings.Rotate90 + dir) & 3;
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
        var d = _session.Active;
        if (d == null) return;
        string name = "User " + DateTime.Now.ToString("HHmmss");
        PresetStore.Save(name, d.Settings);
        _presets.SetItems(PresetStore.Names());
        SetStatus("Saved preset " + name);
    }

    private void OpenFiles()
    {
        string[]? paths = null;
        try { paths = FileDialogs.OpenPhotos(); }
        catch (Exception ex) { Log.Warning(ex.Message); }
        if (paths == null || paths.Length == 0) return;
        OpenPaths(paths);
    }

    private void OnFilesDropped(string[] paths) => OpenPaths(paths);

    private void OpenPaths(string[] paths)
    {
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            var doc = _session.Add(path);
            _engine.Open(doc);
        }
        RefreshSession();
    }

    private void StartExport()
    {
        var d = _session.Active;
        if (d == null) { SetStatus("Nothing to export."); return; }
        _export.Open(Path.ChangeExtension(d.Name, ".jpg"));
    }

    private void OnExportConfirmed(ExportRequest req)
    {
        var d = _session.Active;
        if (d?.Display == null) return;
        string? dest = FileDialogs.SavePhoto(Path.ChangeExtension(d.Path, Ext(req.Format)));
        if (dest == null) return;
        var img = d.Display;
        Task.Run(() =>
        {
            try
            {
                Exporter.Export(img, dest, req.Format, req.Quality, req.LongEdge);
                Browser.Post(() => SetStatus("Exported " + Path.GetFileName(dest)));
            }
            catch (Exception ex)
            {
                Browser.Post(() => SetStatus("Export failed: " + ex.Message));
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

    private VisualElement Chip(string caption, float x, float y, float w, float h)
    {
        var e = new VisualElement
        {
            Name = "Chip_" + caption,
            Text = caption,
            Cursor = StandardCursor.Hand,
            Style = new ElementStyle
            {
                BackColor = Theme.Button,
                Border = new BorderStyle { Width = 1, Color = Theme.Hairline, Roundness = Theme.RadiusSm },
                Text = new TextStyle { Color = Theme.Text, Size = 12, Weight = 600, Alignment = TextAlign.Center }
            },
            Transform = new Transform(x, y, w, h) { Anchor = Anchor.Top }
        };
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
