using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Blossom;
using Blossom.Core;
using Blossom.Core.Input;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Raw75.Develop;
using Raw75.Imaging;
using Raw75.Pipeline;
using SkiaSharp;

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

    private void InvalidateAllLooks()
    {
        _look.Invalidate();
        _tileLook.Invalidate();
        _beforeLook.Invalidate();
        _tileBeforeLook.Invalidate();
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
        }
        return b;
    }
    private bool _showBefore;
    private SKRect _drawDest;
    private SKRect _drawClip;
    private bool _ownsDisplay = true;
    private int _viewW;
    private int _viewH;
    private bool _busy;
    private bool _splitDrag;
    private string? _droppedPath;
    private string _probeNote = "";
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
        sb.AppendLine($"  view { _viewW}x{_viewH}  zoom={Zoom:0.###}  mode={ZoomMode}  probe={_probeNote}");
        sb.AppendLine($"  lookFailed={_look.Failed}  showBefore={_showBefore}  split={_splitBefore}");
    }

    public bool ShowBefore
    {
        get => _showBefore;
        set
        {
            if (_showBefore == value) return;
            _showBefore = value;
            _beforeLook.Invalidate();
            _tileBeforeLook.Invalidate();
            InvalidatePaint();
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
    private float _rotateDragStart;
    private int _savedRotate90;
    private readonly List<(SKRect Rect, string Id)> _cropHits = new();
    private string? _cropHoverId;

    public bool CropTool => _cropTool;

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
            _beforeLook.Invalidate();
            _tileBeforeLook.Invalidate();
            InvalidatePaint();
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
        Events.OnScroll += OnWheel;
    }

    public override void AddedToView()
    {
        AllocateProbe();
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
        screenW = Math.Max(1, (int)MathF.Ceiling(vis.Width));
        screenH = Math.Max(1, (int)MathF.Ceiling(vis.Height));
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

    private void AllocateProbe()
    {
        if (!Gpu.IsReady)
        {
            Log.Warning("PhotoPane: Gpu is not ready; probe skipped.");
            return;
        }

        SKSurface? surface = Gpu.CreateSurface(256, 256, SKColorType.RgbaF16);
        if (surface != null)
        {
            _probeNote = "F16";
        }
        else
        {
            surface = Gpu.CreateSurface(256, 256, SKColorType.Rgba8888);
            _probeNote = surface != null ? "8888 fallback" : "";
        }

        if (surface == null)
        {
            Log.Warning("PhotoPane: could not allocate a GPU surface.");
            return;
        }

        try
        {
            var canvas = surface.Canvas;
            canvas.Clear(new SKColor(32, 32, 36));
            using var fill = new SKPaint { Color = new SKColor(48, 48, 54), IsAntialias = true };
            canvas.DrawRect(new SKRect(32, 32, 224, 224), fill);
            canvas.Flush();

            var snap = Gpu.Snapshot(surface);
            if (snap == null)
            {
                Log.Warning("PhotoPane: GPU snapshot failed.");
                return;
            }

            SetDeveloped(snap);
            Log.Info($"PhotoPane: GPU probe ok ({_probeNote}).");
        }
        finally
        {
            surface.Dispose();
        }
    }

    protected override void OnAfterStyleDraw(List<DrawCommand> cmds)
    {
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
        if (_source == null || _settings == null || _source.Handle == IntPtr.Zero)
            return;

        var dest = _drawDest;
        var pane = new SKRect(0, 0, Transform.Computed.Width, Transform.Computed.Height);
        bool applyCrop = !_cropTool && _settings.HasCrop;

        bool isFast = _lookFast || _isInteracting;

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
                    canvas.DrawImage(_source, SourceOf(_source, dest, left), left);
            }

            if (right.Width > 0.5f)
            {
                GetFrameSize(out int fwA, out int fhA);
                if (!_look.Draw(canvas, dest, right, _source, _settings, isFast, applyCrop,
                        0f, 0f, 1f, 1f, float.NaN, float.NaN, float.NaN, float.NaN, fwA, fhA, showClipping: _showClipping))
                    canvas.DrawImage(_source, SourceOf(_source, dest, right), right);
            }

            if (_tile != null && _tile.Handle != IntPtr.Zero && _tileW > 1e-5f && _tileH > 1e-5f)
            {
                GetFrameSize(out int fw, out int fh);
                var tileSrc = new SKRect(_tileX, _tileY, _tileX + _tileW, _tileY + _tileH);
                var tileDest = DevelopGeom.SourceAabbToDest(tileSrc, dest, _settings, fw, fh);

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

        var activeSettings = _showBefore ? GetBaselineSettings() : _settings;
        var activeLook = _showBefore ? _beforeLook : _look;
        var activeTileLook = _showBefore ? _tileBeforeLook : _tileLook;

        if (!activeLook.Draw(canvas, dest, vis, _source, activeSettings, isFast, applyCrop: true,
                0f, 0f, 1f, 1f, float.NaN, float.NaN, float.NaN, float.NaN, fwSingle, fhSingle, showClipping: _showClipping))
            canvas.DrawImage(_source, SourceOf(_source, dest, vis), vis);

        if (_tile == null || _tile.Handle == IntPtr.Zero || _tileW < 1e-5f || _tileH < 1e-5f)
            return;

        var tileSrcSingle = new SKRect(_tileX, _tileY, _tileX + _tileW, _tileY + _tileH);
        var tileDestSingle = DevelopGeom.SourceAabbToDest(tileSrcSingle, dest, activeSettings, fwSingle, fhSingle);
        tileDestSingle.Intersect(vis);
        if (tileDestSingle.Width < 1f || tileDestSingle.Height < 1f)
            return;

        activeTileLook.Draw(canvas, dest, tileDestSingle, _tile, activeSettings, isFast, applyCrop: true,
            _tileX, _tileY, _tileW, _tileH, float.NaN, float.NaN, float.NaN, float.NaN, fwSingle, fhSingle, showClipping: _showClipping);
    }

    private void DrawOverlay(SKCanvas canvas)
    {
        float w = Transform.Computed.Width;
        float h = Transform.Computed.Height;
        var pane = new SKRect(0, 0, w, h);

        if (_cropTool && HasPhoto)
        {
            _crop.Draw(canvas, ImageDest, pane, _straightenPreview);
            DrawCropBar(canvas, pane);
        }

        if (_splitBefore)
        {
            float x = w * _splitT;

            // Shadow under divider line for contrast against bright highlights
            using var shadowPaint = new SKPaint
            {
                Color = new SKColor(0, 0, 0, 160),
                IsAntialias = true,
                StrokeWidth = 4,
                Style = SKPaintStyle.Stroke
            };
            canvas.DrawLine(x, 0, x, h, shadowPaint);

            using var wipe = new SKPaint
            {
                Color = new SKColor(255, 255, 255, 230),
                IsAntialias = true,
                StrokeWidth = 2,
                Style = SKPaintStyle.Stroke
            };
            canvas.DrawLine(x, 0, x, h, wipe);

            // Center grab handle pill on the divider
            float midY = h * 0.5f;
            using var handleBg = new SKPaint
            {
                Color = new SKColor(24, 24, 27, 235),
                IsAntialias = true,
                Style = SKPaintStyle.Fill
            };
            using var handleBorder = new SKPaint
            {
                Color = new SKColor(255, 255, 255, 180),
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.5f
            };
            var pillRect = new SKRoundRect(new SKRect(x - 12, midY - 20, x + 12, midY + 20), 12, 12);
            canvas.DrawRoundRect(pillRect, handleBg);
            canvas.DrawRoundRect(pillRect, handleBorder);

            using var arrowPaint = new SKPaint
            {
                Color = Theme.Text,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.8f,
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round
            };
            using var leftPath = new SKPath();
            leftPath.MoveTo(x - 2.5f, midY - 4.5f);
            leftPath.LineTo(x - 6.5f, midY);
            leftPath.LineTo(x - 2.5f, midY + 4.5f);
            canvas.DrawPath(leftPath, arrowPaint);

            using var rightPath = new SKPath();
            rightPath.MoveTo(x + 2.5f, midY - 4.5f);
            rightPath.LineTo(x + 6.5f, midY);
            rightPath.LineTo(x + 2.5f, midY + 4.5f);
            canvas.DrawPath(rightPath, arrowPaint);

            // Badges: "BEFORE" on left side, "AFTER" on right side
            DrawSplitBadges(canvas, x, w, h);
        }
        else if (_showBefore)
        {
            DrawBeforeBadge(canvas, w);
        }

        if (_showInfoOverlay && HasPhoto && _metadata != null)
        {
            DrawInfoOverlay(canvas, pane);
        }

        if (_busy)
        {
            DrawBusy(canvas, w);
            InvalidatePaint();
        }

        using var stroke = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 28),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1
        };
        canvas.DrawRect(new SKRect(8.5f, 8.5f, w - 8.5f, h - 8.5f), stroke);

        if (_showingPhoto)
            return;

        string line = _droppedPath != null
            ? _droppedPath
            : "Drop a RAW here";
        if (_probeNote.Length > 0 && _droppedPath == null)
            line += $"  ·  GPU {_probeNote}";

        using var text = new SKPaint
        {
            Color = Theme.TextSecondary,
            IsAntialias = true,
            SubpixelText = true,
            LcdRenderText = true,
            HintingLevel = SKPaintHinting.Normal,
            TextSize = 14,
            Typeface = SKTypeface.Default
        };
        canvas.DrawText(line, 16, h - 16, text);
    }

    private void DrawSplitBadges(SKCanvas canvas, float splitX, float w, float h)
    {
        using var badgeBg = new SKPaint
        {
            Color = new SKColor(20, 20, 23, 200),
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };
        using var badgeBorder = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 40),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1
        };
        using var badgeText = new SKPaint
        {
            Color = Theme.Text,
            IsAntialias = true,
            SubpixelText = true,
            LcdRenderText = true,
            HintingLevel = SKPaintHinting.Normal,
            TextSize = 11,
            FakeBoldText = true,
            TextAlign = SKTextAlign.Center
        };

        // Left badge (BEFORE)
        if (splitX > 75)
        {
            float bx = Math.Max(35, splitX - 50);
            var rect = new SKRoundRect(new SKRect(bx - 36, 16, bx + 36, 40), 4, 4);
            canvas.DrawRoundRect(rect, badgeBg);
            canvas.DrawRoundRect(rect, badgeBorder);
            canvas.DrawText("BEFORE", bx, 32, badgeText);
        }

        // Right badge (AFTER)
        if (w - splitX > 75)
        {
            float bx = Math.Min(w - 35, splitX + 50);
            var rect = new SKRoundRect(new SKRect(bx - 36, 16, bx + 36, 40), 4, 4);
            canvas.DrawRoundRect(rect, badgeBg);
            canvas.DrawRoundRect(rect, badgeBorder);
            badgeText.Color = Theme.Accent;
            canvas.DrawText("AFTER", bx, 32, badgeText);
        }
    }

    private void DrawBeforeBadge(SKCanvas canvas, float w)
    {
        using var badgeBg = new SKPaint
        {
            Color = new SKColor(20, 20, 23, 220),
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };
        using var badgeBorder = new SKPaint
        {
            Color = Theme.Accent,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f
        };
        using var badgeText = new SKPaint
        {
            Color = Theme.Accent,
            IsAntialias = true,
            SubpixelText = true,
            LcdRenderText = true,
            HintingLevel = SKPaintHinting.Normal,
            TextSize = 12,
            FakeBoldText = true,
            TextAlign = SKTextAlign.Center
        };

        float cx = w * 0.5f;
        var rect = new SKRoundRect(new SKRect(cx - 52, 16, cx + 52, 42), 6, 6);
        canvas.DrawRoundRect(rect, badgeBg);
        canvas.DrawRoundRect(rect, badgeBorder);
        canvas.DrawText("BEFORE", cx, 34, badgeText);
    }

    private void DrawInfoOverlay(SKCanvas canvas, SKRect pane)
    {
        if (_metadata == null) return;

        float cardW = 340f;
        float cardH = 136f;
        float cardX = pane.Left + 24f;
        float cardY = pane.Top + 24f;
        _infoCardRect = new SKRect(cardX, cardY, cardX + cardW, cardY + cardH);

        // Soft drop shadow
        using var shadow = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 120),
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };
        canvas.DrawRoundRect(new SKRoundRect(new SKRect(cardX, cardY + 3, cardX + cardW, cardY + cardH + 3), 8, 8), shadow);

        // Card background
        using var bg = new SKPaint
        {
            Color = new SKColor(20, 20, 25, 235),
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };
        var cardR = new SKRoundRect(_infoCardRect, 8, 8);
        canvas.DrawRoundRect(cardR, bg);

        // Card border
        using var border = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 38),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f
        };
        canvas.DrawRoundRect(cardR, border);

        float padX = cardX + 16f;
        float curY = cardY + 22f;

        // Top tag: "PHOTO INFO"
        using var tagBg = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 22),
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };
        var tagRect = new SKRoundRect(new SKRect(padX, curY - 12, padX + 80, curY + 4), 3, 3);
        canvas.DrawRoundRect(tagRect, tagBg);

        using var tagText = new SKPaint
        {
            Color = Theme.TextSecondary,
            IsAntialias = true,
            SubpixelText = true,
            LcdRenderText = true,
            TextSize = 9.5f,
            FakeBoldText = true,
            TextAlign = SKTextAlign.Center
        };
        canvas.DrawText("PHOTO INFO", padX + 40, curY - 1, tagText);

        // Date/Time on the right
        using var dateText = new SKPaint
        {
            Color = Theme.TextDim,
            IsAntialias = true,
            SubpixelText = true,
            LcdRenderText = true,
            TextSize = 11f,
            TextAlign = SKTextAlign.Right
        };
        canvas.DrawText(_metadata.FormattedDateTime, cardX + cardW - 16f, curY, dateText);

        // Camera name
        curY += 26f;
        using var camText = new SKPaint
        {
            Color = Theme.Text,
            IsAntialias = true,
            SubpixelText = true,
            LcdRenderText = true,
            TextSize = 14f,
            FakeBoldText = true
        };
        canvas.DrawText(_metadata.CameraName, padX, curY, camText);

        // Lens name
        curY += 20f;
        using var lensText = new SKPaint
        {
            Color = Theme.TextSecondary,
            IsAntialias = true,
            SubpixelText = true,
            LcdRenderText = true,
            TextSize = 12f
        };
        canvas.DrawText(_metadata.LensName, padX, curY, lensText);

        // Exposure parameter strip (Signature Warm Amber)
        curY += 24f;
        using var expText = new SKPaint
        {
            Color = Theme.Accent,
            IsAntialias = true,
            SubpixelText = true,
            LcdRenderText = true,
            TextSize = 13f,
            FakeBoldText = true
        };
        canvas.DrawText(_metadata.FormattedExposure, padX, curY, expText);

        // Dimensions and file size
        curY += 20f;
        using var dimText = new SKPaint
        {
            Color = Theme.TextDim,
            IsAntialias = true,
            SubpixelText = true,
            LcdRenderText = true,
            TextSize = 11f
        };
        string dimStr = $"{_metadata.FormattedDimensions}   ·   {_metadata.FormattedFileSize}";
        canvas.DrawText(dimStr, padX, curY, dimText);
    }

    private void OnPointerDown(object sender, MouseEventArgs e)
    {
        if (e.Button != 0 || !HasPhoto)
            return;

        if (_showInfoOverlay && _infoCardRect.Contains(e.Relative.X, e.Relative.Y))
        {
            _showInfoOverlay = false;
            InvalidatePaint();
            e.Handled = true;
            return;
        }

        _pointerDown = true;
        _didDrag = false;
        _cropTool = _crop.Active != CropHandle.None;
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

        if (_cropTool)
        {
            if (HitCropBar(e.Relative.X, e.Relative.Y))
                return;
            var handle = _crop.Hit(ImageDest, e.Relative.X, e.Relative.Y);
            if (handle != CropHandle.None)
            {
                if (handle == CropHandle.Rotate)
                    _rotateDragStart = StraightenPreview;
                _crop.BeginDrag(handle, e.Relative.X, e.Relative.Y);
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
                var box = _crop.DestOnImage(ImageDest);
                float a0 = MathF.Atan2(_downLocal.Y - box.MidY, _downLocal.X - box.MidX);
                float a1 = MathF.Atan2(e.Relative.Y - box.MidY, e.Relative.X - box.MidX);
                float deg = _rotateDragStart + (a1 - a0) * (180f / MathF.PI);
                StraightenPreview = Math.Clamp(deg, -45f, 45f);
                if (_settings != null)
                    _settings.Straighten = StraightenPreview;
                _crop.ClampInside(rot, StraightenPreview, flipH, flipV, fw, fh);
                InvalidateAllLooks();
                InvalidatePaint();
                CropChanged?.Invoke();
            }
            else if (_crop.UpdateDrag(e.Relative.X, e.Relative.Y, ImageDest, fw, fh, rot, StraightenPreview, flipH, flipV))
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
        bool wasDrag = _didDrag;
        _crop.EndDrag();
        ReleasePointer();
        e.Handled = true;

        if (_isInteracting)
        {
            _isInteracting = false;
            InvalidatePaint();
            if (wasDrag)
                ViewSettled?.Invoke();
        }

        if (wasDrag || wasCrop || wasSplit || _cropTool || !HasPhoto)
            return;

        ToggleClickZoom(e.Relative);
    }

    private void OnWheel(object sender, MouseScrollEventArgs e)
    {
        if (!HasPhoto || e.Offset.Y == 0f)
            return;

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
            Browser.Post(() =>
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
        if (!_cropTool)
            return;
        string? id = CropBarIdAt(_pointerLocal.X, _pointerLocal.Y);
        if (id == _cropHoverId)
            return;
        _cropHoverId = id;
        InvalidatePaint();
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



    private static SKRect SourceOf(SKImage image, SKRect dest, SKRect vis)
    {
        int iw = image.Width;
        int ih = image.Height;
        return new SKRect(
            (vis.Left - dest.Left) / dest.Width * iw,
            (vis.Top - dest.Top) / dest.Height * ih,
            (vis.Right - dest.Left) / dest.Width * iw,
            (vis.Bottom - dest.Top) / dest.Height * ih);
    }

    private static void DrawBusy(SKCanvas canvas, float paneW)
    {
        float cx = paneW * 0.5f;
        float cy = 22f;
        float r = 8f;
        double ang = DateTime.UtcNow.TimeOfDay.TotalSeconds * 4.0;
        using var paint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 220),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2.5f,
            StrokeCap = SKStrokeCap.Round
        };
        using var bg = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 120),
            IsAntialias = true
        };
        canvas.DrawCircle(cx, cy, r + 6, bg);
        var rect = new SKRect(cx - r, cy - r, cx + r, cy + r);
        canvas.DrawArc(rect, (float)(ang * 180.0 / Math.PI), 270, false, paint);
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
        return new SKRect(left, top, left + dw, top + dh);
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
        _savedStraighten = _settings?.Straighten ?? StraightenPreview;
        _savedRotate90 = _settings?.Rotate90 ?? 0;
        StraightenPreview = _savedStraighten;
        if (_crop.W < 0.001f || _crop.H < 0.001f)
            _crop.ResetFull();
        _cropTool = true;
        _crop.EndDrag();
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
        StraightenPreview = _savedStraighten;
        if (_settings != null)
        {
            _settings.Straighten = _savedStraighten;
            _settings.Rotate90 = _savedRotate90;
        }
        _cropTool = false;
        _crop.EndDrag();
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
        _look.Invalidate();
        _tileLook.Invalidate();
        GetFrameSize(out int fw, out int fh);
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
        using var bg = new SKPaint { Color = new SKColor(0, 0, 0, 170), IsAntialias = true };
        using var text = new SKPaint
        {
            Color = Theme.Text,
            IsAntialias = true,
            SubpixelText = true,
            LcdRenderText = true,
            HintingLevel = SKPaintHinting.Normal,
            TextSize = 12,
            Typeface = SKTypeface.Default,
            TextAlign = SKTextAlign.Center
        };
        float barH = ids.Length * (h + gap) + 8f;
        canvas.DrawRoundRect(new SKRect(x - 4, y - 4, x + w + 4, y + barH), 6, 6, bg);
        for (int i = 0; i < ids.Length; i++)
        {
            var r = new SKRect(x, y, x + w, y + h);
            _cropHits.Add((r, ids[i]));
            bool hover = ids[i] == _cropHoverId;
            SKColor fill = ids[i] == "ok" ? Theme.Accent : Theme.Button;
            if (hover)
                fill = Theme.Lighten(fill, 22);
            using (var paint = new SKPaint { Color = fill, IsAntialias = true })
                canvas.DrawRoundRect(r, 4, 4, paint);
            text.Color = ids[i] == "ok" ? Theme.ButtonTextOnAccent : Theme.Text;
            canvas.DrawText(LabelOf(ids[i]), r.MidX, r.MidY + 4, text);
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
                    _crop.Aspect = 0f;
                    break;
                case "orig":
                    _crop.ApplyAspect(fw / (float)fh, fw, fh);
                    _crop.ClampInside(_settings?.Rotate90 ?? 0, StraightenPreview, _settings?.FlipH ?? false, _settings?.FlipV ?? false, fw, fh);
                    break;
                case "1:1":
                    _crop.ApplyAspect(1f, fw, fh);
                    _crop.ClampInside(_settings?.Rotate90 ?? 0, StraightenPreview, _settings?.FlipH ?? false, _settings?.FlipV ?? false, fw, fh);
                    break;
                case "4:3":
                    _crop.ApplyAspect(fw < fh ? 3f / 4f : 4f / 3f, fw, fh);
                    _crop.ClampInside(_settings?.Rotate90 ?? 0, StraightenPreview, _settings?.FlipH ?? false, _settings?.FlipV ?? false, fw, fh);
                    break;
                case "3:2":
                    _crop.ApplyAspect(fw < fh ? 2f / 3f : 3f / 2f, fw, fh);
                    _crop.ClampInside(_settings?.Rotate90 ?? 0, StraightenPreview, _settings?.FlipH ?? false, _settings?.FlipV ?? false, fw, fh);
                    break;
                case "16:9":
                    _crop.ApplyAspect(fw < fh ? 9f / 16f : 16f / 9f, fw, fh);
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
                    _crop.Aspect = 0f;
                    StraightenPreview = 0f;
                    if (_settings != null)
                        _settings.Straighten = 0f;
                    _look.Invalidate();
                    _tileLook.Invalidate();
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

    public override void Dispose()
    {
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
