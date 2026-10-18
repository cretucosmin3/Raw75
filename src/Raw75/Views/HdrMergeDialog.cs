using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Blossom;
using Blossom.Core;
using Blossom.Core.Input;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Blossom.Desktop;
using Blossom.Hosting;
using Blossom.Primitives;
using Blossom.Reactive;
using Raw75.Controls;
using Raw75.Develop;
using Raw75.Imaging;
using Raw75.Io;
using Raw75.Pipeline;
using SkiaSharp;

namespace Raw75.Views;

public sealed class HdrMergeRequest
{
    public IReadOnlyList<PhotoDocument> Documents { get; init; } = Array.Empty<PhotoDocument>();
    public string OutputDirectory { get; init; } = "";
    public string OutputFileName { get; init; } = "";
    public bool Align { get; init; } = true;
    public HdrDeghostMode Deghost { get; init; } = HdrDeghostMode.Low;
}

/// <summary>
/// Modal dialog for HDR exposure merge: bracket strip sorted by EV, save folder, file name,
/// alignment and deghosting options; runs the merge in the background with progress and cancel.
/// </summary>
public sealed class HdrMergeDialog : ModalDialog
{
    private const float StripH = 84f;
    private const float StripPad = 6f;
    private const float CellGap = 6f;
    private const float CardW = 580f;

    private static readonly HdrDeghostMode[] DeghostModes = { HdrDeghostMode.Off, HdrDeghostMode.Low, HdrDeghostMode.High };

    private readonly RichBox _subtitle;
    private readonly Stack _strip;
    private readonly TextField _fileField;
    private readonly IconButton _browse;
    private readonly CheckToggle _align;
    private readonly SegmentedControl _deghostSeg;
    private readonly Stack _footer;
    private readonly IconButton _merge;

    private readonly Signal<string> _saveDir = new("");
    private readonly Signal<HdrDeghostMode> _deghost = new(HdrDeghostMode.Low);
    private readonly Signal<bool> _running = new(false);
    private readonly Signal<float> _progress = new(0f);
    private readonly Signal<(string Text, bool Error)> _status = new(("", false));

    private List<(PhotoDocument doc, float ev)> _sortedStack = new();
    private CancellationTokenSource? _jobCts;

    public event Action<string>? MergeSucceeded;

