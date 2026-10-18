using System;
using Blossom.Animations;
using Blossom.Core.Input;
using Blossom.Hosting;
using Blossom.Core.Visual.Enums;
using Blossom.Primitives;
using Blossom.Reactive;
using Raw75.Pipeline;
using SkiaSharp;

namespace Raw75.Controls;

/// <summary>
/// Hue/saturation disk with a luminance slider under it, on <see cref="Control"/>.
/// The hue disk is cached as an SKImage and rebuilt when its pixel size or the theme changes
/// (its neutral centre and dimming are mixed from theme tokens).
/// </summary>
public sealed class ColorWheelControl : Control
{
    public const float LabelH = 16f;
    public const float LumH = 20f;
    public const float Gap = 8f;
    private const float SunSlot = 18f;
    private const float PuckR = 6.5f;

    private float _hue;
    private float _sat;
    private float _lum;
    private bool _draggingWheel;
    private bool _draggingLum;
    private bool _labelHovered;
    private float _hover;
    private float _drag;
    private SKRect _wheelRect;
    private SKRect _lumRect;
    private SKRect _labelRect;
    private SKImage? _disk;
    private int _diskSize = -1;
    private float _lastGlobalX;

    public string RangeLabel { get; }

    public float Hue => _hue;
    public float Saturation => _sat;
    public float Luminance => _lum;

    public event Action? Changed;
    public event Action? DragStarted;
    public event Action? DragEnded;

    public ColorWheelControl(string label)
    {
        RangeLabel = label ?? "";
        Name = $"ColorWheel_{RangeLabel}";
        Cursor = UiCursor.Hand;
        Overflow = OverflowMode.Visible;
        Style.BackColor = SKColors.Transparent;

        Events.OnMouseDown += OnMouseDown;
        Events.OnMouseMove += OnMouseMove;
        Events.OnMouseUp += OnMouseUp;
        Events.OnMouseDoubleClick += OnDoubleClick;

        this.Bind(_ =>
        {
            Theme.Observe();
            DropDisk();
            InvalidatePaint();
        });
    }

    protected override void OnHoverChanged()
    {
        _hover = IsHovered ? 1f : 0f;
        InvalidatePaint();
        if (!IsHovered)
            _labelHovered = false;
    }

    public static float HeightForWidth(float width)
    {
        float wheel = Math.Max(48f, width);
        return LabelH + wheel + Gap + LumH;
    }

    /// <summary>Fits the widest wheel the box allows: <paramref name="maxHeight"/>, when given, caps the disk.</summary>
    public override SKSize GetPreferredSize(float maxWidth, float maxHeight)
    {
        float w = maxWidth > 0 ? maxWidth : 112f;
        if (maxHeight > 0)
            w = Math.Min(w, Math.Max(48f, maxHeight - LabelH - Gap - LumH));
        return new SKSize(w, HeightForWidth(w));
    }

    public void Set(float hue, float sat, float lum, bool fire = false)
    {
        _hue = WrapHue(hue);
        _sat = Math.Clamp(sat, 0f, 100f);
        _lum = Math.Clamp(lum, -100f, 100f);
        InvalidatePaint();
        if (fire)
            Changed?.Invoke();
    }

    public void Reset(bool fire = true)
    {
        Set(0f, 0f, 0f, fire);
    }

    private bool IsModified => _sat > 0.05f || Math.Abs(_lum) > 0.05f;

    protected override void Paint(SKCanvas canvas)
    {
        float w = W;
        float h = H;
        if (w < 24f || h < 40f)
            return;

        _labelRect = new SKRect(0, 0, w, LabelH);
        float wheel = Math.Max(8f, Math.Min(w, h - LabelH - Gap - LumH));
        float wheelX = (w - wheel) * 0.5f;
        _wheelRect = new SKRect(wheelX, LabelH, wheelX + wheel, LabelH + wheel);
        _lumRect = new SKRect(SunSlot, h - LumH, w, h);

        DrawLabel(canvas);
        DrawDisk(canvas, _wheelRect);
        DrawPuck(canvas, _wheelRect);
        DrawSun(canvas, SunSlot * 0.5f, _lumRect.MidY);
        DrawLumSlider(canvas, _lumRect);
    }

