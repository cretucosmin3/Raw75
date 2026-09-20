using System;
using System.Collections.Generic;
using Blossom;
using Blossom.Core;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Raw75.Controls;
using Raw75.Develop;
using Raw75.Imaging;
using Raw75.Io;
using Silk.NET.Input;
using SkiaSharp;

namespace Raw75.Views;

/// <summary>Modal settings: vertical tabs. Dev tab dumps a memory inventory to disk.</summary>
public sealed class SettingsDialog : VisualElement
{
    private const float CardW = 960f;
    private const float CardH = 720f;
    private const float TabW = 140f;

    private readonly VisualElement _card;
    private readonly VisualElement _title;
    private readonly IconButton _close;
    private readonly IconButton _tabThemes;
    private readonly IconButton _tabGeneral;
    private readonly IconButton _tabDev;
    private readonly VisualElement _panelGeneral;
    private readonly VisualElement _panelThemes;
    private readonly ScrollContainer _themesScroll;
    private readonly VisualElement _panelDev;
    private readonly VisualElement _themesHint;
    private readonly ThemePickerCard[] _themeCards;
    private readonly VisualElement _genWorkspace;
    private readonly VisualElement _genPhotos;
    private readonly VisualElement _genCache;
    private readonly VisualElement _genDiskCache;
    private readonly VisualElement _genLog;
    private readonly VisualElement _clearCacheDivider;
    private readonly VisualElement _clearCacheHeading;
    private readonly VisualElement _clearCacheHint;
    private readonly IconButton _clearCacheBtn;
    private readonly VisualElement _clearCacheStatus;
    private readonly IconButton _dumpBtn;
    private readonly VisualElement _dumpHint;
    private readonly VisualElement _dumpSummary;
    private readonly VisualElement _dumpPath;

    public event Action? CacheCleared;

    private string _tab = "Themes";
    private Session? _session;
    private PhotoPane? _pane;

