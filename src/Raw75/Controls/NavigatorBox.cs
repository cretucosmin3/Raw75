using System;
using Blossom.Core;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Blossom.Hosting;
using Blossom.Primitives;
using Blossom.Reactive;
using SkiaSharp;

namespace Raw75.Controls;

/// <summary>
/// Navigator card, a vertical <see cref="Stack"/>: a contain-fit preview in a hairline well with the
/// visible viewport outlined in accent, over a Fit / Fill / 1:1 <see cref="SegmentedControl"/>.
/// </summary>
public class NavigatorBox : Stack
{
    private static readonly string[] Modes = { "Fit", "Fill", "1:1" };

    private readonly Preview _preview;
    private readonly SegmentedControl _modes;

    public SKImage? Image
    {
        get => _preview.Image;
        set => _preview.Image = value;
    }

    /// <summary>The visible part of the photo in normalized image coordinates (0..1), or null to hide the outline.</summary>
    public SKRect? Viewport
    {
        get => _preview.Viewport;
        set => _preview.Viewport = value;
    }

    public event Action<string>? ModePicked;
    public event Action<float, float>? PreviewClicked;

    public NavigatorBox()
    {
        Name = "NavigatorBox";
        Orientation = Orientation.Vertical;
        Gap = 6f;
        Padding = new Thickness(6f);
        Overflow = OverflowMode.Clip;
        Style = new ElementStyle
        {
            Border = new BorderStyle { Width = 1 },
            Shadow = new ShadowStyle(0, 2.5f, 3, 3, SKColors.Transparent)
        };

        _preview = new Preview();
        _preview.ClickedNorm += (nx, ny) => PreviewClicked?.Invoke(nx, ny);
        AddChild(_preview);
        SetGrow(_preview, 1f);

        _modes = new SegmentedControl(Modes) { Name = "NavigatorBox_Modes" };
        _modes.SelectionChanged += i => ModePicked?.Invoke(Modes[i]);
        AddChild(_modes);

        this.Bind(_ => ApplyTheme());
    }

    public void SetMode(string mode)
    {
        int i = Array.IndexOf(Modes, mode);
        if (i >= 0) _modes.SelectedIndex = i;
    }

    private void ApplyTheme()
    {
        Style.BackColor = Theme.Section;
        Style.Border.Color = Theme.Hairline;
        Style.Border.Roundness = Theme.Radius;
        Theme.ApplyCardShadow(Style);
        InvalidatePaint();
    }

    public override SKSize GetPreferredSize(float maxWidth, float maxHeight)
    {
        float w = maxWidth > 0 ? maxWidth : (Transform.Width > 0 ? Transform.Width : Theme.LeftW);
        float h = 168f;
        if (maxHeight > 0) h = Math.Min(h, maxHeight);
        return new SKSize(w, h);
    }

    private sealed class Preview : Control
    {
        private SKImage? _image;
        private SKRect? _viewport;

        public event Action<float, float>? ClickedNorm;

        public SKImage? Image
        {
            get => _image;
            set => SetAndPaint(ref _image, value);
        }

        public SKRect? Viewport
        {
            get => _viewport;
            set => SetAndPaint(ref _viewport, value);
        }

        public Preview()
        {
            Name = "NavigatorBox_Preview";
            Cursor = UiCursor.Crosshair;
            Style = new ElementStyle { Border = new BorderStyle { Width = 1 } };
            Events.OnClick += (_, args) =>
            {
                if (args.Button != PointerButton.Left) return;
                var local = PointToClient(args.Global.X, args.Global.Y);
                if (W > 0 && H > 0)
                    ClickedNorm?.Invoke(local.X / W, local.Y / H);
                args.Handled = true;
            };

            this.Bind(_ =>
            {
                Style.BackColor = Theme.Well;
                Style.Border.Color = Theme.HairlineSubtle;
                Style.Border.Roundness = Theme.RadiusSm;
                InvalidatePaint();
            });
        }

        protected override void Paint(SKCanvas canvas)
        {
            var image = _image;
            if (image == null || image.Handle == IntPtr.Zero || W < 4 || H < 4) return;

            float scale = Math.Min((W - 2f) / image.Width, (H - 2f) / image.Height);
            float dw = image.Width * scale;
            float dh = image.Height * scale;
            var photo = SKRect.Create((W - dw) * 0.5f, (H - dh) * 0.5f, dw, dh);

            using var paint = new SKPaint { IsAntialias = true };
            canvas.DrawImage(image, photo, Sampling.Linear, paint);
            ThumbInk.StrokeRound(canvas, Inset(photo, 0.5f), 0f, Theme.Hairline, 1f);

            if (_viewport is not { } v) return;
            var view = new SKRect(
                photo.Left + Math.Clamp(v.Left, 0f, 1f) * dw,
                photo.Top + Math.Clamp(v.Top, 0f, 1f) * dh,
                photo.Left + Math.Clamp(v.Right, 0f, 1f) * dw,
                photo.Top + Math.Clamp(v.Bottom, 0f, 1f) * dh);
            if (view.Width < 1f || view.Height < 1f) return;

            // Dim what is off-screen, then outline the visible region.
            canvas.Save();
            canvas.ClipRect(view, SKClipOperation.Difference);
            ThumbInk.FillRound(canvas, photo, 0f, Theme.Scrim);
            canvas.Restore();
            ThumbInk.StrokeRound(canvas, Inset(view, 0.75f), 0f, Theme.Accent, 1.5f);
        }

        private static SKRect Inset(SKRect r, float d)
        {
            r.Inflate(-d, -d);
            return r;
        }
    }
}
