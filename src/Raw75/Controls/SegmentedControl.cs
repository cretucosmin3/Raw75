using System;
using System.Collections.Generic;
using Blossom.Animations;
using Blossom.Core;
using Blossom.Core.Visual;
using Blossom.Primitives;
using Blossom.Reactive;
using SkiaSharp;

namespace Raw75.Controls;

/// <summary>
/// Mutually exclusive mode switch: a recessed well holding equal-width segments, with a raised
/// indicator under the selected one. Segments are <see cref="ButtonBase"/> shells laid
/// out by the underlying horizontal <see cref="Stack"/>.
/// </summary>
public class SegmentedControl : Stack
{
    public const float DefaultHeight = 26f;
    private const float WellInset = 2f;

    private readonly List<Segment> _segments = new();
    private int _selected;

    /// <summary>Raised when the user picks a segment (not when <see cref="SelectedIndex"/> is set in code).</summary>
    public event Action<int>? SelectionChanged;

    public SegmentedControl(params string[] labels)
        : this(Array.ConvertAll(labels, l => (l, (SKColor?)null)))
    {
    }

    public SegmentedControl((string Label, SKColor? Swatch)[] segments)
    {
        Name = "SegmentedControl";
        Orientation = Orientation.Horizontal;
        Gap = 0f;
        Padding = new Thickness(WellInset);
        Transform.Height = DefaultHeight;
        Style = new ElementStyle { BackColor = SKColors.Transparent };

        for (int i = 0; i < segments.Length; i++)
        {
            int index = i;
            var seg = new Segment(this, segments[i].Label, segments[i].Swatch);
            seg.Clicked += () => Pick(index);
            _segments.Add(seg);
            AddChild(seg);
            SetGrow(seg, 1f);
        }

        this.Bind(_ => { Theme.Observe(); InvalidatePaint(); });
    }

