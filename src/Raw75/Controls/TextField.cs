using System;
using Blossom.Animations;
using Blossom.Primitives;
using Blossom.Reactive;
using SkiaSharp;

namespace Raw75.Controls;

/// <summary>
/// Single-line text entry on <see cref="TextInputBase"/> (caret, selection, word jumps, clipboard,
/// Enter/Escape). Drawn as a recessed well that lights an accent ring while focused.
/// </summary>
public sealed class TextField : TextInputBase
{
    public const float DefaultHeight = 30f;

    private float _focus;
    private float _hover;
    private float _scrollX;
    private bool _filtering;

    public TextField(string placeholder = "")
    {
        Name = "TextField";
        Placeholder = placeholder;
        Transform.Height = DefaultHeight;
        Style.BackColor = SKColors.Transparent;

        this.Bind(_ => { Theme.Observe(); InvalidatePaint(); });
    }

    /// <summary>Characters for which this returns false are dropped as they are typed or pasted.</summary>
    public Func<char, bool>? CharFilter { get; set; }

    public float FontSize { get; set; } = 12.5f;

    public int FontWeight { get; set; } = 400;

    /// <summary>Horizontal text inset inside the well.</summary>
    public float PadX { get; set; } = 10f;

    /// <summary>Reads as plain text until hovered or focused; the well only shows up for editing.</summary>
    public bool Flat { get; set; }

    private string Display => IsPassword ? new string('•', Text.Length) : Text;
    private float InnerWidth => Math.Max(1f, W - PadX * 2f);

    private TextPaint NewPaint() => new()
    {
        TextSize = FontSize,
        Typeface = Theme.GetTypeface(FontWeight)
    };

    protected override void OnStateChanged()
    {
        _focus = IsFocused ? 1f : 0f;
        InvalidatePaint();
        InvalidatePaint();
    }

    protected override void OnHoverChanged()
    {
        _hover = IsHovered ? 1f : 0f;
        InvalidatePaint();
    }

    protected override float PrefixWidth(int charCount)
    {
        var s = Display;
        charCount = Math.Clamp(charCount, 0, s.Length);
        if (charCount == 0) return 0f;
        using var p = NewPaint();
        return p.MeasureText(s[..charCount]);
    }

    protected override int IndexAt(float localX)
    {
        float x = localX - PadX + _scrollX;
        var s = Display;
        float prev = 0f;
        for (int i = 1; i <= s.Length; i++)
        {
            float w = PrefixWidth(i);
            if (x < (prev + w) * 0.5f) return i - 1;
            prev = w;
        }
        return s.Length;
    }

    protected override void OnCaretMoved() => EnsureCaretVisible();

    protected override void OnTextModified()
    {
        if (CharFilter != null && !_filtering)
        {
            var src = Text;
            int caret = CaretIndex;
            var kept = new System.Text.StringBuilder(src.Length);
            int newCaret = caret;
            for (int i = 0; i < src.Length; i++)
            {
                if (CharFilter(src[i])) kept.Append(src[i]);
                else if (i < caret) newCaret--;
            }
            if (kept.Length != src.Length)
            {
                _filtering = true;
                Text = kept.ToString();
                MoveCaret(Math.Clamp(newCaret, 0, Text.Length));
                _filtering = false;
            }
        }
        EnsureCaretVisible();
    }

    private void EnsureCaretVisible()
    {
        float caret = PrefixWidth(CaretIndex);
        float total = PrefixWidth(Display.Length);
        if (caret - _scrollX > InnerWidth) _scrollX = caret - InnerWidth;
        if (caret - _scrollX < 0) _scrollX = caret;
        _scrollX = Math.Clamp(_scrollX, 0f, Math.Max(0f, total - InnerWidth));
    }

    public override SKSize GetPreferredSize(float maxWidth, float maxHeight)
    {
        float w = MinWidth ?? 200f;
        if (maxWidth > 0) w = Math.Min(Math.Max(w, maxWidth), maxWidth);
        return new SKSize(w, DefaultHeight);
    }

    protected override void Paint(SKCanvas c)
    {
        float w = W, h = H;
        if (w < 8f || h < 8f) return;

        float focus = _focus;
        float r = Theme.RadiusSm;
        var box = new SKRect(0.5f, 0.5f, w - 0.5f, h - 0.5f);

        float chrome = Flat ? Math.Max(_hover, focus) : 1f;
        if (chrome > 0.01f)
        {
            // Accent halo when focused.
            if (focus > 0.01f)
            {
                using var halo = new SKPaint
                {
                    Color = Theme.Accent.WithAlpha((byte)(60 * focus)),
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 3f,
                    IsAntialias = true
                };
                var outer = box;
                outer.Inflate(1f, 1f);
                c.DrawRoundRect(outer, r + 1f, r + 1f, halo);
            }

            using (var well = new SKPaint { Color = Anim.Lerp(Theme.Well, Theme.WellHover, Math.Max(_hover * 0.6f, focus * 0.4f)), IsAntialias = true })
                c.DrawRoundRect(box, r, r, well);

            // Recess shade under the top edge.
            c.Save();
            c.ClipRoundRect(new SKRoundRect(box, r), antialias: true);
            using (var shade = new SKPaint
            {
                Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, 6f),
                    new[] { new SKColor(0, 0, 0, 70), new SKColor(0, 0, 0, 0) }, SKShaderTileMode.Clamp)
            })
            {
                c.DrawRect(box, shade);
            }
            c.Restore();

            using (var edge = new SKPaint
            {
                Color = Anim.Lerp(Anim.Lerp(Theme.Hairline, Theme.HairlineStrong, _hover), Theme.Accent, focus),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1f,
                IsAntialias = true
            })
            {
                c.DrawRoundRect(box, r, r, edge);
            }
        }

        using var text = NewPaint();
        var m = text.FontMetrics;
        float baseline = h * 0.5f - (m.Ascent + m.Descent) * 0.5f;
        float originX = PadX - _scrollX;

        c.Save();
        c.ClipRect(new SKRect(PadX - 1f, 0, w - PadX + 1f, h));

        if (IsFocused && HasSelection)
        {
            float x0 = originX + PrefixWidth(SelectionStart);
            float x1 = originX + PrefixWidth(SelectionStart + SelectionLength);
            using var sel = new SKPaint { Color = Theme.Accent.WithAlpha(70), IsAntialias = true };
            c.DrawRoundRect(new SKRect(x0, h * 0.5f - 8f, x1, h * 0.5f + 8f), 2f, 2f, sel);
        }

        if (Text.Length == 0)
        {
            text.Color = Theme.TextDisabled;
            c.DrawText(Placeholder, PadX, baseline, text);
        }
        else
        {
            text.Color = Enabled ? Theme.Text : Theme.TextDim;
            c.DrawText(Display, originX, baseline, text);
        }

        if (IsFocused && IsCaretShown && !HasSelection)
        {
            float cx = MathF.Round(originX + PrefixWidth(CaretIndex)) + 0.5f;
            using var caret = new SKPaint { Color = Theme.Accent, StrokeWidth = 1.5f, IsAntialias = true };
            c.DrawLine(cx, h * 0.5f - 8f, cx, h * 0.5f + 8f, caret);
        }
        c.Restore();
    }
}
