using System;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Blossom.Primitives;
using Blossom.Reactive;
using Raw75.Develop;
using SkiaSharp;

namespace Raw75.Controls;

/// <summary>
/// 3-way color grading as a vertical <see cref="Stack"/>: a wheel <see cref="Grid"/> (midtones
/// centred on top, shadows and highlights below), a hairline rule, then blending and balance sliders.
/// </summary>
public sealed class ColorGradingPanel : Stack
{
    private const float PairGap = 8f;
    private const float RowGap = 12f;
    private const float SliderSpacer = 32f;
    private const float MaxWheel = 168f;

    private readonly ColorWheelControl _midtones;
    private readonly ColorWheelControl _shadows;
    private readonly ColorWheelControl _highlights;
    private readonly SliderRow _blending;
    private readonly SliderRow _balance;
    private bool _sync;

    public event Action? Changed;
    public event Action? DragStarted;
    public event Action? DragEnded;

    public ColorGradingPanel()
    {
        Name = "ColorGradingPanel";
        Orientation = Orientation.Vertical;
        Gap = Theme.SliderGap;
        Overflow = OverflowMode.Visible;
        Style = new ElementStyle { BackColor = SKColors.Transparent };

        _midtones = new ColorWheelControl("Midtones");
        _shadows = new ColorWheelControl("Shadows");
        _highlights = new ColorWheelControl("Highlights");
        BindWheel(_midtones);
        BindWheel(_shadows);
        BindWheel(_highlights);

        var wheels = new WheelGrid { Name = "ColorGrading_Wheels" };
        wheels.AddChild(_midtones);
        wheels.AddChild(_shadows);
        wheels.AddChild(_highlights);
        Grid.SetCell(_midtones, column: 0, row: 0, columnSpan: 2);
        Grid.SetCell(_shadows, column: 0, row: 1);
        Grid.SetCell(_highlights, column: 1, row: 1);

        // Hairline centred in the spacer band between wheels and sliders.
        float band = SliderSpacer - Gap * 2f;
        var divider = new Stack
        {
            Name = "ColorGrading_Divider",
            IsClickthrough = true,
            Padding = new Thickness(8f, (band - 1f) * 0.5f, 8f, (band - 1f) * 0.5f),
            Style = new ElementStyle { BackColor = SKColors.Transparent }
        };
        var rule = new VisualElement { Name = "ColorGrading_Rule", IsClickthrough = true };
        rule.Transform.Height = 1f;
        rule.Bind(r => r.Style.BackColor = Theme.Hairline);
        divider.AddChild(rule);

        _blending = new SliderRow("Blending", 0f, 100f, "0", inline: false);
        _blending.DefaultValue = 50f;
        _blending.Value = 50f;
        _balance = new SliderRow("Balance", -100f, 100f, "0", inline: false);
        _balance.DefaultValue = 0f;

        BindSlider(_blending);
        BindSlider(_balance);

        AddChild(wheels);
        AddChild(divider);
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

    public override SKSize GetPreferredSize(float maxWidth, float maxHeight)
    {
        var size = base.GetPreferredSize(maxWidth > 0 ? maxWidth : Theme.RightW, 0f);
        return new SKSize(maxWidth > 0 ? maxWidth : Theme.RightW, size.Height);
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

    /// <summary>
    /// Two equal columns, two equal rows; cells centre their wheel. The grid's height follows the
    /// wheel diameter its width allows, so it sizes inside a stack.
    /// </summary>
    private sealed class WheelGrid : Grid
    {
        public WheelGrid()
        {
            Columns = "*, *";
            Rows = "*, *";
            ColumnGap = PairGap;
            RowGap = ColorGradingPanel.RowGap;
            Align = LayoutAlign.Center;
            Overflow = OverflowMode.Visible;
            Style = new ElementStyle { BackColor = SKColors.Transparent };
        }

        public override SKSize GetPreferredSize(float maxWidth, float maxHeight)
        {
            float w = maxWidth > 0 ? maxWidth : Theme.RightW;
            float wheel = Math.Clamp((w - PairGap) * 0.5f, 96f, MaxWheel);
            return new SKSize(w, ColorWheelControl.HeightForWidth(wheel) * 2f + ColorGradingPanel.RowGap);
        }
    }
}