    public HdrMergeDialog() : base("HdrMergeDialog", zIndex: 1150, cardWidth: CardW)
    {
        _strip = new Stack
        {
            Name = "HdrThumbStrip",
            Orientation = Orientation.Horizontal,
            Gap = CellGap,
            Padding = new Thickness(StripPad),
            Overflow = OverflowMode.Clip,
            Style = new ElementStyle { Border = new BorderStyle { Width = 1 } }
        };
        _strip.MinHeight = StripH;
        _strip.Bind(s =>
        {
            s.Style.BackColor = Theme.Well;
            s.Style.Border.Color = Theme.HairlineSubtle;
            s.Style.Border.Roundness = Theme.RadiusSm;
        });

        var pathWell = new RichBox
        {
            Name = "HdrSavePath",
            IsClickthrough = true,
            Padding = new Thickness(10f, 0f, 10f, 0f),
            Style = new ElementStyle
            {
                Border = new BorderStyle { Width = 1 },
                Text = new TextStyle { Size = 12f, Weight = 400, Alignment = TextAlign.Left, Padding = 0, Overflow = TextOverflow.Ellipsis, MaxLines = 1 }
            }
        };
        pathWell.MinHeight = pathWell.MaxHeight = TextField.DefaultHeight;
        pathWell.BindText(() => _saveDir.Value);
        pathWell.Bind(p =>
        {
            p.Style.BackColor = Theme.Well;
            p.Style.Border.Color = Theme.Hairline;
            p.Style.Border.Roundness = Theme.RadiusSm;
            p.Style.Text.Color = Theme.TextSecondary;
        });
        _browse = new IconButton("Browse…", "folder");
        _browse.Clicked += BrowseFolder;
        var saveRow = new Stack { Name = "HdrSaveRow", Orientation = Orientation.Horizontal, Align = LayoutAlign.Center, Gap = 8f };
        saveRow.AddChild(pathWell);
        saveRow.AddChild(_browse);
        Stack.SetGrow(pathWell, 1f);

        _fileField = new TextField("File name") { CharFilter = IsFileNameChar };
        _fileField.Submitted += StartMerge;
        _fileField.Escaped += CancelOrClose;

        _align = new CheckToggle(initialChecked: true);
        _align.Transform.Width = 16f;
        _align.Transform.Height = 16f;
        var alignLabel = Label("Align frames before merging", TextRole.Body);
        alignLabel.IsClickthrough = false;
        alignLabel.Cursor = UiCursor.Hand;
        alignLabel.Events.OnClick += (_, e) =>
        {
            if (e.Button != PointerButton.Left || _running.Peek()) return;
            _align.Toggle();
            e.Handled = true;
        };
        var alignRow = new Stack { Name = "HdrAlignRow", Orientation = Orientation.Horizontal, Align = LayoutAlign.Center, Gap = 8f };
        alignRow.MinHeight = SegmentedControl.DefaultHeight;
        alignRow.AddChild(_align);
        alignRow.AddChild(alignLabel);
        Stack.SetGrow(alignLabel, 1f);

        _deghostSeg = Segmented(_deghost, DeghostModes, "Off", "Low", "High");

        var options = new Stack { Name = "HdrOptions", Orientation = Orientation.Horizontal, Gap = 16f };
        var alignField = Field("Alignment", alignRow);
        var ghostField = Field("Ghost removal", _deghostSeg);
        options.AddChild(alignField);
        options.AddChild(ghostField);
        Stack.SetGrow(alignField, 1f);
        Stack.SetGrow(ghostField, 1f);

        var status = Label("", TextRole.Dim);
        status.Bind(s =>
        {
            var (text, error) = _status.Value;
            s.Text = text;
            s.Style.Text.Color = error ? Theme.RedChannel : Theme.TextDim;
        });
        var cancel = new IconButton("Cancel");
        cancel.Clicked += CancelOrClose;
        _merge = new IconButton("Merge HDR", primary: true);
        _merge.Clicked += StartMerge;
        _footer = Footer(status, cancel, _merge);

        var body = Body();
        body.AddChild(_strip);
        body.AddChild(Field("Save to", saveRow));
        body.AddChild(Field("File name", _fileField));
        body.AddChild(options);

        Card.AddChild(Header("Merge HDR", "", out _subtitle));
        Card.AddChild(Separator());
        Card.AddChild(body);
        Card.AddChild(new ProgressLine(_progress, _running));
        Card.AddChild(_footer);

        // While a merge runs the options freeze and only Cancel stays live.
        this.Bind(_ =>
        {
            bool idle = !_running.Value;
            _merge.Visible = idle;
            _fileField.Enabled = idle;
            _browse.Enabled = idle;
            _align.Enabled = idle;
            alignRow.Opacity = idle ? 1f : 0.45f;
            _deghostSeg.Interactive = idle;
            _deghostSeg.Opacity = idle ? 1f : 0.45f;
            _footer.InvalidateLayout();
        });
    }

    protected override bool DismissOnScrim => !_running.Peek();

