using System;
using System.Collections.Generic;
using Blossom.Core;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Blossom.Primitives;
using Blossom.Reactive;
using Raw75.Develop;
using Raw75.Io;
using SkiaSharp;

namespace Raw75.Controls;

public enum GalleryThumbSize
{
    Small,
    Medium,
    Large
}

/// <summary>
/// Full-canvas lightbox / gallery grid view:
/// Displays a multi-column contact sheet with ready / favourite marks, filter tabs,
/// and double-click to open in develop view. Virtualized for fast rendering.
/// </summary>
public sealed class GalleryView : VisualElement
{
    private const float TopBarH = 46f;
    private const float Gap = 12f;
    private const float Pad = 16f;

    private readonly record struct MarkCounts(int Total, int Ready, int Favorites);

    private readonly Stack _headerBar;
    private readonly VisualElement _headerDivider;
    private readonly VisualElement _groupSep;
    private readonly IconButton _btnFilterReady;
    private readonly IconButton _btnFilterFavorite;
    private readonly VisualElement _countLabel;
    private readonly GalleryScroller _scroller;

    private readonly Signal<PhotoFilter> _filter = new(PhotoFilter.All);
    private readonly Signal<GalleryThumbSize> _thumbSize = new(GalleryThumbSize.Small);
    private readonly Signal<MarkCounts> _counts = new(default);

    private float CardW => _thumbSize.Peek() switch
    {
        GalleryThumbSize.Medium => 336f,
        GalleryThumbSize.Large => 456f,
        _ => 240f
    };

    private float CardH => _thumbSize.Peek() switch
    {
        GalleryThumbSize.Medium => 360f,
        GalleryThumbSize.Large => 488f,
        _ => 258f
    };

    private readonly Dictionary<int, GalleryCard> _activeCards = new();
    private readonly Stack<GalleryCard> _cardPool = new();
    private readonly List<int> _recycledKeys = new();
    private readonly List<int> _filteredIndices = new();

    private IReadOnlyList<PhotoDocument> _docs = Array.Empty<PhotoDocument>();
    private int _activeIndex = -1;

    private int _lastFirstRow = -1;
    private int _lastLastRow = -1;
    private float _lastOx = float.NaN;
    private float _lastOy = float.NaN;
    private float _lastW = float.NaN;
    private int _lastCols = -1;

    public event Action<int>? PhotoSelected;
    public event Action<int, bool, bool>? SelectionRequested;
    public event Action<int>? PhotoDoubleClicked;
    public event Action<int, bool>? ReadyToggled;
    public event Action<int, bool>? FavoriteToggled;
    public event Action? BatchReadyChanged;
    public event Action<PhotoFilter>? FilterChanged;
    public event Action<int, float, float>? ContextMenuRequested;

    private readonly HashSet<int> _selectedIndices = new();

    public PhotoFilter ActiveFilter => _filter.Value;

    public float ScrollY
    {
        get => _scroller.ScrollY;
        set => _scroller.ScrollY = value;
    }

