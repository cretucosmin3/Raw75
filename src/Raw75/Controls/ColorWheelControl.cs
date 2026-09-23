using System;
using System.Collections.Generic;
using Blossom.Core;
using Blossom.Core.Input;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Raw75.Pipeline;
using Silk.NET.Input;
using SkiaSharp;

namespace Raw75.Controls;

/// <summary>
/// Hue/saturation disk with a luminance slider under it.
/// The hue disk is cached as an SKImage and only rebuilt when its pixel size changes.
/// </summary>
public sealed class ColorWheelControl : VisualElement
{
    public const float LabelH = 16f;
    public const float LumH = 22f;
    public const float Gap = 4f;

    private float _hue;
    private float _sat;
    private float _lum;
    private bool _draggingWheel;
    private bool _draggingLum;
    private bool _hovered;
    private bool _labelHovered;
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
        Cursor = StandardCursor.Hand;
        Style = new ElementStyle { BackColor = SKColors.Transparent };

        Events.OnMouseDown += OnMouseDown;
        Events.OnMouseMove += OnMouseMove;
        Events.OnMouseUp += OnMouseUp;
        Events.OnMouseDoubleClick += OnDoubleClick;
        Events.OnMouseEnter += _ => { _hovered = true; InvalidatePaint(); };
        Events.OnMouseLeave += _ =>
        {
            _hovered = false;
            _labelHovered = false;
            InvalidatePaint();
        };
    }

    public static float HeightForWidth(float width)
    {
        float wheel = Math.Max(48f, width);
        return LabelH + wheel + Gap + LumH;
    }

    public override SKSize GetPreferredSize(float maxWidth, float maxHeight)
    {
        float w = maxWidth > 0 ? maxWidth : 112f;
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

    protected override void OnAfterStyleDraw(List<DrawCommand> cmds)
    {
        cmds.Add(new DrawCallbackCommand(DrawWheel));
    }

    private void DrawWheel(SKCanvas canvas)
    {
        float w = Transform.Computed.Width;
        float h = Transform.Computed.Height;
        if (w < 24f || h < 40f)
            return;

        _labelRect = new SKRect(0, 0, w, LabelH);
        float wheel = Math.Max(8f, Math.Min(w, h - LabelH - Gap - LumH));
        float wheelX = (w - wheel) * 0.5f;
        _wheelRect = new SKRect(wheelX, LabelH, wheelX + wheel, LabelH + wheel);
        _lumRect = new SKRect(0, h - LumH, w, h);

        DrawLabel(canvas);
        DrawDisk(canvas, _wheelRect);
        DrawPointer(canvas, _wheelRect);
        DrawLumSlider(canvas, _lumRect);
    }

    private void DrawLabel(SKCanvas canvas)
    {
        using var paint = new SKPaint
        {
            IsAntialias = true,
            TextSize = 11f,
            Typeface = Theme.GetTypeface(600),
            TextAlign = SKTextAlign.Center,
            Color = Theme.TextSecondary
        };

        string text;
        if (_draggingWheel)
            text = $"H {MathF.Round(_hue):0}°   S {MathF.Round(_sat):0}";
        else if (_draggingLum)
            text = $"L {_lum:+0;-0;0}";
        else if (_labelHovered)
        {
            text = "Reset";
            paint.Color = Theme.Text;
        }
        else
            text = RangeLabel;

        float baseline = _labelRect.MidY + 4f;
        canvas.DrawText(text, _labelRect.MidX, baseline, paint);
    }

    private void DrawDisk(SKCanvas canvas, SKRect wheel)
    {
        int size = Math.Max(16, (int)MathF.Round(wheel.Width));
        EnsureDisk(size);

        using var well = new SKPaint { Color = Theme.Well, IsAntialias = true };
        canvas.DrawOval(wheel, well);

        if (_disk != null)
            canvas.DrawImage(_disk, wheel);

        using var ring = new SKPaint
        {
            Color = _hovered || _draggingWheel ? Theme.HairlineStrong : Theme.Hairline,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            IsAntialias = true
        };
        canvas.DrawOval(wheel, ring);
    }

    private void DrawPointer(SKCanvas canvas, SKRect wheel)
    {
        float cx = wheel.MidX;
        float cy = wheel.MidY;
        float r = wheel.Width * 0.5f - 2f;
        float rad = _hue * (MathF.PI / 180f);
        float dist = (_sat / 100f) * r;
        float px = cx + MathF.Cos(rad) * dist;
        float py = cy + MathF.Sin(rad) * dist;
        float pr = _draggingWheel ? 7f : 5.5f;

        ColorGradeMath.HsvToRgb(_hue, _sat / 100f, 1f, out float rr, out float gg, out float bb);
        SKColor fill = _sat > 5f
            ? new SKColor((byte)(rr * 255f), (byte)(gg * 255f), (byte)(bb * 255f))
            : SKColors.Transparent;

        using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 120), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f };
        canvas.DrawCircle(px, py, pr, shadow);

        if (fill.Alpha > 0)
        {
            using var fillPaint = new SKPaint { Color = fill, IsAntialias = true };
            canvas.DrawCircle(px, py, pr, fillPaint);
        }

        using var border = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f };
        canvas.DrawCircle(px, py, pr, border);
    }

    private void DrawLumSlider(SKCanvas canvas, SKRect lum)
    {
        canvas.Save();
        canvas.Translate(lum.Left, lum.Top);
        float t = Math.Clamp((_lum + 100f) / 200f, 0f, 1f);
        SliderChrome.Draw(canvas, lum.Width, lum.Height, t, 0.5f, bipolar: true, _hovered || _draggingLum, _draggingLum);
        canvas.Restore();
    }

    private void EnsureDisk(int size)
    {
        if (_disk != null && _diskSize == size)
            return;
        _disk?.Dispose();
        _disk = null;
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

        using (var radial = SKShader.CreateRadialGradient(
                   center,
                   radius,
                   new[] { SKColors.White, new SKColor(255, 255, 255, 0) },
                   new[] { 0f, 1f },
                   SKShaderTileMode.Clamp))
        using (var paint = new SKPaint { Shader = radial, IsAntialias = true })
        {
            canvas.DrawCircle(center, radius, paint);
        }

        _disk = surface.Snapshot();
    }

    private void OnMouseDown(object sender, MouseEventArgs args)
    {
        if (args.Button != (int)MouseButton.Left)
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
            CapturePointer();
            DragStarted?.Invoke();
            ApplyWheel(local.X, local.Y);
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
            Cursor = _lumRect.Contains(local.X, local.Y) ? StandardCursor.HResize : StandardCursor.Hand;

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
        if (args.Button != (int)MouseButton.Left)
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
        float r = _wheelRect.Width * 0.5f + 4f;
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
}
