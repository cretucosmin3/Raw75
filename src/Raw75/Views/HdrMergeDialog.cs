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
using Raw75.Pipeline;
using Silk.NET.Input;
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
/// Modal dialog for HDR exposure merge:
/// Displays thumbnail strip sorted by EV, save path, filename, alignment, deghosting options,
/// and manages background merge job with progress and cancellation.
/// </summary>
public sealed class HdrMergeDialog : VisualElement
{
    private const float CardW = 560f;
    private const float CardH = 430f;

    private readonly VisualElement _card;
    private readonly VisualElement _title;
    private readonly VisualElement _thumbStrip;
    private readonly List<VisualElement> _thumbCells = new();

    private readonly VisualElement _saveLabel;
    private readonly VisualElement _saveField;
    private readonly IconButton _btnBrowse;

    private readonly VisualElement _fileLabel;
    private readonly VisualElement _fileField;
    private readonly VisualElement _fileCaret;

    private readonly VisualElement _optionsLabel;
    private readonly IconButton _btnAlign;
    private readonly IconButton[] _deghostBtns;

    private readonly VisualElement _progressTrack;
    private readonly VisualElement _progressBar;
    private readonly VisualElement _statusLabel;

    private readonly IconButton _btnMerge;
    private readonly IconButton _btnCancel;

    private IReadOnlyList<PhotoDocument> _docs = Array.Empty<PhotoDocument>();
    private List<(PhotoDocument doc, float ev)> _sortedStack = new();
    private string _saveDir = "";
    private string _fileName = "";
    private bool _align = true;
    private HdrDeghostMode _deghost = HdrDeghostMode.Low;

    private int _caretIndex = 0;
    private bool _caretVisible = true;
    private System.Threading.Timer? _blinkTimer;

    private CancellationTokenSource? _jobCts;
    private bool _isRunning = false;

    public event Action<string>? MergeSucceeded;

