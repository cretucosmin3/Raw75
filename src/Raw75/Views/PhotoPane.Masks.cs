using System;
using System.Collections.Generic;
using System.Numerics;
using Blossom.Hosting;
using Raw75.Controls;
using Raw75.Develop;
using Raw75.Imaging;
using Raw75.Pipeline;
using SkiaSharp;

namespace Raw75.Views;

/// <summary>
/// Mask editing on the photo: placing new shapes, the selected mask's handles, pins for the other masks,
/// drawing custom spline masks point by point, and the coverage overlay.
/// Geometry lives in source UVs (see <see cref="MaskLayer"/>); everything here maps through the same
/// affine source ↔ screen transform the spot tool uses, so handles stay glued through crop and rotation.
/// </summary>
public sealed partial class PhotoPane
{
    private const float MaskHandleR = 5f;
    private const float MaskPinR = 7f;
    private const float MaskHitR = 11f;
    private const float MaskRotateGap = 28f;
    private const float MaskCurveHit = 7f;
    private const int OverlayLongSide = 640;

    private enum MaskPart { None, Create, Center, AxisX, AxisY, Rotate, Start, End, Point, Pin, Curve }

    private int _selectedMask = -1;
    private MaskShape? _placing;
    private readonly List<MaskPoint> _draft = new();
    private bool _pendingDraftClick;
    private MaskPart _maskDrag = MaskPart.None;
    private int _maskDragIndex = -1;
    private MaskPart _maskHover = MaskPart.None;
    private int _maskHoverIndex = -1;
    private float _maskDownU, _maskDownV;
    private float _maskGrabX, _maskGrabY;
    private MaskLayer? _maskDownState;
    private bool _maskOverlay;
    private MaskAtlas? _overlayAtlas;
    private SKImage? _overlayImage;

    /// <summary>Index into the settings' masks of the mask being edited, or -1.</summary>
    public int SelectedMaskIndex => _selectedMask;

    /// <summary>The shape being placed (next press on the photo creates it), or null.</summary>
    public MaskShape? PlacingMask => _placing;

    /// <summary>Points clicked so far while drawing a custom (spline) mask.</summary>
    public int DraftPointCount => _draft.Count;

    /// <summary>Tints the selected mask's coverage. It also shows on its own while a shape is dragged.</summary>
    public bool MaskOverlay
    {
        get => _maskOverlay;
        set
        {
            if (_maskOverlay == value) return;
            _maskOverlay = value;
            InvalidatePaint();
        }
    }

    /// <summary>Live mask geometry change (drag, scroll); the look already repaints.</summary>
    public event Action? MasksChanged;
    public event Action? MaskEditBegan;
    public event Action? MaskEditEnded;

    /// <summary>The user picked a mask on the photo (a pin) or created one; the owner should select it.</summary>
    public event Action<int>? MaskPicked;

    /// <summary>Placement finished or was cancelled.</summary>
    public event Action? MaskPlacementEnded;

    private MaskLayer? SelectedMask =>
        _settings?.Masks != null && _selectedMask >= 0 && _selectedMask < _settings.Masks.Count
            ? _settings.Masks[_selectedMask]
            : null;

    private bool MaskMode => _source != null && !_cropTool && !_spotTool && (_selectedMask >= 0 || _placing != null);

    private float MaskAspect => _source != null && _source.Height > 0 ? _source.Width / (float)_source.Height : 1f;

    public void SelectMask(int index)
    {
        int count = _settings?.Masks?.Count ?? 0;
        int next = index >= 0 && index < count ? index : -1;
        if (next == _selectedMask)
            return;
        _selectedMask = next;
        _maskHover = MaskPart.None;
        _maskHoverIndex = -1;
        if (next < 0 && _placing == null && !_cropTool && !_spotTool)
            Cursor = null;
        InvalidatePaint();
    }

    public void BeginMaskPlacement(MaskShape shape)
    {
        if (_cropTool)
            CancelCrop();
        EndSpotTool();
        _placing = shape;
        _draft.Clear();
        _pendingDraftClick = false;
        Cursor = UiCursor.Crosshair;
        InvalidatePaint();
    }

    public void CancelMaskPlacement()
    {
        if (_placing == null)
            return;
        EndPlacement();
    }

    private void EndPlacement()
    {
        _placing = null;
        _draft.Clear();
        _pendingDraftClick = false;
        Cursor = null;
        InvalidatePaint();
        MaskPlacementEnded?.Invoke();
    }

    /// <summary>Closes the spline being drawn into a mask (needs three points). False when there is nothing to close.</summary>
    public bool FinishSplineDraft()
    {
        if (_placing != MaskShape.Spline)
            return false;
        if (_draft.Count < 3)
        {
            EndPlacement();
            return false;
        }
        var mask = new MaskLayer { Shape = MaskShape.Spline, Feather = 25f };
        foreach (var p in _draft)
            mask.Points.Add(new MaskPoint(p.X, p.Y));
        AddMask(mask);
        MaskEditEnded?.Invoke();
        EndPlacement();
        return true;
    }

    public void RemoveLastDraftPoint()
    {
        if (_draft.Count == 0)
            return;
        _draft.RemoveAt(_draft.Count - 1);
        InvalidatePaint();
    }