    public SettingsDialog()
    {
        Name = "SettingsDialog";
        Visible = false;
        ZIndex = 1000;
        ReceivesKeyboard = true;
        Transform = new Transform(0, 0, 800, 600)
        {
            Anchor = Anchor.Left | Anchor.Right | Anchor.Top | Anchor.Bottom
        };
        Style = new ElementStyle { BackColor = new SKColor(0, 0, 0, 160) };

        _card = Box("Card", Theme.Panel, Theme.Radius);
        _title = Label("Title", "Settings", Theme.Text, 18, 700, TextAlign.Left);
        _close = new IconButton("Close");
        _close.Clicked += Close;

        _tabThemes = new IconButton("Themes");
        _tabGeneral = new IconButton("General");
        _tabDev = new IconButton("Dev");
        _tabThemes.Clicked += () => SetTab("Themes");
        _tabGeneral.Clicked += () => SetTab("General");
        _tabDev.Clicked += () => SetTab("Dev");

        _panelGeneral = Box("PanelGeneral", Theme.PanelAlt, 6f);
        _genWorkspace = Label("GenWs", "", Theme.Text, 14, 500, TextAlign.Left);
        _genPhotos = Label("GenPhotos", "", Theme.Text, 14, 500, TextAlign.Left);
        _genCache = Label("GenCache", "", Theme.TextDim, 12.5f, 400, TextAlign.Left);
        _genDiskCache = Label("GenDiskCache", "", Theme.TextDim, 12.5f, 400, TextAlign.Left);
        _genLog = Label("GenLog", "", Theme.TextDim, 12, 400, TextAlign.Left);

        _clearCacheDivider = Box("ClearCacheDivider", Theme.HairlineSubtle, 0f);
        _clearCacheHeading = Label("ClearCacheHeading", "STORED PHOTO MEMORY & CACHE", Theme.TextSecondary, 12f, 600, TextAlign.Left);
        _clearCacheHint = Label("ClearCacheHint",
            "Delete cached preview images, thumbnails, and saved edit metadata (.raw75/cache). Resets loaded photos to original defaults if they were edited by previous versions.",
            Theme.TextDim, 12f, 400, TextAlign.Left);
        _clearCacheBtn = new IconButton("Clear Photo Cache & History", "rotate_left");
        _clearCacheBtn.Clicked += ClearCacheClicked;
        _clearCacheStatus = Label("ClearCacheStatus", "", Theme.Accent, 12f, 500, TextAlign.Left);

        _panelGeneral.AddChild(_genWorkspace);
        _panelGeneral.AddChild(_genPhotos);
        _panelGeneral.AddChild(_genCache);
        _panelGeneral.AddChild(_genDiskCache);
        _panelGeneral.AddChild(_genLog);
        _panelGeneral.AddChild(_clearCacheDivider);
        _panelGeneral.AddChild(_clearCacheHeading);
        _panelGeneral.AddChild(_clearCacheHint);
        _panelGeneral.AddChild(_clearCacheBtn);
        _panelGeneral.AddChild(_clearCacheStatus);

        _panelThemes = Box("PanelThemes", Theme.PanelAlt, Theme.Radius);
        _themesScroll = new ScrollContainer
        {
            Name = "ThemesScroll",
            OverflowX = OverflowMode.Clip,
            OverflowY = OverflowMode.Scroll,
            ScrollbarVisibilityX = ScrollbarVisibility.Hidden,
            ScrollbarVisibilityY = ScrollbarVisibility.Auto,
            ScrollbarThickness = 8f,
            ScrollbarRadius = 4f,
            ScrollbarThumbColor = Theme.HairlineStrong,
            ScrollbarTrackColor = SKColors.Transparent,
            Style = new ElementStyle { BackColor = SKColors.Transparent }
        };
        _themesHint = Label("ThemesHint",
            "Chrome stays color-neutral. Accents only hit buttons and selection. Photo well stays dark for grading.",
            Theme.TextDim, 12.5f, 400, TextAlign.Left);
        _themeCards = new ThemePickerCard[Theme.All.Length];
        for (int i = 0; i < Theme.All.Length; i++)
        {
            var pack = Theme.All[i];
            var card = new ThemePickerCard(pack);
            card.Clicked += () => Theme.Apply(pack);
            _themeCards[i] = card;
            _themesScroll.AddChild(card);
        }
        _themesScroll.AddChild(_themesHint);
        _panelThemes.AddChild(_themesScroll);

        _panelDev = Box("PanelDev", Theme.PanelAlt, Theme.Radius);
        _dumpHint = Label("DumpHint",
            "Generate an on-disk diagnostic report of active managed objects, cached images, and native handles.",
            Theme.TextDim, 13, 400, TextAlign.Left);
        _dumpBtn = new IconButton("Dump Memory Inventory", "save");
        _dumpBtn.Clicked += Dump;
        _dumpSummary = Label("DumpSummary", "No dump yet.", Theme.Text, 13, 500, TextAlign.Left);
        if (_dumpSummary is RichBox dumpBox)
            dumpBox.Scrollable = true;
        _dumpPath = Label("DumpPath", "", Theme.TextDim, 12, 400, TextAlign.Left);
        _panelDev.AddChild(_dumpHint);
        _panelDev.AddChild(_dumpBtn);
        _panelDev.AddChild(_dumpSummary);
        _panelDev.AddChild(_dumpPath);

        AddChild(_card);
        _card.AddChild(_title);
        _card.AddChild(_close);
        _card.AddChild(_tabThemes);
        _card.AddChild(_tabGeneral);
        _card.AddChild(_tabDev);
        _card.AddChild(_panelGeneral);
        _card.AddChild(_panelThemes);
        _card.AddChild(_panelDev);

        Events.OnClick += (_, args) =>
        {
            if (!_card.Transform.Computed.RectF.Contains(args.Global.X, args.Global.Y))
            {
                args.Handled = true;
                Close();
            }
        };
        Events.OnKeyDown += k =>
        {
            if (!Visible)
                return;
            if ((Key)k == Key.Escape)
                Close();
        };

        SetTab("Themes");
    }

    public void Open(Session session, PhotoPane pane)
    {
        _session = session;
        _pane = pane;
        _clearCacheStatus.Text = "";
        RefreshGeneral();
        CoverView();
        Visible = true;
        ParentView?.SetActiveKeyboardElement(this);
        InvalidateLayout();
        ForceLayoutSubtree();
        InvalidatePaint();
    }

    public void Close()
    {
        if (!Visible)
            return;
        if (ParentView?.ActiveKeyboardElement == this)
            ParentView.SetActiveKeyboardElement(null);
        Visible = false;
        InvalidatePaint();
    }