    public int Count => _segments.Count;

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            int next = Math.Clamp(value, 0, Math.Max(0, _segments.Count - 1));
            if (_selected == next) return;
            _selected = next;
            foreach (var s in _segments) s.InvalidatePaint();
            InvalidatePaint();
        }
    }

    private float _compactWidth = -1f;
    private bool _compact;

    /// <summary>True when any swatch segment of this width can't fit dot + caption.</summary>
    private bool SwatchesCompact(float segmentWidth)
    {
        if (Math.Abs(segmentWidth - _compactWidth) < 0.5f) return _compact;
        _compactWidth = segmentWidth;
        using var p = new TextPaint { TextSize = 11.5f, Typeface = Theme.GetTypeface(600) };
        _compact = false;
        foreach (var seg in _segments)
            if (7f + 5f + p.MeasureText(seg.Label) > segmentWidth - 8f) { _compact = true; break; }
        return _compact;
    }

    private void Pick(int index)
    {
        if (index == _selected) return;
        SelectedIndex = index;
        SelectionChanged?.Invoke(index);
    }

    public override SKSize GetPreferredSize(float maxWidth, float maxHeight)
    {
        float w = maxWidth > 0 ? maxWidth : Theme.RightW;
        if (MinWidth.HasValue) w = Math.Max(w, MinWidth.Value);
        if (MaxWidth.HasValue) w = Math.Min(w, MaxWidth.Value);
        return new SKSize(w, DefaultHeight);
    }

    protected override void OnAfterStyleDraw(List<DrawCommand> cmds) =>
        cmds.Add(new DrawCallbackCommand(PaintWell));

    private void PaintWell(SKCanvas c)
    {
        float w = Transform.Computed.Width, h = Transform.Computed.Height;
        if (w < 8f || h < 8f || _segments.Count == 0) return;

        float r = Math.Min(Theme.RadiusSm + 1f, h * 0.5f);
        var well = new SKRect(0.5f, 0.5f, w - 0.5f, h - 0.5f);
        using (var fill = new SKPaint { Color = Theme.Well, IsAntialias = true })
            c.DrawRoundRect(well, r, r, fill);
        using (var edge = new SKPaint { Color = Theme.HairlineSubtle, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = true })
            c.DrawRoundRect(well, r, r, edge);

        // Hairline separators between unselected neighbours.
        float innerW = w - WellInset * 2f;
        float segW = innerW / _segments.Count;
        using (var sep = new SKPaint { Color = Theme.HairlineSubtle, StrokeWidth = 1f })
        {
            for (int i = 1; i < _segments.Count; i++)
            {
                if (i == _selected || i - 1 == _selected) continue;
                float x = MathF.Round(WellInset + segW * i) + 0.5f;
                c.DrawLine(x, h * 0.28f, x, h * 0.72f, sep);
            }
        }

        // Raised indicator under the selected segment.
        float pos = _selected;
        var ind = SKRect.Create(WellInset + segW * pos, WellInset, segW, h - WellInset * 2f);
        float ir = Math.Max(0f, r - 1.5f);
        if (Theme.UseShadows)
        {
            using var blur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1.4f);
            using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 90), MaskFilter = blur, IsAntialias = true };
            var s = ind;
            s.Offset(0, 1f);
            c.DrawRoundRect(s, ir, ir, shadow);
        }
        using (var raised = new SKPaint { Color = Theme.Lighten(Theme.Button, 8), IsAntialias = true })
            c.DrawRoundRect(ind, ir, ir, raised);
        using (var rim = new SKPaint
        {
            Color = Theme.OutlineAccent ? Theme.Accent : Theme.HairlineStrong,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            IsAntialias = true
        })
        {
            var rr = ind;
            rr.Inflate(-0.5f, -0.5f);
            c.DrawRoundRect(rr, ir, ir, rim);
        }
        using (var lip = new SKPaint { Color = SKColors.White.WithAlpha(14), StrokeWidth = 1f, IsAntialias = true })
            c.DrawLine(ind.Left + ir, ind.Top + 1.5f, ind.Right - ir, ind.Top + 1.5f, lip);
    }

    private sealed class Segment : ButtonBase
    {
        private readonly SegmentedControl _owner;
        private readonly string _label;
        private readonly SKColor? _swatch;
        private float _hover;

        public Segment(SegmentedControl owner, string label, SKColor? swatch)
        {
            _owner = owner;
            _label = label ?? "";
            _swatch = swatch;
            Name = $"Segment_{_label}";
            ReceivesKeyboard = false;
            Style.BackColor = SKColors.Transparent;
            Events.OnClick += (_, e) => { if (e.Button == PointerButton.Left) e.Handled = true; };
        }

        public string Label => _label;

        private bool IsSelected => _owner._segments.IndexOf(this) == _owner._selected;

        protected override void OnHoverChanged()
        {
            _hover = IsHovered ? 1f : 0f;
            InvalidatePaint();
        }

        public override SKSize GetPreferredSize(float maxWidth, float maxHeight) => new(0f, DefaultHeight - WellInset * 2f);

        protected override void Paint(SKCanvas c)
        {
            bool selected = IsSelected;
            using var text = new TextPaint
            {
                TextSize = 11.5f,
                Typeface = Theme.GetTypeface(selected ? 600 : 500),
                Color = selected ? Theme.Text : Anim.Lerp(Theme.TextDim, Theme.TextSecondary, _hover),
                TextAlign = SKTextAlign.Left
            };

            var m = text.FontMetrics;
            float baseline = H * 0.5f - (m.Ascent + m.Descent) * 0.5f;

            if (_swatch.HasValue && _owner.SwatchesCompact(W))
            {
                // Too narrow for dot + caption everywhere: caption over a band-coloured underline.
                c.DrawText(_label, (W - text.MeasureText(_label)) * 0.5f, baseline - 1f, text);
                float barW = Math.Min(W - 10f, 18f);
                using var bar = new SKPaint { Color = selected ? _swatch.Value : _swatch.Value.WithAlpha(110), IsAntialias = true };
                c.DrawRoundRect(SKRect.Create((W - barW) * 0.5f, H - 4f, barW, 2f), 1f, 1f, bar);
                return;
            }

            float dot = _swatch.HasValue ? 7f : 0f;
            float gap = _swatch.HasValue ? 5f : 0f;
            float x = (W - (dot + gap + text.MeasureText(_label))) * 0.5f;

            if (_swatch.HasValue)
            {
                var sw = _swatch.Value;
                using var fill = new SKPaint { Color = selected ? sw : sw.WithAlpha(150), IsAntialias = true };
                c.DrawCircle(x + dot * 0.5f, H * 0.5f, dot * 0.5f, fill);
            }

            c.DrawText(_label, x + dot + gap, baseline, text);
        }
    }
}
