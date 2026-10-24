using System;
using System.Collections.Generic;
using Blossom.Core;
using Blossom.Core.Visual;
using Blossom.Core.Input;
using Blossom.Core.Visual.Enums;
using Blossom.Primitives;
using Blossom.Reactive;
using Raw75.Controls;
using Raw75.Develop;
using Raw75.Io;
using SkiaSharp;

namespace Raw75.Views;

/// <summary>
/// Masks: the left-dock card (add shapes, list, edge settings) and, while a mask is selected, a
/// mask-only inspector on the right (Light and Color essentials) that edits just that mask.
/// </summary>
public sealed partial class WorkspaceView
{
    private MaskPanel _masks = null!;
    private Stack _maskHeader = null!;
    private const int MaxMaskNameLength = 32;

    private TextField _maskTitle = null!;
    private VisualElement _maskIcon = null!;
    private MaskShape? _maskIconShape;
    private PanelGroup _maskLightGroup = null!;
    private PanelGroup _maskColorGroup = null!;
    private readonly List<(SliderRow Row, Func<MaskLayer, float> Get)> _maskSliders = new();
    private PhotoDocument? _maskDoc;
    // The mask whose name the header field is editing, its photo, and the name it had before.
    private MaskLayer? _renaming;
    private PhotoDocument? _renameDoc;
    private string _renameFrom = "";

    private bool MaskSelected => _state.ActiveMask.Peek() >= 0;

    private MaskLayer? ActiveMask
    {
        get
        {
            var masks = _session.Active?.Settings.Masks;
            int i = _state.ActiveMask.Peek();
            return masks != null && i >= 0 && i < masks.Count ? masks[i] : null;
        }
    }

    private void BuildMaskPanel(float x, float y, float w, float h)
    {
        _masks = new MaskPanel
        {
            Transform = new Transform(x, y, w, h) { Anchor = Anchor.Left | Anchor.Bottom }
        };
        _masks.AddRequested += StartMaskPlacement;
        _masks.RowClicked += i => SelectMask(i == _state.ActiveMask.Peek() ? -1 : i);
        _masks.RenameRequested += StartMaskRename;
        _masks.EnableToggled += ToggleMaskEnabled;
        _masks.DeleteRequested += DeleteMask;
        _masks.EnabledChanged += en => OnSectionToggled(s => s.EnableMasks = en);
        _masks.ExpandedChanged += _ => ApplyLayout();
        _masks.InvertButton.Clicked += ToggleMaskInvert;
        _masks.OverlayButton.Clicked += ToggleMaskOverlay;
        _masks.OverlayButton.Bind(b => b.Toggled = _state.MaskOverlay.Value);
        WireMaskSlider(_masks.Feather, (m, v) => m.Feather = v);
        WireMaskSlider(_masks.Amount, (m, v) => m.Amount = v);
        AddElement(_masks);
    }