    public GalleryView()
    {
        Name = "GalleryView";
        Style = new ElementStyle { Border = new BorderStyle { Width = 0 } };

        // Header: [filters] | [batch marks] ........ count  [S M L]
        var btnAll = new IconButton("All");
        btnAll.Clicked += () => SetFilter(PhotoFilter.All);
        btnAll.Bind(b =>
        {
            b.Toggled = _filter.Value == PhotoFilter.All;
            b.Caption = $"All ({_counts.Value.Total})";
        });

        _btnFilterReady = new IconButton("Ready", "check");
        _btnFilterReady.Clicked += () => ToggleFilterFlag(PhotoFilter.Ready);
        _btnFilterReady.Bind(b =>
        {
            b.Toggled = (_filter.Value & PhotoFilter.Ready) != 0;
            b.Caption = $"Ready ({_counts.Value.Ready})";
        });

        _btnFilterFavorite = new IconButton("Favorites", "heart_filled");
        _btnFilterFavorite.Clicked += () => ToggleFilterFlag(PhotoFilter.Favorite);
        _btnFilterFavorite.Bind(b =>
        {
            b.Toggled = (_filter.Value & PhotoFilter.Favorite) != 0;
            b.Caption = $"Favorites ({_counts.Value.Favorites})";
        });

        _groupSep = new VisualElement { Name = "Gallery_HeaderSep", IsClickthrough = true };
        _groupSep.Transform.Width = 1f;
        _groupSep.Transform.Height = 18f;

        var markAll = new IconButton("Mark All Ready", "check");
        markAll.Clicked += () => SetAllReady(true);
        var clearAll = new IconButton("Clear Ready", "cross");
        clearAll.Clicked += () => SetAllReady(false);

        _countLabel = new VisualElement
        {
            Name = "Gallery_CountLabel",
            IsClickthrough = true,
            Style = new ElementStyle
            {
                BackColor = SKColors.Transparent,
                Text = new TextStyle
                {
                    Size = 12,
                    Weight = 500,
                    Alignment = TextAlign.Right,
                    Padding = 0
                }
            }
        };
        _countLabel.BindText(() =>
        {
            var n = _counts.Value;
            return $"{n.Total} photos · {n.Ready} ready · {n.Favorites} favorites";
        });

        _headerBar = new Stack
        {
            Name = "Gallery_HeaderBar",
            Orientation = Orientation.Horizontal,
            Align = LayoutAlign.Center,
            Gap = 12f,
            Padding = new Thickness(Pad, 0, Pad, 0),
            Style = new ElementStyle { Border = new BorderStyle { Width = 0 } }
        };
        _headerBar.AddChild(Cluster("Gallery_Filters", btnAll, _btnFilterReady, _btnFilterFavorite));
        _headerBar.AddChild(_groupSep);
        _headerBar.AddChild(Cluster("Gallery_Marks", markAll, clearAll));
        _headerBar.AddChild(_countLabel);
        Stack.SetGrow(_countLabel, 1f);
        _headerBar.AddChild(Cluster("Gallery_Sizes",
            SizeButton("gallery_size_s", GalleryThumbSize.Small),
            SizeButton("gallery_size_m", GalleryThumbSize.Medium),
            SizeButton("gallery_size_l", GalleryThumbSize.Large)));
        AddChild(_headerBar);

        _headerDivider = new VisualElement { Name = "Gallery_HeaderDivider", IsClickthrough = true };
        AddChild(_headerDivider);

        _scroller = new GalleryScroller(this)
        {
            Name = "Gallery_Scroller",
            OverflowX = OverflowMode.Clip,
            OverflowY = OverflowMode.Scroll,
            ScrollbarVisibilityX = ScrollbarVisibility.Hidden,
            ScrollbarVisibilityY = ScrollbarVisibility.Auto,
            ScrollbarThickness = 6f,
            ScrollbarRadius = 3f,
            ScrollbarPadding = 2f,
            ScrollbarTrackColor = SKColors.Transparent,
            SmoothScroll = true,
            Style = new ElementStyle { Border = new BorderStyle { Width = 0 } }
        };
        AddChild(_scroller);

        this.Bind(_ => ApplyTheme());
    }

    /// <summary>A tight row of related header buttons.</summary>
    private static Stack Cluster(string name, params IconButton[] buttons)
    {
        var row = new Stack
        {
            Name = name,
            Orientation = Orientation.Horizontal,
            Align = LayoutAlign.Center,
            Gap = 4f
        };
        foreach (var b in buttons)
            row.AddChild(b);
        return row;
    }

    private IconButton SizeButton(string icon, GalleryThumbSize size)
    {
        var btn = IconButton.Icon(icon);
        btn.Clicked += () => SetThumbSize(size);
        btn.Bind(b => b.Toggled = _thumbSize.Value == size);
        return btn;
    }

    private void ApplyTheme()
    {
        Style.BackColor = Theme.Canvas;
        _headerBar.Style.BackColor = Theme.Window;
        _headerDivider.Style.BackColor = Theme.Hairline;
        _groupSep.Style.BackColor = Theme.Hairline;
        _countLabel.Style.Text.Color = Theme.TextDim;
        _btnFilterReady.IconColor = Theme.Success;
        _btnFilterFavorite.IconColor = Theme.Favorite;
        _scroller.Style.BackColor = Theme.Canvas;
        ThumbTile.StyleScrollbars(_scroller);
        InvalidatePaint();
    }