    private void DrawLabel(SKCanvas canvas)
    {
        bool live = _draggingWheel || _draggingLum;
        using var paint = new TextPaint
        {
            TextSize = 11f,
            Typeface = live ? Theme.MonospaceTypeface : Theme.GetTypeface(600),
            TextAlign = SKTextAlign.Center,
            Color = live ? Theme.Accent : (_labelHovered ? Theme.Text : Theme.TextSecondary)
        };

        string text;
        if (_draggingWheel)
            text = $"H {MathF.Round(_hue):0}°  S {MathF.Round(_sat):0}";
        else if (_draggingLum)
            text = $"L {_lum:+0;-0;0}";
        else if (_labelHovered)
            text = "Reset";
        else
            text = RangeLabel;

        var m = paint.FontMetrics;
        float baseline = _labelRect.MidY - (m.Ascent + m.Descent) * 0.5f;
        canvas.DrawText(text, _labelRect.MidX, baseline, paint);

        // Modified marker beside the range name, like an amber slider readout.
        if (!live && !_labelHovered && IsModified)
        {
            using var dot = new SKPaint { Color = Theme.Modified, IsAntialias = true };
            float tx = _labelRect.MidX + paint.MeasureText(text) * 0.5f + 6f;
            canvas.DrawCircle(tx, baseline - 3.5f, 2f, dot);
        }
    }

    private void DrawDisk(SKCanvas canvas, SKRect wheel)
    {
        int size = Math.Max(16, (int)MathF.Round(wheel.Width));
        EnsureDisk(size);

        using (var well = new SKPaint { Color = Theme.Well, IsAntialias = true })
            canvas.DrawOval(wheel, well);

        if (_disk != null)
            canvas.DrawImage(_disk, wheel, Sampling.Linear);

        float cx = wheel.MidX;
        float cy = wheel.MidY;
        float lit = Math.Max(_hover, _drag);

        // Neutral centre crosshair.
        using (var cross = new SKPaint { Color = Theme.Well.WithAlpha(150), StrokeWidth = 1f, IsAntialias = true })
        {
            const float Arm = 4.5f;
            float px = MathF.Round(cx) + 0.5f;
            float py = MathF.Round(cy) + 0.5f;
            canvas.DrawLine(px - Arm, py, px + Arm, py, cross);
            canvas.DrawLine(px, py - Arm, px, py + Arm, cross);
        }

        using var rim = new SKPaint
        {
            Color = Anim.Lerp(Theme.Hairline, Theme.HairlineStrong, lit),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            IsAntialias = true
        };
        var r = wheel;
        r.Inflate(0.5f, 0.5f);
        canvas.DrawOval(r, rim);
    }

    private void DrawPuck(SKCanvas canvas, SKRect wheel)
    {
        float cx = wheel.MidX;
        float cy = wheel.MidY;
        float r = wheel.Width * 0.5f - 2f;
        float rad = _hue * (MathF.PI / 180f);
        float dist = (_sat / 100f) * r;
        float px = cx + MathF.Cos(rad) * dist;
        float py = cy + MathF.Sin(rad) * dist;
        float drag = _drag;
        float pr = PuckR + 1.5f * drag;

        ColorGradeMath.HsvToRgb(_hue, _sat / 100f, 1f, out float rr, out float gg, out float bb);
        SKColor fill = _sat > 5f
            ? new SKColor((byte)(rr * 255f), (byte)(gg * 255f), (byte)(bb * 255f))
            : Theme.WellHover;

        if (drag > 0.01f)
        {
            using var haloBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3f);
            using var halo = new SKPaint { Color = Theme.Accent.WithAlpha((byte)(100 * drag)), MaskFilter = haloBlur, IsAntialias = true };
            canvas.DrawCircle(px, py, pr + 3.5f, halo);
        }