    /// <summary>The mask list or a mask changed from outside the pane (sliders, undo, delete).</summary>
    public void InvalidateMaskLook(bool fast)
    {
        _lookFast = fast;
        _histogramDirty = true;
        _look.InvalidateMasks();
        _tileLook.InvalidateMasks();
        if (_selectedMask >= (_settings?.Masks?.Count ?? 0))
            _selectedMask = -1;
        InvalidatePaint();
    }

    private void AddMask(MaskLayer mask)
    {
        var masks = _settings!.Masks ??= new List<MaskLayer>();
        MaskEditBegan?.Invoke();
        mask.Name = MaskLayer.NextName(masks, mask.Shape);
        masks.Add(mask);
        _selectedMask = masks.Count - 1;
        MaskPicked?.Invoke(_selectedMask);
        MaskChangedLive();
    }

    private void MaskChangedLive()
    {
        _look.InvalidateMasks();
        _tileLook.InvalidateMasks();
        _histogramDirty = true;
        InvalidatePaint();
        MasksChanged?.Invoke();
    }

    // ---- coordinates -------------------------------------------------------------------------------

    private SKPoint UvToScreen(float u, float v)
    {
        GetFrameSize(out int fw, out int fh);
        DevelopGeom.SourceUvToDest(u, v, ImageDest, _settings, fw, fh, out float x, out float y);
        return new SKPoint(x, y);
    }

    private bool ScreenToUv(float lx, float ly, out float u, out float v)
    {
        GetFrameSize(out int fw, out int fh);
        return DevelopGeom.DestToSourceUv(lx, ly, ImageDest, _settings, fw, fh, out u, out v, slack: 8f);
    }

    /// <summary>Mask-local (isotropic, rotated) offset from the center to UV.</summary>
    private void LocalToUv(MaskLayer m, float lx, float ly, out float u, out float v)
    {
        float a = m.Angle * (MathF.PI / 180f);
        float c = MathF.Cos(a), s = MathF.Sin(a);
        float ix = m.CenterX * MaskAspect + lx * c - ly * s;
        float iy = m.CenterY + lx * s + ly * c;
        u = ix / MaskAspect;
        v = iy;
    }

    private SKPoint LocalToScreen(MaskLayer m, float lx, float ly)
    {
        LocalToUv(m, lx, ly, out float u, out float v);
        return UvToScreen(u, v);
    }

    /// <summary>Isotropic offset of UV (u, v) from the mask center, in the mask's rotated frame.</summary>
    private void UvToLocal(MaskLayer m, float u, float v, out float lx, out float ly)
    {
        float dx = (u - m.CenterX) * MaskAspect;
        float dy = v - m.CenterY;
        float a = m.Angle * (MathF.PI / 180f);
        float c = MathF.Cos(a), s = MathF.Sin(a);
        lx = dx * c + dy * s;
        ly = -dx * s + dy * c;
    }

    /// <summary>Angle (degrees, isotropic source space) that the screen's x axis points along at (lx, ly).</summary>
    private float ScreenAxisAngle(float lx, float ly)
    {
        if (!ScreenToUv(lx, ly, out float u0, out float v0) || !ScreenToUv(lx + 10f, ly, out float u1, out float v1))
            return 0f;
        return MathF.Atan2(v1 - v0, (u1 - u0) * MaskAspect) * (180f / MathF.PI);
    }

    private SKPoint PinScreen(MaskLayer m)
    {
        switch (m.Shape)
        {
            case MaskShape.Linear:
                return UvToScreen((m.StartX + m.EndX) * 0.5f, (m.StartY + m.EndY) * 0.5f);
            case MaskShape.Spline:
                Centroid(m, out float cu, out float cv);
                return UvToScreen(cu, cv);
            default:
                return UvToScreen(m.CenterX, m.CenterY);
        }
    }

    private static void Centroid(MaskLayer m, out float u, out float v)
    {
        u = v = 0.5f;
        int n = m.Points?.Count ?? 0;
        if (n == 0)
            return;
        float su = 0f, sv = 0f;
        foreach (var p in m.Points!)
        {
            su += p.X;
            sv += p.Y;
        }
        u = su / n;
        v = sv / n;
    }

    private SKPoint RotateHandle(MaskLayer m, out SKPoint edge)
    {
        edge = LocalToScreen(m, 0f, -m.RadiusY);
        var c = UvToScreen(m.CenterX, m.CenterY);
        float dx = edge.X - c.X, dy = edge.Y - c.Y;
        float len = MathF.Max(1e-3f, MathF.Sqrt(dx * dx + dy * dy));
        return new SKPoint(edge.X + dx / len * MaskRotateGap, edge.Y + dy / len * MaskRotateGap);
    }

    // ---- hit testing -------------------------------------------------------------------------------

    private void HitMask(float lx, float ly, out MaskPart part, out int index) =>
        (part, index) = HitMaskAt(lx, ly);

