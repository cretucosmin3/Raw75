using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Blossom;
using Blossom.Animations;
using Blossom.Core;
using Blossom.Core.Input;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Raw75.Develop;
using Raw75.Imaging;
using Raw75.Pipeline;
using SkiaSharp;
using Blossom.Hosting;
using Blossom.Desktop;
using Blossom.Reactive;
using Raw75.Controls;

namespace Raw75.Views;

public enum ZoomMode
{
    Fit,
    Fill,
    OneToOne
}

/// <summary>
/// Photo surface: GPU image blit + smooth zoom/pan and crop overlay.
/// Decode/develop stay out of this type.
/// </summary>
public sealed class PhotoPane : VisualElement
{
    private const float DragSlop = 4f;
    private const float MinZoom = 0.02f;
    private const float MaxZoom = 32f;

    private SKImage? _developed;
    private SKImage? _before;
    private SKImage? _source;
    private SKImage? _tile;
    private float _tileX, _tileY, _tileW, _tileH;
    private int _nativeW, _nativeH;
    private DevelopSettings? _settings;
    private readonly DevelopLook _look = new();
    private readonly DevelopLook _tileLook = new();
    private readonly DevelopLook _beforeLook = new();
    private readonly DevelopLook _tileBeforeLook = new();
    private bool _lookFast;

    // Histogram sampled from the GPU look itself: small and throttled while dragging, finer once settled.
    private const int HistogramFastEdge = 256;
    private const int HistogramFineEdge = 768;
    private static readonly long HistogramInterval = System.Diagnostics.Stopwatch.Frequency / 20;
    private readonly HistogramSampler _histogramSampler = new();
    private bool _histogramDirty = true;
    private long _histogramLast;
    private IDisposable? _histogramRetry;
    private SKImage? _histogramSource;

    /// <summary>Raised (posted after the paint) with R, G, B, luma bins of the displayed look.</summary>
    public event Action<float[], float[], float[], float[]>? HistogramSampled;

    /// <summary>Asks for a fresh GPU histogram on the next paint.</summary>
    public void RequestHistogram()
    {
        _histogramDirty = true;
        InvalidatePaint();
    }

    private void SampleHistogram(SKRect dest, bool fast, bool applyCrop, int frameW, int frameH)
    {
        // Read the sample submitted on an earlier frame: the GPU has finished it, so no stall.
        if (_histogramSampler.HasPending)
        {
            var sampled = _histogramSource;
            Half[]? pixels = _histogramSampler.TryRead();
            if (pixels != null && ReferenceEquals(sampled, _source))
            {
                Task.Run(() =>
                {
                    var bins = HistogramSampler.Bin(pixels);
                    Shell.Post(() =>
                    {
                        // Dropped if another photo was bound meanwhile.
                        if (ReferenceEquals(sampled, _source))
                            HistogramSampled?.Invoke(bins.R, bins.G, bins.B, bins.Y);
                    });
                });
            }
        }

        if (!_histogramDirty || _source == null || _settings == null || dest.Height < 1f)
            return;

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (fast && now - _histogramLast < HistogramInterval)
        {
            // Throttled: come back for it once the interval has passed.
            var wait = TimeSpan.FromSeconds((HistogramInterval - (now - _histogramLast)) / (double)System.Diagnostics.Stopwatch.Frequency);
            ScheduleHistogramFrame(wait);
            return;
        }

        if (!_histogramSampler.Render(_look, _source, _settings, dest.Width / dest.Height, fast, applyCrop,
                frameW, frameH, fast ? HistogramFastEdge : HistogramFineEdge))
            return;

        _histogramDirty = false;
        _histogramLast = now;
        _histogramSource = _source;
        // Guarantee a later paint to read it, even when nothing else changes (e.g. after release).
        ScheduleHistogramFrame(TimeSpan.FromMilliseconds(16));
    }

    private void ScheduleHistogramFrame(TimeSpan wait)
    {
        _histogramRetry ??= Shell.PostDelayed(wait, () =>
        {
            _histogramRetry = null;
            InvalidatePaint();
        });
    }

    private void InvalidateAllLooks()
    {
        _look.InvalidateAll();
        _tileLook.InvalidateAll();
        _beforeLook.InvalidateAll();
        _tileBeforeLook.InvalidateAll();
    }

    private void InvalidateSpotLooks()
    {
        _look.InvalidateSpots();
        _tileLook.InvalidateSpots();
        _beforeLook.InvalidateSpots();
        _tileBeforeLook.InvalidateSpots();
    }

    private DevelopSettings GetBaselineSettings()
    {
        var b = new DevelopSettings();
        if (_settings != null)
        {
            b.Straighten = _settings.Straighten;
            b.Rotate90 = _settings.Rotate90;
            b.FlipH = _settings.FlipH;
            b.FlipV = _settings.FlipV;
            b.CropX = _settings.CropX;
            b.CropY = _settings.CropY;
            b.CropW = _settings.CropW;
            b.CropH = _settings.CropH;
            b.ToneMode = _settings.ToneMode;
            b.AtmosphereR = _settings.AtmosphereR;
            b.AtmosphereG = _settings.AtmosphereG;
            b.AtmosphereB = _settings.AtmosphereB;
            b.AtmosphereDepthMax = _settings.AtmosphereDepthMax;
            b.Exposure = _settings.BaseExposure;
            b.BaseExposure = _settings.BaseExposure;
        }
        return b;
    }
    private bool _showBefore;

    // Before/after crossfade: 0 = edited look, 1 = baseline. A full swap takes BeforeFadeSeconds.
    private const float BeforeFadeSeconds = 0.5f;
    private readonly AnimatedValue _beforeFade;
    private SKRect _drawDest;
    private SKRect _drawClip;
    private bool _ownsDisplay = true;
    private int _viewW;
    private int _viewH;
    private bool _busy;
    private bool _splitDrag;
    private string? _droppedPath;
    private bool _showingPhoto;

    private bool _freeZoom;
    private float _lastScrollZoom = 1.0f;
    private bool _pointerDown;
    private bool _didDrag;
    private Vector2 _downLocal;
    private Vector2 _pointerLocal;
    private float _downPanX;
    private float _downPanY;
    private readonly CropSession _crop = new();

    public string? DroppedPath => _droppedPath;

    /// <summary>Named zoom. Wheel zoom is free (<see cref="Zoom"/>) until <see cref="SetZoomMode"/> or a click-toggle.</summary>
    public ZoomMode ZoomMode { get; private set; } = ZoomMode.Fit;

    /// <summary>True if the user is in free-form (wheel / custom) zoom.</summary>
    public bool IsFreeZoom => _freeZoom;

    /// <summary>Last zoom ratio reached via wheel scroll or 1:1, used for click-to-zoom toggling.</summary>
    public float LastScrollZoom => _lastScrollZoom;

    /// <summary>Screen pixels per image pixel. 1 = 1:1.</summary>
    public float Zoom { get; private set; } = 1f;

    /// <summary>Image-space point shown at the viewport center.</summary>
    public float PanX { get; private set; }

    public float PanY { get; private set; }

    /// <summary>Last image destination in local coordinates (contain/cover/1:1+pan).</summary>
    public SKRect ImageDest { get; private set; }

    public event Action? ViewChanged;
    public event Action? ViewSettled;

    private bool _isInteracting;
    private CancellationTokenSource? _wheelSettleCts;

    public bool LookFailed => _look.Failed;

    internal void DescribeMemory(System.Text.StringBuilder sb)
    {
        sb.AppendLine("PhotoPane");
        sb.AppendLine($"  _source     {MemSize.ImageLabel(_source)}");
        sb.AppendLine($"  _developed  {MemSize.ImageLabel(_developed)}  owns={_ownsDisplay}");
        sb.AppendLine($"  _before     {MemSize.ImageLabel(_before)}");
        sb.AppendLine($"  view { _viewW}x{_viewH}  zoom={Zoom:0.###}  mode={ZoomMode}");
        sb.AppendLine($"  lookFailed={_look.Failed}  showBefore={_showBefore}  split={_splitBefore}");
    }

    public event Action<bool>? ShowBeforeChanged;
    public event Action<bool>? SplitBeforeChanged;

    public bool ShowBefore
    {
        get => _showBefore;
        set
        {
            if (_showBefore == value) return;
            _showBefore = value;
            if (_showBefore && _splitBefore)
            {
                _splitBefore = false;
                SplitBeforeChanged?.Invoke(false);
            }
            _beforeFade.Target = _showBefore ? 1f : 0f;
            _beforeLook.InvalidateAll();
            _tileBeforeLook.InvalidateAll();
            InvalidatePaint();
            ShowBeforeChanged?.Invoke(_showBefore);
        }
    }

    public bool Busy
    {
        get => _busy;
        set
        {
            if (_busy == value) return;
            _busy = value;
            InvalidatePaint();
        }
    }

    public void SetBefore(SKImage? image)
    {
        _before = image;
        if (_splitBefore)
            InvalidatePaint();
    }

    private bool _cropTool;
    private float _savedCropX, _savedCropY, _savedCropW = 1f, _savedCropH = 1f;
    private float _savedStraighten;
    private float _savedAspect;
    private string _savedRatioId = "free";
    private float _rotateDragStart;
    private float _rotatePivotX, _rotatePivotY;
    private int _savedRotate90;
    private readonly List<(SKRect Rect, string Id)> _cropHits = new();
    private string? _cropHoverId;

    public bool CropTool => _cropTool;

    private bool _spotTool;
    private int _selectedSpot = -1;
    private int _hoverSpot = -1;
    private SpotPart _hoverPart = SpotPart.None;
    private SpotPart _dragPart = SpotPart.None;
    private float _dragDx, _dragDy;
    private bool _pendingSpotClick;

    private enum SpotPart { None, Dest, Src }

    public bool SpotTool => _spotTool;
    public int SelectedSpotIndex => _selectedSpot;

    public event Action? SpotsChanged;
    public event Action? SpotEditBegan;
    public event Action? SpotEditEnded;
    public event Action? SpotSelectionChanged;

    public event Action? CropCommitted;
    public event Action? CropCancelled;
    public event Action<int>? Rotate90Clicked;

    private bool _splitBefore;
    public bool SplitBefore
    {
        get => _splitBefore;
        set
        {
            if (_splitBefore == value) return;
            _splitBefore = value;
            if (_splitBefore && _showBefore)
            {
                _showBefore = false;
                _beforeFade.Snap(0f);
                ShowBeforeChanged?.Invoke(false);
            }
            _beforeLook.InvalidateAll();
            _tileBeforeLook.InvalidateAll();
            InvalidatePaint();
            SplitBeforeChanged?.Invoke(_splitBefore);
        }
    }