    /// <summary>Takes the inspector tabs' place while a mask is selected: the mask's name (click to rename), Reset and Done.</summary>
    private void BuildMaskHeader(float x, float y, float w, float h)
    {
        _maskHeader = new Stack
        {
            Name = "MaskHeader",
            Orientation = Orientation.Horizontal,
            Align = LayoutAlign.Center,
            Gap = 6f,
            Padding = new Thickness(10, 0, 5, 0),
            Overflow = OverflowMode.Clip,
            Style = new ElementStyle { Border = new BorderStyle { Width = 1 } },
            Transform = new Transform(x, y, w, h) { Anchor = Anchor.Right | Anchor.Top }
        };
        var icon = _maskIcon = new VisualElement
        {
            Name = "MaskHeader_Icon",
            IsClickthrough = true,
            BackgroundImageScale = ImageScaleMode.Contain,
            BackgroundImageTintBlendMode = SKBlendMode.SrcIn,
            BackgroundSvg = IconStore.LoadSvg("mask_radial")
        };
        icon.Transform.Width = 15f;
        icon.Transform.Height = 15f;
        _maskHeader.AddChild(icon);

        _maskTitle = new TextField("Mask name")
        {
            Name = "MaskHeader_Title",
            Flat = true,
            FontWeight = 600,
            PadX = 6f,
            MaxLength = MaxMaskNameLength,
            MinWidth = 40f
        };
        _maskTitle.MinHeight = _maskTitle.MaxHeight = 26f;
        _maskTitle.OnFocused += _ => BeginMaskRename();
        _maskTitle.OnFocusLost += _ => EndMaskRename(commit: true);
        _maskTitle.Changed += OnMaskNameTyped;
        _maskTitle.Submitted += () => SetActiveKeyboardElement(null);
        _maskTitle.Escaped += () =>
        {
            EndMaskRename(commit: false);
            SetActiveKeyboardElement(null);
        };
        _maskHeader.AddChild(_maskTitle);
        Stack.SetGrow(_maskTitle, 1f);

        var reset = new IconButton(null, "rotate_left") { Variant = ButtonVariant.Ghost, IconSize = 13f };
        reset.MinWidth = reset.MaxWidth = 26f;
        reset.MinHeight = reset.MaxHeight = 26f;
        reset.Clicked += ResetMaskAdjustments;
        _maskHeader.AddChild(reset);

        var done = new IconButton("Done", "check") { FontSize = 12f, IconSize = 13f, MinWidth = 74f };
        done.MinHeight = done.MaxHeight = 28f;
        done.Clicked += () => SelectMask(-1);
        _maskHeader.AddChild(done);

        _maskHeader.Bind(e =>
        {
            e.Style.BackColor = Theme.Section;
            e.Style.Border.Color = Theme.AccentSoft;
            e.Style.Border.Roundness = Theme.Radius;
            icon.BackgroundImageTintColor = Theme.Accent;
        });
        _maskHeader.BindVisible(() => !_state.GalleryMode.Value && _state.ActiveMask.Value >= 0);
        AddElement(_maskHeader);
    }

    /// <summary>The mask inspector: the Light and Color controls that make sense locally.</summary>
    private void BuildMaskInspector(RightColumn host)
    {
        _maskLightGroup = new PanelGroup("Light");
        BindMaskSlider(_maskLightGroup, "Exposure", -5f, 5f, "0.00", (m, v) => m.Exposure = v, m => m.Exposure);
        BindMaskSlider(_maskLightGroup, "Contrast", -100, 100, "0", (m, v) => m.Contrast = v, m => m.Contrast, RangedControl.Contrast);
        BindMaskSlider(_maskLightGroup, "Highlight", -100, 100, "0", (m, v) => m.Highlights = v, m => m.Highlights, RangedControl.Highlights);
        BindMaskSlider(_maskLightGroup, "Shadow", -100, 100, "0", (m, v) => m.Shadows = v, m => m.Shadows, RangedControl.Shadows);
        BindMaskSlider(_maskLightGroup, "Whites", -100, 100, "0", (m, v) => m.Whites = v, m => m.Whites, RangedControl.Whites);
        BindMaskSlider(_maskLightGroup, "Blacks", -100, 100, "0", (m, v) => m.Blacks = v, m => m.Blacks, RangedControl.Blacks);
        BindMaskSlider(_maskLightGroup, "Dehaze", -100, 100, "0", (m, v) => m.Dehaze = v, m => m.Dehaze);
        _maskLightGroup.EnableReset(() => ResetMaskSection(light: true), "Reset Mask Light");
        _maskLightGroup.EnabledChanged += en => OnMaskToggled(m => m.EnableLight = en);

        _maskColorGroup = new PanelGroup("Color");
        var temp = BindMaskSlider(_maskColorGroup, "Kelvin", -100, 100, "0", (m, v) => m.Temperature = v, m => m.Temperature);
        temp.GradientMode = SliderGradientMode.Kelvin;
        var tint = BindMaskSlider(_maskColorGroup, "Tint", -100, 100, "0", (m, v) => m.Tint = v, m => m.Tint);
        tint.GradientMode = SliderGradientMode.Tint;
        BindMaskSlider(_maskColorGroup, "Saturation", -100, 100, "0", (m, v) => m.Saturation = v, m => m.Saturation);
        BindMaskSlider(_maskColorGroup, "Vibrance", -100, 100, "0", (m, v) => m.Vibrance = v, m => m.Vibrance);
        _maskColorGroup.EnableReset(() => ResetMaskSection(light: false), "Reset Mask Color");
        _maskColorGroup.EnabledChanged += en => OnMaskToggled(m => m.EnableColor = en);

        foreach (var g in new[] { _maskLightGroup, _maskColorGroup })
        {
            host.AddBody(g);
            g.BindVisible(() => _state.ActiveMask.Value >= 0);
        }
    }