        using (var blur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1.6f))
        using (var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 130), MaskFilter = blur, IsAntialias = true })
            canvas.DrawCircle(px, py + 1f, pr + 1f, shadow);

        using (var fillPaint = new SKPaint { Color = fill, IsAntialias = true })
            canvas.DrawCircle(px, py, pr, fillPaint);

        using (var ring = new SKPaint { Color = Theme.Handle, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f })
            canvas.DrawCircle(px, py, pr - 1f, ring);

        using (var outline = new SKPaint { Color = new SKColor(0, 0, 0, 150), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f })
            canvas.DrawCircle(px, py, pr + 0.5f, outline);

        if (drag > 0.01f)
        {
            using var accent = new SKPaint
            {
                Color = Theme.Accent.WithAlpha((byte)(255 * drag)),
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.5f
            };
            canvas.DrawCircle(px, py, pr + 2.5f, accent);
        }
    }

    private static void DrawSun(SKCanvas canvas, float cx, float cy)
    {
        using var paint = new SKPaint
        {
            Color = Theme.TextDim,
            IsAntialias = true,
            StrokeWidth = 1.15f,
            StrokeCap = SKStrokeCap.Round,
            Style = SKPaintStyle.Stroke
        };
        canvas.DrawCircle(cx, cy, 2.6f, paint);
        for (int i = 0; i < 8; i++)
        {
            float a = i * (MathF.PI / 4f);
            float c = MathF.Cos(a);
            float s = MathF.Sin(a);
            canvas.DrawLine(cx + c * 4.1f, cy + s * 4.1f, cx + c * 6.4f, cy + s * 6.4f, paint);
        }
    }

    private void DrawLumSlider(SKCanvas canvas, SKRect lum)
    {
        canvas.Save();
        canvas.Translate(lum.Left, lum.Top);
        float t = Math.Clamp((_lum + 100f) / 200f, 0f, 1f);
        SliderChrome.Draw(canvas, lum.Width, lum.Height, t, 0.5f, bipolar: true, _hover, _draggingLum);
        canvas.Restore();
    }

    private void DropDisk()
    {
        _disk?.Dispose();
        _disk = null;
        _diskSize = -1;
    }

    private void EnsureDisk(int size)
    {
        if (_disk != null && _diskSize == size)
            return;
        DropDisk();
        _diskSize = size;

        var info = new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        if (surface == null)
            return;

        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        var center = new SKPoint(size * 0.5f, size * 0.5f);
        float radius = size * 0.5f - 1f;

        using (var sweep = SKShader.CreateSweepGradient(
                   center,
                   new[]
                   {
                       SKColors.Red, SKColors.Yellow, SKColors.Lime, SKColors.Cyan,
                       SKColors.Blue, SKColors.Magenta, SKColors.Red
                   }))
        using (var paint = new SKPaint { Shader = sweep, IsAntialias = true })
        {
            canvas.DrawCircle(center, radius, paint);
        }

        // Neutral light-grey centre rather than pure white, so the disk sits in the dark chrome.
        SKColor neutral = Anim.Lerp(Theme.Well, Theme.Text, 0.72f);
        using (var radial = SKShader.CreateRadialGradient(
                   center,
                   radius,
                   new[] { neutral, neutral.WithAlpha(0) },
                   new[] { 0f, 1f },
                   SKShaderTileMode.Clamp))
        using (var paint = new SKPaint { Shader = radial, IsAntialias = true })
        {
            canvas.DrawCircle(center, radius, paint);
        }

        // A light veil of the well tone mutes the rim saturation a touch.
        using (var veil = new SKPaint { Color = Theme.Well.WithAlpha(46), IsAntialias = true })
            canvas.DrawCircle(center, radius, veil);

        _disk = surface.Snapshot();
    }

    private void OnMouseDown(object sender, MouseEventArgs args)
    {
        if (args.Button != PointerButton.Left)
            return;
        var local = PointToClient(args.Global.X, args.Global.Y);
        _lastGlobalX = args.Global.X;

        if (_labelRect.Contains(local.X, local.Y))
        {
            Reset(fire: true);
            args.Handled = true;
            return;
        }

        if (HitWheel(local.X, local.Y))
        {
            _draggingWheel = true;
            _drag = 1f;
            InvalidatePaint();
            CapturePointer();
            DragStarted?.Invoke();
            ApplyWheel(local.X, local.Y);
            args.Handled = true;
            return;
        }

        if (local.X >= 0f && local.X < SunSlot && _lumRect.Contains(SunSlot, local.Y))
        {
            Set(_hue, _sat, 0f, fire: true);
            args.Handled = true;
            return;
        }

        if (_lumRect.Contains(local.X, local.Y))
        {
            _draggingLum = true;
            CapturePointer();
            DragStarted?.Invoke();
            ApplyLum(local.X);
            args.Handled = true;
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs args)
    {
        var local = PointToClient(args.Global.X, args.Global.Y);
        bool labelHover = _labelRect.Contains(local.X, local.Y);
        if (labelHover != _labelHovered)
        {
            _labelHovered = labelHover;
            InvalidatePaint();
        }
        if (!_draggingWheel && !_draggingLum)
            Cursor = _lumRect.Contains(local.X, local.Y) ? UiCursor.HResize : UiCursor.Hand;

        if (!HasPointerCapture)
            return;
        args.Handled = true;

        if (_draggingWheel)
        {
            ApplyWheel(local.X, local.Y);
        }
        else if (_draggingLum)
        {
            bool shift = (ParentView?.Events.IsShiftDown ?? false) || Events.IsShiftDown;
            if (shift)
            {
                float dx = args.Global.X - _lastGlobalX;
                _lastGlobalX = args.Global.X;
                float travel = Math.Max(1f, _lumRect.Width - Theme.ThumbW);
                Set(Hue, Saturation, _lum + (dx / travel) * 200f * 0.25f, fire: true);
            }
            else
            {
                _lastGlobalX = args.Global.X;
                ApplyLum(local.X);
            }
        }
    }

    private void OnMouseUp(object sender, MouseEventArgs args)
    {
        if (!HasPointerCapture && !_draggingWheel && !_draggingLum)
            return;
        EndDrag();
        args.Handled = true;
    }

    private void OnDoubleClick(object sender, MouseEventArgs args)
    {
        if (args.Button != PointerButton.Left)
            return;
        var local = PointToClient(args.Global.X, args.Global.Y);
        if (HitWheel(local.X, local.Y) || _labelRect.Contains(local.X, local.Y))
            Reset(fire: true);
        else if (_lumRect.Contains(local.X, local.Y))
            Set(_hue, _sat, 0f, fire: true);
        args.Handled = true;
    }

    private void EndDrag()
    {
        bool was = _draggingWheel || _draggingLum || HasPointerCapture;
        _draggingWheel = false;
        _draggingLum = false;
        _drag = 0f;
        InvalidatePaint();
        if (HasPointerCapture)
            ReleasePointer();
        InvalidatePaint();
        if (was)
            DragEnded?.Invoke();
    }

    private bool HitWheel(float x, float y)
    {
        float cx = _wheelRect.MidX;
        float cy = _wheelRect.MidY;
        float r = _wheelRect.Width * 0.5f + 8f;
        float dx = x - cx;
        float dy = y - cy;
        return dx * dx + dy * dy <= r * r;
    }

    private void ApplyWheel(float x, float y)
    {
        float cx = _wheelRect.MidX;
        float cy = _wheelRect.MidY;
        float r = MathF.Max(1f, _wheelRect.Width * 0.5f - 2f);
        float dx = x - cx;
        float dy = y - cy;
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        float hue = MathF.Atan2(dy, dx) * (180f / MathF.PI);
        if (hue < 0f) hue += 360f;
        float sat = Math.Clamp(dist / r, 0f, 1f) * 100f;

        bool ctrl = (ParentView?.Events.IsControlDown ?? false) || Events.IsControlDown;
        bool shift = (ParentView?.Events.IsShiftDown ?? false) || Events.IsShiftDown;

        if (ctrl && !shift)
        {
            Set(hue, _sat, _lum, fire: true);
        }
        else if (shift && !ctrl)
        {
            float hueDelta = MathF.Abs(hue - _hue);
            if (hueDelta > 30f)
                sat = 0f;
            Set(_hue, sat, _lum, fire: true);
        }
        else
        {
            Set(hue, sat, _lum, fire: true);
        }
    }

    public override bool HitTestLocal(float localX, float localY)
    {
        if (_wheelRect.Width < 1f)
            return base.HitTestLocal(localX, localY);
        if (_labelRect.Contains(localX, localY))
            return true;
        if (localY >= _lumRect.Top && localY <= _lumRect.Bottom && localX >= 0f && localX <= Transform.Width)
            return true;
        return HitWheel(localX, localY);
    }

    private void ApplyLum(float localX)
    {
        float thumbR = Theme.ThumbW * 0.5f;
        float travel = Math.Max(1f, _lumRect.Width - Theme.ThumbW);
        float t = Math.Clamp((localX - _lumRect.Left - thumbR) / travel, 0f, 1f);
        Set(_hue, _sat, t * 200f - 100f, fire: true);
    }

    private static float WrapHue(float h)
    {
        h %= 360f;
        if (h < 0f) h += 360f;
        return h;
    }

    public override void Dispose()
    {
        DropDisk();
        base.Dispose();
    }
}