    public void Open(IReadOnlyList<PhotoDocument> docs)
    {
        docs ??= Array.Empty<PhotoDocument>();
        if (docs.Count < 2)
            return;

        // Sort stack by EV (darkest -> brightest)
        _sortedStack.Clear();
        foreach (var d in docs)
            _sortedStack.Add((d, HdrMerge.ComputeEv(d.Metadata)));
        _sortedStack.Sort((a, b) => a.ev.CompareTo(b.ev));

        // Default save directory: WorkspaceStore.Root ?? dir of first file
        string firstPath = _sortedStack[0].doc.Path;
        string saveDir = !string.IsNullOrEmpty(WorkspaceStore.Root) && Directory.Exists(WorkspaceStore.Root)
            ? WorkspaceStore.Root
            : (Path.GetDirectoryName(firstPath) ?? "");

        _subtitle.Text = $"{_sortedStack.Count} exposures, darkest to brightest. The median frame (highlighted) is the reference.";
        _fileField.Text = $"{Path.GetFileNameWithoutExtension(firstPath)}-HDR.tif";
        _align.IsChecked = true;

        ReactiveEngine.Batch(() =>
        {
            _saveDir.Value = saveDir;
            _deghost.Value = HdrDeghostMode.Low;
            _running.Value = false;
            _progress.Value = 0f;
            _status.Value = ($"{_sortedStack.Count} photos ready to merge", false);
        });

        BuildThumbCells();
        ShowModal(_fileField);

        // Select the stem so typing replaces the name and keeps the extension.
        _fileField.MoveCaret(0);
        _fileField.MoveCaret(Path.GetFileNameWithoutExtension(_fileField.Text).Length, extendSelection: true);
    }

    protected override bool HandleKey(UiKey key)
    {
        switch (key)
        {
            case UiKey.Escape:
                CancelOrClose();
                return true;
            case UiKey.Enter or UiKey.KeypadEnter:
                StartMerge();
                return true;
            default:
                return false;
        }
    }

    private void CancelOrClose()
    {
        if (_running.Peek() && _jobCts != null)
        {
            _jobCts.Cancel();
            _status.Value = ("Cancelling…", false);
            return;
        }

        Close();
    }

    private void BrowseFolder()
    {
        if (_running.Peek()) return;
        string? picked = FileDialogs.OpenFolder();
        if (!string.IsNullOrEmpty(picked) && Directory.Exists(picked))
            _saveDir.Value = picked;
    }

    private void BuildThumbCells()
    {
        foreach (var old in new List<VisualElement>(_strip.Children))
        {
            _strip.RemoveChild(old);
            old.Dispose();
        }

        int n = _sortedStack.Count;
        float inner = CardW - CardPad * 2f - StripPad * 2f - 2f;
        float cellW = Math.Clamp(MathF.Floor((inner - CellGap * (n - 1)) / n), 40f, 112f);
        float medianEv = _sortedStack[n / 2].ev;

        foreach (var (doc, ev) in _sortedStack)
        {
            float relEv = ev - medianEv;
            bool reference = MathF.Abs(relEv) < 0.05f;
            string evText = reference ? "0.0 EV" : relEv > 0f ? $"+{relEv:0.0} EV" : $"{relEv:0.0} EV";
            var cell = new BracketCell(doc.Thumb ?? doc.Preview ?? doc.Look, evText, reference);
            cell.Transform.Width = cellW;
            _strip.AddChild(cell);
        }
        _strip.InvalidateLayout();
    }