    private SliderRow BindMaskSlider(PanelGroup group, string label, float min, float max, string fmt,
        Action<MaskLayer, float> set, Func<MaskLayer, float> get, RangedControl? range = null)
    {
        var row = new SliderRow(label, min, max, fmt, inline: false) { DefaultValue = 0f, Value = 0f };
        WireMaskSlider(row, set);
        if (range is RangedControl control)
            BindRange(row, control);
        group.AddBody(row);
        _maskSliders.Add((row, get));
        return row;
    }

    private void WireMaskSlider(SliderRow row, Action<MaskLayer, float> set)
    {
        row.Changed += v =>
        {
            if (_sync) return;
            var m = ActiveMask;
            if (m == null) return;
            set(m, v);
            PushMaskLook(settle: !_sliderDrag);
        };
        row.DragStarted += () =>
        {
            _sliderDrag = true;
            _session.Active?.Undo.BeginDrag(_session.Active.Settings);
        };
        row.DragEnded += () =>
        {
            _sliderDrag = false;
            var d = _session.Active;
            if (d == null) return;
            d.Undo.EndDrag(d.Settings);
            PushMaskLook(settle: true);
        };
    }

    private void WireMaskPhotoEvents()
    {
        _photo.MaskPicked += SelectMask;
        _photo.MaskPlacementEnded += () =>
        {
            _state.MaskPlacing.Value = null;
            SyncMaskUi();
        };
        _photo.MaskEditBegan += () => _session.Active?.Undo.BeginDrag(_session.Active.Settings);
        _photo.MasksChanged += () =>
        {
            var d = _session.Active;
            if (d == null) return;
            _engine.RequestHistogram(d);
            if (_photo.LookFailed)
                _engine.Invalidate(d, preview: true);
        };
        _photo.MaskEditEnded += () =>
        {
            var d = _session.Active;
            if (d == null) return;
            d.Undo.EndDrag(d.Settings);
            PushLook(fast: false, settle: true);
            SyncMaskUi();
        };
    }

    /// <summary>
    /// A mask changed from the panels. While dragging only the final shader pass redraws; once settled
    /// the full look, the CPU previews and the saved settings follow.
    /// </summary>
    private void PushMaskLook(bool settle)
    {
        var d = _session.Active;
        if (d == null) return;
        if (settle)
        {
            PushLook(fast: false, settle: true);
            return;
        }
        _photo.InvalidateMaskLook(fast: true);
        _engine.RequestHistogram(d);
        if (_photo.LookFailed)
            _engine.Invalidate(d, preview: true);
    }

    /// <summary>Selects mask <paramref name="index"/> for editing (-1 = back to the whole photo).</summary>
    private void SelectMask(int index)
    {
        // A name being typed is kept before the header goes away or switches mask.
        if (_renaming != null)
            SetActiveKeyboardElement(null);
        var d = _session.Active;
        int count = d?.Settings.Masks?.Count ?? 0;
        if (index < -1 || index >= count)
            index = -1;
        int current = _state.ActiveMask.Peek();

        if (index >= 0)
        {
            if (_photo.CropTool)
                _photo.CancelCrop();
            if (_photo.SpotTool)
            {
                _photo.EndSpotTool();
                _state.SpotTool.Value = false;
            }
        }

        if (current != index)
        {
            // The mask inspector scrolls on its own; the tab sections get their offset back afterwards.
            if (current < 0)
                _inspectorScroll[_state.Inspector.Peek()] = _rightColumn.ScrollY;
            _state.ActiveMask.Value = index;
            _photo.SelectMask(index);
            ForceLayoutEvaluation();
            _rightColumn.ScrollY = index >= 0
                ? 0f
                : (_inspectorScroll.TryGetValue(_state.Inspector.Peek(), out float y) ? y : 0f);
        }
        SyncMaskUi();
    }