    private (MaskPart Part, int Index) HitMaskAt(float lx, float ly)
    {
        var part = MaskPart.None;
        int index = -1;
        if (_settings?.Masks == null)
            return (part, index);
        var sel = SelectedMask;
        float best = MaskHitR * MaskHitR;

        void Try(SKPoint p, MaskPart what, int idx)
        {
            float d = (p.X - lx) * (p.X - lx) + (p.Y - ly) * (p.Y - ly);
            if (d <= best)
            {
                best = d;
                part = what;
                index = idx;
            }
        }

        if (sel != null)
        {
            switch (sel.Shape)
            {
                case MaskShape.Linear:
                    Try(UvToScreen(sel.StartX, sel.StartY), MaskPart.Start, -1);
                    Try(UvToScreen(sel.EndX, sel.EndY), MaskPart.End, -1);
                    break;
                case MaskShape.Spline:
                    for (int i = 0; i < sel.Points.Count; i++)
                        Try(UvToScreen(sel.Points[i].X, sel.Points[i].Y), MaskPart.Point, i);
                    break;
                default:
                    Try(LocalToScreen(sel, sel.RadiusX, 0f), MaskPart.AxisX, 1);
                    Try(LocalToScreen(sel, -sel.RadiusX, 0f), MaskPart.AxisX, -1);
                    Try(LocalToScreen(sel, 0f, sel.RadiusY), MaskPart.AxisY, 1);
                    Try(LocalToScreen(sel, 0f, -sel.RadiusY), MaskPart.AxisY, -1);
                    Try(RotateHandle(sel, out _), MaskPart.Rotate, -1);
                    break;
            }
            if (part != MaskPart.None)
                return (part, index);
            Try(PinScreen(sel), MaskPart.Center, -1);
            if (part != MaskPart.None)
                return (part, index);
        }

        for (int i = 0; i < _settings.Masks.Count; i++)
        {
            if (i != _selectedMask)
                Try(PinScreen(_settings.Masks[i]), MaskPart.Pin, i);
        }
        if (part != MaskPart.None || sel == null || sel.Shape != MaskShape.Spline || sel.Points.Count < 3)
            return (part, index);

        // Near the outline: a press inserts a point on that segment.
        int seg = NearestSplineSegment(sel, lx, ly, out float dist);
        if (seg >= 0 && dist <= MaskCurveHit)
            return (MaskPart.Curve, seg);
        return (part, index);
    }

    private int NearestSplineSegment(MaskLayer m, float lx, float ly, out float dist)
    {
        dist = float.MaxValue;
        int best = -1;
        int n = m.Points.Count;
        for (int i = 0; i < n; i++)
        {
            MaskRaster.SplineSegment(m.Points, i, out float c1x, out float c1y, out float c2x, out float c2y, out float ex, out float ey);
            var p0 = UvToScreen(m.Points[i].X, m.Points[i].Y);
            var p1 = UvToScreen(c1x, c1y);
            var p2 = UvToScreen(c2x, c2y);
            var p3 = UvToScreen(ex, ey);
            var prev = p0;
            for (int k = 1; k <= 16; k++)
            {
                var q = Bezier(p0, p1, p2, p3, k / 16f);
                float d = SegmentDistance(lx, ly, prev, q);
                if (d < dist)
                {
                    dist = d;
                    best = i;
                }
                prev = q;
            }
        }
        return best;
    }

    private static SKPoint Bezier(SKPoint p0, SKPoint p1, SKPoint p2, SKPoint p3, float t)
    {
        float mt = 1f - t;
        float a = mt * mt * mt, b = 3f * mt * mt * t, c = 3f * mt * t * t, d = t * t * t;
        return new SKPoint(a * p0.X + b * p1.X + c * p2.X + d * p3.X, a * p0.Y + b * p1.Y + c * p2.Y + d * p3.Y);
    }