    public HdrMergeDialog()
    {
        Name = "HdrMergeDialog";
        Visible = false;
        ZIndex = 1150;
        ReceivesKeyboard = true;
        Transform = new Transform(0, 0, 800, 600)
        {
            Anchor = Anchor.Left | Anchor.Right | Anchor.Top | Anchor.Bottom
        };
        Style = new ElementStyle { BackColor = new SKColor(0, 0, 0, 160) };

        _card = new VisualElement
        {
            Name = "HdrCard",
            Style = new ElementStyle
            {
                BackColor = Theme.Panel,
                Border = new BorderStyle { Width = 1, Color = Theme.Hairline, Roundness = Theme.Radius },
                Shadow = new ShadowStyle(0, 4f, 12, 12, new SKColor(0, 0, 0, 160))
            }
        };

        _title = new VisualElement
        {
            Name = "HdrTitle",
            Text = "Merge HDR Bracket",
            IsClickthrough = true,
            Style = new ElementStyle
            {
                Text = new TextStyle { Color = Theme.Text, Size = 17, Weight = 700, Alignment = TextAlign.Left }
            }
        };

        _thumbStrip = new VisualElement
        {
            Name = "HdrThumbStrip",
            Style = new ElementStyle
            {
                BackColor = Theme.Canvas,
                Border = new BorderStyle { Width = 1, Color = Theme.Hairline, Roundness = Theme.RadiusSm }
            }
        };

        _saveLabel = Label("SaveLabel", "Save to", Theme.TextDim, 12, 500);
        _saveField = new VisualElement
        {
            Name = "SaveField",
            Style = new ElementStyle
            {
                BackColor = Theme.Track,
                Border = new BorderStyle { Width = 1, Color = Theme.Hairline, Roundness = Theme.RadiusSm },
                Text = new TextStyle { Color = Theme.Text, Size = 12, Weight = 400, Alignment = TextAlign.Left, Padding = 8, Overflow = TextOverflow.Ellipsis }
            }
        };
        _btnBrowse = new IconButton("Browse…");
        _btnBrowse.Clicked += BrowseFolder;

        _fileLabel = Label("FileLabel", "Filename", Theme.TextDim, 12, 500);
        _fileField = new VisualElement
        {
            Name = "FileField",
            Cursor = StandardCursor.IBeam,
            Style = new ElementStyle
            {
                BackColor = Theme.Track,
                Border = new BorderStyle { Width = 1, Color = Theme.Accent, Roundness = Theme.RadiusSm },
                Text = new TextStyle { Color = Theme.Text, Size = 13, Weight = 500, Alignment = TextAlign.Left, Padding = 8 }
            }
        };
        _fileCaret = new VisualElement
        {
            Name = "FileCaret",
            IsClickthrough = true,
            Visible = false,
            Style = new ElementStyle { BackColor = Theme.Accent }
        };

        _optionsLabel = Label("OptionsLabel", "Options", Theme.TextDim, 12, 500);
        _btnAlign = new IconButton("Align", "check");
        _btnAlign.Toggled = true;
        _btnAlign.Clicked += () =>
        {
            _align = !_align;
            _btnAlign.Toggled = _align;
            InvalidatePaint();
        };

        _deghostBtns = new IconButton[3];
        _deghostBtns[0] = new IconButton("Ghost: Off");
        _deghostBtns[0].Clicked += () => SetDeghost(HdrDeghostMode.Off);
        _deghostBtns[1] = new IconButton("Ghost: Low");
        _deghostBtns[1].Clicked += () => SetDeghost(HdrDeghostMode.Low);
        _deghostBtns[2] = new IconButton("Ghost: High");
        _deghostBtns[2].Clicked += () => SetDeghost(HdrDeghostMode.High);
        SetDeghost(HdrDeghostMode.Low); // Default Low once tests pass

        _progressTrack = new VisualElement
        {
            Name = "ProgressTrack",
            Visible = false,
            Style = new ElementStyle
            {
                BackColor = Theme.Track,
                Border = new BorderStyle { Width = 1, Color = Theme.Hairline, Roundness = 2 }
            }
        };
        _progressBar = new VisualElement
        {
            Name = "ProgressBar",
            Style = new ElementStyle
            {
                BackColor = Theme.Accent,
                Border = new BorderStyle { Width = 0, Roundness = 2 }
            }
        };
        _progressTrack.AddChild(_progressBar);

        _statusLabel = new VisualElement
        {
            Name = "StatusLabel",
            IsClickthrough = true,
            Style = new ElementStyle
            {
                Text = new TextStyle { Color = Theme.TextDim, Size = 12, Weight = 400, Alignment = TextAlign.Left }
            }
        };

        _btnMerge = new IconButton("Merge HDR", primary: true);
        _btnMerge.Clicked += StartMerge;

        _btnCancel = new IconButton("Cancel");
        _btnCancel.Clicked += CancelOrClose;

        AddChild(_card);
        _card.AddChild(_title);
        _card.AddChild(_thumbStrip);
        _card.AddChild(_saveLabel);
        _card.AddChild(_saveField);
        _card.AddChild(_btnBrowse);
        _card.AddChild(_fileLabel);
        _card.AddChild(_fileField);
        _card.AddChild(_fileCaret);
        _card.AddChild(_optionsLabel);
        _card.AddChild(_btnAlign);
        foreach (var b in _deghostBtns) _card.AddChild(b);
        _card.AddChild(_progressTrack);
        _card.AddChild(_statusLabel);
        _card.AddChild(_btnMerge);
        _card.AddChild(_btnCancel);

        Events.OnClick += (_, e) =>
        {
            if (!_isRunning && !_card.Transform.Computed.RectF.Contains(e.Global.X, e.Global.Y))
            {
                e.Handled = true;
                Close();
            }
        };

        _fileField.Events.OnClick += (_, e) =>
        {
            if (_isRunning) return;
            float localX = e.Global.X - (_fileField.Transform.Computed.X + 8f);
            _caretIndex = Math.Clamp((int)MathF.Round(localX / 8f), 0, _fileName.Length);
            SyncFileField();
            e.Handled = true;
        };

        Events.OnKeyDown += k =>
        {
            if (!Visible) return;
            var key = (Key)k;
            if (key == Key.Escape) { CancelOrClose(); return; }
            if (key == Key.Enter || key == Key.KeypadEnter) { if (!_isRunning) StartMerge(); return; }

            if (_isRunning) return;

            if (key == Key.Backspace || (int)key == 259 || (int)key == 8 || (int)key == 127)
            {
                if (_caretIndex > 0)
                {
                    _fileName = _fileName.Remove(_caretIndex - 1, 1);
                    _caretIndex--;
                    SyncFileField();
                }
                return;
            }

            if (key == Key.Delete || (int)key == 261)
            {
                if (_caretIndex < _fileName.Length)
                {
                    _fileName = _fileName.Remove(_caretIndex, 1);
                    SyncFileField();
                }
                return;
            }

            if (key == Key.Left) { _caretIndex = Math.Max(0, _caretIndex - 1); SyncFileField(); return; }
            if (key == Key.Right) { _caretIndex = Math.Min(_fileName.Length, _caretIndex + 1); SyncFileField(); return; }
        };

        Events.OnKeyType += ch =>
        {
            if (!Visible || _isRunning) return;
            if (char.IsControl(ch) || "<>:\"/\\|?*".Contains(ch)) return;
            _fileName = _fileName.Insert(_caretIndex, ch.ToString());
            _caretIndex++;
            SyncFileField();
        };
    }