    private float _splitT = 0.5f;
    public float SplitT
    {
        get => _splitT;
        set
        {
            float t = Math.Clamp(value, 0f, 1f);
            if (Math.Abs(_splitT - t) < 1e-6f) return;
            _splitT = t;
            InvalidatePaint();
        }
    }

    public event Action<bool>? ClippingChanged;
    public event Action<bool>? InfoOverlayChanged;

    private bool _showClipping;
    public bool ShowClipping
    {
        get => _showClipping;
        set
        {
            if (_showClipping == value) return;
            _showClipping = value;
            InvalidateAllLooks();
            InvalidatePaint();
            ClippingChanged?.Invoke(_showClipping);
        }
    }

    public void ToggleClipping() => ShowClipping = !ShowClipping;

    private bool _showInfoOverlay;
    public bool ShowInfoOverlay
    {
        get => _showInfoOverlay;
        set
        {
            if (_showInfoOverlay == value) return;
            _showInfoOverlay = value;
            if (!_showInfoOverlay)
            {
                _infoCardRect = default;
                _infoCloseRect = default;
                _infoCloseHover = false;
            }
            InvalidatePaint();
            InfoOverlayChanged?.Invoke(_showInfoOverlay);
        }
    }

    public void ToggleInfoOverlay() => ShowInfoOverlay = !ShowInfoOverlay;

    private Develop.PhotoMetadata? _metadata;
    public Develop.PhotoMetadata? Metadata
    {
        get => _metadata;
        set
        {
            _metadata = value;
            InvalidatePaint();
        }
    }

    private SKRect _infoCardRect;
    private SKRect _infoCloseRect;
    private bool _infoCloseHover;

    public float CropX
    {
        get => _crop.X;
        set { if (SetCrop(_crop.X, value, v => _crop.X = v)) RaiseCropChanged(); }
    }

    public float CropY
    {
        get => _crop.Y;
        set { if (SetCrop(_crop.Y, value, v => _crop.Y = v)) RaiseCropChanged(); }
    }

    public float CropW
    {
        get => _crop.W;
        set { if (SetCrop(_crop.W, value, v => _crop.W = v)) RaiseCropChanged(); }
    }

    public float CropH
    {
        get => _crop.H;
        set { if (SetCrop(_crop.H, value, v => _crop.H = v)) RaiseCropChanged(); }
    }

    public event Action? CropChanged;

    private float _straightenPreview;
    public float StraightenPreview
    {
        get => _straightenPreview;
        set
        {
            if (Math.Abs(_straightenPreview - value) < 1e-4f) return;
            _straightenPreview = value;
            if (_settings != null)
                _settings.Straighten = value;
            if (_cropTool)
            {
                GetFrameSize(out int fw, out int fh);
                _crop.ClampInside(
                    _settings?.Rotate90 ?? 0, _straightenPreview,
                    _settings?.FlipH ?? false, _settings?.FlipV ?? false,
                    fw, fh);
                CropChanged?.Invoke();
            }
            InvalidateAllLooks();
            InvalidatePaint();
        }
    }

    public PhotoPane()
    {
        Name = "PhotoPane";
        Overflow = OverflowMode.Clip;
        Style = new ElementStyle
        {
            BackColor = Theme.PhotoWell
        };

        Events.OnMouseDown += OnPointerDown;
        Events.OnMouseMove += OnPointerMove;
        Events.OnMouseUp += OnPointerUp;
        Events.OnMouseHover += OnHover;
        Events.OnMouseLeave += OnLeave;
        Events.OnScroll += OnWheel;

        _beforeFade = new AnimatedValue(this, 0f, BeforeFadeSeconds, Ease.InOutSine);
        _beforeFade.Changed += _ => InvalidatePaint();

        this.Bind(_ =>
        {
            Style.BackColor = Theme.PhotoWell;
            InvalidatePaint();
        });
    }

    public void SetDroppedPath(string path)
    {
        _droppedPath = path;
        InvalidatePaint();
    }

    /// <summary>
    /// Undeveloped proxy + current settings. The pane shades this on the GPU while sliders move.
    /// </summary>
    public void SetLook(SKImage? source, DevelopSettings? settings, bool fast)
    {
        _source = source;
        _settings = settings;
        _lookFast = fast;
        _histogramDirty = true;
        InvalidateAllLooks();
        if (source != null && source.Handle != IntPtr.Zero)
            SyncViewToSource();
        _showingPhoto = source != null || _developed != null;
        InvalidatePaint();
    }

    /// <summary>Fit + crop from the document before the first paint of a newly selected photo.</summary>
    public void PrepareBind(DevelopSettings? settings)
    {
        _freeZoom = false;
        ZoomMode = ZoomMode.Fit;
        _crop.EndDrag();
        ApplyCropFromSettings(settings);
    }

    public void ApplyCropFromSettings(DevelopSettings? s)
    {
        if (s == null)
            return;
        _crop.X = s.CropX;
        _crop.Y = s.CropY;
        _crop.W = s.CropW > 0.001f ? s.CropW : 1f;
        _crop.H = s.CropH > 0.001f ? s.CropH : 1f;
        _crop.Clamp();
        _straightenPreview = s.Straighten;
    }

    public void RefreshGeometry() => SyncViewToSource(force: true);

    public void SetNativeSize(int width, int height)
    {
        if (width == _nativeW && height == _nativeH)
            return;
        _nativeW = Math.Max(0, width);
        _nativeH = Math.Max(0, height);
        InvalidatePaint();
    }

    public void SetViewportTile(SKImage? image, float x, float y, float w, float h)
    {
        _tile = image;
        _tileX = x;
        _tileY = y;
        _tileW = w;
        _tileH = h;
        InvalidateAllLooks();
        InvalidatePaint();
    }

    public void ClearViewportTile()
    {
        if (_tile == null && _tileW <= 0)
            return;
        _tile = null;
        _tileW = _tileH = 0;
        InvalidateAllLooks();
        InvalidatePaint();
    }

    /// <summary>True when the proxy would be magnified on screen (need native pixels for the visible crop).</summary>
    public bool TryGetViewportRequest(out SKRect sourceAabb, out int screenW, out int screenH)
    {
        sourceAabb = SKRect.Empty;
        screenW = 1;
        screenH = 1;
        if (_cropTool || _source == null || _source.Handle == IntPtr.Zero)
            return false;

        float w = Transform.Computed.Width;
        float h = Transform.Computed.Height;
        var dest = ImageDest.Width > 0.5f ? ImageDest : ComputeDest(w, h);
        var pane = new SKRect(0, 0, w, h);
        var vis = dest;
        vis.Intersect(pane);
        if (vis.Width < 2f || vis.Height < 2f)
            return false;

        int rot = _settings != null ? _settings.Rotate90 & 3 : 0;
        int texX = (rot & 1) != 0 ? _source.Height : _source.Width;
        if (dest.Width <= texX * 1.02f)
            return false;

        GetFrameSize(out int fw, out int fh);
        sourceAabb = DevelopGeom.VisibleSourceAabb(dest, vis, _settings, fw, fh);
        if (sourceAabb.Width < 1e-5f || sourceAabb.Height < 1e-5f)
            return false;
        SKRect visAabb = sourceAabb;
        if (_settings != null && SpotHeal.ExpandAabbForSpots(_settings, ref sourceAabb, fw, fh))
        {
            float sx = sourceAabb.Width / Math.Max(1e-6f, visAabb.Width);
            float sy = sourceAabb.Height / Math.Max(1e-6f, visAabb.Height);
            screenW = Math.Max(1, (int)MathF.Ceiling(vis.Width * sx));
            screenH = Math.Max(1, (int)MathF.Ceiling(vis.Height * sy));
            const int maxEdge = 4096;
            if (screenW > maxEdge || screenH > maxEdge)
            {
                float k = maxEdge / (float)Math.Max(screenW, screenH);
                screenW = Math.Max(1, (int)(screenW * k));
                screenH = Math.Max(1, (int)(screenH * k));
            }
        }
        else
        {
            screenW = Math.Max(1, (int)MathF.Ceiling(vis.Width));
            screenH = Math.Max(1, (int)MathF.Ceiling(vis.Height));
        }
        return true;
    }

    /// <summary>Show an image. When <paramref name="owns"/> is false the caller keeps ownership.</summary>
    /// <param name="live">Slider-drag preview: swap pixels only, keep zoom/layout on the last settled size.</param>
    public void SetDeveloped(SKImage? image, bool owns = true, bool live = false)
    {
        if (ReferenceEquals(_developed, image))
            return;

        int oldW = ViewW;
        int oldH = ViewH;
        if (_ownsDisplay)
            _developed?.Dispose();
        _developed = image;
        _ownsDisplay = owns;
        _showingPhoto = image != null;

        if (image == null)
        {
            _source = null;
            _settings = null;
            _viewW = 0;
            _viewH = 0;
            _crop.ResetFull();
            _freeZoom = false;
            ZoomMode = ZoomMode.Fit;
            SyncViewport();
            ViewChanged?.Invoke();
            InvalidatePaint();
            return;
        }

        if (_source != null || (live && _viewW > 0 && _viewH > 0))
        {
            InvalidatePaint();
            return;
        }

        int nw = image.Width;
        int nh = image.Height;
        if (oldW > 0 && oldH > 0 && (nw != oldW || nh != oldH))
        {
            bool swapped = oldW > oldH != nw > nh
                && Math.Abs(nw * oldW - nh * oldH) / (float)Math.Max(1, oldW * oldH) < 0.15f;
            if (swapped)
            {
                _freeZoom = false;
                ZoomMode = ZoomMode.Fit;
            }
            else
            {
                float sx = nw / (float)oldW;
                PanX *= sx;
                PanY *= nh / (float)oldH;
                Zoom /= Math.Max(sx, 1e-6f);
                Zoom = Math.Clamp(Zoom, MinZoom, MaxZoom);
                _freeZoom = true;
            }
            _viewW = nw;
            _viewH = nh;
            SyncViewport();
            ViewChanged?.Invoke();
        }
        else if (oldW == 0)
        {
            _viewW = nw;
            _viewH = nh;
            _freeZoom = false;
            ZoomMode = ZoomMode.Fit;
            SyncViewport();
            ViewChanged?.Invoke();
        }
        else
        {
            _viewW = nw;
            _viewH = nh;
        }

        InvalidatePaint();
    }

    public void SetZoomMode(ZoomMode m)
    {
        ZoomMode = m;
        _freeZoom = false;
        if (m == ZoomMode.OneToOne)
            _lastScrollZoom = 1.0f;
        if ((m == ZoomMode.Fit || m == ZoomMode.Fill) && ViewW > 0)
        {
            PanX = ViewW * 0.5f;
            PanY = ViewH * 0.5f;
        }
        SyncViewport();
        InvalidatePaint();
        ViewChanged?.Invoke();
        ViewSettled?.Invoke();
    }