    private static float SegmentDistance(float px, float py, SKPoint a, SKPoint b)
    {
        float vx = b.X - a.X, vy = b.Y - a.Y;
        float len2 = vx * vx + vy * vy;
        float t = len2 > 1e-6f ? Math.Clamp(((px - a.X) * vx + (py - a.Y) * vy) / len2, 0f, 1f) : 0f;
        float dx = a.X + vx * t - px, dy = a.Y + vy * t - py;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    // ---- pointer -----------------------------------------------------------------------------------

    /// <summary>True when the press belongs to mask placement or editing.</summary>
    private bool OnMaskPointerDown(float lx, float ly)
    {
        if (!MaskMode || _settings == null)
            return false;

        if (_placing is MaskShape shape)
        {
            if (shape == MaskShape.Spline)
            {
                // A click adds a point (on release, so a drag can still pan).
                _pendingDraftClick = true;
                return false;
            }
            if (!ScreenToUv(lx, ly, out float u0, out float v0))
                return true;
            _maskDownU = u0;
            _maskDownV = v0;
            var mask = new MaskLayer { Shape = shape, Feather = shape == MaskShape.Rectangle ? 30f : 50f };
            ShapeFromDrag(mask, lx, ly, lx, ly, defaultSize: true);
            AddMask(mask);
            _maskDrag = MaskPart.Create;
            return true;
        }

        HitMask(lx, ly, out var part, out int index);
        if (part == MaskPart.None)
            return false;

        if (part == MaskPart.Pin)
        {
            MaskPicked?.Invoke(index);
            return true;
        }

        var sel = SelectedMask!;
        if (part == MaskPart.Point && AltHeld)
        {
            if (sel.Points.Count > 3)
            {
                MaskEditBegan?.Invoke();
                sel.Points.RemoveAt(index);
                MaskChangedLive();
                MaskEditEnded?.Invoke();
            }
            return true;
        }

        if (!ScreenToUv(lx, ly, out float u, out float v))
            return true;
        MaskEditBegan?.Invoke();
        if (part == MaskPart.Curve)
        {
            sel.Points.Insert(index + 1, new MaskPoint(u, v));
            part = MaskPart.Point;
            index++;
            MaskChangedLive();
        }

        _maskDrag = part;
        _maskDragIndex = index;
        _maskDownU = u;
        _maskDownV = v;
        _maskDownState = sel.Clone();
        // Keep the grab offset so a handle doesn't jump to the pointer.
        SKPoint anchor = part switch
        {
            MaskPart.Start => UvToScreen(sel.StartX, sel.StartY),
            MaskPart.End => UvToScreen(sel.EndX, sel.EndY),
            MaskPart.Point => UvToScreen(sel.Points[index].X, sel.Points[index].Y),
            MaskPart.AxisX => LocalToScreen(sel, sel.RadiusX * index, 0f),
            MaskPart.AxisY => LocalToScreen(sel, 0f, sel.RadiusY * index),
            MaskPart.Rotate => new SKPoint(lx, ly),
            _ => new SKPoint(lx, ly)
        };
        _maskGrabX = lx - anchor.X;
        _maskGrabY = ly - anchor.Y;
        InvalidatePaint();
        return true;
    }

    private bool OnMaskPointerMove(float lx, float ly)
    {
        if (_maskDrag == MaskPart.None)
            return false;
        var sel = SelectedMask;
        if (sel == null)
        {
            _maskDrag = MaskPart.None;
            return false;
        }

        if (_maskDrag == MaskPart.Create)
        {
            var p0 = UvToScreen(_maskDownU, _maskDownV);
            ShapeFromDrag(sel, p0.X, p0.Y, lx, ly, defaultSize: false);
            _lookFast = true;
            MaskChangedLive();
            return true;
        }

        if (!ScreenToUv(lx - _maskGrabX, ly - _maskGrabY, out float u, out float v))
            return true;
        var s0 = _maskDownState!;
        float du = u - _maskDownU, dv = v - _maskDownV;
        switch (_maskDrag)
        {
            case MaskPart.Center:
                // Grab offset is applied to the pointer, so compare raw pointer UVs.
                ScreenToUv(lx, ly, out float pu, out float pv);
                du = pu - _maskDownU;
                dv = pv - _maskDownV;
                sel.CenterX = s0.CenterX + du;
                sel.CenterY = s0.CenterY + dv;
                sel.StartX = s0.StartX + du;
                sel.StartY = s0.StartY + dv;
                sel.EndX = s0.EndX + du;
                sel.EndY = s0.EndY + dv;
                for (int i = 0; i < sel.Points.Count && i < s0.Points.Count; i++)
                {
                    sel.Points[i].X = s0.Points[i].X + du;
                    sel.Points[i].Y = s0.Points[i].Y + dv;
                }
                break;
            case MaskPart.AxisX:
            case MaskPart.AxisY:
            {
                UvToLocal(sel, u, v, out float qx, out float qy);
                float r = MathF.Max(MaskLayer.MinRadius, MathF.Abs(_maskDrag == MaskPart.AxisX ? qx : qy));
                if (_maskDrag == MaskPart.AxisX) sel.RadiusX = r; else sel.RadiusY = r;
                if (ShiftHeld)
                    sel.RadiusX = sel.RadiusY = r;
                break;
            }
            case MaskPart.Rotate:
            {
                ScreenToUv(lx, ly, out float ru, out float rv);
                float ax = (ru - sel.CenterX) * MaskAspect, ay = rv - sel.CenterY;
                float deg = MathF.Atan2(ay, ax) * (180f / MathF.PI) + 90f;
                if (ShiftHeld)
                    deg = MathF.Round(deg / 15f) * 15f;
                sel.Angle = NormalizeAngle(deg);
                break;
            }
            case MaskPart.Start:
                sel.StartX = u;
                sel.StartY = v;
                break;
            case MaskPart.End:
                sel.EndX = u;
                sel.EndY = v;
                break;
            case MaskPart.Point when _maskDragIndex >= 0 && _maskDragIndex < sel.Points.Count:
                sel.Points[_maskDragIndex].X = u;
                sel.Points[_maskDragIndex].Y = v;
                break;
        }
        _lookFast = true;
        MaskChangedLive();
        return true;
    }

    /// <summary>True when the release belonged to the mask tools (no click-zoom afterwards).</summary>
    private bool OnMaskPointerUp(float lx, float ly, bool wasDrag)
    {
        if (_maskDrag != MaskPart.None)
        {
            bool created = _maskDrag == MaskPart.Create;
            _maskDrag = MaskPart.None;
            _maskDragIndex = -1;
            _maskDownState = null;
            _lookFast = false;
            MaskEditEnded?.Invoke();
            if (created)
                EndPlacement();
            InvalidatePaint();
            return true;
        }

        if (_placing == MaskShape.Spline)
        {
            bool click = _pendingDraftClick && !wasDrag;
            _pendingDraftClick = false;
            if (click)
                AddDraftPoint(lx, ly);
            return true;
        }

        // While a mask is selected a stray click shouldn't zoom the photo.
        return MaskMode;
    }

    private void AddDraftPoint(float lx, float ly)
    {
        if (_draft.Count >= 3)
        {
            var first = UvToScreen(_draft[0].X, _draft[0].Y);
            float dx = first.X - lx, dy = first.Y - ly;
            if (dx * dx + dy * dy <= MaskHitR * MaskHitR)
            {
                FinishSplineDraft();
                return;
            }
        }
        if (!ScreenToUv(lx, ly, out float u, out float v))
            return;
        _draft.Add(new MaskPoint(u, v));
        InvalidatePaint();
    }

    private void OnMaskHover(float lx, float ly)
    {
        if (!MaskMode)
            return;
        if (_placing != null)
        {
            Cursor = UiCursor.Crosshair;
            if (_placing == MaskShape.Spline)
                InvalidatePaint();
            return;
        }
        HitMask(lx, ly, out var part, out int index);
        if (part != _maskHover || index != _maskHoverIndex)
        {
            _maskHover = part;
            _maskHoverIndex = index;
            Cursor = part switch
            {
                MaskPart.None => null,
                MaskPart.Center => UiCursor.ResizeAll,
                MaskPart.Curve => UiCursor.Crosshair,
                _ => UiCursor.Hand
            };
            InvalidatePaint();
        }
    }

    /// <summary>Shift+scroll scales the selected mask, Ctrl+scroll changes its feather.</summary>
    private bool OnMaskScroll(float wheelY)
    {
        var sel = SelectedMask;
        if (!MaskMode || sel == null || _placing != null)
            return false;
        bool size = ShiftHeld && !CtrlHeld;
        bool feather = CtrlHeld && sel.HasFeather;
        if (!size && !feather)
            return false;

        MaskEditBegan?.Invoke();
        if (feather)
        {
            sel.Feather = Math.Clamp(sel.Feather + MathF.Sign(wheelY) * 4f, 0f, 100f);
        }
        else
        {
            float k = MathF.Pow(1.06f, MathF.Sign(wheelY));
            switch (sel.Shape)
            {
                case MaskShape.Linear:
                {
                    float mu = (sel.StartX + sel.EndX) * 0.5f, mv = (sel.StartY + sel.EndY) * 0.5f;
                    sel.StartX = mu + (sel.StartX - mu) * k;
                    sel.StartY = mv + (sel.StartY - mv) * k;
                    sel.EndX = mu + (sel.EndX - mu) * k;
                    sel.EndY = mv + (sel.EndY - mv) * k;
                    break;
                }
                case MaskShape.Spline:
                {
                    Centroid(sel, out float cu, out float cv);
                    foreach (var p in sel.Points)
                    {
                        p.X = cu + (p.X - cu) * k;
                        p.Y = cv + (p.Y - cv) * k;
                    }
                    break;
                }
                default:
                    sel.RadiusX = MathF.Max(MaskLayer.MinRadius, sel.RadiusX * k);
                    sel.RadiusY = MathF.Max(MaskLayer.MinRadius, sel.RadiusY * k);
                    break;
            }
        }
        MaskChangedLive();
        MaskEditEnded?.Invoke();
        return true;
    }

    /// <summary>
    /// Geometry of a shape being dragged out from screen (x0, y0) to (x1, y1), axis-aligned to the screen:
    /// radial grows from its center, rectangle spans corner to corner, linear runs start to end.
    /// A click (no drag) gives a default size.
    /// </summary>
    private void ShapeFromDrag(MaskLayer m, float x0, float y0, float x1, float y1, bool defaultSize)
    {
        if (!ScreenToUv(x0, y0, out float u0, out float v0))
            return;
        float dist = MathF.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
        if (defaultSize || dist < DragSlop)
        {
            float h = MathF.Max(40f, ImageDest.Height * 0.22f);
            switch (m.Shape)
            {
                case MaskShape.Linear:
                    x1 = x0;
                    y1 = y0 + h;
                    break;
                case MaskShape.Rectangle:
                    x0 -= h * 0.7f;
                    y0 -= h * 0.5f;
                    x1 = x0 + h * 1.4f;
                    y1 = y0 + h;
                    ScreenToUv(x0, y0, out u0, out v0);
                    break;
                default:
                    x1 = x0 + h * 0.7f;
                    y1 = y0 + h * 0.5f;
                    break;
            }
        }
        if (!ScreenToUv(x1, y1, out float u1, out float v1))
            return;

        if (m.Shape == MaskShape.Linear)
        {
            m.StartX = u0;
            m.StartY = v0;
            m.EndX = u1;
            m.EndY = v1;
            return;
        }

        float angle = ScreenAxisAngle(x0, y0);
        float a = angle * (MathF.PI / 180f);
        float c = MathF.Cos(a), s = MathF.Sin(a);
        float dx = (u1 - u0) * MaskAspect, dy = v1 - v0;
        float px = dx * c + dy * s;
        float py = -dx * s + dy * c;
        m.Angle = NormalizeAngle(angle);
        if (m.Shape == MaskShape.Rectangle)
        {
            m.CenterX = (u0 + u1) * 0.5f;
            m.CenterY = (v0 + v1) * 0.5f;
            px *= 0.5f;
            py *= 0.5f;
        }
        else
        {
            m.CenterX = u0;
            m.CenterY = v0;
        }
        m.RadiusX = MathF.Max(MaskLayer.MinRadius, MathF.Abs(px));
        m.RadiusY = MathF.Max(MaskLayer.MinRadius, MathF.Abs(py));
        if (ShiftHeld)
            m.RadiusX = m.RadiusY = MathF.Max(m.RadiusX, m.RadiusY);
    }

    private static float NormalizeAngle(float deg)
    {
        deg %= 360f;
        if (deg > 180f) deg -= 360f;
        if (deg <= -180f) deg += 360f;
        return deg;
    }

    // ---- drawing -----------------------------------------------------------------------------------

    private void DrawMasks(SKCanvas canvas, SKRect pane)
    {
        if (!MaskMode || _settings == null)
            return;
        var masks = _settings.Masks;
        var sel = SelectedMask;

        canvas.Save();
        canvas.ClipRect(pane);

        if (sel != null && (_maskOverlay || _maskDrag != MaskPart.None))
            DrawMaskOverlay(canvas, sel, pane);

        if (masks != null)
        {
            for (int i = 0; i < masks.Count; i++)
            {
                if (i == _selectedMask)
                    continue;
                bool hover = _maskHover == MaskPart.Pin && _maskHoverIndex == i;
                DrawPin(canvas, PinScreen(masks[i]), selected: false, hover, masks[i].Enabled);
            }
        }

        if (sel != null)
            DrawMaskShape(canvas, sel);

        if (_placing == MaskShape.Spline)
            DrawDraft(canvas);

        canvas.Restore();
    }

    private void DrawMaskShape(SKCanvas canvas, MaskLayer m)
    {
        using var under = new SKPaint { Color = new SKColor(0, 0, 0, 110), IsAntialias = true, StrokeWidth = 3f, Style = SKPaintStyle.Stroke };
        using var line = new SKPaint { Color = Theme.Handle.WithAlpha(235), IsAntialias = true, StrokeWidth = 1.5f, Style = SKPaintStyle.Stroke };
        using var dash = new SKPaint
        {
            Color = Theme.Handle.WithAlpha(170),
            IsAntialias = true,
            StrokeWidth = 1.1f,
            Style = SKPaintStyle.Stroke,
            PathEffect = SKPathEffect.CreateDash(new[] { 5f, 4f }, 0)
        };

        switch (m.Shape)
        {
            case MaskShape.Radial:
            {
                using var outer = EllipsePath(m, 1f);
                canvas.DrawPath(outer, under);
                canvas.DrawPath(outer, line);
                float inner = 1f - Math.Clamp(m.Feather / 100f, 0f, 1f);
                if (inner > 0.04f && inner < 0.98f)
                {
                    using var feather = EllipsePath(m, inner);
                    canvas.DrawPath(feather, dash);
                }
                DrawBoxHandles(canvas, m, line);
                break;
            }
            case MaskShape.Rectangle:
            {
                using var outer = RectPath(m, 0f);
                canvas.DrawPath(outer, under);
                canvas.DrawPath(outer, line);
                float fade = Math.Clamp(m.Feather / 100f, 0f, 1f) * Math.Min(m.RadiusX, m.RadiusY);
                if (fade > 0.002f && fade < Math.Min(m.RadiusX, m.RadiusY) * 0.98f)
                {
                    using var feather = RectPath(m, fade);
                    canvas.DrawPath(feather, dash);
                }
                DrawBoxHandles(canvas, m, line);
                break;
            }
            case MaskShape.Linear:
                DrawLinear(canvas, m, under, line, dash);
                break;
            case MaskShape.Spline:
            {
                using var path = new SKPath();
                MaskRaster.AppendSpline(path, m.Points, (u, v) => UvToScreen(u, v));
                canvas.DrawPath(path, under);
                canvas.DrawPath(path, line);
                for (int i = 0; i < m.Points.Count; i++)
                {
                    bool hover = (_maskHover == MaskPart.Point && _maskHoverIndex == i)
                        || (_maskDrag == MaskPart.Point && _maskDragIndex == i);
                    DrawHandle(canvas, UvToScreen(m.Points[i].X, m.Points[i].Y), hover);
                }
                break;
            }
        }

        bool pinHover = _maskHover == MaskPart.Center || _maskDrag == MaskPart.Center;
        DrawPin(canvas, PinScreen(m), selected: true, pinHover, m.Enabled);
    }

    private void DrawBoxHandles(SKCanvas canvas, MaskLayer m, SKPaint line)
    {
        var rot = RotateHandle(m, out var edge);
        using (var stem = new SKPaint { Color = Theme.Handle.WithAlpha(150), IsAntialias = true, StrokeWidth = 1f, Style = SKPaintStyle.Stroke })
            canvas.DrawLine(edge, rot, stem);
        DrawHandle(canvas, rot, _maskHover == MaskPart.Rotate || _maskDrag == MaskPart.Rotate, round: true, small: true);

        DrawHandle(canvas, LocalToScreen(m, m.RadiusX, 0f), IsAxisActive(MaskPart.AxisX, 1));
        DrawHandle(canvas, LocalToScreen(m, -m.RadiusX, 0f), IsAxisActive(MaskPart.AxisX, -1));
        DrawHandle(canvas, LocalToScreen(m, 0f, m.RadiusY), IsAxisActive(MaskPart.AxisY, 1));
        DrawHandle(canvas, LocalToScreen(m, 0f, -m.RadiusY), IsAxisActive(MaskPart.AxisY, -1));
    }

    private bool IsAxisActive(MaskPart part, int sign) =>
        (_maskHover == part && _maskHoverIndex == sign) || (_maskDrag == part && _maskDragIndex == sign);

    private SKPath EllipsePath(MaskLayer m, float scale)
    {
        var path = new SKPath();
        const int steps = 72;
        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps * MathF.PI * 2f;
            var p = LocalToScreen(m, MathF.Cos(t) * m.RadiusX * scale, MathF.Sin(t) * m.RadiusY * scale);
            if (i == 0) path.MoveTo(p);
            else path.LineTo(p);
        }
        path.Close();
        return path;
    }