    /// <summary>Refreshes the mask card, the mask inspector values and the left dock split.</summary>
    private void SyncMaskUi()
    {
        if (_masks == null) return;
        var d = _session.Active;
        IReadOnlyList<MaskLayer> masks = d?.Settings.Masks ?? (IReadOnlyList<MaskLayer>)Array.Empty<MaskLayer>();
        if (_state.ActiveMask.Peek() >= masks.Count)
        {
            _state.ActiveMask.Value = -1;
            _photo.SelectMask(-1);
        }
        int active = _state.ActiveMask.Peek();

        bool had = _sync;
        _sync = true;
        _masks.SetMasks(masks, active, _photo.PlacingMask);
        _masks.SectionEnabled = d?.Settings.EnableMasks ?? true;
        var m = ActiveMask;
        if (m != null)
        {
            foreach (var (row, get) in _maskSliders)
                row.Value = get(m);
            _masks.Feather.Value = m.Feather;
            _masks.Amount.Value = m.Amount;
            _maskLightGroup.SectionEnabled = m.EnableLight;
            _maskColorGroup.SectionEnabled = m.EnableColor;
            if (!_maskTitle.IsFocused)
                _maskTitle.Text = m.Name;
            if (_maskIconShape != m.Shape)
            {
                _maskIconShape = m.Shape;
                _maskIcon.BackgroundSvg = IconStore.LoadSvg(MaskPanel.IconFor(m.Shape));
            }
        }
        _sync = had;
        ApplyLayout();
    }

    /// <summary>Double-click on a row: select that mask and put the cursor in its name, all selected.</summary>
    private void StartMaskRename(int index)
    {
        if (_state.ActiveMask.Peek() != index)
            SelectMask(index);
        if (_state.ActiveMask.Peek() != index)
            return;
        SetActiveKeyboardElement(_maskTitle);
        _maskTitle.SelectAll();
    }

    private void BeginMaskRename()
    {
        var m = ActiveMask;
        if (m == null) return;
        _renaming = m;
        _renameDoc = _session.Active;
        _renameFrom = m.Name ?? "";
    }

    /// <summary>The list follows the name as it is typed; undo and saving wait for the commit.</summary>
    private void OnMaskNameTyped(string text)
    {
        if (_sync || _renaming == null) return;
        string name = text.Trim();
        _renaming.Name = name.Length > 0 ? name : _renameFrom;
        if (_renameDoc == _session.Active && _renameDoc?.Settings.Masks is { } masks)
            _masks.SetMasks(masks, _state.ActiveMask.Peek(), _photo.PlacingMask);
    }

    /// <summary>Enter or a click elsewhere keeps the name (one undo step); Esc or an empty name puts the old one back.</summary>
    private void EndMaskRename(bool commit)
    {
        var m = _renaming;
        var d = _renameDoc;
        if (m == null) return;
        _renaming = null;
        _renameDoc = null;

        string typed = _maskTitle.Text.Trim();
        string name = commit && typed.Length > 0 ? typed : _renameFrom;
        m.Name = _renameFrom;
        bool alive = d?.Settings.Masks?.Exists(x => ReferenceEquals(x, m)) == true;
        if (alive && name != _renameFrom)
        {
            d!.Undo.Push(d.Settings);
            m.Name = name;
            WorkspaceStore.SaveSettings(d);
            SetStatus($"Renamed {_renameFrom} to {name}");
        }
        // Esc lands here while the field still has focus, which SyncMaskUi leaves alone.
        bool had = _sync;
        _sync = true;
        _maskTitle.Text = m.Name;
        _sync = had;
        SyncMaskUi();
    }

    /// <summary>Leaves mask editing when another photo comes up or the view changes.</summary>
    private void ResetMaskEditing()
    {
        _photo.CancelMaskPlacement();
        _state.MaskPlacing.Value = null;
        if (MaskSelected)
            SelectMask(-1);
        else
            SyncMaskUi();
    }

    private void StartMaskPlacement(MaskShape shape)
    {
        var d = _session.Active;
        if (IsGallery || d == null)
            return;
        if (_photo.PlacingMask == shape)
        {
            _photo.CancelMaskPlacement();
            return;
        }
        if (d.Proxy == null)
        {
            SetStatus("Masks need the photo loaded first.");
            return;
        }
        if ((d.Settings.Masks?.Count ?? 0) >= MaskLayer.MaxMasks)
        {
            SetStatus($"Up to {MaskLayer.MaxMasks} masks per photo.");
            return;
        }
        if (_photo.CropTool)
            _photo.CancelCrop();
        if (_photo.SpotTool)
        {
            _photo.EndSpotTool();
            _state.SpotTool.Value = false;
        }
        _photo.BeginMaskPlacement(shape);
        _state.MaskPlacing.Value = shape;
        SyncMaskUi();
        SetStatus(shape switch
        {
            MaskShape.Spline => "Click points · first point or Enter closes",
            MaskShape.Linear => "Drag from full effect to none",
            MaskShape.Rectangle => "Drag corner to corner",
            _ => "Drag out from the center · Shift = circle"
        });
    }

