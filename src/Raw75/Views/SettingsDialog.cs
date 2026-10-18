using System;
using Blossom;
using Blossom.Animations;
using Blossom.Core;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Blossom.Primitives;
using Blossom.Reactive;
using Raw75.Controls;
using Raw75.Develop;
using Raw75.Imaging;
using Raw75.Io;
using SkiaSharp;

namespace Raw75.Views;

/// <summary>Modal settings with Themes / General / Dev tabs. Dev dumps a memory inventory to disk.</summary>
public sealed class SettingsDialog : ModalDialog
{
    private enum Tab { Themes, General, Dev }

    private const float CardW = 1040f;
    private const float CardH = 720f;
    private const float KeyW = 150f;

    private readonly Signal<Tab> _tab = new(Tab.Themes);
    private readonly Signal<(string Text, bool Error)> _clearStatus = new(("", false));

    private readonly RichBox _genWorkspace;
    private readonly RichBox _genPhotos;
    private readonly RichBox _genCache;
    private readonly RichBox _genDiskCache;
    private readonly RichBox _genLog;
    private readonly RichBox _dumpSummary;
    private readonly RichBox _dumpPath;

    public event Action? CacheCleared;

    private Session? _session;
    private PhotoPane? _pane;

    public SettingsDialog() : base("SettingsDialog", zIndex: 1000, cardWidth: CardW, cardHeight: CardH)
    {
        // Header: title over a strip of navigation tabs whose underline sits on the hairline below.
        var tabs = new Stack { Name = "SettingsTabs", Orientation = Orientation.Horizontal, Gap = 4f };
        tabs.MinHeight = 34f;
        var header = new Stack
        {
            Name = "SettingsHeader",
            Orientation = Orientation.Vertical,
            Gap = 8f,
            Padding = new Thickness(CardPad, 16f, CardPad, 0f)
        };
        header.AddChild(Label("Settings", TextRole.Title));
        header.AddChild(tabs);

        // Themes
        var themesPanel = Panel("Themes");
        themesPanel.AddChild(Label(
            "Chrome stays colour-neutral; the accent only marks buttons and selection, and the photo well stays dark for grading.",
            TextRole.Body));
        var gallery = new ThemeGallery();
        themesPanel.AddChild(gallery);
        Stack.SetGrow(gallery, 1f);

        // General
        var generalPanel = Panel("General");
        var info = new Stack { Name = "GeneralInfo", Orientation = Orientation.Vertical, Gap = 6f };
        info.AddChild(InfoRow("Workspace", out _genWorkspace));
        info.AddChild(InfoRow("Photos", out _genPhotos));
        info.AddChild(InfoRow("Working copies", out _genCache));
        info.AddChild(InfoRow("Disk cache", out _genDiskCache));
        info.AddChild(InfoRow("Log", out _genLog));
        generalPanel.AddChild(Field("Session", info));
        generalPanel.AddChild(Separator());

        var clearBtn = new IconButton("Clear photo cache & edits", "rotate_left");
        clearBtn.Clicked += ClearCacheClicked;
        var clearStatus = Label("", TextRole.Dim);
        clearStatus.Bind(s =>
        {
            var (text, error) = _clearStatus.Value;
            s.Text = text;
            s.Style.Text.Color = error ? Theme.RedChannel : Theme.Accent;
        });
        generalPanel.AddChild(Field("Stored photo memory & cache",
            Label("Deletes cached previews, thumbnails and saved edit metadata (.raw75/cache). Loaded photos reset to their original defaults.",
                TextRole.Body),
            ActionRow(clearBtn, clearStatus)));

        // Dev
        var devPanel = Panel("Dev");
        var dumpBtn = new IconButton("Dump memory inventory", "info");
        dumpBtn.Clicked += Dump;
        _dumpPath = Label("", TextRole.Dim);
        _dumpSummary = Label("No dump yet.", TextRole.Body);
        _dumpSummary.Scrollable = true;
        _dumpSummary.IsClickthrough = false;
        devPanel.AddChild(Field("Diagnostics",
            Label("Writes an on-disk report of live managed objects, cached images and native handles.", TextRole.Body),
            ActionRow(dumpBtn, _dumpPath)));
        devPanel.AddChild(_dumpSummary);
        Stack.SetGrow(_dumpSummary, 1f);

        var content = new Stack { Name = "SettingsContent", Orientation = Orientation.Vertical };
        var panels = new (Tab Id, IconButton Button, Stack Panel)[]
        {
            (Tab.Themes, TabButton("Themes"), themesPanel),
            (Tab.General, TabButton("General"), generalPanel),
            (Tab.Dev, TabButton("Dev"), devPanel)
        };
        foreach (var (id, button, panel) in panels)
        {
            button.Clicked += () => _tab.Value = id;
            tabs.AddChild(button);
            content.AddChild(panel);
            Stack.SetGrow(panel, 1f);
        }

        var done = new IconButton("Done", primary: true);
        done.Clicked += Close;

        Card.AddChild(header);
        Card.AddChild(Separator());
        Card.AddChild(content);
        Stack.SetGrow(content, 1f);
        Card.AddChild(Separator());
        Card.AddChild(Footer(null, done));

        this.Bind(_ =>
        {
            var tab = _tab.Value;
            foreach (var (id, button, panel) in panels)
            {
                button.Toggled = id == tab;
                panel.Visible = id == tab;
            }
            content.InvalidateLayout();
        });
    }