    private SKPath RectPath(MaskLayer m, float inset)
    {
        float rx = MathF.Max(0f, m.RadiusX - inset), ry = MathF.Max(0f, m.RadiusY - inset);
        var path = new SKPath();
        path.MoveTo(LocalToScreen(m, -rx, -ry));
        path.LineTo(LocalToScreen(m, rx, -ry));
        path.LineTo(LocalToScreen(m, rx, ry));
        path.LineTo(LocalToScreen(m, -rx, ry));
        path.Close();
        return path;
    }

    /// <summary>Lines across the photo at the start (full effect), the middle and the end (none).</summary>
    private void DrawLinear(SKCanvas canvas, MaskLayer m, SKPaint under, SKPaint line, SKPaint dash)
    {
        var s = UvToScreen(m.StartX, m.StartY);
        var e = UvToScreen(m.EndX, m.EndY);
        float dx = e.X - s.X, dy = e.Y - s.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        float nx = len > 1e-3f ? -dy / len : 1f, ny = len > 1e-3f ? dx / len : 0f;
        float reach = ImageDest.Width + ImageDest.Height;
        var mid = new SKPoint((s.X + e.X) * 0.5f, (s.Y + e.Y) * 0.5f);

        canvas.Save();
        canvas.ClipRect(ImageDest);
        void Across(SKPoint p, SKPaint paint)
        {
            var a = new SKPoint(p.X - nx * reach, p.Y - ny * reach);
            var b = new SKPoint(p.X + nx * reach, p.Y + ny * reach);
            canvas.DrawLine(a, b, under);
            canvas.DrawLine(a, b, paint);
        }
        Across(s, line);
        Across(mid, dash);
        using (var faint = new SKPaint { Color = Theme.Handle.WithAlpha(150), IsAntialias = true, StrokeWidth = 1.2f, Style = SKPaintStyle.Stroke })
            Across(e, faint);
        canvas.Restore();

        using (var spine = new SKPaint { Color = Theme.Handle.WithAlpha(120), IsAntialias = true, StrokeWidth = 1f, Style = SKPaintStyle.Stroke })
            canvas.DrawLine(s, e, spine);
        DrawHandle(canvas, s, _maskHover == MaskPart.Start || _maskDrag == MaskPart.Start, round: true);
        DrawHandle(canvas, e, _maskHover == MaskPart.End || _maskDrag == MaskPart.End, round: true);
    }