    protected override void OnAfterStyleDraw(List<DrawCommand> cmds)
    {
        if (!Visible || !EffectiveVisible || ComputedVisibility == Visibility.Hidden)
            return;

        float w = Transform.Computed.Width;
        float h = Transform.Computed.Height;
        if (w <= 1 || h <= 1)
            return;

        SyncViewport(w, h);

        SKImage? blit = _developed;
        if (blit == null || blit.Handle == IntPtr.Zero)
            blit = _source;

        bool gpuLook = !_look.Failed
            && _source != null
            && _source.Handle != IntPtr.Zero
            && _settings != null;

        if (gpuLook || (blit != null && blit.Handle != IntPtr.Zero))
        {
            GetContainSize(out int iw, out int ih);
            var dest = ImageDest;
            if (iw > 0 && ih > 0)
            {
                if (!_freeZoom && ZoomMode == ZoomMode.Fit)
                    dest = Contain(w, h, iw, ih);
                else if (dest.Width < 1f || dest.Height < 1f)
                    dest = Contain(w, h, iw, ih);
            }

            var pane = new SKRect(0, 0, w, h);
            if (dest.IntersectsWith(pane) && dest.Width > 0.5f && dest.Height > 0.5f)
            {
                var vis = dest;
                vis.Intersect(pane);
                _drawDest = dest;
                _drawClip = vis;

                if (gpuLook)
                {
                    cmds.Add(new DrawCallbackCommand(DrawGpuLook));
                }
                else if (blit != null)
                {
                    cmds.Add(new DrawSkImageCommand(blit, vis, SourceOf(blit, dest, vis)));
                }
            }
        }

        cmds.Add(new DrawCallbackCommand(DrawOverlay));
    }