    public void Open(Session session, PhotoPane pane)
    {
        _session = session;
        _pane = pane;
        _clearStatus.Value = ("", false);
        RefreshGeneral();
        ShowModal();
    }

    private void ClearCacheClicked()
    {
        try
        {
            int entries = WorkspaceStore.ClearCache();
            _session?.ResetAllDocuments();
            CacheCleared?.Invoke();
            _clearStatus.Value = ($"Cleared {entries} cache items. Memory and edits reset.", false);
            RefreshGeneral();
        }
        catch (Exception ex)
        {
            _clearStatus.Value = ("Clear failed: " + ex.Message, true);
        }
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
    }

    private void RefreshGeneral()
    {
        string? root = WorkspaceStore.Root;
        _genWorkspace.Text = string.IsNullOrEmpty(root) ? "None (caches in app data)" : root;
        int n = _session?.Documents.Count ?? 0;
        int i = _session?.ActiveIndex ?? -1;
        _genPhotos.Text = n == 0 ? "None" : $"{n}  ·  active {i + 1}/{n}";
        _genCache.Text = $"{_session?.CacheLimit ?? 1} kept in RAM  (inactive photos drop proxy/linear, keep preview)";
        var stats = WorkspaceStore.GetCacheStats();
        _genDiskCache.Text = $"{stats.Count} items  ·  {MemSize.Bytes(stats.Bytes)}";
        _genLog.Text = Log.LogFilePath;
    }

    private static Stack Panel(string name) => new()
    {
        Name = $"SettingsPanel_{name}",
        Orientation = Orientation.Vertical,
        Gap = 16f,
        Padding = new Thickness(CardPad, 18f, CardPad, 18f)
    };

    private static IconButton TabButton(string caption)
    {
        var button = new IconButton(caption) { Variant = ButtonVariant.Tab };
        button.MinWidth = 76f;
        button.MinHeight = button.MaxHeight = 34f;
        return button;
    }

    /// <summary>Fixed-width key with its value filling the rest of the row.</summary>
    private static Stack InfoRow(string key, out RichBox value)
    {
        var row = new Stack { Name = $"Info_{key}", Orientation = Orientation.Horizontal, Align = LayoutAlign.Center, Gap = 12f };
        var keyLabel = Label(key, TextRole.Dim);
        keyLabel.MinWidth = keyLabel.MaxWidth = KeyW;
        value = Label("", TextRole.Body);
        value.Bind(v => v.Style.Text.Color = Theme.Text);
        row.AddChild(keyLabel);
        row.AddChild(value);
        Stack.SetGrow(value, 1f);
        return row;
    }

    private static Stack ActionRow(IconButton action, VisualElement note)
    {
        var row = new Stack { Name = "ActionRow", Orientation = Orientation.Horizontal, Align = LayoutAlign.Center, Gap = 12f };
        row.AddChild(action);
        row.AddChild(note);
        Stack.SetGrow(note, 1f);
        return row;
    }

    /// <summary>Scrolling three-column gallery of compact theme cards. Scroll content keeps explicit frames.</summary>
    private sealed class ThemeGallery : ScrollContainer
    {
        private const int Columns = 3;
        private const float TileH = 60f;
        private const float TileGap = 8f;
        private readonly ThemePickerCard[] _cards;

        public ThemeGallery()
        {
            Name = "ThemesScroll";
            OverflowX = OverflowMode.Clip;
            OverflowY = OverflowMode.Scroll;
            ScrollbarVisibilityX = ScrollbarVisibility.Hidden;
            ScrollbarVisibilityY = ScrollbarVisibility.Auto;
            ScrollbarThickness = 6f;
            ScrollbarRadius = 3f;
            ScrollbarTrackColor = SKColors.Transparent;
            Style = new ElementStyle { BackColor = SKColors.Transparent };

            _cards = new ThemePickerCard[Theme.All.Length];
            for (int i = 0; i < _cards.Length; i++)
            {
                var pack = Theme.All[i];
                var card = new ThemePickerCard(pack);
                card.Clicked += () => Theme.Apply(pack);
                _cards[i] = card;
                AddChild(card);
            }

            this.Bind(s =>
            {
                s.ScrollbarThumbColor = Theme.HairlineStrong;
                s.ScrollbarThumbHoverColor = Theme.TextDim;
                s.ScrollbarThumbDragColor = Theme.Accent;
                s.InvalidatePaint();
            });
        }