    public void Open(IReadOnlyList<PhotoDocument> docs)
    {
        _docs = docs ?? Array.Empty<PhotoDocument>();
        if (_docs.Count < 2)
            return;

        // Sort stack by EV (darkest -> brightest)
        _sortedStack.Clear();
        foreach (var d in _docs)
        {
            float ev = HdrMerge.ComputeEv(d.Metadata);
            _sortedStack.Add((d, ev));
        }
        _sortedStack.Sort((a, b) => a.ev.CompareTo(b.ev));

        // Default save directory: WorkspaceStore.Root ?? dir of first file
        string firstPath = _sortedStack[0].doc.Path;
        _saveDir = !string.IsNullOrEmpty(WorkspaceStore.Root) && Directory.Exists(WorkspaceStore.Root)
            ? WorkspaceStore.Root
            : (Path.GetDirectoryName(firstPath) ?? "");

        string firstStem = Path.GetFileNameWithoutExtension(firstPath);
        _fileName = $"{firstStem}-HDR.tif";
        _caretIndex = _fileName.Length;

        _align = true;
        _btnAlign.Toggled = true;
        SetDeghost(HdrDeghostMode.Low);

        _isRunning = false;
        _btnMerge.Caption = "Merge HDR";
        _btnMerge.Visible = true;
        _btnCancel.Caption = "Cancel";
        _progressTrack.Visible = false;
        _statusLabel.Text = $"{_sortedStack.Count} photos ready to merge";
        float w = ParentView?.Width ?? Transform.Width;
        float h = ParentView?.Height ?? Transform.Height;
        Transform.SetAbsoluteFrame(0, 0, Math.Max(1f, w), Math.Max(1f, h));
        Transform.Anchor = Anchor.Left | Anchor.Right | Anchor.Top | Anchor.Bottom;

        BuildThumbCells();
        LayoutCard();

        Visible = true;
        ParentView?.SetActiveKeyboardElement(this);

        StartBlink();
        SyncFileField();
        InvalidatePaint();
    }

    protected override void LayoutChildren()
    {
        LayoutCard();
    }

    public void Close()
    {
        Visible = false;
        StopBlink();
        if (ReferenceEquals(ParentView?.ActiveKeyboardElement, this))
            ParentView.SetActiveKeyboardElement(null);
        InvalidatePaint();
    }

    private void CancelOrClose()
    {
        if (_isRunning && _jobCts != null)
        {
            _jobCts.Cancel();
            _statusLabel.Text = "Cancelling…";
            return;
        }

        Close();
    }