    public void Bind(IReadOnlyList<PhotoDocument> docs, int activeIndex, IEnumerable<int>? selectedIndices = null)
    {
        _docs = docs ?? Array.Empty<PhotoDocument>();
        _activeIndex = activeIndex;
        _selectedIndices.Clear();
        if (selectedIndices != null)
        {
            foreach (var idx in selectedIndices)
                _selectedIndices.Add(idx);
        }
        else if (activeIndex >= 0)
        {
            _selectedIndices.Add(activeIndex);
        }
        _lastFirstRow = -1;
        _lastLastRow = -1;

        UpdateFilteredIndices();
        UpdateFilterCounts();

        float w = Math.Max(1f, _scroller.Transform.Computed.Width > 0 ? _scroller.Transform.Computed.Width : (_scroller.Transform.Width > 0 ? _scroller.Transform.Width : Transform.Width));
        float h = Math.Max(1f, _scroller.Transform.Computed.Height > 0 ? _scroller.Transform.Computed.Height : Math.Max(1f, Transform.Height - TopBarH));
        _scroller.SetContentSize(w, Math.Max(ContentHeight(w), h));
        UpdateVirtualCards(force: true);
        InvalidatePaint();
    }

    public void SetSelection(IEnumerable<int> selectedIndices, int activeIndex)
    {
        _selectedIndices.Clear();
        if (selectedIndices != null)
        {
            foreach (var idx in selectedIndices)
                _selectedIndices.Add(idx);
        }
        else if (activeIndex >= 0)
        {
            _selectedIndices.Add(activeIndex);
        }
        _activeIndex = activeIndex;
        foreach (var card in _activeCards.Values)
            card.SetSelection(card.Index == activeIndex, _selectedIndices.Contains(card.Index));
        InvalidatePaint();
    }

    public void SetSelected(int index)
    {
        SetSelection(new[] { index }, index);
    }

    public void SetReady(int index, bool ready)
    {
        if (index >= 0 && index < _docs.Count)
            _docs[index].IsReady = ready;

        foreach (var card in _activeCards.Values)
        {
            if (card.Index == index)
            {
                card.SetReady(ready);
                break;
            }
        }
        OnMarksChanged(PhotoFilter.Ready | PhotoFilter.Favorite);
    }

    public void SetFavorite(int index, bool favorite)
    {
        if (index >= 0 && index < _docs.Count)
            _docs[index].IsFavorite = favorite;

        foreach (var card in _activeCards.Values)
        {
            if (card.Index == index)
            {
                card.SetFavorite(favorite);
                break;
            }
        }
        OnMarksChanged(PhotoFilter.Ready | PhotoFilter.Favorite);
    }

    public void RefreshThumbs()
    {
        foreach (var card in _activeCards.Values)
            card.RefreshThumb();
        InvalidatePaint();
    }

    public void RefreshThumb(PhotoDocument doc)
    {
        if (doc == null) return;
        foreach (var card in _activeCards.Values)
        {
            if (ReferenceEquals(card.Doc, doc))
            {
                card.RefreshThumb();
                break;
            }
        }
        InvalidatePaint();
    }

    public void SetFilter(PhotoFilter filter)
    {
        if (_filter.Peek() == filter) return;
        _filter.Value = filter;
        UpdateFilteredIndices();
        ScrollY = 0f;
        InvalidateLayout();
        InvalidatePaint();
        FilterChanged?.Invoke(filter);
    }

    public void SetThumbSize(GalleryThumbSize size)
    {
        if (_thumbSize.Peek() == size) return;

        _thumbSize.Value = size;
        _lastFirstRow = -1;
        _lastLastRow = -1;
        _lastCols = -1;
        ScrollY = 0f;
        InvalidateLayout();
        InvalidatePaint();
    }

    private void ToggleFilterFlag(PhotoFilter flag) => SetFilter(_filter.Peek() ^ flag);

    private void UpdateFilterCounts()
    {
        int total = _docs.Count;
        int ready = 0;
        int favorites = 0;
        for (int i = 0; i < total; i++)
        {
            if (_docs[i].IsReady) ready++;
            if (_docs[i].IsFavorite) favorites++;
        }
        _counts.Value = new MarkCounts(total, ready, favorites);
    }

    private void UpdateFilteredIndices()
    {
        var filter = _filter.Peek();
        _filteredIndices.Clear();
        for (int i = 0; i < _docs.Count; i++)
        {
            if (PhotoFilterMatch.Matches(_docs[i], filter))
                _filteredIndices.Add(i);
        }
    }

