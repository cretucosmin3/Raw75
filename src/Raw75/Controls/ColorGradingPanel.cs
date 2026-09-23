using System;
using Blossom.Core.Visual;
using Raw75.Develop;
using SkiaSharp;

namespace Raw75.Controls;

/// <summary>
/// 3-way color grading: midtones on top, shadows and highlights below,
/// then blending and balance sliders.
/// </summary>
public sealed class ColorGradingPanel : VisualElement
{
    private const float PairGap = 8f;
    private const float RowGap = 22f;
    private const float SliderSpacer = 22f;
    private const float MaxWheel = 168f;

    private readonly ColorWheelControl _midtones;
    private readonly ColorWheelControl _shadows;
    private readonly ColorWheelControl _highlights;
    private readonly VisualElement _spacer;
    private readonly SliderRow _blending;
    private readonly SliderRow _balance;
    private bool _sync;

    public event Action? Changed;
    public event Action? DragStarted;
    public event Action? DragEnded;

    public ColorGradingPanel()
    {
        Name = "ColorGradingPanel";
        Style = new ElementStyle { BackColor = SKColors.Transparent };

        _midtones = new ColorWheelControl("Midtones");
        _shadows = new ColorWheelControl("Shadows");
        _highlights = new ColorWheelControl("Highlights");
        BindWheel(_midtones);
        BindWheel(_shadows);
        BindWheel(_highlights);

        _spacer = new VisualElement
        {
            Name = "ColorGrading_Spacer",
            IsClickthrough = true,
            Style = new ElementStyle { BackColor = Theme.HairlineSubtle }
        };

        _blending = new SliderRow("Blending", 0f, 100f, "0");
        _blending.DefaultValue = 50f;
        _blending.Value = 50f;
        _balance = new SliderRow("Balance", -100f, 100f, "0");
        _balance.DefaultValue = 0f;

        BindSlider(_blending);
        BindSlider(_balance);

        AddChild(_midtones);
        AddChild(_shadows);
        AddChild(_highlights);
        AddChild(_spacer);
        AddChild(_blending);
        AddChild(_balance);
    }

    public void LoadFromSettings(DevelopSettings s)
    {
        _sync = true;
        _midtones.Set(s.GradingMidHue, s.GradingMidSat, s.GradingMidLum);
        _shadows.Set(s.GradingShadowHue, s.GradingShadowSat, s.GradingShadowLum);
        _highlights.Set(s.GradingHighlightHue, s.GradingHighlightSat, s.GradingHighlightLum);
        _blending.Value = s.GradingBlending;
        _balance.Value = s.GradingBalance;
        _sync = false;
        InvalidatePaint();
    }

    public void SaveToSettings(DevelopSettings s)
    {
        s.GradingMidHue = _midtones.Hue;
        s.GradingMidSat = _midtones.Saturation;
        s.GradingMidLum = _midtones.Luminance;
        s.GradingShadowHue = _shadows.Hue;
        s.GradingShadowSat = _shadows.Saturation;
        s.GradingShadowLum = _shadows.Luminance;
        s.GradingHighlightHue = _highlights.Hue;
        s.GradingHighlightSat = _highlights.Saturation;
        s.GradingHighlightLum = _highlights.Luminance;
        s.GradingBlending = _blending.Value;
        s.GradingBalance = _balance.Value;
    }

    public void Reset()
    {
        _sync = true;
        _midtones.Reset(fire: false);
        _shadows.Reset(fire: false);
        _highlights.Reset(fire: false);
        _blending.Value = 50f;
        _balance.Value = 0f;
        _sync = false;
        Changed?.Invoke();
    }

    public void RefreshTheme()
    {
        if (_spacer.Style != null)
            _spacer.Style.BackColor = Theme.HairlineSubtle;
        _blending.RefreshTheme();
        _balance.RefreshTheme();
        InvalidatePaint();
    }

    public override SKSize GetPreferredSize(float maxWidth, float maxHeight)
    {
        float w = maxWidth > 0 ? maxWidth : Theme.RightW;
        float wheel = MeasureWheel(w);
        float h = ColorWheelControl.HeightForWidth(wheel)
                  + RowGap
                  + ColorWheelControl.HeightForWidth(wheel)
                  + SliderSpacer
                  + Theme.RowH
                  + Theme.SliderGap
                  + Theme.RowH;
        return new SKSize(w, h);
    }

    protected override void LayoutChildren()
    {
        float ox = Transform.Computed.X;
        float oy = Transform.Computed.Y;
        float w = Math.Max(1f, Transform.Width);
        float wheel = MeasureWheel(w);
        float wheelH = ColorWheelControl.HeightForWidth(wheel);

        _midtones.Transform.SetAbsoluteFrame(ox + (w - wheel) * 0.5f, oy, wheel, wheelH);

        float pairW = wheel * 2f + PairGap;
        float pairX = ox + (w - pairW) * 0.5f;
        float botY = oy + wheelH + RowGap;
        _shadows.Transform.SetAbsoluteFrame(pairX, botY, wheel, wheelH);
        _highlights.Transform.SetAbsoluteFrame(pairX + wheel + PairGap, botY, wheel, wheelH);

        float spacerY = wheelH + RowGap + wheelH + (SliderSpacer - 1f) * 0.5f;
        _spacer.Transform.SetAbsoluteFrame(ox + 12f, oy + spacerY, Math.Max(1f, w - 24f), 1f);

        float y = wheelH + RowGap + wheelH + SliderSpacer;
        _blending.Transform.SetAbsoluteFrame(ox, oy + y, w, Theme.RowH);
        y += Theme.RowH + Theme.SliderGap;
        _balance.Transform.SetAbsoluteFrame(ox, oy + y, w, Theme.RowH);
    }

    private static float MeasureWheel(float width)
    {
        return Math.Clamp((width - PairGap) * 0.5f, 96f, MaxWheel);
    }

    private void BindWheel(ColorWheelControl wheel)
    {
        wheel.Changed += () =>
        {
            if (_sync) return;
            Changed?.Invoke();
        };
        wheel.DragStarted += () => DragStarted?.Invoke();
        wheel.DragEnded += () => DragEnded?.Invoke();
    }

    private void BindSlider(SliderRow row)
    {
        row.Changed += _ =>
        {
            if (_sync) return;
            Changed?.Invoke();
        };
        row.DragStarted += () => DragStarted?.Invoke();
        row.DragEnded += () => DragEnded?.Invoke();
    }
}