    private void SetDeghost(HdrDeghostMode mode)
    {
        _deghost = mode;
        _deghostBtns[0].Toggled = mode == HdrDeghostMode.Off;
        _deghostBtns[1].Toggled = mode == HdrDeghostMode.Low;
        _deghostBtns[2].Toggled = mode == HdrDeghostMode.High;
        InvalidatePaint();
    }

    private void BrowseFolder()
    {
        if (_isRunning) return;
        string? picked = FileDialogs.OpenFolder();
        if (!string.IsNullOrEmpty(picked) && Directory.Exists(picked))
        {
            _saveDir = picked;
            _saveField.Text = _saveDir;
            InvalidatePaint();
        }
    }

    private void BuildThumbCells()
    {
        foreach (var c in _thumbCells)
            _thumbStrip.RemoveChild(c);
        _thumbCells.Clear();

        float medianEv = _sortedStack.Count > 0 ? _sortedStack[_sortedStack.Count / 2].ev : 0f;

        for (int i = 0; i < _sortedStack.Count; i++)
        {
            var (doc, ev) = _sortedStack[i];
            var cell = new VisualElement
            {
                Name = $"HdrThumb_{i}",
                IsClickthrough = true,
                Style = new ElementStyle
                {
                    BackColor = Theme.Section,
                    Border = new BorderStyle { Width = 1, Color = Theme.Hairline, Roundness = Theme.RadiusSm }
                }
            };

            SKImage? img = doc.Thumb ?? doc.Preview ?? doc.Look;
            var thumbWell = new HdrThumbWell(img)
            {
                Name = $"HdrThumbWell_{i}",
                IsClickthrough = true
            };
            cell.AddChild(thumbWell);

            float relEv = ev - medianEv;
            string evText = relEv >= 0f ? $"+{relEv:0.0} EV" : $"{relEv:0.0} EV";
            if (MathF.Abs(relEv) < 0.05f) evText = "0.0 EV (Ref)";

            var badge = new VisualElement
            {
                Name = "EvBadge",
                IsClickthrough = true,
                Text = evText,
                Style = new ElementStyle
                {
                    BackColor = new SKColor(0, 0, 0, 180),
                    Border = new BorderStyle { Width = 0, Roundness = 3 },
                    Text = new TextStyle { Color = SKColors.White, Size = 9f, Weight = 600, Alignment = TextAlign.Center, Padding = 2 }
                }
            };
            cell.AddChild(badge);

            _thumbCells.Add(cell);
            _thumbStrip.AddChild(cell);
        }
    }