    /// <summary>Recounts marks and re-filters when the active filter depends on <paramref name="affected"/>.</summary>
    private void OnMarksChanged(PhotoFilter affected)
    {
        UpdateFilterCounts();
        if ((_filter.Peek() & affected) != 0)
        {
            UpdateFilteredIndices();
            InvalidateLayout();
        }
        InvalidatePaint();
    }

    private void SetAllReady(bool ready)
    {
        for (int i = 0; i < _docs.Count; i++)
            _docs[i].IsReady = ready;

        foreach (var card in _activeCards.Values)
            card.SetReady(ready);

        OnMarksChanged(PhotoFilter.Ready | PhotoFilter.Favorite);
        BatchReadyChanged?.Invoke();
    }

    private int Columns(float viewW)
    {
        float availW = Math.Max(1f, viewW - Pad * 2f);
        return Math.Max(1, (int)((availW + Gap) / (CardW + Gap)));
    }

    private float ContentHeight(float viewW)
    {
        int cols = Columns(viewW);
        int totalRows = (_filteredIndices.Count + cols - 1) / cols;
        return Pad + totalRows * (CardH + Gap) + Pad;
    }

    protected override void LayoutChildren()
    {
        float ox = Transform.Computed.X;
        float oy = Transform.Computed.Y;
        float w = Math.Max(1f, Transform.Computed.Width);
        float h = Math.Max(1f, Transform.Computed.Height);

        _headerBar.Transform.SetAbsoluteFrame(ox, oy, w, TopBarH);
        _headerDivider.Transform.SetAbsoluteFrame(ox, oy + TopBarH - 1f, w, 1f);

        float scrollerH = Math.Max(1f, h - TopBarH);
        _scroller.Transform.SetAbsoluteFrame(ox, oy + TopBarH, w, scrollerH);
        _scroller.SetContentSize(w, Math.Max(ContentHeight(w), scrollerH));
        base.LayoutChildren();

        UpdateVirtualCards(force: false);
    }

    private void UpdateVirtualCards(bool force = false)
    {
        if (_filteredIndices.Count == 0)
        {
            foreach (var card in _activeCards.Values)
            {
                card.Visible = false;
                _cardPool.Push(card);
            }
            _activeCards.Clear();
            _lastFirstRow = -1;
            _lastLastRow = -1;
            return;
        }

        float ox = _scroller.Transform.Computed.X;
        float oy = _scroller.Transform.Computed.Y;
        float w = Math.Max(1f, _scroller.Transform.Computed.Width > 0 ? _scroller.Transform.Computed.Width : _scroller.Transform.Width);
        float h = Math.Max(1f, _scroller.Transform.Computed.Height > 0 ? _scroller.Transform.Computed.Height : _scroller.Transform.Height);

        float cardH = CardH;
        float startY = Pad;
        float availW = Math.Max(1f, w - Pad * 2f);
        int cols = Columns(w);
        float actualCellW = (availW - (cols - 1) * Gap) / cols;

        int totalCount = _filteredIndices.Count;
        int totalRows = (totalCount + cols - 1) / cols;
        float rowStride = cardH + Gap;

        float scrollY = _scroller.ScrollY;
        int firstRow = Math.Max(0, (int)Math.Floor((scrollY - startY - cardH) / rowStride) - 1);
        int lastRow = Math.Min(totalRows - 1, (int)Math.Ceiling((scrollY + h - startY) / rowStride) + 1);

        bool boundsChanged = ox != _lastOx || oy != _lastOy || w != _lastW || cols != _lastCols;
        if (!force && !boundsChanged && firstRow == _lastFirstRow && lastRow == _lastLastRow)
        {
            return;
        }

        _lastFirstRow = firstRow;
        _lastLastRow = lastRow;
        _lastOx = ox;
        _lastOy = oy;
        _lastW = w;
        _lastCols = cols;

        int minItem = firstRow * cols;
        int maxItem = Math.Min(totalCount - 1, (lastRow + 1) * cols - 1);

        // Recycle cards no longer in range
        _recycledKeys.Clear();
        foreach (var kvp in _activeCards)
        {
            int itemIdx = kvp.Key;
            if (itemIdx < minItem || itemIdx > maxItem)
            {
                var card = kvp.Value;
                card.Visible = false;
                _cardPool.Push(card);
                _recycledKeys.Add(itemIdx);
            }
        }
        for (int i = 0; i < _recycledKeys.Count; i++)
            _activeCards.Remove(_recycledKeys[i]);

        // Position and rebind
        for (int itemIdx = minItem; itemIdx <= maxItem; itemIdx++)
        {
            int docIdx = _filteredIndices[itemIdx];
            int col = itemIdx % cols;
            int row = itemIdx / cols;

            float cx = ox + Pad + col * (actualCellW + Gap);
            float cy = oy + startY + row * rowStride;

            if (_activeCards.TryGetValue(itemIdx, out var card))
            {
                if (force || card.Index != docIdx)
                {
                    card.Rebind(docIdx, _docs[docIdx], docIdx == _activeIndex, _selectedIndices.Contains(docIdx));
                    card.Transform.SetAbsoluteFrame(cx, cy, actualCellW, cardH);
                }
                else if (boundsChanged)
                {
                    card.Transform.SetAbsoluteFrame(cx, cy, actualCellW, cardH);
                }
                card.Visible = true;
            }
            else
            {
                if (_cardPool.Count > 0)
                {
                    card = _cardPool.Pop();
                }
                else
                {
                    card = ThumbTile.Create(() => new GalleryCard(this));
                    _scroller.AddChild(card);
                }

                card.Rebind(docIdx, _docs[docIdx], docIdx == _activeIndex, _selectedIndices.Contains(docIdx));
                card.Transform.SetAbsoluteFrame(cx, cy, actualCellW, cardH);
                card.Visible = true;
                _activeCards[itemIdx] = card;
            }
        }
    }