        protected override void LayoutChildren()
        {
            float x = Transform.Computed.X;
            float y = Transform.Computed.Y;
            float w = Math.Max(1f, Transform.Computed.Width - ScrollbarThickness - 6f);
            float tileW = (w - TileGap * (Columns - 1)) / Columns;
            int rows = (_cards.Length + Columns - 1) / Columns;
            for (int i = 0; i < _cards.Length; i++)
                _cards[i].Transform.SetAbsoluteFrame(
                    x + (i % Columns) * (tileW + TileGap), y + (i / Columns) * (TileH + TileGap), tileW, TileH);
            SetContentSize(w, rows * TileH + (rows - 1) * TileGap);
            base.LayoutChildren();
        }
    }
}

/// <summary>Theme preview tile on <see cref="ButtonBase"/>, painted in its own pack's colours.</summary>
internal sealed class ThemePickerCard : ButtonBase
{
    private float _hover;
    private bool _selected;

    public UiThemePack Pack { get; }

    public ThemePickerCard(UiThemePack pack)
    {
        Pack = pack;
        Name = "ThemeCard_" + pack.Id;
        ReceivesKeyboard = false;
        Style.BackColor = SKColors.Transparent;
        Events.OnClick += (_, e) => { if (e.Button == PointerButton.Left) e.Handled = true; };

        this.Bind(card =>
        {
            card._selected = Theme.Id == pack.Id;
            card.InvalidatePaint();
        });
    }

    protected override void OnHoverChanged()
    {
        _hover = IsHovered ? 1f : 0f;
        InvalidatePaint();
    }

    protected override void Paint(SKCanvas canvas)
    {
        float w = W, h = H;
        if (w < 8f || h < 8f) return;

        var p = Pack;
        float r = p.Radius;
        float hover = _hover;
        var box = new SKRect(0.5f, 0.5f, w - 0.5f, h - 0.5f);

        using (var fill = new SKPaint { Color = Anim.Lerp(p.Section, p.SectionHeaderHover, hover * 0.5f), IsAntialias = true })
            canvas.DrawRoundRect(box, r, r, fill);
        using (var edge = new SKPaint
        {
            Color = _selected ? p.Accent : Anim.Lerp(p.Hairline, p.HairlineStrong, hover),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = _selected ? 2f : 1f,
            IsAntialias = true
        })
        {
            var rim = box;
            if (_selected) rim.Inflate(-0.5f, -0.5f);
            canvas.DrawRoundRect(rim, r, r, edge);
        }

        // Preview chip: the pack's window, a panel with a section card, and an accent bar.
        const float pad = 10f;
        float chipS = h - pad * 2f;
        var chipRect = SKRect.Create(pad, pad, chipS, chipS);
        float cr = Math.Min(p.RadiusSm + 1f, chipS * 0.25f);
        using (var paint = new SKPaint { IsAntialias = true })
        {
            paint.Color = p.Window;
            canvas.DrawRoundRect(chipRect, cr, cr, paint);
            canvas.Save();
            canvas.ClipRoundRect(new SKRoundRect(chipRect, cr), antialias: true);
            paint.Color = p.Panel;
            canvas.DrawRect(SKRect.Create(chipRect.MidX, chipRect.Top, chipRect.Width * 0.5f, chipRect.Height), paint);
            paint.Color = p.Section;
            canvas.DrawRoundRect(SKRect.Create(chipRect.MidX + 3f, chipRect.Top + 4f, chipRect.Width * 0.5f - 6f, chipRect.Height * 0.42f), p.RadiusSm * 0.5f, p.RadiusSm * 0.5f, paint);
            paint.Color = p.Accent;
            canvas.DrawRoundRect(SKRect.Create(chipRect.Left + 4f, chipRect.Bottom - 9f, chipRect.Width - 8f, 4f), 2f, 2f, paint);
            canvas.Restore();
        }
        using (var chipEdge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, Color = p.HairlineStrong })
            canvas.DrawRoundRect(chipRect, cr, cr, chipEdge);

        float textX = chipRect.Right + 10f;
        float textW = Math.Max(10f, w - textX - pad - (_selected ? 20f : 0f));
        using var title = new TextPaint { Color = p.Text, TextSize = 13f, Typeface = Theme.GetTypeface(600) };
        using var body = new TextPaint { Color = p.TextDim, TextSize = 11f, Typeface = Theme.GetTypeface(400) };
        canvas.DrawText(Blossom.Utils.Text.Ellipsize(p.Name, title.Font, textW), textX, h * 0.5f - 3f, title);
        canvas.DrawText(Blossom.Utils.Text.Ellipsize(p.Description, body.Font, textW), textX, h * 0.5f + 13f, body);

        if (!_selected) return;

        // Active badge: accent disc with a check, top-right.
        float cx = w - pad - 7f, cy = pad + 7f;
        using (var disc = new SKPaint { Color = p.Accent, IsAntialias = true })
            canvas.DrawCircle(cx, cy, 7f, disc);
        using var check = new SKPaint
        {
            Color = p.OutlineAccent ? p.Window : p.ButtonTextOnAccent,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            StrokeCap = SKStrokeCap.Round,
            IsAntialias = true
        };
        canvas.DrawLine(cx - 3f, cy + 0.2f, cx - 0.9f, cy + 2.3f, check);
        canvas.DrawLine(cx - 0.9f, cy + 2.3f, cx + 3.1f, cy - 2.1f, check);
    }
}