    private void LayoutCard()
    {
        float w = ParentView?.Width ?? Transform.Width;
        float h = ParentView?.Height ?? Transform.Height;

        float cardX = MathF.Round((w - CardW) * 0.5f);
        float cardY = MathF.Round((h - CardH) * 0.5f);
        _card.Transform.SetAbsoluteFrame(cardX, cardY, CardW, CardH);

        float pad = 20f;
        float curY = pad;

        _title.Transform.SetAbsoluteFrame(cardX + pad, cardY + curY, CardW - pad * 2, 24);
        curY += 32;

        // Thumb strip
        float stripH = 76f;
        _thumbStrip.Transform.SetAbsoluteFrame(cardX + pad, cardY + curY, CardW - pad * 2, stripH);

        int count = _thumbCells.Count;
        if (count > 0)
        {
            float gap = 6f;
            float availableW = CardW - pad * 2 - 8f;
            float cellW = MathF.Floor((availableW - gap * (count - 1)) / count);
            cellW = Math.Clamp(cellW, 40f, 80f);
            float cellH = stripH - 8f;

            for (int i = 0; i < count; i++)
            {
                float cx = cardX + pad + 4f + i * (cellW + gap);
                float cy = cardY + curY + 4f;
                _thumbCells[i].Transform.SetAbsoluteFrame(cx, cy, cellW, cellH);

                if (_thumbCells[i].Children.Count > 0)
                {
                    _thumbCells[i].Children[0].Transform.SetAbsoluteFrame(cx, cy, cellW, cellH);
                }
                if (_thumbCells[i].Children.Count > 1)
                {
                    _thumbCells[i].Children[1].Transform.SetAbsoluteFrame(cx + 2, cy + cellH - 16, cellW - 4, 14);
                }
            }
        }
        curY += stripH + 14;

        // Save to
        _saveLabel.Transform.SetAbsoluteFrame(cardX + pad, cardY + curY, CardW - pad * 2, 16);
        curY += 20;

        float browseW = 84f;
        _saveField.Text = _saveDir;
        _saveField.Transform.SetAbsoluteFrame(cardX + pad, cardY + curY, CardW - pad * 2 - browseW - 8, 30);
        _btnBrowse.Transform.SetAbsoluteFrame(cardX + CardW - pad - browseW, cardY + curY, browseW, 30);
        curY += 38;

        // Filename
        _fileLabel.Transform.SetAbsoluteFrame(cardX + pad, cardY + curY, CardW - pad * 2, 16);
        curY += 20;

        _fileField.Transform.SetAbsoluteFrame(cardX + pad, cardY + curY, CardW - pad * 2, 30);
        curY += 38;

        // Options
        _optionsLabel.Transform.SetAbsoluteFrame(cardX + pad, cardY + curY, CardW - pad * 2, 16);
        curY += 20;

        float optW = 100f;
        _btnAlign.Transform.SetAbsoluteFrame(cardX + pad, cardY + curY, optW, 30);
        for (int i = 0; i < _deghostBtns.Length; i++)
        {
            _deghostBtns[i].Transform.SetAbsoluteFrame(cardX + pad + optW + 8 + i * 94, cardY + curY, 88, 30);
        }
        curY += 40;

        // Progress bar
        _progressTrack.Transform.SetAbsoluteFrame(cardX + pad, cardY + curY, CardW - pad * 2, 4);
        _progressBar.Transform.SetAbsoluteFrame(cardX + pad, cardY + curY, 0, 4);

        // Bottom action row: Status on left, Cancel and Merge on right
        float btnW = 104f;
        float btnH = 34f;
        float bottomY = cardY + CardH - pad - btnH;
        _statusLabel.Transform.SetAbsoluteFrame(cardX + pad, bottomY + 5f, CardW - pad * 2 - (btnW * 2 + 16f), 24);
        _btnCancel.Transform.SetAbsoluteFrame(cardX + CardW - pad - btnW * 2 - 10f, bottomY, btnW, btnH);
        _btnMerge.Transform.SetAbsoluteFrame(cardX + CardW - pad - btnW, bottomY, btnW, btnH);
    }

    private void SyncFileField()
    {
        _fileField.Text = _fileName;
        float cardX = _card.Transform.Computed.X;
        float cardY = _card.Transform.Computed.Y;
        float fx = _fileField.Transform.Computed.X;
        float fy = _fileField.Transform.Computed.Y;
        float textW = _caretIndex * 7.5f;
        _fileCaret.Transform.SetAbsoluteFrame(fx + 8f + textW, fy + 6f, 1.5f, 18f);
        InvalidatePaint();
    }

    private void StartBlink()
    {
        StopBlink();
        _caretVisible = true;
        _fileCaret.Visible = true;
        _blinkTimer = new System.Threading.Timer(_ =>
        {
            _caretVisible = !_caretVisible;
            Browser.Post(() =>
            {
                if (Visible && !_isRunning)
                {
                    _fileCaret.Visible = _caretVisible;
                    InvalidatePaint();
                }
            });
        }, null, 500, 500);
    }

    private void StopBlink()
    {
        _blinkTimer?.Dispose();
        _blinkTimer = null;
        _fileCaret.Visible = false;
    }