    private void ClearCacheClicked()
    {
        try
        {
            int entries = WorkspaceStore.ClearCache();
            _session?.ResetAllDocuments();
            CacheCleared?.Invoke();
            _clearCacheStatus.Text = $"Cleared {entries} cache items. Memory and edits reset.";
            if (_clearCacheStatus.Style?.Text != null)
                _clearCacheStatus.Style.Text.Color = Theme.Accent;
            RefreshGeneral();
        }
        catch (Exception ex)
        {
            _clearCacheStatus.Text = "Clear failed: " + ex.Message;
            if (_clearCacheStatus.Style?.Text != null)
                _clearCacheStatus.Style.Text.Color = Theme.RedChannel;
        }
        InvalidatePaint();
    }

    private void Dump()
    {
        try
        {
            var result = MemoryInventory.Write(_session, _pane);
            _dumpSummary.Text = result.Summary;
            _dumpPath.Text = result.FilePath;
            Log.Info("Memory dump " + result.FilePath + "  process=" + MemSize.Bytes(result.ProcessBytes)
                     + "  named=" + MemSize.Bytes(result.NamedBytes));
        }
        catch (Exception ex)
        {
            _dumpSummary.Text = "Dump failed: " + ex.Message;
            _dumpPath.Text = "";
            Log.Error("Memory dump failed: " + ex);
        }

        InvalidatePaint();
    }

    private void RefreshGeneral()
    {
        string? root = WorkspaceStore.Root;
        _genWorkspace.Text = string.IsNullOrEmpty(root)
            ? "Workspace  none  (caches in app data)"
            : "Workspace  " + root;
        int n = _session?.Documents.Count ?? 0;
        int i = _session?.ActiveIndex ?? -1;
        _genPhotos.Text = n == 0 ? "Photos  none" : $"Photos  {n}   active  {i + 1}/{n}";
        _genCache.Text = "Working copies kept in RAM  " + (_session?.CacheLimit ?? 1)
                         + "  (inactive photos drop proxy/linear, keep preview)";
        var stats = WorkspaceStore.GetCacheStats();
        _genDiskCache.Text = $"Disk cache  {stats.Count} items  ·  {MemSize.Bytes(stats.Bytes)}";
        _genLog.Text = "Log  " + Log.LogFilePath;
    }

    private void SetTab(string tab)
    {
        _tab = tab;
        _panelGeneral.Visible = tab == "General";
        _panelThemes.Visible = tab == "Themes";
        _panelDev.Visible = tab == "Dev";
        _tabGeneral.Toggled = tab == "General";
        _tabThemes.Toggled = tab == "Themes";
        _tabDev.Toggled = tab == "Dev";
        SyncThemeCards();
        InvalidateLayout();
        InvalidatePaint();
    }

    public void RefreshTheme()
    {
        PaintBox(_card, Theme.Panel, Theme.Radius);
        PaintBox(_panelGeneral, Theme.PanelAlt, Theme.Radius);
        PaintBox(_panelThemes, Theme.PanelAlt, Theme.Radius);
        _themesScroll.ScrollbarThumbColor = Theme.HairlineStrong;
        _themesScroll.ScrollbarThumbDragColor = Theme.Accent;
        PaintBox(_panelDev, Theme.PanelAlt, Theme.Radius);
        if (_title.Style?.Text != null) _title.Style.Text.Color = Theme.Text;
        PaintLabel(_themesHint, Theme.TextDim);
        PaintLabel(_genWorkspace, Theme.Text);
        PaintLabel(_genPhotos, Theme.Text);
        PaintLabel(_genCache, Theme.TextDim);
        PaintLabel(_genDiskCache, Theme.TextDim);
        PaintLabel(_genLog, Theme.TextDim);
        _clearCacheDivider.Style.BackColor = Theme.HairlineSubtle;
        PaintLabel(_clearCacheHeading, Theme.TextSecondary);
        PaintLabel(_clearCacheHint, Theme.TextDim);
        PaintLabel(_dumpHint, Theme.TextDim);
        PaintLabel(_dumpSummary, Theme.Text);
        PaintLabel(_dumpPath, Theme.TextDim);
        SyncThemeCards();
        InvalidatePaint();
    }

    private void SyncThemeCards()
    {
        for (int i = 0; i < _themeCards.Length; i++)
            _themeCards[i].Selected = _themeCards[i].Pack.Id == Theme.Id;
    }

    private static void PaintBox(VisualElement e, SKColor fill, float round)
    {
        e.Style.BackColor = fill;
        if (e.Style.Border != null)
        {
            e.Style.Border.Color = Theme.Hairline;
            e.Style.Border.Roundness = round;
        }
    }