    private void DrawGpuLook(SKCanvas canvas)
    {
        if (!Visible || !EffectiveVisible || ComputedVisibility == Visibility.Hidden)
            return;


        if (_source == null || _settings == null || _source.Handle == IntPtr.Zero)
            return;

        var dest = _drawDest;
        var pane = new SKRect(0, 0, Transform.Computed.Width, Transform.Computed.Height);
        bool applyCrop = !_cropTool && _settings.HasCrop;

        bool isFast = _lookFast;
        GetFrameSize(out int fwHist, out int fhHist);
        SampleHistogram(dest, isFast, applyCrop, fwHist, fhHist);

        if (_splitBefore)
        {
            float splitX = Transform.Computed.Width * _splitT;
            var left = dest;
            left.Intersect(pane);
            left.Right = Math.Min(left.Right, splitX);
            var right = dest;
            right.Intersect(pane);
            right.Left = Math.Max(right.Left, splitX);

            DevelopSettings baseSettings = GetBaselineSettings();

            if (left.Width > 0.5f)
            {
                GetFrameSize(out int fwB, out int fhB);
                if (!_beforeLook.Draw(canvas, dest, left, _source, baseSettings, isFast, applyCrop,
                        0f, 0f, 1f, 1f, float.NaN, float.NaN, float.NaN, float.NaN, fwB, fhB))
                    canvas.DrawImage(_source, SourceOf(_source, dest, left), left, Sampling.Linear, null);
            }

            if (right.Width > 0.5f)
            {
                GetFrameSize(out int fwA, out int fhA);
                if (!_look.Draw(canvas, dest, right, _source, _settings, isFast, applyCrop,
                        0f, 0f, 1f, 1f, float.NaN, float.NaN, float.NaN, float.NaN, fwA, fhA, showClipping: _showClipping))
                    canvas.DrawImage(_source, SourceOf(_source, dest, right), right, Sampling.Linear, null);
            }

            if (!_cropTool && _tile != null && _tile.Handle != IntPtr.Zero && _tileW > 1e-5f && _tileH > 1e-5f)
            {
                GetFrameSize(out int fw, out int fh);
                var tileSrc = new SKRect(_tileX, _tileY, _tileX + _tileW, _tileY + _tileH);
                var tileDest = DevelopGeom.SourceAabbToDest(tileSrc, dest, _settings, fw, fh);
                tileDest.Inflate(-1f, -1f);

                var tileLeft = tileDest;
                tileLeft.Intersect(left);
                if (tileLeft.Width >= 1f && tileLeft.Height >= 1f)
                {
                    _tileBeforeLook.Draw(canvas, dest, tileLeft, _tile, baseSettings, isFast, applyCrop,
                        _tileX, _tileY, _tileW, _tileH, float.NaN, float.NaN, float.NaN, float.NaN, fw, fh);
                }

                var tileRight = tileDest;
                tileRight.Intersect(right);
                if (tileRight.Width >= 1f && tileRight.Height >= 1f)
                {
                    _tileLook.Draw(canvas, dest, tileRight, _tile, _settings, isFast, applyCrop,
                        _tileX, _tileY, _tileW, _tileH, float.NaN, float.NaN, float.NaN, float.NaN, fw, fh, showClipping: _showClipping);
                }
            }

            return;
        }

        var vis = _drawClip;
        GetFrameSize(out int fwSingle, out int fhSingle);

        float before = _beforeFade.Value;
        if (before >= 0.999f)
        {
            DrawBeforeLook(canvas, dest, vis, isFast, applyCrop, fwSingle, fhSingle);
        }
        else
        {
            DrawAfterLook(canvas, dest, vis, isFast, applyCrop, fwSingle, fhSingle);
            if (before > 0.001f)
            {
                // Mid-fade: the baseline on top at the fade's opacity.
                using var alphaPaint = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Clamp((int)(before * 255f), 0, 255)) };
                canvas.SaveLayer(vis, alphaPaint);
                DrawBeforeLook(canvas, dest, vis, isFast, applyCrop, fwSingle, fhSingle);
                canvas.Restore();
            }
        }
    }

    private void DrawBeforeLook(SKCanvas canvas, SKRect dest, SKRect vis, bool isFast, bool applyCrop, int fw, int fh)
    {
        var baseSettings = GetBaselineSettings();
        if (!_beforeLook.Draw(canvas, dest, vis, _source!, baseSettings, isFast, applyCrop,
                0f, 0f, 1f, 1f, float.NaN, float.NaN, float.NaN, float.NaN, fw, fh))
            canvas.DrawImage(_source!, SourceOf(_source!, dest, vis), vis, Sampling.Linear, null);

        DrawTileLook(_tileBeforeLook, canvas, dest, vis, _tile, baseSettings, isFast, applyCrop, fw, fh, showClipping: false);
    }

    private void DrawAfterLook(SKCanvas canvas, SKRect dest, SKRect vis, bool isFast, bool applyCrop, int fw, int fh)
    {
        if (!_look.Draw(canvas, dest, vis, _source!, _settings!, isFast, applyCrop,
                0f, 0f, 1f, 1f, float.NaN, float.NaN, float.NaN, float.NaN, fw, fh, showClipping: _showClipping))
            canvas.DrawImage(_source!, SourceOf(_source!, dest, vis), vis, Sampling.Linear, null);

        DrawTileLook(_tileLook, canvas, dest, vis, _tile, _settings!, isFast, applyCrop, fw, fh, _showClipping);
    }

    private void DrawTileLook(
        DevelopLook look, SKCanvas canvas, SKRect dest, SKRect vis, SKImage? tile,
        DevelopSettings settings, bool isFast, bool applyCrop, int fw, int fh, bool showClipping)
    {
        if (_cropTool)
            return;
        if (tile == null || tile.Handle == IntPtr.Zero || _tileW < 1e-5f || _tileH < 1e-5f)
            return;

        var tileSrc = new SKRect(_tileX, _tileY, _tileX + _tileW, _tileY + _tileH);
        var tileDest = DevelopGeom.SourceAabbToDest(tileSrc, dest, settings, fw, fh);
        tileDest.Intersect(vis);
        if (tileDest.Width < 1f || tileDest.Height < 1f)
            return;

        // Inset so bilinear tile-edge clamp never paints a smear over the proxy.
        tileDest.Inflate(-1f, -1f);
        if (tileDest.Width < 1f || tileDest.Height < 1f)
            return;

        look.Draw(canvas, dest, tileDest, tile, settings, isFast, applyCrop,
            _tileX, _tileY, _tileW, _tileH, float.NaN, float.NaN, float.NaN, float.NaN, fw, fh, showClipping: showClipping);
    }

    private void DrawOverlay(SKCanvas canvas)
    {
        if (!Visible || !EffectiveVisible || ComputedVisibility == Visibility.Hidden)
            return;
        float w = Transform.Computed.Width;
        float h = Transform.Computed.Height;
        var pane = new SKRect(0, 0, w, h);

        if (_cropTool && HasPhoto)
        {
            _crop.Draw(canvas, ImageDest, pane, _straightenPreview);
            DrawCropBar(canvas, pane);
        }

        if (_spotTool && HasPhoto)
            DrawSpots(canvas, pane);

        if (_splitBefore)
        {
            float x = w * _splitT;

            // Dark under-stroke keeps the wipe readable over bright highlights.
            using (var under = new SKPaint { Color = new SKColor(0, 0, 0, 140), IsAntialias = true, StrokeWidth = 3f, Style = SKPaintStyle.Stroke })
                canvas.DrawLine(x, 0, x, h, under);
            using (var wipe = new SKPaint { Color = Theme.Handle.WithAlpha(230), IsAntialias = true, StrokeWidth = 1.5f, Style = SKPaintStyle.Stroke })
                canvas.DrawLine(x, 0, x, h, wipe);

            // Grab handle on the divider: a glass pill with chevrons.
            float midY = h * 0.5f;
            var pill = new SKRoundRect(new SKRect(x - 11, midY - 18, x + 11, midY + 18), 11, 11);
            DrawGlass(canvas, pill, Theme.HairlineStrong);

            using var arrowPaint = new SKPaint
            {
                Color = Theme.Text,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.6f,
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round
            };
            using var leftPath = new SKPath();
            leftPath.MoveTo(x - 2.5f, midY - 4f);
            leftPath.LineTo(x - 6f, midY);
            leftPath.LineTo(x - 2.5f, midY + 4f);
            canvas.DrawPath(leftPath, arrowPaint);

            using var rightPath = new SKPath();
            rightPath.MoveTo(x + 2.5f, midY - 4f);
            rightPath.LineTo(x + 6f, midY);
            rightPath.LineTo(x + 2.5f, midY + 4f);
            canvas.DrawPath(rightPath, arrowPaint);

            DrawSplitBadges(canvas, x, w);
        }
        else if (_beforeFade.Value > 0.01f)
        {
            DrawBeforeBadge(canvas, w, _beforeFade.Value);
        }

        if (_showInfoOverlay && HasPhoto && _metadata != null)
        {
            DrawInfoOverlay(canvas, pane);
        }

        if (_busy)
        {
            DrawBusy(canvas, w, h);
            InvalidatePaint();
        }

        using (var frame = new SKPaint { Color = Theme.HairlineSubtle, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 })
            canvas.DrawRoundRect(new SKRect(8.5f, 8.5f, w - 8.5f, h - 8.5f), Theme.RadiusSm, Theme.RadiusSm, frame);

        if (_showingPhoto)
            return;

        string line = _droppedPath ?? "Drop a RAW here";

        using var text = new TextPaint
        {
            Color = Theme.TextDim,
            HintingLevel = SKFontHinting.Normal,
            TextSize = 12.5f,
            Typeface = Theme.GetTypeface(500)
        };
        canvas.DrawText(line, 20, h - 20, text);
    }

    /// <summary>Translucent chrome over the photo: soft shadow, Window-toned glass, hairline edge, top lip.</summary>
    private static void DrawGlass(SKCanvas canvas, SKRoundRect shape, SKColor edge, byte alpha = 214)
    {
        if (Theme.UseShadows)
        {
            using var blur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 6f);
            using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, (byte)Math.Min(255, Theme.ShadowAlpha + 30)), MaskFilter = blur, IsAntialias = true };
            var drop = new SKRoundRect(shape);
            drop.Offset(0, Theme.ShadowY + 1f);
            canvas.DrawRoundRect(drop, shadow);
        }

        using (var fill = new SKPaint { Color = Theme.Window.WithAlpha(alpha), IsAntialias = true })
            canvas.DrawRoundRect(shape, fill);

        var r = shape.Rect;
        float rad = shape.Radii[0].X;
        using (var lip = new SKPaint { Color = SKColors.White.WithAlpha((byte)(16 * alpha / 214)), StrokeWidth = 1f, IsAntialias = true })
            canvas.DrawLine(r.Left + rad, r.Top + 1.5f, r.Right - rad, r.Top + 1.5f, lip);

        using var stroke = new SKPaint { Color = edge, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f };
        var inner = new SKRoundRect(shape);
        inner.Inflate(-0.5f, -0.5f);
        canvas.DrawRoundRect(inner, stroke);
    }

    private static TextPaint BadgeText(SKColor color) => new()
    {
        Color = color,
        Typeface = Theme.GetTypeface(600),
        HintingLevel = SKFontHinting.Normal,
        TextSize = 10.5f,
        TextAlign = SKTextAlign.Center
    };

    private static void DrawBadge(SKCanvas canvas, float cx, string label, SKColor textColor, SKColor edge, byte alpha = 214)
    {
        using var text = BadgeText(textColor);
        float bw = Math.Max(64f, text.MeasureText(label) + 26f);
        var rect = new SKRoundRect(new SKRect(cx - bw * 0.5f, 18, cx + bw * 0.5f, 40), Theme.RadiusSm, Theme.RadiusSm);
        DrawGlass(canvas, rect, edge, alpha);
        var m = text.FontMetrics;
        canvas.DrawText(label, cx, rect.Rect.MidY - (m.Ascent + m.Descent) * 0.5f, text);
    }

    private static void DrawSplitBadges(SKCanvas canvas, float splitX, float w)
    {
        if (splitX > 75)
            DrawBadge(canvas, Math.Max(40, splitX - 50), "BEFORE", Theme.TextSecondary, Theme.HairlineStrong);
        if (w - splitX > 75)
            DrawBadge(canvas, Math.Min(w - 40, splitX + 50), "AFTER", Theme.Accent, Theme.HairlineStrong);
    }

    private static void DrawBeforeBadge(SKCanvas canvas, float w, float alpha = 1f)
    {
        byte a = (byte)Math.Clamp((int)(255 * alpha), 0, 255);
        byte bgA = (byte)Math.Clamp((int)(214 * alpha), 0, 255);
        DrawBadge(canvas, w * 0.5f, "BEFORE", Theme.Accent.WithAlpha(a), Theme.Accent.WithAlpha((byte)(a * 0.7f)), bgA);
    }

    private void DrawInfoOverlay(SKCanvas canvas, SKRect pane)
    {
        if (_metadata == null)
        {
            if (!string.IsNullOrEmpty(_droppedPath) && System.IO.File.Exists(_droppedPath))
            {
                try { _metadata = RawDecoder.ReadMetadata(_droppedPath); } catch { }
            }
        }
        if (_metadata == null) return;

        if (_metadata.Width <= 0 && _nativeW > 0)
        {
            _metadata.Width = _nativeW;
            _metadata.Height = _nativeH;
        }
        if (_metadata.FileSizeBytes <= 0 && !string.IsNullOrEmpty(_droppedPath) && System.IO.File.Exists(_droppedPath))
        {
            try { _metadata.FileSizeBytes = new System.IO.FileInfo(_droppedPath).Length; } catch { }
        }

        var rows = new List<(string Label, string Value, SKColor Color)>(10);

        string? filename = !string.IsNullOrEmpty(_droppedPath) ? System.IO.Path.GetFileName(_droppedPath) : null;
        if (!string.IsNullOrEmpty(filename))
            rows.Add(("File", filename, Theme.Text));

        if (!string.IsNullOrEmpty(_metadata.CameraName))
            rows.Add(("Camera", _metadata.CameraName, Theme.Text));

        if (!string.IsNullOrEmpty(_metadata.LensName) && _metadata.LensName != "Unknown Lens")
            rows.Add(("Lens", _metadata.LensName, Theme.TextSecondary));

        if (_metadata.ShutterSpeed > 0.000001f)
            rows.Add(("Shutter", _metadata.FormattedShutter, Theme.Accent));

        if (_metadata.Aperture > 0.01f)
            rows.Add(("Aperture", _metadata.FormattedAperture, Theme.Accent));

        if (_metadata.Iso > 0.1f)
            rows.Add(("ISO", _metadata.FormattedIso, Theme.Accent));

        if (_metadata.FocalLength > 0.1f)
            rows.Add(("Focal Length", _metadata.FormattedFocal, Theme.Accent));

        if (_metadata.Width > 0 && _metadata.Height > 0)
            rows.Add(("Dimensions", _metadata.FormattedDimensions, Theme.Text));

        if (_metadata.FileSizeBytes > 0)
            rows.Add(("File Size", _metadata.FormattedFileSize, Theme.Text));

        if (_metadata.CaptureTime.HasValue)
            rows.Add(("Date & Time", _metadata.FormattedDateTime, Theme.TextDim));

        if (_metadata.HasGps)
            rows.Add(("Location", _metadata.FormattedLocation, Theme.TextSecondary));

        if (rows.Count == 0) return;

        using var labelPaint = new TextPaint
        {
            Typeface = Theme.GetTypeface(500),
            TextSize = 11.5f,
            Color = Theme.TextDim
        };

        using var valuePaint = new TextPaint
        {
            Typeface = Theme.GetTypeface(600),
            TextSize = 12.5f
        };

        float maxLabelW = 0f;
        float maxValueW = 0f;
        foreach (var r in rows)
        {
            float lw = labelPaint.MeasureText(r.Label);
            if (lw > maxLabelW) maxLabelW = lw;
            float vw = valuePaint.MeasureText(r.Value);
            if (vw > maxValueW) maxValueW = vw;
        }

        float padX = 14f;
        float gap = 16f;
        float rowH = 22f;
        float headerH = 36f;
        float bottomPad = 10f;

        float cardW = Math.Clamp(padX + maxLabelW + gap + maxValueW + padX, 300f, 480f);
        float cardH = headerH + (rows.Count * rowH) + bottomPad;
        float cardX = _cropTool ? (pane.Left + 8f + 84f + 12f) : (pane.Left + 24f);
        float cardY = pane.Top + 24f;
        _infoCardRect = new SKRect(cardX, cardY, cardX + cardW, cardY + cardH);

        DrawGlass(canvas, new SKRoundRect(_infoCardRect, Theme.Radius, Theme.Radius), Theme.HairlineStrong, alpha: 222);

        // Header: uppercase section-style title.
        using var title = new TextPaint
        {
            Typeface = Theme.GetTypeface(600),
            TextSize = 10.5f,
            Color = Theme.TextSecondary
        };
        var tm = title.FontMetrics;
        float headMid = cardY + headerH * 0.5f;
        canvas.DrawText("PHOTO INFO", cardX + padX, headMid - (tm.Ascent + tm.Descent) * 0.5f, title);

        // Close button: vector "✕" (lines drawn manually so no font/tofu glyph dependency)
        float closeW = 22f;
        float closeH = 22f;
        float closeX = cardX + cardW - padX + 6f - closeW;
        float closeY = headMid - closeH * 0.5f;
        _infoCloseRect = new SKRect(closeX, closeY, closeX + closeW, closeY + closeH);

        if (_infoCloseHover)
        {
            using var closeHoverBg = new SKPaint { Color = Theme.Hover, IsAntialias = true };
            canvas.DrawRoundRect(new SKRoundRect(_infoCloseRect, Theme.RadiusSm, Theme.RadiusSm), closeHoverBg);
        }

        using var xPaint = new SKPaint
        {
            Color = _infoCloseHover ? Theme.Text : Theme.TextDim,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            StrokeCap = SKStrokeCap.Round
        };
        float cx = _infoCloseRect.MidX;
        float cy = _infoCloseRect.MidY;
        float arm = 4f;
        canvas.DrawLine(cx - arm, cy - arm, cx + arm, cy + arm, xPaint);
        canvas.DrawLine(cx + arm, cy - arm, cx - arm, cy + arm, xPaint);

        using var divider = new SKPaint { Color = Theme.Hairline, StrokeWidth = 1f };
        float divY = MathF.Round(cardY + headerH) - 0.5f;
        canvas.DrawLine(cardX + 1f, divY, cardX + cardW - 1f, divY, divider);

        // Rows: dim labels, values in a left-aligned column; long values ellipsize.
        var vm = valuePaint.FontMetrics;
        float rowMid = divY + 4f + rowH * 0.5f;
        float valueX = cardX + padX + maxLabelW + gap;
        float maxValueAvailW = cardX + cardW - padX - valueX;

        foreach (var r in rows)
        {
            float baseline = rowMid - (vm.Ascent + vm.Descent) * 0.5f;
            canvas.DrawText(r.Label, cardX + padX, baseline, labelPaint);

            valuePaint.Color = r.Color;
            string valStr = r.Value;
            if (valuePaint.MeasureText(valStr) > maxValueAvailW && valStr.Length > 8)
            {
                while (valStr.Length > 4 && valuePaint.MeasureText(valStr + "…") > maxValueAvailW)
                {
                    valStr = valStr[..^1];
                }
                valStr += "…";
            }
            canvas.DrawText(valStr, valueX, baseline, valuePaint);

            rowMid += rowH;
        }
    }

    private void OnPointerDown(object sender, MouseEventArgs e)
    {
        if (e.Button != 0 || !HasPhoto)
            return;

        if (_showInfoOverlay)
        {
            if (_infoCloseRect.Contains(e.Relative.X, e.Relative.Y))
            {
                ShowInfoOverlay = false;
                e.Handled = true;
                return;
            }
            if (_infoCardRect.Contains(e.Relative.X, e.Relative.Y))
            {
                // Clicking inside the info panel absorbs the event so it doesn't dismiss the panel or drag/zoom the photo
                e.Handled = true;
                return;
            }
        }

        _pointerDown = true;
        _didDrag = false;
        _downLocal = e.Relative;
        _downPanX = PanX;
        _downPanY = PanY;
        CapturePointer();
        _wheelSettleCts?.Cancel();
        _wheelSettleCts?.Dispose();
        _wheelSettleCts = null;
        e.Handled = true;

        if (_splitBefore)
        {
            float splitX = Transform.Computed.Width * _splitT;
            if (Math.Abs(e.Relative.X - splitX) < 16f)
            {
                _splitDrag = true;
                return;
            }
        }

        if (_spotTool)
        {
            if (OnSpotPointerDown(e.Relative.X, e.Relative.Y))
                return;
            _pendingSpotClick = true;
            return;
        }

        if (_cropTool)
        {
            if (HitCropBar(e.Relative.X, e.Relative.Y))
                return;
            var handle = _crop.Hit(ImageDest, e.Relative.X, e.Relative.Y);
            if (handle != CropHandle.None)
            {
                if (handle == CropHandle.Rotate)
                {
                    var box = _crop.DestOnImage(ImageDest);
                    _rotateDragStart = StraightenPreview;
                    _rotatePivotX = box.MidX;
                    _rotatePivotY = box.MidY;
                }
                _crop.BeginDrag(handle, e.Relative.X, e.Relative.Y);
                ApplyCropCursor(e.Relative.X, e.Relative.Y);
            }
        }
    }

    private void OnPointerMove(object sender, MouseEventArgs e)
    {
        _pointerLocal = e.Relative;
        if (!_pointerDown)
            return;

        float dx = e.Relative.X - _downLocal.X;
        float dy = e.Relative.Y - _downLocal.Y;
        if (!_didDrag && dx * dx + dy * dy > DragSlop * DragSlop)
            _didDrag = true;

        if (_splitDrag)
        {
            float w = Math.Max(1f, Transform.Computed.Width);
            SplitT = e.Relative.X / w;
            e.Handled = true;
            return;
        }

        if (_spotTool && _dragPart != SpotPart.None)
        {
            OnSpotPointerMove(e.Relative.X, e.Relative.Y);
            e.Handled = true;
            return;
        }

        if (_pendingSpotClick && _didDrag)
            _pendingSpotClick = false;

        if (!_didDrag)
            return;

        if (_cropTool && _crop.Active != CropHandle.None)
        {
            GetFrameSize(out int fw, out int fh);
            int rot = _settings?.Rotate90 ?? 0;
            bool flipH = _settings?.FlipH ?? false;
            bool flipV = _settings?.FlipV ?? false;

            if (_crop.Active == CropHandle.Rotate)
            {
                float a0 = MathF.Atan2(_downLocal.Y - _rotatePivotY, _downLocal.X - _rotatePivotX);
                float a1 = MathF.Atan2(e.Relative.Y - _rotatePivotY, e.Relative.X - _rotatePivotX);
                float deg = _rotateDragStart + (a1 - a0) * (180f / MathF.PI);
                StraightenPreview = Math.Clamp(deg, -45f, 45f);
                if (_settings != null)
                    _settings.Straighten = StraightenPreview;
                _crop.ClampInside(rot, StraightenPreview, flipH, flipV, fw, fh);
                InvalidateAllLooks();
                InvalidatePaint();
                CropChanged?.Invoke();
            }
            else if (_crop.UpdateDrag(e.Relative.X, e.Relative.Y, ImageDest, fw, fh, rot, StraightenPreview, flipH, flipV, fromCenter: ShiftHeld))
            {
                _crop.ClampInside(rot, StraightenPreview, flipH, flipV, fw, fh);
                InvalidateAllLooks();
                InvalidatePaint();
                CropChanged?.Invoke();
            }
            e.Handled = true;
            return;
        }

        if (_cropTool || !CanPan())
            return;

        _isInteracting = true;
        float z = Math.Max(Zoom, 1e-6f);
        PanX = _downPanX - dx / z;
        PanY = _downPanY - dy / z;
        ClampPan();
        ImageDest = ComputeDest();
        InvalidatePaint();
        ViewChanged?.Invoke();
        e.Handled = true;
    }

    private void OnPointerUp(object sender, MouseEventArgs e)
    {
        if (e.Button != 0 || !_pointerDown)
            return;

        bool wasSplit = _splitDrag;
        _pointerDown = false;
        _splitDrag = false;
        bool wasCrop = _crop.Active != CropHandle.None;
        bool wasSpot = _spotTool && _dragPart != SpotPart.None;
        bool wasDrag = _didDrag;
        bool placeSpot = _pendingSpotClick && !wasDrag;
        _pendingSpotClick = false;
        if (wasSpot)
        {
            _dragPart = SpotPart.None;
            SpotEditEnded?.Invoke();
        }
        _crop.EndDrag();
        ReleasePointer();
        ApplyCropCursor(e.Relative.X, e.Relative.Y);
        e.Handled = true;

        if (_isInteracting)
        {
            _isInteracting = false;
            InvalidatePaint();
            if (wasDrag)
                ViewSettled?.Invoke();
        }

        if (placeSpot)
        {
            AddSpotAt(_downLocal.X, _downLocal.Y);
            return;
        }

        if (wasDrag || wasCrop || wasSpot || _cropTool || _spotTool || !HasPhoto)
            return;

        ToggleClickZoom(e.Relative);
    }

    private void OnWheel(object sender, MouseScrollEventArgs e)
    {
        if (!HasPhoto || e.Offset.Y == 0f)
            return;

        if (_spotTool && OnSpotScroll(e.Offset.Y))
        {
            e.Handled = true;
            return;
        }

        float w = Transform.Computed.Width;
        float h = Transform.Computed.Height;
        if (w <= 1 || h <= 1)
            return;

        float lx = _pointerLocal.X;
        float ly = _pointerLocal.Y;
        if (lx == 0 && ly == 0 && !_pointerDown)
        {
            lx = w * 0.5f;
            ly = h * 0.5f;
        }

        float z = Math.Max(Zoom, 1e-6f);
        float imgX = PanX + (lx - w * 0.5f) / z;
        float imgY = PanY + (ly - h * 0.5f) / z;
        float next = Math.Clamp(z * MathF.Pow(1.1f, e.Offset.Y), MinZoom, MaxZoom);
        if (Math.Abs(next - z) < 1e-6f)
        {
            e.Handled = true;
            return;
        }

        _isInteracting = true;
        Zoom = next;
        _freeZoom = true;
        ZoomMode = ZoomMode.OneToOne;
        float fitZ = GetFitZoom();
        if (next > fitZ * 1.02f || next >= 1.0f)
            _lastScrollZoom = next;

        PanX = imgX - (lx - w * 0.5f) / Zoom;
        PanY = imgY - (ly - h * 0.5f) / Zoom;
        ClampPan();
        ImageDest = ComputeDest();
        InvalidatePaint();
        ViewChanged?.Invoke();
        ScheduleWheelSettled();
        e.Handled = true;
    }

    private void ScheduleWheelSettled()
    {
        _wheelSettleCts?.Cancel();
        _wheelSettleCts?.Dispose();
        _wheelSettleCts = new CancellationTokenSource();
        var token = _wheelSettleCts.Token;

        Task.Delay(120, token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            Shell.Post(() =>
            {
                if (token.IsCancellationRequested) return;
                _isInteracting = false;
                InvalidatePaint();
                ViewSettled?.Invoke();
            });
        }, TaskScheduler.Default);
    }

    private void OnHover(VisualElement el, Vector2 pos)
    {
        _pointerLocal = PointToClient(pos.X, pos.Y);

        if (_showInfoOverlay)
        {
            bool wasHover = _infoCloseHover;
            _infoCloseHover = _infoCloseRect.Contains(_pointerLocal.X, _pointerLocal.Y);
            if (wasHover != _infoCloseHover)
                InvalidatePaint();
        }

        if (_spotTool)
        {
            HitSpot(_pointerLocal.X, _pointerLocal.Y, out int idx, out SpotPart part);
            if (idx != _hoverSpot || part != _hoverPart)
            {
                _hoverSpot = idx;
                _hoverPart = part;
                Cursor = part != SpotPart.None ? UiCursor.Hand : UiCursor.Default;
                InvalidatePaint();
            }
            return;
        }

        if (!_cropTool)
            return;
        string? id = CropBarIdAt(_pointerLocal.X, _pointerLocal.Y);
        var handle = _crop.Active != CropHandle.None
            ? _crop.Active
            : (id != null ? CropHandle.None : _crop.Hit(ImageDest, _pointerLocal.X, _pointerLocal.Y));
        bool same = id == _cropHoverId && handle == _crop.Hover;
        _cropHoverId = id;
        _crop.Hover = handle;
        ApplyCropCursor(_pointerLocal.X, _pointerLocal.Y);
        if (!same)
            InvalidatePaint();
    }

    private void OnLeave(VisualElement el)
    {
        if (_pointerDown)
            return;
        _crop.Hover = CropHandle.None;
        _cropHoverId = null;
        if (!_cropTool && !_spotTool)
            Cursor = null;
        CropCursors.Reset();
        _hoverSpot = -1;
        _hoverPart = SpotPart.None;
        if (_cropTool)
            InvalidatePaint();
    }

    private bool ShiftHeld =>
        (ParentView?.Events.IsShiftDown ?? false) || Events.IsShiftDown;

    private bool CtrlHeld =>
        (ParentView?.Events.IsControlDown ?? false) || Events.IsControlDown;

    private void ApplyCropCursor(float lx, float ly)
    {
        if (!_cropTool)
        {
            if (Cursor != null)
            {
                Cursor = null;
                CropCursors.Reset();
            }
            return;
        }

        if (CropBarIdAt(lx, ly) != null && _crop.Active == CropHandle.None)
        {
            Cursor = UiCursor.Hand;
            Shell.SetCursor(UiCursor.Hand);
            return;
        }

        var handle = _crop.Active != CropHandle.None
            ? _crop.Active
            : _crop.Hit(ImageDest, lx, ly);
        Cursor = handle switch
        {
            CropHandle.N or CropHandle.S => UiCursor.VResize,
            CropHandle.E or CropHandle.W => UiCursor.HResize,
            CropHandle.Move => UiCursor.Hand,
            CropHandle.None => UiCursor.Default,
            _ => UiCursor.Crosshair
        };
        CropCursors.Apply(handle);
    }

    /// <summary>
    /// Toggles between Fit and the memorized zoom scale (either from mouse wheel or 1:1).
    /// If <paramref name="centerLocal"/> is provided, centers the view on that client point when zooming in.
    /// </summary>
    public void ToggleZoom(Vector2? centerLocal = null)
    {
        if (!HasPhoto)
            return;

        float fitZ = GetFitZoom();
        bool isZoomedIn = (ZoomMode != ZoomMode.Fit && !_freeZoom)
            || (_freeZoom && Zoom > fitZ * 1.05f);

        if (isZoomedIn)
        {
            SetZoomMode(ZoomMode.Fit);
            return;
        }

        float w = Transform.Computed.Width;
        float h = Transform.Computed.Height;
        var dest = ImageDest.Width > 0 ? ImageDest : ComputeDest(w, h);
        if (dest.Width < 1f || dest.Height < 1f)
            return;

        float imgX, imgY;
        if (centerLocal.HasValue)
        {
            var local = centerLocal.Value;
            imgX = (local.X - dest.Left) / dest.Width * ViewW;
            imgY = (local.Y - dest.Top) / dest.Height * ViewH;
            imgX = Math.Clamp(imgX, 0f, ViewW);
            imgY = Math.Clamp(imgY, 0f, ViewH);
        }
        else
        {
            imgX = PanX > 0f ? PanX : ViewW * 0.5f;
            imgY = PanY > 0f ? PanY : ViewH * 0.5f;
        }

        float targetZoom = _lastScrollZoom;
        if (targetZoom <= fitZ * 1.05f)
            targetZoom = Math.Max(1.0f, fitZ * 2.0f);

        Zoom = Math.Clamp(targetZoom, MinZoom, MaxZoom);
        ZoomMode = ZoomMode.OneToOne;
        _freeZoom = Math.Abs(Zoom - 1f) > 1e-4f;
        PanX = imgX;
        PanY = imgY;
        ClampPan();
        ImageDest = ComputeDest();
        InvalidatePaint();
        ViewChanged?.Invoke();
        ViewSettled?.Invoke();
    }

    private void ToggleClickZoom(Vector2 local)
    {
        ToggleZoom(local);
    }

    public float GetFitZoom()
    {
        float w = Transform.Computed.Width;
        float h = Transform.Computed.Height;
        GetContainSize(out int imgW, out int imgH);
        if (w <= 1 || h <= 1 || imgW <= 0 || imgH <= 0)
            return 1.0f;
        return Math.Min(w / imgW, h / imgH);
    }

    public void PanToNorm(float nx, float ny)
    {
        if (!HasPhoto || ViewW <= 0 || ViewH <= 0) return;
        PanX = nx * ViewW;
        PanY = ny * ViewH;
        ClampPan();
        ImageDest = ComputeDest();
        InvalidatePaint();
        ViewChanged?.Invoke();
        ViewSettled?.Invoke();
    }

    private void SyncViewport()
    {
        float w = Transform.Computed.Width;
        float h = Transform.Computed.Height;
        if (w > 1 && h > 1)
            SyncViewport(w, h);
    }

    private void GetContainSize(out int imgW, out int imgH)
    {
        imgW = ViewW;
        imgH = ViewH;
        if (imgW > 0 && imgH > 0)
            return;
        imgW = _source?.Width ?? 0;
        imgH = _source?.Height ?? 0;
        if (imgW > 0 && imgH > 0)
            return;
        imgW = _developed?.Width ?? 0;
        imgH = _developed?.Height ?? 0;
    }

    private void SyncViewport(float w, float h)
    {
        GetContainSize(out int imgW, out int imgH);
        if (!HasPhoto || imgW <= 0 || imgH <= 0)
        {
            // Empty well only — never stretch a photo to the pane.
            ImageDest = SKRect.Empty;
            return;
        }

        if (!_freeZoom)
        {
            if (ZoomMode == ZoomMode.Fit)
            {
                var box = Contain(w, h, imgW, imgH);
                Zoom = box.Width / imgW;
                PanX = imgW * 0.5f;
                PanY = imgH * 0.5f;
                ImageDest = box;
                return;
            }

            if (ZoomMode == ZoomMode.Fill)
            {
                Zoom = Math.Max(w / imgW, h / imgH);
            }
            else
            {
                Zoom = 1f;
            }

            Zoom = Math.Clamp(Zoom, MinZoom, MaxZoom);
        }

        ClampPan();
        ImageDest = ComputeDest(w, h);
    }

    private static SKRect Contain(float boxW, float boxH, int imgW, int imgH)
    {
        if (imgW <= 0 || imgH <= 0)
            return new SKRect(0, 0, boxW, boxH);
        float scale = Math.Min(boxW / imgW, boxH / imgH);
        float dw = imgW * scale;
        float dh = imgH * scale;
        float x = (boxW - dw) * 0.5f;
        float y = (boxH - dh) * 0.5f;
        return new SKRect(x, y, x + dw, y + dh);
    }



    private SKRect SourceOf(SKImage image, SKRect dest, SKRect vis)
    {
        int iw = image.Width;
        int ih = image.Height;
        if (dest.Width < 1e-6f || dest.Height < 1e-6f)
            return new SKRect(0, 0, iw, ih);

        float u0 = (vis.Left - dest.Left) / dest.Width;
        float v0 = (vis.Top - dest.Top) / dest.Height;
        float u1 = (vis.Right - dest.Left) / dest.Width;
        float v1 = (vis.Bottom - dest.Top) / dest.Height;
        if (!_cropTool && _settings != null && _settings.HasCrop && ReferenceEquals(image, _source))
        {
            u0 = _settings.CropX + u0 * _settings.CropW;
            v0 = _settings.CropY + v0 * _settings.CropH;
            u1 = _settings.CropX + u1 * _settings.CropW;
            v1 = _settings.CropY + v1 * _settings.CropH;
        }
        return new SKRect(u0 * iw, v0 * ih, u1 * iw, v1 * ih);
    }

    /// <summary>Spinner on a glass disc at the bottom centre, clear of the top badges and info card.</summary>
    private static void DrawBusy(SKCanvas canvas, float paneW, float paneH)
    {
        float cx = paneW * 0.5f;
        float cy = paneH - 34f;
        float r = 7f;
        double ang = DateTime.UtcNow.TimeOfDay.TotalSeconds * 4.0;
        DrawGlass(canvas, new SKRoundRect(new SKRect(cx - r - 7, cy - r - 7, cx + r + 7, cy + r + 7), r + 7), Theme.HairlineStrong);

        var rect = new SKRect(cx - r, cy - r, cx + r, cy + r);
        using (var track = new SKPaint { Color = Theme.Hairline, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f })
            canvas.DrawOval(rect, track);
        using var arc = new SKPaint
        {
            Color = Theme.Accent,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f,
            StrokeCap = SKStrokeCap.Round
        };
        canvas.DrawArc(rect, (float)(ang * 180.0 / Math.PI), 100, false, arc);
    }

    private SKRect ComputeDest() =>
        ComputeDest(Transform.Computed.Width, Transform.Computed.Height);

    private SKRect ComputeDest(float w, float h)
    {
        if (!HasPhoto)
            return new SKRect(0, 0, w, h);

        float z = Math.Max(Zoom, 1e-6f);
        float dw = ViewW * z;
        float dh = ViewH * z;
        float left = w * 0.5f - PanX * z;
        float top = h * 0.5f - PanY * z;
        // Pixel-snap dest so cropped zoom/pan doesn't shimmer along clip edges.
        return new SKRect(
            MathF.Round(left),
            MathF.Round(top),
            MathF.Round(left + dw),
            MathF.Round(top + dh));
    }

    private void ClampPan()
    {
        if (!HasPhoto)
            return;

        float w = Transform.Computed.Width;
        float h = Transform.Computed.Height;
        if (w <= 1 || h <= 1)
            return;

        float z = Math.Max(Zoom, 1e-6f);
        float imgW = ViewW;
        float imgH = ViewH;
        float visW = w / z;
        float visH = h / z;

        if (visW >= imgW)
            PanX = imgW * 0.5f;
        else
            PanX = Math.Clamp(PanX, visW * 0.5f, imgW - visW * 0.5f);

        if (visH >= imgH)
            PanY = imgH * 0.5f;
        else
            PanY = Math.Clamp(PanY, visH * 0.5f, imgH - visH * 0.5f);
    }

    public bool HasPhoto => _source != null || _developed != null;

    private int ViewW => _viewW > 0 ? _viewW : _source?.Width ?? _developed?.Width ?? 0;
    private int ViewH => _viewH > 0 ? _viewH : _source?.Height ?? _developed?.Height ?? 0;

    public void BeginCrop()
    {
        _savedCropX = _crop.X;
        _savedCropY = _crop.Y;
        _savedCropW = _crop.W > 0.001f ? _crop.W : 1f;
        _savedCropH = _crop.H > 0.001f ? _crop.H : 1f;
        _savedAspect = _crop.Aspect;
        _savedRatioId = _crop.RatioId;
        _savedStraighten = _settings?.Straighten ?? StraightenPreview;
        _savedRotate90 = _settings?.Rotate90 ?? 0;
        StraightenPreview = _savedStraighten;
        if (_crop.W < 0.001f || _crop.H < 0.001f)
            _crop.ResetFull();
        EndSpotTool();
        _cropTool = true;
        _crop.EndDrag();
        _crop.Hover = CropHandle.None;
        GetFrameSize(out int fw, out int fh);
        if (_crop.RatioId == "free")
            _crop.ApplyRatio("orig", fw, fh);
        _freeZoom = false;
        ZoomMode = ZoomMode.Fit;
        SyncViewToSource(force: true);
        InvalidatePaint();
    }

    public bool CommitCrop()
    {
        if (!_cropTool)
            return false;
        _crop.Clamp();
        _cropTool = false;
        _crop.EndDrag();
        Cursor = null;
        CropCursors.Reset();
        _freeZoom = false;
        ZoomMode = ZoomMode.Fit;
        SyncViewToSource(force: true);
        CropCommitted?.Invoke();
        InvalidatePaint();
        return true;
    }

    public bool CancelCrop()
    {
        if (!_cropTool)
            return false;
        _crop.X = _savedCropX;
        _crop.Y = _savedCropY;
        _crop.W = _savedCropW;
        _crop.H = _savedCropH;
        _crop.Aspect = _savedAspect;
        _crop.RatioId = _savedRatioId;
        StraightenPreview = _savedStraighten;
        if (_settings != null)
        {
            _settings.Straighten = _savedStraighten;
            _settings.Rotate90 = _savedRotate90;
        }
        _cropTool = false;
        _crop.EndDrag();
        Cursor = null;
        CropCursors.Reset();
        _freeZoom = false;
        ZoomMode = ZoomMode.Fit;
        SyncViewToSource(force: true);
        CropCancelled?.Invoke();
        InvalidatePaint();
        return true;
    }

    public void NotifyOrientation(int dir = 0, bool flipH = false, bool flipV = false)
    {
        if (dir != 0)
            _crop.RotateCrop(dir);
        if (flipH)
            _crop.FlipCrop(h: true);
        if (flipV)
            _crop.FlipCrop(h: false);
        _freeZoom = false;
        ZoomMode = ZoomMode.Fit;
        _look.InvalidateAll();
        _tileLook.InvalidateAll();
        GetFrameSize(out int fw, out int fh);
        if (_crop.RatioId != "free")
            _crop.ApplyRatio(_crop.RatioId, fw, fh);
        _crop.ClampInside(
            _settings?.Rotate90 ?? 0, StraightenPreview,
            _settings?.FlipH ?? false, _settings?.FlipV ?? false,
            fw, fh);
        SyncViewToSource(force: true);
        InvalidatePaint();
    }

    public void CaptureView(PhotoDocument doc)
    {
        if (doc == null) return;
        doc.SavedZoomMode = ZoomMode;
        doc.SavedZoom = Zoom;
        doc.SavedPanX = PanX;
        doc.SavedPanY = PanY;
        doc.SavedFreeZoom = _freeZoom;
        doc.HasSavedView = true;
    }

    public void RestoreView(PhotoDocument doc)
    {
        if (doc == null || !doc.HasSavedView)
            return;
        ZoomMode = doc.SavedZoomMode;
        Zoom = doc.SavedZoom;
        PanX = doc.SavedPanX;
        PanY = doc.SavedPanY;
        _freeZoom = doc.SavedFreeZoom;
        SyncViewport();
        InvalidatePaint();
    }

    private void GetFrameSize(out int fw, out int fh)
    {
        int sw = _source?.Width ?? (_developed?.Width ?? (_viewW > 0 ? _viewW : 1));
        int sh = _source?.Height ?? (_developed?.Height ?? (_viewH > 0 ? _viewH : 1));
        int rot = _settings != null ? _settings.Rotate90 & 3 : 0;
        fw = (rot & 1) != 0 ? sh : sw;
        fh = (rot & 1) != 0 ? sw : sh;
        if (fw < 1) fw = 1;
        if (fh < 1) fh = 1;
    }

    private void SyncViewToSource(bool force = false)
    {
        if (_source == null || _source.Handle == IntPtr.Zero)
            return;

        GetFrameSize(out int fw, out int fh);
        int nw = fw;
        int nh = fh;
        if (!_cropTool && _crop.W > 0.001f && _crop.H > 0.001f && (_crop.W < 0.999f || _crop.H < 0.999f))
        {
            nw = Math.Max(1, (int)Math.Round(fw * _crop.W));
            nh = Math.Max(1, (int)Math.Round(fh * _crop.H));
        }

        if (!force && nw == _viewW && nh == _viewH && _viewW > 0)
            return;

        int oldW = _viewW;
        int oldH = _viewH;
        _viewW = nw;
        _viewH = nh;
        if (oldW > 0 && oldH > 0 && (oldW > oldH != nw > nh))
        {
            _freeZoom = false;
            ZoomMode = ZoomMode.Fit;
        }
        else if (oldW == 0)
        {
            _freeZoom = false;
            ZoomMode = ZoomMode.Fit;
        }

        SyncViewport();
        ViewChanged?.Invoke();
    }

    private void DrawCropBar(SKCanvas canvas, SKRect pane)
    {
        _cropHits.Clear();
        string[] ids = { "free", "orig", "1:1", "4:3", "3:2", "16:9", "rotl", "rotr", "reset", "cancel", "ok" };
        float w = 84f;
        float h = 28f;
        float gap = 4f;
        float x = pane.Left + 8f;
        float y = pane.Top + 8f;
        using var text = new TextPaint
        {
            Color = Theme.Text,
            HintingLevel = SKFontHinting.Normal,
            TextSize = 11.5f,
            Typeface = Theme.GetTypeface(500),
            TextAlign = SKTextAlign.Center
        };
        var tm = text.FontMetrics;
        float barH = ids.Length * (h + gap) + 4f;
        DrawGlass(canvas, new SKRoundRect(new SKRect(x - 4, y - 4, x + w + 4, y + barH), Theme.Radius, Theme.Radius), Theme.HairlineStrong);
        static bool IsRatioId(string id) =>
            id is "free" or "orig" or "1:1" or "4:3" or "3:2" or "16:9";

        for (int i = 0; i < ids.Length; i++)
        {
            var r = new SKRect(x, y, x + w, y + h);
            _cropHits.Add((r, ids[i]));
            bool hover = ids[i] == _cropHoverId;
            bool selected = IsRatioId(ids[i]) && ids[i] == _crop.RatioId;
            SKColor fill = ids[i] == "ok" ? Theme.Accent : Theme.Button;
            if (selected)
                fill = Theme.Selected;
            if (hover)
                fill = Theme.Lighten(fill, 22);
            using (var paint = new SKPaint { Color = fill, IsAntialias = true })
                canvas.DrawRoundRect(r, Theme.RadiusSm, Theme.RadiusSm, paint);
            if (selected)
            {
                using var ring = new SKPaint
                {
                    Color = Theme.Accent,
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 1.5f
                };
                canvas.DrawRoundRect(r, Theme.RadiusSm, Theme.RadiusSm, ring);
            }
            text.Color = ids[i] == "ok" ? Theme.ButtonTextOnAccent : Theme.Text;
            canvas.DrawText(LabelOf(ids[i]), r.MidX, r.MidY - (tm.Ascent + tm.Descent) * 0.5f, text);
            y += h + gap;
        }
    }

    private static string LabelOf(string id) => id switch
    {
        "free" => "Free",
        "orig" => "Orig",
        "rotl" => "Rot L",
        "rotr" => "Rot R",
        "reset" => "Reset",
        "cancel" => "Cancel",
        "ok" => "OK",
        _ => id
    };

    private string? CropBarIdAt(float lx, float ly)
    {
        foreach (var (rect, id) in _cropHits)
        {
            if (rect.Contains(lx, ly))
                return id;
        }
        return null;
    }

    private bool HitCropBar(float lx, float ly)
    {
        foreach (var (rect, id) in _cropHits)
        {
            if (!rect.Contains(lx, ly))
                continue;
            GetFrameSize(out int fw, out int fh);
            switch (id)
            {
                case "free":
                    _crop.RatioId = "free";
                    _crop.Aspect = 0f;
                    break;
                case "orig":
                case "1:1":
                case "4:3":
                case "3:2":
                case "16:9":
                    _crop.ApplyRatio(id, fw, fh);
                    _crop.ClampInside(_settings?.Rotate90 ?? 0, StraightenPreview, _settings?.FlipH ?? false, _settings?.FlipV ?? false, fw, fh);
                    break;
                case "rotl":
                    Rotate90Clicked?.Invoke(-1);
                    return true;
                case "rotr":
                    Rotate90Clicked?.Invoke(1);
                    return true;
                case "reset":
                    _crop.ResetFull();
                    StraightenPreview = 0f;
                    if (_settings != null)
                        _settings.Straighten = 0f;
                    _look.InvalidateAll();
                    _tileLook.InvalidateAll();
                    break;
                case "cancel":
                    CancelCrop();
                    return true;
                case "ok":
                    CommitCrop();
                    return true;
            }
            InvalidatePaint();
            CropChanged?.Invoke();
            return true;
        }
        return false;
    }

    private bool CanPan()
    {
        if (_developed == null && _source == null)
            return false;
        float w = Transform.Computed.Width;
        float h = Transform.Computed.Height;
        return ImageDest.Width > w + 0.5f || ImageDest.Height > h + 0.5f;
    }

    private static bool SetCrop(float current, float next, Action<float> assign)
    {
        if (Math.Abs(current - next) < 1e-6f)
            return false;
        assign(next);
        return true;
    }

    private void RaiseCropChanged()
    {
        _crop.Clamp();
        if (!_cropTool)
            SyncViewToSource(force: true);
        InvalidatePaint();
        CropChanged?.Invoke();
    }

    public void BeginSpotTool()
    {
        if (_cropTool)
            CancelCrop();
        _spotTool = true;
        Cursor = UiCursor.Default;
        InvalidatePaint();
    }

    public void EndSpotTool()
    {
        if (!_spotTool)
            return;
        _spotTool = false;
        _dragPart = SpotPart.None;
        _pendingSpotClick = false;
        _hoverSpot = -1;
        _hoverPart = SpotPart.None;
        Cursor = null;
        InvalidatePaint();
    }

    public bool DeleteSelectedSpot()
    {
        if (_settings?.Spots == null || _selectedSpot < 0 || _selectedSpot >= _settings.Spots.Count)
            return false;
        _settings.Spots.RemoveAt(_selectedSpot);
        _selectedSpot = Math.Min(_selectedSpot, _settings.Spots.Count - 1);
        SpotSelectionChanged?.Invoke();
        SpotsChanged?.Invoke();
        InvalidateSpotLooks();
        InvalidatePaint();
        return true;
    }

    public void SelectSpot(int index)
    {
        int next = _settings?.Spots == null ? -1 : Math.Clamp(index, -1, _settings.Spots.Count - 1);
        if (_settings?.Spots != null && _settings.Spots.Count == 0)
            next = -1;
        if (next == _selectedSpot)
            return;
        _selectedSpot = next;
        SpotSelectionChanged?.Invoke();
        InvalidatePaint();
    }

    private bool OnSpotPointerDown(float lx, float ly)
    {
        HitSpot(lx, ly, out int idx, out SpotPart part);
        if (idx >= 0 && part != SpotPart.None)
        {
            _selectedSpot = idx;
            _dragPart = part;
            GetSpotScreen(_settings!.Spots[idx], out float dx, out float dy, out float sx, out float sy, out _);
            if (part == SpotPart.Dest)
            {
                _dragDx = lx - dx;
                _dragDy = ly - dy;
            }
            else
            {
                _dragDx = lx - sx;
                _dragDy = ly - sy;
            }
            SpotSelectionChanged?.Invoke();
            SpotEditBegan?.Invoke();
            InvalidatePaint();
            return true;
        }

        return false;
    }

    private void AddSpotAt(float lx, float ly)
    {
        if (!DestToSourceUv(lx, ly, out float ux, out float uy))
            return;
        var spots = _settings!.Spots ??= new List<SpotPatch>();
        float radius = _selectedSpot >= 0 && _selectedSpot < spots.Count
            ? spots[_selectedSpot].Radius
            : 0.028f;
        float feather = _selectedSpot >= 0 && _selectedSpot < spots.Count
            ? spots[_selectedSpot].Feather
            : 0.4f;
        float offset = Math.Clamp(radius * 2.2f, 0.03f, 0.12f);
        float srcX = Math.Clamp(ux + offset, 0.02f, 0.98f);
        float srcY = Math.Clamp(uy, 0.02f, 0.98f);
        spots.Add(new SpotPatch
        {
            DestX = Math.Clamp(ux, 0.02f, 0.98f),
            DestY = Math.Clamp(uy, 0.02f, 0.98f),
            SrcX = srcX,
            SrcY = srcY,
            Radius = radius,
            Feather = feather
        });
        _selectedSpot = spots.Count - 1;
        SpotSelectionChanged?.Invoke();
        SpotEditBegan?.Invoke();
        SpotEditEnded?.Invoke();
        SpotsChanged?.Invoke();
        InvalidateSpotLooks();
        InvalidatePaint();
    }

    private void OnSpotPointerMove(float lx, float ly)
    {
        if (_settings?.Spots == null || _selectedSpot < 0 || _selectedSpot >= _settings.Spots.Count)
            return;
        if (!DestToSourceUv(lx - _dragDx, ly - _dragDy, out float ux, out float uy))
            return;
        var p = _settings.Spots[_selectedSpot];
        ux = Math.Clamp(ux, 0.01f, 0.99f);
        uy = Math.Clamp(uy, 0.01f, 0.99f);
        if (_dragPart == SpotPart.Dest)
        {
            p.DestX = ux;
            p.DestY = uy;
        }
        else if (_dragPart == SpotPart.Src)
        {
            p.SrcX = ux;
            p.SrcY = uy;
        }
        SpotsChanged?.Invoke();
        InvalidateSpotLooks();
        InvalidatePaint();
    }

    private bool OnSpotScroll(float wheelY)
    {
        bool size = ShiftHeld && !CtrlHeld;
        bool diffusion = CtrlHeld;
        if (!size && !diffusion)
            return false;

        HitSpot(_pointerLocal.X, _pointerLocal.Y, out int idx, out SpotPart part);
        if (idx < 0)
            idx = _selectedSpot;
        if (_settings?.Spots == null || idx < 0 || idx >= _settings.Spots.Count)
            return false;
        if (part == SpotPart.None && idx != _selectedSpot && _hoverPart == SpotPart.None)
            return false;

        var p = _settings.Spots[idx];
        _selectedSpot = idx;
        if (diffusion)
        {
            p.Feather = Math.Clamp(p.Feather + MathF.Sign(wheelY) * 0.04f, 0f, 1f);
        }
        else
        {
            float sz = SpotPatch.RadiusToSize(p.Radius);
            sz = Math.Clamp(sz + MathF.Sign(wheelY) * 3f, 1f, 100f);
            p.Radius = SpotPatch.SizeToRadius(sz);
        }
        SpotSelectionChanged?.Invoke();
        SpotsChanged?.Invoke();
        InvalidateSpotLooks();
        InvalidatePaint();
        return true;
    }

    private void HitSpot(float lx, float ly, out int index, out SpotPart part)
    {
        index = -1;
        part = SpotPart.None;
        if (_settings?.Spots == null || _settings.Spots.Count == 0)
            return;
        float best = float.MaxValue;
        for (int i = 0; i < _settings.Spots.Count; i++)
        {
            GetSpotScreen(_settings.Spots[i], out float dx, out float dy, out float sx, out float sy, out float r);
            float pad = Math.Max(r, 10f);
            float dd = (lx - dx) * (lx - dx) + (ly - dy) * (ly - dy);
            float ds = (lx - sx) * (lx - sx) + (ly - sy) * (ly - sy);
            if (dd <= pad * pad && dd < best)
            {
                best = dd;
                index = i;
                part = SpotPart.Dest;
            }
            if (ds <= pad * pad && ds < best)
            {
                best = ds;
                index = i;
                part = SpotPart.Src;
            }
        }
    }

    private void GetSpotScreen(SpotPatch p, out float dx, out float dy, out float sx, out float sy, out float rPx)
    {
        GetFrameSize(out int fw, out int fh);
        var dest = ImageDest;
        DevelopGeom.SourceUvToDest(p.DestX, p.DestY, dest, _settings, fw, fh, out dx, out dy);
        DevelopGeom.SourceUvToDest(p.SrcX, p.SrcY, dest, _settings, fw, fh, out sx, out sy);
        float minSide = Math.Min(Math.Max(fw, 1), Math.Max(fh, 1));
        float rUv = Math.Clamp(p.Radius, SpotPatch.MinRadius, SpotPatch.MaxRadius);
        DevelopGeom.SourceUvToDest(
            Math.Clamp(p.DestX + rUv * minSide / Math.Max(fw, 1), 0f, 1f),
            p.DestY, dest, _settings, fw, fh, out float rx, out float ry);
        rPx = Math.Max(4f, MathF.Abs(rx - dx));
    }

    private bool DestToSourceUv(float lx, float ly, out float sx, out float sy)
    {
        GetFrameSize(out int fw, out int fh);
        return DevelopGeom.DestToSourceUv(lx, ly, ImageDest, _settings, fw, fh, out sx, out sy);
    }

    private void DrawSpots(SKCanvas canvas, SKRect pane)
    {
        if (_settings?.Spots == null)
            return;
        canvas.Save();
        canvas.ClipRect(pane);
        for (int i = 0; i < _settings.Spots.Count; i++)
        {
            bool sel = i == _selectedSpot;
            GetSpotScreen(_settings.Spots[i], out float dx, out float dy, out float sx, out float sy, out float r);
            DrawSpotPair(canvas, dx, dy, sx, sy, r, _settings.Spots[i].Feather, sel);
        }
        canvas.Restore();
    }

    private static void DrawSpotPair(SKCanvas canvas, float dx, float dy, float sx, float sy, float r, float feather, bool selected)
    {
        // Destination (healed) ring is solid, the sampled source dashed; the selected pair goes accent.
        SKColor destCol = selected ? Theme.Accent : Theme.Handle.WithAlpha(230);
        SKColor srcCol = selected ? Theme.AccentHover.WithAlpha(230) : Theme.Handle.WithAlpha(170);

        // Dark under-strokes keep the rings legible over bright areas.
        using (var under = new SKPaint { Color = new SKColor(0, 0, 0, 90), IsAntialias = true, StrokeWidth = 3f, Style = SKPaintStyle.Stroke })
        {
            canvas.DrawCircle(dx, dy, r, under);
            canvas.DrawCircle(sx, sy, r, under);
        }

        using var line = new SKPaint
        {
            Color = selected ? destCol : Theme.Handle.WithAlpha(160),
            IsAntialias = true,
            StrokeWidth = selected ? 1.8f : 1.2f,
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round
        };
        canvas.DrawLine(sx, sy, dx, dy, line);
        DrawArrowHead(canvas, sx, sy, dx, dy, line);

        using var destStroke = new SKPaint
        {
            Color = destCol,
            IsAntialias = true,
            StrokeWidth = selected ? 2.2f : 1.5f,
            Style = SKPaintStyle.Stroke
        };
        canvas.DrawCircle(dx, dy, r, destStroke);
        using var srcStroke = new SKPaint
        {
            Color = srcCol,
            IsAntialias = true,
            StrokeWidth = selected ? 2f : 1.3f,
            PathEffect = SKPathEffect.CreateDash(new[] { 5f, 4f }, 0),
            Style = SKPaintStyle.Stroke
        };
        canvas.DrawCircle(sx, sy, r, srcStroke);

        float inner = r * (1f - Math.Clamp(feather, 0f, 1f));
        if (inner > 2.5f && inner < r - 0.8f)
        {
            using var destInner = new SKPaint
            {
                Color = destCol.WithAlpha(selected ? (byte)200 : (byte)140),
                IsAntialias = true,
                StrokeWidth = 1.15f,
                Style = SKPaintStyle.Stroke
            };
            canvas.DrawCircle(dx, dy, inner, destInner);
            using var srcInner = new SKPaint
            {
                Color = srcCol.WithAlpha(selected ? (byte)190 : (byte)130),
                IsAntialias = true,
                StrokeWidth = 1.15f,
                Style = SKPaintStyle.Stroke
            };
            canvas.DrawCircle(sx, sy, inner, srcInner);
        }

        using var destFill = new SKPaint { Color = destCol.WithAlpha(selected ? (byte)34 : (byte)16), IsAntialias = true };
        canvas.DrawCircle(dx, dy, r, destFill);
        using var srcFill = new SKPaint { Color = srcCol.WithAlpha(selected ? (byte)24 : (byte)10), IsAntialias = true };
        canvas.DrawCircle(sx, sy, r, srcFill);
    }

    private static void DrawArrowHead(SKCanvas canvas, float x0, float y0, float x1, float y1, SKPaint paint)
    {
        float vx = x1 - x0;
        float vy = y1 - y0;
        float len = MathF.Sqrt(vx * vx + vy * vy);
        if (len < 8f)
            return;
        float ux = vx / len;
        float uy = vy / len;
        float ah = 8f;
        float aw = 5f;
        float bx = x1 - ux * ah;
        float by = y1 - uy * ah;
        canvas.DrawLine(x1, y1, bx + -uy * aw, by + ux * aw, paint);
        canvas.DrawLine(x1, y1, bx + uy * aw, by + -ux * aw, paint);
    }

    public override void Dispose()
    {
        _histogramRetry?.Dispose();
        _histogramSampler.Dispose();
        Events.OnMouseDown -= OnPointerDown;
        Events.OnMouseMove -= OnPointerMove;
        Events.OnMouseUp -= OnPointerUp;
        Events.OnMouseHover -= OnHover;
        Events.OnScroll -= OnWheel;
        if (_ownsDisplay)
            _developed?.Dispose();
        _developed = null;
        _source = null;
        _settings = null;
        _look.Dispose();
        _tileLook.Dispose();
        _beforeLook.Dispose();
        _tileBeforeLook.Dispose();
        _viewW = 0;
        _viewH = 0;
        base.Dispose();
    }
}