    private void StartMerge()
    {
        if (_isRunning || _sortedStack.Count < 2)
            return;

        string outDir = _saveDir.Trim();
        if (string.IsNullOrEmpty(outDir))
            outDir = Directory.GetCurrentDirectory();

        string outName = _fileName.Trim();
        if (string.IsNullOrEmpty(outName))
            outName = "HDR-Merge.tif";
        if (!outName.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) &&
            !outName.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase))
        {
            outName += ".tif";
        }

        string fullOutPath = Path.Combine(outDir, outName);

        _isRunning = true;
        _jobCts = new CancellationTokenSource();
        var ct = _jobCts.Token;

        _btnMerge.Visible = false;
        _btnCancel.Caption = "Cancel";
        _progressTrack.Visible = true;
        _fileCaret.Visible = false;

        var stackCopy = new List<(PhotoDocument doc, float ev)>(_sortedStack);
        bool doAlign = _align;
        HdrDeghostMode deghostMode = _deghost;

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

                Browser.Post(() =>
                {
                    _isRunning = false;
                    Close();
                    MergeSucceeded?.Invoke(fullOutPath);
                });
            }
            catch (OperationCanceledException)
            {
                Browser.Post(() =>
                {
                    _isRunning = false;
                    _btnMerge.Visible = true;
                    _progressTrack.Visible = false;
                    _statusLabel.Text = "Merge cancelled";
                    InvalidatePaint();
                });
            }
            catch (Exception ex)
            {
                Log.Error("HDR Merge failed: " + ex);
                Browser.Post(() =>
                {
                    _isRunning = false;
                    _btnMerge.Visible = true;
                    _progressTrack.Visible = false;
                    _statusLabel.Text = "Error: " + ex.Message;
                    InvalidatePaint();
                });
            }
        });
    }

    private void UpdateStatus(string message, float progressFraction)
    {
        Browser.Post(() =>
        {
            if (!Visible) return;
            _statusLabel.Text = message;
            float totalW = CardW - 40f;
            float w = Math.Clamp(progressFraction * totalW, 0f, totalW);
            _progressBar.Transform.SetAbsoluteFrame(
                _progressTrack.Transform.Computed.X,
                _progressTrack.Transform.Computed.Y,
                w,
                _progressTrack.Transform.Computed.Height);
            InvalidatePaint();
        });
    }

    private static VisualElement Label(string name, string text, SKColor color, float size, int weight)
    {
        return new VisualElement
        {
            Name = name,
            Text = text,
            IsClickthrough = true,
            Style = new ElementStyle
            {
                Text = new TextStyle { Color = color, Size = size, Weight = weight, Alignment = TextAlign.Left }
            }
        };
    }

    private sealed class HdrThumbWell : VisualElement
    {
        private static readonly SKPaint SmoothPaint = new()
        {
            FilterQuality = SKFilterQuality.Medium,
            IsAntialias = true
        };

        private SKImage? _image;

        public HdrThumbWell(SKImage? image)
        {
            _image = image;
            Style = new ElementStyle
            {
                BackColor = SKColors.Transparent,
                Border = new BorderStyle { Width = 0 }
            };
            Overflow = OverflowMode.Clip;
        }

        public void SetImage(SKImage? img)
        {
            _image = img;
            InvalidatePaint();
        }

        protected override void OnAfterStyleDraw(List<DrawCommand> cmds)
        {
            if (_image == null || _image.Handle == IntPtr.Zero) return;
            float w = Transform.Computed.Width;
            float h = Transform.Computed.Height;
            var dest = Contain(w, h, _image.Width, _image.Height);
            cmds.Add(new DrawSkImageCommand(_image, dest, SmoothPaint));
        }

        private static SKRect Contain(float boxW, float boxH, int imgW, int imgH)
        {
            if (imgW <= 0 || imgH <= 0) return new SKRect(0, 0, boxW, boxH);
            float scale = Math.Min(boxW / imgW, boxH / imgH);
            float dw = imgW * scale;
            float dh = imgH * scale;
            float x = (boxW - dw) * 0.5f;
            float y = (boxH - dh) * 0.5f;
            return new SKRect(x, y, x + dw, y + dh);
        }
    }
}