    private static void PaintLabel(VisualElement e, SKColor color)
    {
        if (e.Style?.Text != null)
            e.Style.Text.Color = color;
    }

    private void CoverView()
    {
        float w = ParentView?.Width ?? Transform.Width;
        float h = ParentView?.Height ?? Transform.Height;
        if (w < 1f) w = 1f;
        if (h < 1f) h = 1f;
        Transform.SetAbsoluteFrame(0, 0, w, h);
        Transform.Anchor = Anchor.Left | Anchor.Right | Anchor.Top | Anchor.Bottom;
    }

    protected override void LayoutChildren()
    {
        float ox = Transform.Computed.X;
        float oy = Transform.Computed.Y;
        float w = Math.Max(1f, Transform.Computed.Width);
        float h = Math.Max(1f, Transform.Computed.Height);
        float cardX = ox + Math.Max(0, (w - CardW) / 2f);
        float cardY = oy + Math.Max(0, (h - CardH) / 2f);
        _card.Transform.SetAbsoluteFrame(cardX, cardY, CardW, CardH);

        const float pad = 18f;
        _title.Transform.SetAbsoluteFrame(cardX + pad, cardY + 14, 240, 26);
        _close.Transform.SetAbsoluteFrame(cardX + CardW - pad - 96, cardY + 12, 96, 32);

        float tabX = cardX + 10;
        float tabY = cardY + 54;
        float tabH = 34f;
        _tabThemes.Transform.SetAbsoluteFrame(tabX, tabY, TabW, tabH);
        _tabGeneral.Transform.SetAbsoluteFrame(tabX, tabY + tabH + 6, TabW, tabH);
        _tabDev.Transform.SetAbsoluteFrame(tabX, tabY + (tabH + 6) * 2, TabW, tabH);

        float px = cardX + 10 + TabW + 12;
        float py = cardY + 54;
        float pw = cardX + CardW - pad - px;
        float ph = cardY + CardH - pad - py;
        _panelGeneral.Transform.SetAbsoluteFrame(px, py, pw, ph);
        _panelThemes.Transform.SetAbsoluteFrame(px, py, pw, ph);
        _panelDev.Transform.SetAbsoluteFrame(px, py, pw, ph);

        float ix = px + 16;
        float iy = py + 16;
        float iw = pw - 32;
        _genWorkspace.Transform.SetAbsoluteFrame(ix, iy, iw, 28);
        _genPhotos.Transform.SetAbsoluteFrame(ix, iy + 32, iw, 24);
        _genCache.Transform.SetAbsoluteFrame(ix, iy + 60, iw, 24);
        _genDiskCache.Transform.SetAbsoluteFrame(ix, iy + 88, iw, 24);
        _genLog.Transform.SetAbsoluteFrame(ix, iy + 116, iw, 24);

        _clearCacheDivider.Transform.SetAbsoluteFrame(ix, iy + 152, iw, 1);
        _clearCacheHeading.Transform.SetAbsoluteFrame(ix, iy + 166, iw, 20);
        _clearCacheHint.Transform.SetAbsoluteFrame(ix, iy + 190, iw, 36);
        _clearCacheBtn.Transform.SetAbsoluteFrame(ix, iy + 236, 260, 36);
        _clearCacheStatus.Transform.SetAbsoluteFrame(ix + 272, iy + 236, iw - 272, 36);

        _dumpHint.Transform.SetAbsoluteFrame(ix, iy, iw, 56);
        _dumpBtn.Transform.SetAbsoluteFrame(ix, iy + 68, 220, 34);
        _dumpSummary.Transform.SetAbsoluteFrame(ix, iy + 112, iw, 48);
        _dumpPath.Transform.SetAbsoluteFrame(ix, iy + 164, iw, 36);

        float sx = px + 10f;
        float sy = py + 10f;
        float sw = pw - 20f;
        float sh = ph - 20f;
        _themesScroll.Transform.SetAbsoluteFrame(sx, sy, sw, sh);

        float padX = 8f;
        float innerW = Math.Max(1f, sw - padX * 2f - 10f);
        _themesHint.Transform.SetAbsoluteFrame(sx + padX, sy + 8f, innerW, 36f);
        float gap = 10f;
        float cardW = (innerW - gap) * 0.5f;
        float cardH = 100f;
        float gridY = sy + 50f;
        int rows = (_themeCards.Length + 1) / 2;
        for (int i = 0; i < _themeCards.Length; i++)
        {
            int col = i % 2;
            int row = i / 2;
            _themeCards[i].Transform.SetAbsoluteFrame(
                sx + padX + col * (cardW + gap),
                gridY + row * (cardH + gap),
                cardW, cardH);
        }
        float contentH = 50f + rows * (cardH + gap) + 8f;
        _themesScroll.SetContentSize(sw, Math.Max(contentH, sh));
    }