    private void DeleteMask(int index)
    {
        var d = _session.Active;
        var masks = d?.Settings.Masks;
        if (d == null || masks == null || index < 0 || index >= masks.Count) return;
        d.Undo.Push(d.Settings);
        string name = masks[index].Name;
        masks.RemoveAt(index);
        int active = _state.ActiveMask.Peek();
        if (active == index)
            SelectMask(-1);
        else if (active > index)
        {
            _state.ActiveMask.Value = active - 1;
            _photo.SelectMask(active - 1);
        }
        PushLook(fast: false, settle: true);
        SyncMaskUi();
        SetStatus($"Deleted {name}");
    }

    private void ToggleMaskEnabled(int index)
    {
        var d = _session.Active;
        var masks = d?.Settings.Masks;
        if (d == null || masks == null || index < 0 || index >= masks.Count) return;
        d.Undo.Push(d.Settings);
        masks[index].Enabled = !masks[index].Enabled;
        PushLook(fast: false, settle: true);
        SyncMaskUi();
    }

    private void ToggleMaskInvert()
    {
        var d = _session.Active;
        var m = ActiveMask;
        if (d == null || m == null) return;
        d.Undo.Push(d.Settings);
        m.Invert = !m.Invert;
        PushLook(fast: false, settle: true);
        SyncMaskUi();
    }

    private void ToggleMaskOverlay()
    {
        bool on = !_state.MaskOverlay.Peek();
        _state.MaskOverlay.Value = on;
        _photo.MaskOverlay = on;
    }

    private void OnMaskToggled(Action<MaskLayer> apply)
    {
        if (_sync) return;
        var d = _session.Active;
        var m = ActiveMask;
        if (d == null || m == null) return;
        d.Undo.Push(d.Settings);
        apply(m);
        PushLook(fast: false, settle: true);
    }

    private void ResetMaskSection(bool light)
    {
        var d = _session.Active;
        var m = ActiveMask;
        if (d == null || m == null) return;
        d.Undo.Push(d.Settings);
        if (light)
        {
            m.Exposure = m.Contrast = m.Highlights = m.Shadows = m.Whites = m.Blacks = m.Dehaze = 0f;
            m.EnableLight = true;
        }
        else
        {
            m.Temperature = m.Tint = m.Saturation = m.Vibrance = 0f;
            m.EnableColor = true;
        }
        PushLook(fast: false, settle: true);
        SyncMaskUi();
        SetStatus(light ? $"Reset {m.Name} light" : $"Reset {m.Name} color");
    }

    private void ResetMaskAdjustments()
    {
        var d = _session.Active;
        var m = ActiveMask;
        if (d == null || m == null) return;
        d.Undo.Push(d.Settings);
        m.ResetAdjustments();
        PushLook(fast: false, settle: true);
        SyncMaskUi();
        SetStatus($"Reset {m.Name}");
    }

    /// <summary>Mask keys: Esc / Enter / Backspace while placing, Esc / Delete / O while a mask is selected.</summary>
    private bool OnMaskKey(UiKey key)
    {
        if (_photo.PlacingMask is MaskShape placing)
        {
            if (key == UiKey.Escape)
            {
                _photo.CancelMaskPlacement();
                return true;
            }
            if (placing == MaskShape.Spline && (key == UiKey.Enter || key == UiKey.KeypadEnter))
            {
                if (!_photo.FinishSplineDraft())
                    SetStatus("A custom mask needs at least 3 points.");
                return true;
            }
            if (placing == MaskShape.Spline && key == UiKey.Backspace)
            {
                _photo.RemoveLastDraftPoint();
                return true;
            }
            return false;
        }

        if (!MaskSelected || _photo.CropTool || _photo.SpotTool)
            return false;
        if (key == UiKey.Escape)
        {
            SelectMask(-1);
            return true;
        }
        if (key == UiKey.Delete || key == UiKey.Backspace)
        {
            DeleteMask(_state.ActiveMask.Peek());
            return true;
        }
        if (key == UiKey.O)
        {
            ToggleMaskOverlay();
            return true;
        }
        return false;
    }
}