    private void OnCardClickedWithModifiers(int index, bool ctrl, bool shift)
    {
        if (SelectionRequested != null)
        {
            SelectionRequested.Invoke(index, ctrl, shift);
        }
        else
        {
            OnCardClicked(index);
        }
    }

    private void OnCardClicked(int index)
    {
        SetSelected(index);
        PhotoSelected?.Invoke(index);
    }

    private void OnCardDoubleClicked(int index)
    {
        SetSelected(index);
        PhotoDoubleClicked?.Invoke(index);
    }

    private void OnCardReadyToggled(int index, bool ready)
    {
        ReadyToggled?.Invoke(index, ready);
        OnMarksChanged(PhotoFilter.Ready);
    }

    private void OnCardFavoriteToggled(int index, bool favorite)
    {
        FavoriteToggled?.Invoke(index, favorite);
        OnMarksChanged(PhotoFilter.Favorite);
    }

    public override void Dispose()
    {
        base.Dispose();
        _scroller.Dispose();
        foreach (var card in _activeCards.Values)
            card.Dispose();
        _activeCards.Clear();

        while (_cardPool.Count > 0)
            _cardPool.Pop().Dispose();
    }

    private sealed class GalleryScroller : ScrollContainer
    {
        private readonly GalleryView _owner;

        public GalleryScroller(GalleryView owner)
        {
            _owner = owner;
        }

        protected override void OnScrollOffsetChanged()
        {
            base.OnScrollOffsetChanged();
            _owner.UpdateVirtualCards(force: false);
        }

        protected override void LayoutChildren()
        {
            base.LayoutChildren();
            _owner.UpdateVirtualCards(force: true);
        }
    }

    /// <summary>Contact-sheet card: photo well, file name caption, index pill, ready / favourite badges.</summary>
    private sealed class GalleryCard : ThumbTile
    {
        private static readonly TimeSpan DoubleClickThreshold = TimeSpan.FromMilliseconds(300);
        private const float CardPad = 6f;
        private const float CaptionH = 24f;
        private const float Badge = 18f;
        private const float PillH = 18f;

        private static readonly TextPaint CaptionInk = new() { TextSize = 12f };
        private static readonly TextPaint PillInk = new() { TextSize = 11f };
        private static SKTypeface? _medium;
        private static SKTypeface? _semibold;
        private static SKTypeface Medium => _medium ??= Theme.GetTypeface(500);
        private static SKTypeface Semibold => _semibold ??= Theme.GetTypeface(600);