    private void DrawDraft(SKCanvas canvas)
    {
        if (_draft.Count == 0)
            return;
        using var under = new SKPaint { Color = new SKColor(0, 0, 0, 110), IsAntialias = true, StrokeWidth = 3f, Style = SKPaintStyle.Stroke };
        using var line = new SKPaint { Color = Theme.Accent, IsAntialias = true, StrokeWidth = 1.6f, Style = SKPaintStyle.Stroke };

        if (_draft.Count >= 3)
        {
            using var path = new SKPath();
            MaskRaster.AppendSpline(path, _draft, (u, v) => UvToScreen(u, v));
            using var fill = new SKPaint { Color = Theme.Accent.WithAlpha(28), IsAntialias = true };
            canvas.DrawPath(path, fill);
            canvas.DrawPath(path, under);
            canvas.DrawPath(path, line);
        }
        else
        {
            var a = UvToScreen(_draft[0].X, _draft[0].Y);
            for (int i = 1; i < _draft.Count; i++)
            {
                var b = UvToScreen(_draft[i].X, _draft[i].Y);
                canvas.DrawLine(a, b, under);
                canvas.DrawLine(a, b, line);
                a = b;
            }
        }

        // Rubber band from the last point to the pointer.
        var last = UvToScreen(_draft[^1].X, _draft[^1].Y);
        using (var band = new SKPaint
        {
            Color = Theme.Accent.WithAlpha(170),
            IsAntialias = true,
            StrokeWidth = 1.2f,
            Style = SKPaintStyle.Stroke,
            PathEffect = SKPathEffect.CreateDash(new[] { 4f, 4f }, 0)
        })
            canvas.DrawLine(last, new SKPoint(_pointerLocal.X, _pointerLocal.Y), band);

        var first = UvToScreen(_draft[0].X, _draft[0].Y);
        float fdx = first.X - _pointerLocal.X, fdy = first.Y - _pointerLocal.Y;
        bool closing = _draft.Count >= 3 && fdx * fdx + fdy * fdy <= MaskHitR * MaskHitR;
        for (int i = 0; i < _draft.Count; i++)
            DrawHandle(canvas, UvToScreen(_draft[i].X, _draft[i].Y), hover: i == 0 && closing);
        if (closing)
        {
            using var ring = new SKPaint { Color = Theme.Accent, IsAntialias = true, StrokeWidth = 1.5f, Style = SKPaintStyle.Stroke };
            canvas.DrawCircle(first, MaskHandleR + 4f, ring);
        }
    }