    private static VisualElement Box(string name, SKColor fill, float round)
    {
        return new VisualElement
        {
            Name = name,
            Style = new ElementStyle
            {
                BackColor = fill,
                Border = new BorderStyle { Width = 1, Color = Theme.Hairline, Roundness = round }
            }
        };
    }

    private static VisualElement Label(string name, string text, SKColor color, float size, int weight, TextAlign align)
    {
        return new RichBox
        {
            Name = name,
            Text = text,
            IsClickthrough = true,
            Overflow = OverflowMode.Clip,
            Style = new ElementStyle
            {
                BackColor = SKColors.Transparent,
                Text = new TextStyle
                {
                    Color = color,
                    Size = size,
                    Weight = weight,
                    Alignment = align,
                    Padding = 2,
                    Overflow = TextOverflow.Ellipsis,
                    MaxLines = name is "DumpPath" or "DumpHint" or "DumpSummary" or "ClearCacheHint" or "ThemesHint" ? 2 : 1
                }
            }
        };
    }

}

internal sealed class ThemePickerCard : VisualElement
{
    public UiThemePack Pack { get; }
    public event Action? Clicked;

    private bool _selected;
    private bool _hovered;

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            ApplyChrome();
        }
    }

    public ThemePickerCard(UiThemePack pack)
    {
        Pack = pack;
        Name = "ThemeCard_" + pack.Id;
        Cursor = StandardCursor.Hand;
        Style = new ElementStyle
        {
            BackColor = pack.Section,
            Border = new BorderStyle
            {
                Width = 1.5f,
                Color = Theme.Hairline,
                Roundness = pack.Radius
            }
        };

        Events.OnMouseEnter += _ => { _hovered = true; ApplyChrome(); };
        Events.OnMouseLeave += _ => { _hovered = false; ApplyChrome(); };
        Events.OnClick += (_, args) =>
        {
            if (args.Button != (int)MouseButton.Left) return;
            Clicked?.Invoke();
            args.Handled = true;
        };
    }

    protected override void OnAfterStyleDraw(List<DrawCommand> cmds)
    {
        cmds.Add(new DrawCallbackCommand(Draw));
    }

    private void ApplyChrome()
    {
        Style.BackColor = Pack.Section;
        if (Style.Border != null)
        {
            Style.Border.Color = _selected ? Pack.Accent : (_hovered ? Pack.HairlineStrong : Pack.Hairline);
            Style.Border.Roundness = Pack.Radius;
            Style.Border.Width = _selected ? 2f : 1f;
        }
        InvalidatePaint();
    }

    private void Draw(SKCanvas canvas)
    {
        float w = Transform.Computed.Width;
        float h = Transform.Computed.Height;
        if (w < 8f || h < 8f) return;

        float pad = 12f;
        float sw = (w - pad * 2f - 12f) / 4f;
        float sh = 22f;
        float sy = 14f;
        SKColor[] chips = { Pack.Window, Pack.Panel, Pack.Section, Pack.Accent };
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, Color = Pack.Hairline };
        for (int i = 0; i < 4; i++)
        {
            fill.Color = chips[i];
            var r = new SKRect(pad + i * (sw + 4f), sy, pad + i * (sw + 4f) + sw, sy + sh);
            canvas.DrawRoundRect(r, Pack.RadiusSm, Pack.RadiusSm, fill);
            canvas.DrawRoundRect(r, Pack.RadiusSm, Pack.RadiusSm, stroke);
        }

        using var title = new SKPaint
        {
            IsAntialias = true,
            Color = Pack.Text,
            TextSize = 14f,
            Typeface = Theme.GetTypeface(700)
        };
        using var body = new SKPaint
        {
            IsAntialias = true,
            Color = Pack.TextDim,
            TextSize = 11.5f,
            Typeface = Theme.GetTypeface(400)
        };
        canvas.DrawText(Pack.Name, pad, sy + sh + 20f, title);
        canvas.DrawText(Pack.Description, pad, sy + sh + 38f, body);
    }
}