        private readonly GalleryView _owner;
        private DateTime _lastClickTime = DateTime.MinValue;
        private string _pill = "";
        private string _caption = "";
        private float _captionFitW = -1f;
        private string _captionFit = "";

        public GalleryCard(GalleryView owner)
        {
            _owner = owner;
        }

        protected override string NamePrefix => "GalleryCard";

        protected override float CornerRadius => Theme.Radius;

        protected override SKRect PhotoRect => new(CardPad, CardPad, W - CardPad, H - CardPad - CaptionH);

        protected override void OnRebind(PhotoDocument doc)
        {
            if (doc.Look == null)
                WorkspaceStore.HydrateLook(doc);

            _pill = $"#{Index + 1}";
            if (_caption != doc.Name)
            {
                _caption = doc.Name;
                _captionFitW = -1f;
            }
        }

        protected override void OnPrimaryClick(bool ctrl, bool shift)
        {
            var now = DateTime.UtcNow;
            if (now - _lastClickTime < DoubleClickThreshold)
            {
                _lastClickTime = DateTime.MinValue;
                _owner.OnCardDoubleClicked(Index);
                return;
            }
            _lastClickTime = now;
            _owner.OnCardClickedWithModifiers(Index, ctrl, shift);
        }

        protected override void OnContextMenu(float x, float y)
        {
            _owner.OnCardClicked(Index);
            _owner.ContextMenuRequested?.Invoke(Index, x, y);
        }

        protected override void OnReadyToggled(bool ready) => _owner.OnCardReadyToggled(Index, ready);

        protected override void OnFavoriteToggled(bool favorite) => _owner.OnCardFavoriteToggled(Index, favorite);

        protected override void LayoutChildren()
        {
            float ox = Transform.Computed.X;
            float oy = Transform.Computed.Y;
            float h = Math.Max(1f, Transform.Height);
            float photoBottom = h - CardPad - CaptionH;
            ReadyBadge.Transform.SetAbsoluteFrame(ox + CardPad + 6f, oy + CardPad + 6f, Badge, Badge);
            FavoriteBadge.Transform.SetAbsoluteFrame(ox + CardPad + 6f, oy + photoBottom - 6f - Badge, Badge, Badge);
        }

        protected override void PaintOverlay(SKCanvas c)
        {
            var photo = PhotoRect;
            PaintPill(c, photo);

            // File name, ellipsized to the card; fitting is cached per width.
            float maxW = W - CardPad * 2f - 8f;
            CaptionInk.Typeface = Medium;
            if (Math.Abs(maxW - _captionFitW) > 0.5f)
            {
                _captionFitW = maxW;
                _captionFit = Blossom.Utils.Text.Ellipsize(_caption, CaptionInk.Font, maxW);
            }
            CaptionInk.Color = IsActive ? Theme.Accent : IsSelected ? Theme.Text : Theme.TextSecondary;
            CaptionInk.TextAlign = SKTextAlign.Center;
            float baseline = Blossom.Utils.Text.Baseline(CaptionInk.Font, photo.Bottom + CaptionH * 0.5f);
            c.DrawText(_captionFit, W * 0.5f, baseline, CaptionInk);
        }

        /// <summary>Index pill in the photo's top-right corner; carries the selection state on busy photos.</summary>
        private void PaintPill(SKCanvas c, SKRect photo)
        {
            PillInk.Typeface = Semibold;
            float pillW = PillInk.MeasureText(_pill) + 12f;
            var pill = SKRect.Create(photo.Right - 6f - pillW, photo.Top + 6f, pillW, PillH);
            float r = PillH * 0.5f;

            if (IsActive)
            {
                ThumbInk.FillRound(c, pill, r, Theme.Accent);
                PillInk.Color = Theme.ButtonTextOnAccent;
            }
            else
            {
                ThumbInk.FillRound(c, pill, r, Theme.Scrim);
                if (IsSelected)
                    ThumbInk.StrokeRound(c, pill, r, Theme.Accent.WithAlpha(170), 1f);
                PillInk.Color = IsSelected ? Theme.Accent : Theme.TextSecondary;
            }

            PillInk.TextAlign = SKTextAlign.Center;
            c.DrawText(_pill, pill.MidX, Blossom.Utils.Text.Baseline(PillInk.Font, pill.MidY), PillInk);
        }
    }
}