    private void StartMerge()
    {
        if (_running.Peek() || _sortedStack.Count < 2)
            return;

        string outDir = _saveDir.Peek().Trim();
        if (string.IsNullOrEmpty(outDir))
            outDir = Directory.GetCurrentDirectory();

        string outName = _fileField.Text.Trim();
        if (string.IsNullOrEmpty(outName))
            outName = "HDR-Merge.tif";
        if (!outName.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) &&
            !outName.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase))
        {
            outName += ".tif";
        }

        string fullOutPath = Path.Combine(outDir, outName);

        _jobCts = new CancellationTokenSource();
        var ct = _jobCts.Token;

        ReactiveEngine.Batch(() =>
        {
            _running.Value = true;
            _progress.Value = 0f;
        });
        // The file field just went disabled; keep Escape (cancel) routed to the dialog.
        ParentView?.SetActiveKeyboardElement(this);

        var stackCopy = new List<(PhotoDocument doc, float ev)>(_sortedStack);
        bool doAlign = _align.IsChecked;
        HdrDeghostMode deghostMode = _deghost.Peek();

        Task.Run(() =>
        {
            try
            {
                // 1. Decode hi-res frames
                int n = stackCopy.Count;
                var frames = new List<RasterBuffer>(n);
                var evs = new List<float>(n);
                var paths = new List<string>(n);

                for (int i = 0; i < n; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var doc = stackCopy[i].doc;
                    float ev = stackCopy[i].ev;

                    UpdateStatus($"Decoding {i + 1}/{n}: {doc.Name}…", (float)i / (n * 3));

                    RasterBuffer buf = RawDecoder.DecodeRasterFull(doc.Path)
                        ?? RawDecoder.DecodeFull(doc.Path);

                    if (!buf.HasPixels)
                        throw new InvalidOperationException($"Could not decode {doc.Name}");

                    if (frames.Count > 0 && (buf.Width != frames[0].Width || buf.Height != frames[0].Height))
                    {
                        throw new InvalidOperationException(
                            $"Size mismatch: {doc.Name} is {buf.Width}×{buf.Height} but stack is {frames[0].Width}×{frames[0].Height}");
                    }

                    frames.Add(buf);
                    evs.Add(ev);
                    paths.Add(doc.Path);
                }

                // Reference frame is median EV
                int refIndex = HdrMerge.FindMedianEvIndex(evs);
                var refDoc = stackCopy[refIndex].doc;

                // 2. Alignment
                if (doAlign && n > 1)
                {
                    for (int i = 0; i < n; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (i == refIndex)
                            continue;

                        UpdateStatus($"Aligning {i + 1}/{n}: {stackCopy[i].doc.Name}…", 0.33f + ((float)i / (n * 3)));

                        var alignResult = HdrAlign.Align(frames[i], frames[refIndex], evs[i], evs[refIndex]);
                        if (!alignResult.IsWeak && !alignResult.Transform.IsIdentity())
                        {
                            frames[i] = HdrAlign.Warp(frames[i], alignResult.Transform);
                        }
                    }
                }

                // 3. Deghost + Merge
                UpdateStatus("Merging exposure tiles…", 0.66f);

                Func<int, int, int, int, float[]?>? deghostProvider = null;
                if (deghostMode != HdrDeghostMode.Off)
                {
                    deghostProvider = (yStart, tileH, w, totalH) =>
                        HdrDeghost.ComputeTileMask(frames, evs, refIndex, deghostMode, yStart, tileH, w, totalH);
                }

                var mergedRaster = HdrMerge.Merge(
                    frames, evs, refIndex,
                    tileRows: 512,
                    ct: ct,
                    progress: p => UpdateStatus($"Merging exposure tiles {p * 100:0}%…", 0.66f + p * 0.25f),
                    deghostMaskProvider: deghostProvider);

                // 4. Write TIFF
                UpdateStatus("Writing 32-bit linear TIFF…", 0.95f);

                PhotoMetadata metaCopy = refDoc.Metadata != null
                    ? new PhotoMetadata
                    {
                        CameraMake = refDoc.Metadata.CameraMake,
                        CameraModel = refDoc.Metadata.CameraModel,
                        LensModel = refDoc.Metadata.LensModel,
                        FocalLength = refDoc.Metadata.FocalLength,
                        FocalLengthIn35mm = refDoc.Metadata.FocalLengthIn35mm,
                        Aperture = refDoc.Metadata.Aperture,
                        ShutterSpeed = refDoc.Metadata.ShutterSpeed,
                        Iso = refDoc.Metadata.Iso,
                        CaptureTime = refDoc.Metadata.CaptureTime,
                        GpsLatitude = refDoc.Metadata.GpsLatitude,
                        GpsLongitude = refDoc.Metadata.GpsLongitude,
                        GpsAltitude = refDoc.Metadata.GpsAltitude,
                        Width = mergedRaster.Width,
                        Height = mergedRaster.Height
                    }
                    : new PhotoMetadata { Width = mergedRaster.Width, Height = mergedRaster.Height };

                HdrTiff.Write(fullOutPath, mergedRaster, metaCopy, paths, evs);

                Shell.Post(() =>
                {
                    _running.Value = false;
                    Close();
                    MergeSucceeded?.Invoke(fullOutPath);
                });
            }
            catch (OperationCanceledException)
            {
                Shell.Post(() => FinishWithStatus("Merge cancelled", error: false));
            }
            catch (Exception ex)
            {
                Log.Error("HDR Merge failed: " + ex);
                Shell.Post(() => FinishWithStatus("Error: " + ex.Message, error: true));
            }
        });
    }

    private void FinishWithStatus(string message, bool error)
    {
        ReactiveEngine.Batch(() =>
        {
            _running.Value = false;
            _status.Value = (message, error);
        });
        if (Visible)
            ParentView?.SetActiveKeyboardElement(_fileField);
    }

    private void UpdateStatus(string message, float progressFraction)
    {
        Shell.Post(() =>
        {
            if (!Visible) return;
            ReactiveEngine.Batch(() =>
            {
                _status.Value = (message, false);
                _progress.Value = Math.Clamp(progressFraction, 0f, 1f);
            });
        });
    }

    /// <summary>One bracket frame: thumbnail fitted in a dark well with its EV offset; the reference frame is lit.</summary>
    private sealed class BracketCell : Control
    {
        private readonly SKImage? _image;
        private readonly string _evText;
        private readonly bool _reference;

        public BracketCell(SKImage? image, string evText, bool reference)
        {
            _image = image;
            _evText = evText;
            _reference = reference;
            Name = "HdrBracketCell";
            IsClickthrough = true;
            Style.BackColor = SKColors.Transparent;
            this.Bind(_ => { Theme.Observe(); InvalidatePaint(); });
        }

        protected override void Paint(SKCanvas c)
        {
            float r = Theme.RadiusSm;
            var box = new SKRect(0.5f, 0.5f, W - 0.5f, H - 0.5f);

            using (var well = new SKPaint { Color = Theme.PhotoWell, IsAntialias = true })
                c.DrawRoundRect(box, r, r, well);

            if (_image != null && _image.Handle != IntPtr.Zero && _image.Width > 0 && _image.Height > 0)
            {
                float scale = Math.Min(W / _image.Width, H / _image.Height);
                float dw = _image.Width * scale, dh = _image.Height * scale;
                var dest = SKRect.Create((W - dw) * 0.5f, (H - dh) * 0.5f, dw, dh);
                c.Save();
                c.ClipRoundRect(new SKRoundRect(box, r), antialias: true);
                c.DrawImage(_image, dest, Sampling.Smooth);
                c.Restore();
            }

            using (var edge = new SKPaint
            {
                Color = _reference ? Theme.Accent : Theme.Hairline,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = _reference ? 1.5f : 1f,
                IsAntialias = true
            })
            {
                c.DrawRoundRect(box, r, r, edge);
            }

            using var text = new TextPaint
            {
                TextSize = 9.5f,
                Typeface = Theme.GetTypeface(600),
                Color = _reference ? Theme.Accent : Theme.Text
            };
            float tw = text.MeasureText(_evText);
            var pill = SKRect.Create((W - tw) * 0.5f - 5f, H - 19f, tw + 10f, 15f);
            using (var bg = new SKPaint { Color = Theme.Canvas.WithAlpha(210), IsAntialias = true })
                c.DrawRoundRect(pill, 7.5f, 7.5f, bg);
            var m = text.FontMetrics;
            c.DrawText(_evText, pill.Left + 5f, pill.MidY - (m.Ascent + m.Descent) * 0.5f, text);
        }
    }

    /// <summary>Hairline between body and footer that turns into an accent progress bar while merging.</summary>
}
