using System;
using System.Collections.Generic;
using System.Numerics;
using Blossom;
using Blossom.Core;
using Blossom.Core.Input;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Raw75.Develop;
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
    private DevelopSettings? _settings;
    private readonly DevelopLook _look = new();
    private bool _lookFast;
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

    /// <summary>Screen pixels per image pixel. 1 = 1:1.</summary>
    public float Zoom { get; private set; } = 1f;

    /// <summary>Image-space point shown at the viewport center.</summary>
    public float PanX { get; private set; }

    public float PanY { get; private set; }

    /// <summary>Last image destination in local coordinates (contain/cover/1:1+pan).</summary>
    public SKRect ImageDest { get; private set; }

    public event Action? ViewChanged;

    public bool LookFailed => _look.Failed;

    public bool ShowBefore
    {
        get => _showBefore;
        set
        {
            if (_showBefore == value) return;
            _showBefore = value;
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
    public bool CropTool
    {
        get => _cropTool;
        set
        {
            if (_cropTool == value) return;
            _cropTool = value;
            _crop.EndDrag();
            InvalidatePaint();
        }
    }

    private bool _splitBefore;
    public bool SplitBefore
    {
        get => _splitBefore;
        set
        {
            if (_splitBefore == value) return;
            _splitBefore = value;
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
        _look.Invalidate();
        if (source != null && source.Handle != IntPtr.Zero)
            SyncViewToSource();
        _showingPhoto = source != null || _developed != null;
        InvalidatePaint();
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
        if ((m == ZoomMode.Fit || m == ZoomMode.Fill) && ViewW > 0)
        {
            PanX = ViewW * 0.5f;
            PanY = ViewH * 0.5f;
        }
        SyncViewport();
        InvalidatePaint();
        ViewChanged?.Invoke();
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

        bool gpuLook = !_showBefore
            && !_look.Failed
            && _source != null
            && _source.Handle != IntPtr.Zero
            && _settings != null;

        if (gpuLook || (blit != null && blit.Handle != IntPtr.Zero))
        {
            var dest = ImageDest;
            if (dest.Width < 1f || dest.Height < 1f)
                dest = Contain(w, h, ViewW, ViewH);

            if (!_freeZoom && ZoomMode == ZoomMode.Fit)
                dest = Contain(w, h, ViewW, ViewH);

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
                else if (_splitBefore && _before != null && _before.Handle != IntPtr.Zero && blit != null)
                {
                    float splitX = w * _splitT;
                    var left = dest;
                    left.Intersect(pane);
                    left.Right = Math.Min(left.Right, splitX);
                    var right = dest;
                    right.Intersect(pane);
                    right.Left = Math.Max(right.Left, splitX);
                    if (left.Width > 0.5f)
                        cmds.Add(new DrawSkImageCommand(_before, left, SourceOf(_before, dest, left)));
                    if (right.Width > 0.5f)
                        cmds.Add(new DrawSkImageCommand(blit, right, SourceOf(blit, dest, right)));
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

        if (_splitBefore)
        {
            float splitX = Transform.Computed.Width * _splitT;
            var left = dest;
            left.Intersect(pane);
            left.Right = Math.Min(left.Right, splitX);
            var right = dest;
            right.Intersect(pane);
            right.Left = Math.Max(right.Left, splitX);
            SKImage before = _before ?? _source;
            if (left.Width > 0.5f && before.Handle != IntPtr.Zero)
                canvas.DrawImage(before, SourceOf(before, dest, left), left);
            if (right.Width > 0.5f)
            {
                if (!_look.Draw(canvas, dest, right, _source, _settings, _lookFast))
                    canvas.DrawImage(_source, SourceOf(_source, dest, right), right);
            }
            return;
        }

        if (!_look.Draw(canvas, dest, _drawClip, _source, _settings, _lookFast))
            canvas.DrawImage(_source, SourceOf(_source, dest, _drawClip), _drawClip);
    }

    private void DrawOverlay(SKCanvas canvas)
    {
        float w = Transform.Computed.Width;
        float h = Transform.Computed.Height;
        var pane = new SKRect(0, 0, w, h);

        if (_cropTool && HasPhoto)
            _crop.Draw(canvas, ImageDest, pane, _straightenPreview);

        if (_splitBefore)
        {
            float x = w * _splitT;
            using var wipe = new SKPaint
            {
                Color = new SKColor(255, 255, 255, 220),
                IsAntialias = true,
                StrokeWidth = 2,
                Style = SKPaintStyle.Stroke
            };
            canvas.DrawLine(x, 0, x, h, wipe);
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
            Color = new SKColor(180, 180, 186),
            IsAntialias = true,
            TextSize = 13,
            Typeface = SKTypeface.Default
        };
        canvas.DrawText(line, 16, h - 16, text);
    }

    private void OnPointerDown(object sender, MouseEventArgs e)
    {
        if (e.Button != 0 || !HasPhoto)
            return;

        _pointerDown = true;
        _didDrag = false;
        _downLocal = e.Relative;
        _pointerLocal = e.Relative;
        _downPanX = PanX;
        _downPanY = PanY;
        CapturePointer();
        e.Handled = true;

        if (_splitBefore)
        {
            float splitX = Transform.Computed.Width * _splitT;
            if (Math.Abs(e.Relative.X - splitX) < 10f)
            {
                _splitDrag = true;
                return;
            }
        }

        if (_cropTool)
        {
            var handle = _crop.Hit(ImageDest, e.Relative.X, e.Relative.Y);
            if (handle != CropHandle.None)
                _crop.BeginDrag(handle, e.Relative.X, e.Relative.Y);
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
            if (_crop.UpdateDrag(e.Relative.X, e.Relative.Y, ImageDest))
            {
                InvalidatePaint();
                CropChanged?.Invoke();
            }
            e.Handled = true;
            return;
        }

        if (_cropTool || !CanPan())
            return;

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

        _pointerDown = false;
        _splitDrag = false;
        bool wasCrop = _crop.Active != CropHandle.None;
        _crop.EndDrag();
        ReleasePointer();
        e.Handled = true;

        if (_didDrag || wasCrop || _cropTool || !HasPhoto)
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

        Zoom = next;
        _freeZoom = true;
        PanX = imgX - (lx - w * 0.5f) / Zoom;
        PanY = imgY - (ly - h * 0.5f) / Zoom;
        ClampPan();
        ImageDest = ComputeDest();
        InvalidatePaint();
        ViewChanged?.Invoke();
        e.Handled = true;
    }

    private void OnHover(VisualElement el, Vector2 pos)
    {
        _pointerLocal = PointToClient(pos.X, pos.Y);
    }

    private void ToggleClickZoom(Vector2 local)
    {
        if (ZoomMode == ZoomMode.OneToOne || _freeZoom)
        {
            SetZoomMode(ZoomMode.Fit);
            return;
        }

        float w = Transform.Computed.Width;
        float h = Transform.Computed.Height;
        var dest = ImageDest.Width > 0 ? ImageDest : ComputeDest(w, h);
        if (dest.Width < 1f || dest.Height < 1f)
            return;

        float imgX = (local.X - dest.Left) / dest.Width * ViewW;
        float imgY = (local.Y - dest.Top) / dest.Height * ViewH;
        ZoomMode = ZoomMode.OneToOne;
        _freeZoom = false;
        Zoom = 1f;
        PanX = imgX;
        PanY = imgY;
        ClampPan();
        ImageDest = ComputeDest();
        InvalidatePaint();
        ViewChanged?.Invoke();
    }

    private void SyncViewport()
    {
        float w = Transform.Computed.Width;
        float h = Transform.Computed.Height;
        if (w > 1 && h > 1)
            SyncViewport(w, h);
    }

    private void SyncViewport(float w, float h)
    {
        if (!HasPhoto || ViewW <= 0 || ViewH <= 0)
        {
            ImageDest = new SKRect(0, 0, w, h);
            return;
        }

        int imgW = ViewW;
        int imgH = ViewH;

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

    private bool HasPhoto => _source != null || _developed != null;

    private int ViewW => _viewW > 0 ? _viewW : _source?.Width ?? _developed?.Width ?? 0;
    private int ViewH => _viewH > 0 ? _viewH : _source?.Height ?? _developed?.Height ?? 0;

    private void SyncViewToSource()
    {
        if (_source == null || _source.Handle == IntPtr.Zero)
            return;

        int sw = _source.Width;
        int sh = _source.Height;
        int rot = _settings != null ? _settings.Rotate90 & 3 : 0;
        int nw = (rot & 1) != 0 ? sh : sw;
        int nh = (rot & 1) != 0 ? sw : sh;
        if (nw == _viewW && nh == _viewH && _viewW > 0)
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
        _viewW = 0;
        _viewH = 0;
        base.Dispose();
    }
}