    private static void DrawHandle(SKCanvas canvas, SKPoint p, bool hover, bool round = false, bool small = false)
    {
        float r = small ? MaskHandleR - 1f : MaskHandleR;
        using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 120), IsAntialias = true };
        using var fill = new SKPaint { Color = hover ? Theme.Accent : Theme.Handle, IsAntialias = true };
        using var ring = new SKPaint { Color = new SKColor(0, 0, 0, 150), IsAntialias = true, StrokeWidth = 1f, Style = SKPaintStyle.Stroke };
        if (round)
        {
            canvas.DrawCircle(p.X, p.Y + 0.5f, r + 1.5f, shadow);
            canvas.DrawCircle(p, r, fill);
            canvas.DrawCircle(p, r, ring);
        }
        else
        {
            var rect = new SKRect(p.X - r, p.Y - r, p.X + r, p.Y + r);
            var drop = rect;
            drop.Inflate(1.5f, 1.5f);
            drop.Offset(0, 0.5f);
            canvas.DrawRoundRect(drop, 2.5f, 2.5f, shadow);
            canvas.DrawRoundRect(rect, 1.5f, 1.5f, fill);
            canvas.DrawRoundRect(rect, 1.5f, 1.5f, ring);
        }
    }

    /// <summary>Mask pin: the selected mask's is an accent dot in a light ring, others are hollow.</summary>
    private static void DrawPin(SKCanvas canvas, SKPoint p, bool selected, bool hover, bool enabled)
    {
        float r = MaskPinR;
        using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 130), IsAntialias = true };
        canvas.DrawCircle(p.X, p.Y + 0.75f, r + 2f, shadow);
        using var ring = new SKPaint { Color = Theme.Handle, IsAntialias = true };
        canvas.DrawCircle(p, r, ring);
        SKColor core = selected ? Theme.Accent : (hover ? Theme.AccentHover : new SKColor(30, 30, 34));
        if (!enabled)
            core = core.WithAlpha(110);
        using var dot = new SKPaint { Color = core, IsAntialias = true };
        canvas.DrawCircle(p, r - 2.5f, dot);
    }

    /// <summary>Coverage of <paramref name="m"/> tinted over the photo through the source → screen transform.</summary>
    private void DrawMaskOverlay(SKCanvas canvas, MaskLayer m, SKRect pane)
    {
        var atlas = MaskRaster.Single(m, MaskAspect, OverlayLongSide);
        if (!ReferenceEquals(atlas, _overlayAtlas) || _overlayImage == null)
        {
            GpuRetain.Retire(_overlayImage);
            _overlayImage = MaskRaster.CreateImage(atlas);
            _overlayAtlas = atlas;
        }
        if (_overlayImage == null)
            return;

        var p00 = UvToScreen(0f, 0f);
        var p10 = UvToScreen(1f, 0f);
        var p01 = UvToScreen(0f, 1f);
        float iw = _overlayImage.Width, ih = _overlayImage.Height;
        var matrix = new SKMatrix(
            (p10.X - p00.X) / iw, (p01.X - p00.X) / ih, p00.X,
            (p10.Y - p00.Y) / iw, (p01.Y - p00.Y) / ih, p00.Y,
            0f, 0f, 1f);

        // Gray coverage → red at up to 55% opacity (inverted masks tint the outside).
        float k = 0.55f * Math.Clamp(m.Amount / 100f, 0.25f, 1f);
        float[] cm = m.Invert
            ? new[] { 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 0.16f, 0f, 0f, 0f, 0f, 0.24f, -k, 0f, 0f, 0f, k }
            : new[] { 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 0.16f, 0f, 0f, 0f, 0f, 0.24f, k, 0f, 0f, 0f, 0f };
        using var filter = SKColorFilter.CreateColorMatrix(cm);
        using var paint = new SKPaint { ColorFilter = filter, IsAntialias = true };

        var clip = ImageDest;
        clip.Intersect(pane);
        canvas.Save();
        canvas.ClipRect(clip);
        canvas.Concat(in matrix);
        canvas.DrawImage(_overlayImage, 0f, 0f, Sampling.Linear, paint);
        canvas.Restore();
    }
}
