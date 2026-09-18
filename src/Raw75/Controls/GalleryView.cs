using System;
using System.Collections.Generic;
using Blossom.Core;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Raw75.Develop;
using Silk.NET.Input;
using SkiaSharp;

namespace Raw75.Controls;

/// <summary>
/// Full-canvas lightbox / gallery grid view:
/// Displays a multi-column contact sheet with ready/star marks, filter tabs,
/// and double-click to open in develop view. Virtualized for fast rendering.
/// </summary>
public enum GalleryThumbSize
{
    Small,
    Medium,
    Large
}

public sealed class GalleryView : ScrollContainer
{
    private const float TopBarH = 46f;
    private const float Gap = 12f;
    private const float Pad = 16f;

    private readonly VisualElement _headerBar;
    private readonly IconButton _btnFilterAll;
    private readonly IconButton _btnFilterReady;
    private readonly IconButton _btnFilterStar;
    private readonly IconButton _btnMarkAll;
    private readonly IconButton _btnClearAll;
    private readonly IconButton _btnSizeS;
    private readonly IconButton _btnSizeM;
    private readonly IconButton _btnSizeL;
    private readonly VisualElement _countLabel;

    private GalleryThumbSize _thumbSize = GalleryThumbSize.Small;

    private float CardW => _thumbSize switch
    {
        GalleryThumbSize.Medium => 336f,
        GalleryThumbSize.Large => 456f,
        _ => 240f
    };

    private float CardH => _thumbSize switch
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
    private PhotoFilter _filter = PhotoFilter.All;

    private int _lastFirstRow = -1;
    private int _lastLastRow = -1;
    private float _lastOx = float.NaN;
    private float _lastOy = float.NaN;
    private float _lastW = float.NaN;
    private int _lastCols = -1;

    public event Action<int>? PhotoSelected;
    public event Action<int>? PhotoDoubleClicked;
    public event Action<int, bool>? ReadyToggled;
    public event Action<int, bool>? FavoriteToggled;
    public event Action? BatchReadyChanged;
    public event Action<PhotoFilter>? FilterChanged;
    public event Action<int, float, float>? ContextMenuRequested;

    public PhotoFilter ActiveFilter => _filter;

    public GalleryView()
    {
        Name = "GalleryView";
        OverflowX = OverflowMode.Clip;
        OverflowY = OverflowMode.Scroll;
        ScrollbarVisibilityX = ScrollbarVisibility.Hidden;
        ScrollbarVisibilityY = ScrollbarVisibility.Auto;
        Style = new ElementStyle
        {
            BackColor = Theme.Canvas,
            Border = new BorderStyle { Width = 0 }
        };

        _headerBar = new VisualElement
        {
            Name = "Gallery_HeaderBar",
            Style = new ElementStyle
            {
                BackColor = Theme.Window,
                Border = new BorderStyle
                {
                    Width = 1,
                    Color = Theme.Hairline,
                    Roundness = 0
                }
            }
        };

        _btnFilterAll = new IconButton("All (0)");
        _btnFilterAll.Toggled = true;
        _btnFilterAll.Clicked += () => SetFilter(PhotoFilter.All);

        _btnFilterReady = new IconButton("Ready (0)", "check");
        _btnFilterReady.Clicked += () => ToggleFilterFlag(PhotoFilter.Ready);

        _btnFilterStar = new IconButton("⭐ (0)", "star");
        _btnFilterStar.Clicked += () => ToggleFilterFlag(PhotoFilter.Favorite);

        _btnMarkAll = new IconButton("Mark All Ready", "check");
        _btnMarkAll.Clicked += MarkAllReady;

        _btnClearAll = new IconButton("Clear Ready", "cross");
        _btnClearAll.Clicked += ClearAllReady;

        _btnSizeS = IconButton.Icon("gallery_size_s");
        _btnSizeS.Toggled = true;
        _btnSizeS.Clicked += () => SetThumbSize(GalleryThumbSize.Small);

        _btnSizeM = IconButton.Icon("gallery_size_m");
        _btnSizeM.Clicked += () => SetThumbSize(GalleryThumbSize.Medium);

        _btnSizeL = IconButton.Icon("gallery_size_l");
        _btnSizeL.Clicked += () => SetThumbSize(GalleryThumbSize.Large);

        _countLabel = new VisualElement
        {
            Name = "Gallery_CountLabel",
            Text = "0 photos",
            IsClickthrough = true,
            Style = new ElementStyle
            {
                BackColor = SKColors.Transparent,
                Text = new TextStyle
                {
                    Color = Theme.TextDim,
                    Size = 12,
                    Weight = 500,
                    Alignment = TextAlign.Right,
                    Padding = 0
                }
            }
        };

        _headerBar.AddChild(_btnFilterAll);
        _headerBar.AddChild(_btnFilterReady);
        _headerBar.AddChild(_btnFilterStar);
        _headerBar.AddChild(_btnMarkAll);
        _headerBar.AddChild(_btnClearAll);
        _headerBar.AddChild(_btnSizeS);
        _headerBar.AddChild(_btnSizeM);
        _headerBar.AddChild(_btnSizeL);
        _headerBar.AddChild(_countLabel);
        AddChild(_headerBar);
    }

    public void Bind(IReadOnlyList<PhotoDocument> docs, int activeIndex)
    {
        _docs = docs ?? Array.Empty<PhotoDocument>();
        _activeIndex = activeIndex;
        _lastFirstRow = -1;
        _lastLastRow = -1;

        UpdateFilteredIndices();
        UpdateFilterCounts();

        float w = Math.Max(1f, Transform.Computed.Width > 0 ? Transform.Computed.Width : Transform.Width);
        float h = Math.Max(1f, Transform.Computed.Height > 0 ? Transform.Computed.Height : Transform.Height);
        float startY = TopBarH + Pad;
        float availW = Math.Max(1f, w - Pad * 2f);
        int cols = Math.Max(1, (int)((availW + Gap) / (CardW + Gap)));
        int totalRows = (_filteredIndices.Count + cols - 1) / cols;
        float totalH = startY + totalRows * (CardH + Gap) + Pad;

        SetContentSize(w, Math.Max(totalH, h));
        UpdateVirtualCards(force: true);
        InvalidatePaint();
    }

    public void SetSelected(int index)
    {
        _activeIndex = index;
        foreach (var card in _activeCards.Values)
            card.SetActive(card.Index == index);
        InvalidatePaint();
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
        UpdateFilterCounts();

        if (_filter != PhotoFilter.All)
        {
            UpdateFilteredIndices();
            InvalidateLayout();
        }
        InvalidatePaint();
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
        UpdateFilterCounts();

        if (_filter != PhotoFilter.All)
        {
            UpdateFilteredIndices();
            InvalidateLayout();
        }
        InvalidatePaint();
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
        if (_filter == filter) return;
        _filter = filter;
        ApplyFilterChrome();
        UpdateFilteredIndices();
        ScrollY = 0f;
        InvalidateLayout();
        InvalidatePaint();
        FilterChanged?.Invoke(_filter);
    }

    public void SetThumbSize(GalleryThumbSize size)
    {
        if (_thumbSize == size)
        {
            ApplySizeChrome();
            return;
        }

        _thumbSize = size;
        ApplySizeChrome();
        _lastFirstRow = -1;
        _lastLastRow = -1;
        _lastCols = -1;
        ScrollY = 0f;
        InvalidateLayout();
        InvalidatePaint();
    }

    private void ApplySizeChrome()
    {
        _btnSizeS.Toggled = _thumbSize == GalleryThumbSize.Small;
        _btnSizeM.Toggled = _thumbSize == GalleryThumbSize.Medium;
        _btnSizeL.Toggled = _thumbSize == GalleryThumbSize.Large;
    }

    private void ToggleFilterFlag(PhotoFilter flag)
    {
        SetFilter(_filter ^ flag);
    }

    private void ApplyFilterChrome()
    {
        _btnFilterAll.Toggled = _filter == PhotoFilter.All;
        _btnFilterReady.Toggled = (_filter & PhotoFilter.Ready) != 0;
        _btnFilterStar.Toggled = (_filter & PhotoFilter.Favorite) != 0;
    }

    private void UpdateFilterCounts()
    {
        int total = _docs.Count;
        int ready = 0;
        int starred = 0;
        for (int i = 0; i < total; i++)
        {
            if (_docs[i].IsReady) ready++;
            if (_docs[i].IsFavorite) starred++;
        }

        _btnFilterAll.Caption = $"All ({total})";
        _btnFilterReady.Caption = $"Ready ({ready})";
        _btnFilterStar.Caption = $"⭐ ({starred})";
        _countLabel.Text = $"{total} photos · {ready} ready · {starred} starred";
    }

    private void UpdateFilteredIndices()
    {
        _filteredIndices.Clear();
        for (int i = 0; i < _docs.Count; i++)
        {
            if (PhotoFilterMatch.Matches(_docs[i], _filter))
                _filteredIndices.Add(i);
        }
    }

    private void MarkAllReady()
    {
        for (int i = 0; i < _docs.Count; i++)
            _docs[i].IsReady = true;

        foreach (var card in _activeCards.Values)
            card.SetReady(true);

        UpdateFilterCounts();
        if (_filter != PhotoFilter.All)
        {
            UpdateFilteredIndices();
            InvalidateLayout();
        }
        InvalidatePaint();
        BatchReadyChanged?.Invoke();
    }

    private void ClearAllReady()
    {
        for (int i = 0; i < _docs.Count; i++)
            _docs[i].IsReady = false;

        foreach (var card in _activeCards.Values)
            card.SetReady(false);

        UpdateFilterCounts();
        if (_filter != PhotoFilter.All)
        {
            UpdateFilteredIndices();
            InvalidateLayout();
        }
        InvalidatePaint();
        BatchReadyChanged?.Invoke();
    }

    protected override void OnScrollOffsetChanged()
    {
        base.OnScrollOffsetChanged();
        UpdateVirtualCards(force: false);
    }

    protected override void LayoutChildren()
    {
        float ox = Transform.Computed.X;
        float oy = Transform.Computed.Y;
        float w = Math.Max(1f, Transform.Computed.Width);
        float h = Math.Max(1f, Transform.Computed.Height);

        // Header bar layout
        _headerBar.Transform.SetAbsoluteFrame(ox, oy, w, TopBarH);
        float hx = ox + Pad;
        float hy = oy + 7f;
        float btnH = 32f;

        _btnFilterAll.Transform.SetAbsoluteFrame(hx, hy, 90f, btnH); hx += 94f;
        _btnFilterReady.Transform.SetAbsoluteFrame(hx, hy, 108f, btnH); hx += 112f;
        _btnFilterStar.Transform.SetAbsoluteFrame(hx, hy, 110f, btnH); hx += 114f;

        _btnMarkAll.Transform.SetAbsoluteFrame(hx, hy, 144f, btnH); hx += 148f;
        _btnClearAll.Transform.SetAbsoluteFrame(hx, hy, 118f, btnH);

        float sizeBtn = 32f;
        float sizeGap = 4f;
        float sizeClusterW = sizeBtn * 3f + sizeGap * 2f;
        float sizeX = ox + w - Pad - sizeClusterW;
        _btnSizeS.Transform.SetAbsoluteFrame(sizeX, hy, sizeBtn, btnH); sizeX += sizeBtn + sizeGap;
        _btnSizeM.Transform.SetAbsoluteFrame(sizeX, hy, sizeBtn, btnH); sizeX += sizeBtn + sizeGap;
        _btnSizeL.Transform.SetAbsoluteFrame(sizeX, hy, sizeBtn, btnH);

        _countLabel.Transform.SetAbsoluteFrame(ox + w - Pad - sizeClusterW - 12f - 200f, hy + 6f, 200f, 20f);

        // Grid layout calculations
        float startY = TopBarH + Pad;
        float availW = Math.Max(1f, w - Pad * 2f);
        int cols = Math.Max(1, (int)((availW + Gap) / (CardW + Gap)));
        int totalRows = (_filteredIndices.Count + cols - 1) / cols;
        float totalH = startY + totalRows * (CardH + Gap) + Pad;

        SetContentSize(w, Math.Max(totalH, h));
        base.LayoutChildren();

        UpdateVirtualCards(force: true);
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

        float ox = Transform.Computed.X;
        float oy = Transform.Computed.Y;
        float w = Math.Max(1f, Transform.Computed.Width > 0 ? Transform.Computed.Width : Transform.Width);
        float h = Math.Max(1f, Transform.Computed.Height > 0 ? Transform.Computed.Height : Transform.Height);

        float startY = TopBarH + Pad;
        float availW = Math.Max(1f, w - Pad * 2f);
        int cols = Math.Max(1, (int)((availW + Gap) / (CardW + Gap)));
        float actualCellW = (availW - (cols - 1) * Gap) / cols;

        int totalCount = _filteredIndices.Count;
        int totalRows = (totalCount + cols - 1) / cols;
        float rowStride = CardH + Gap;

        float scrollY = ScrollY;
        int firstRow = Math.Max(0, (int)Math.Floor((scrollY - startY - CardH) / rowStride) - 1);
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
            float cy = oy + startY + row * (CardH + Gap);

            if (_activeCards.TryGetValue(itemIdx, out var card))
            {
                if (force)
                {
                    card.Rebind(docIdx, _docs[docIdx], docIdx == _activeIndex);
                    card.Transform.SetAbsoluteFrame(cx, cy, actualCellW, CardH);
                }
                else if (boundsChanged)
                {
                    card.Transform.SetAbsoluteFrame(cx, cy, actualCellW, CardH);
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
                    card = new GalleryCard(this);
                    AddChild(card);
                }

                card.Rebind(docIdx, _docs[docIdx], docIdx == _activeIndex);
                card.Transform.SetAbsoluteFrame(cx, cy, actualCellW, CardH);
                card.Visible = true;
                _activeCards[itemIdx] = card;
            }
        }
    }

    internal void OnCardClicked(int index)
    {
        SetSelected(index);
        PhotoSelected?.Invoke(index);
    }

    internal void OnCardDoubleClicked(int index)
    {
        SetSelected(index);
        PhotoDoubleClicked?.Invoke(index);
    }

    internal void OnCardReadyToggled(int index, bool ready)
    {
        ReadyToggled?.Invoke(index, ready);
        UpdateFilterCounts();
        if ((_filter & PhotoFilter.Ready) != 0)
        {
            UpdateFilteredIndices();
            InvalidateLayout();
        }
    }

    internal void OnCardFavoriteToggled(int index, bool favorite)
    {
        FavoriteToggled?.Invoke(index, favorite);
        UpdateFilterCounts();
        if ((_filter & PhotoFilter.Favorite) != 0)
        {
            UpdateFilteredIndices();
            InvalidateLayout();
        }
    }

    internal void OnCardContextMenu(int index, float x, float y)
    {
        ContextMenuRequested?.Invoke(index, x, y);
    }

    public override void Dispose()
    {
        base.Dispose();
        foreach (var card in _activeCards.Values)
            card.Dispose();
        _activeCards.Clear();

        while (_cardPool.Count > 0)
            _cardPool.Pop().Dispose();
    }

    private sealed class GalleryCard : VisualElement
    {
        private static readonly TimeSpan DoubleClickThreshold = TimeSpan.FromMilliseconds(300);

        private readonly GalleryView _owner;
        private int _index;
        private PhotoDocument? _doc;
        private bool _active;
        private bool _isReady;
        private bool _isFavorite;
        private DateTime _lastClickTime = DateTime.MinValue;

        private readonly GalleryThumbWell _thumb;
        private readonly RichBox _caption;
        private readonly VisualElement _readyBtn;
        private readonly VisualElement _starBtn;
        private readonly VisualElement _indexBadge;

        public int Index => _index;
        public PhotoDocument? Doc => _doc;

        public GalleryCard(GalleryView owner)
        {
            _owner = owner;
            Cursor = StandardCursor.Hand;
            Style = new ElementStyle
            {
                BackColor = Theme.Section,
                Border = new BorderStyle
                {
                    Width = 1f,
                    Color = Theme.Hairline,
                    Roundness = Theme.Radius
                },
                Shadow = new ShadowStyle(0, 1.5f, 3, 3, new SKColor(0, 0, 0, 70))
            };

            _thumb = new GalleryThumbWell(null, () => GetValidImage())
            {
                Name = "GalleryCard_Thumb",
                IsClickthrough = true
            };

            _caption = new RichBox
            {
                Name = "GalleryCard_Caption",
                Text = "",
                IsClickthrough = true,
                Overflow = OverflowMode.Clip,
                Style = new ElementStyle
                {
                    BackColor = SKColors.Transparent,
                    Text = new TextStyle
                    {
                        Color = Theme.Text,
                        Size = 12,
                        Weight = 500,
                        Alignment = TextAlign.Center,
                        Padding = 4,
                        Overflow = TextOverflow.Ellipsis,
                        MaxLines = 1
                    }
                }
            };

            _readyBtn = new VisualElement
            {
                Name = "GalleryCard_Ready",
                Cursor = StandardCursor.Hand,
                BackgroundImageScale = ImageScaleMode.Contain,
                BackgroundImageTintBlendMode = SKBlendMode.SrcIn,
                BackgroundImageTintColor = SKColors.White,
                Style = new ElementStyle
                {
                    BackColor = new SKColor(0, 0, 0, 150),
                    Border = new BorderStyle
                    {
                        Width = 1,
                        Color = Theme.HairlineSubtle,
                        Roundness = 11f
                    }
                }
            };
            _readyBtn.Events.OnClick += (_, args) =>
            {
                if (args.Button != (int)MouseButton.Left) return;
                if (_doc == null) return;
                _isReady = !_isReady;
                _doc.IsReady = _isReady;
                UpdateReadyStyle();
                _owner.OnCardReadyToggled(_index, _isReady);
                args.Handled = true;
            };

            _starBtn = new VisualElement
            {
                Name = "GalleryCard_Star",
                Cursor = StandardCursor.Hand,
                IsClickthrough = true,
                ZIndex = 5,
                BackgroundImageScale = ImageScaleMode.Contain,
                BackgroundImageTintBlendMode = SKBlendMode.SrcIn,
                BackgroundImageTintColor = Theme.Favorite,
                Padding = new Thickness(0f),
                Style = new ElementStyle
                {
                    BackColor = SKColors.Transparent,
                    Border = new BorderStyle { Width = 0, Color = SKColors.Transparent, Roundness = 0 }
                }
            };
            _starBtn.Events.OnClick += (_, args) =>
            {
                if (args.Button != (int)MouseButton.Left) return;
                if (_doc == null) return;
                _isFavorite = !_isFavorite;
                _doc.IsFavorite = _isFavorite;
                UpdateStarStyle();
                _owner.OnCardFavoriteToggled(_index, _isFavorite);
                args.Handled = true;
            };

            _indexBadge = new VisualElement
            {
                Name = "GalleryCard_Badge",
                Text = "",
                IsClickthrough = true,
                Style = new ElementStyle
                {
                    BackColor = new SKColor(0, 0, 0, 150),
                    Border = new BorderStyle { Width = 1, Color = Theme.HairlineSubtle, Roundness = 4 },
                    Text = new TextStyle
                    {
                        Color = Theme.TextDim,
                        Size = 11,
                        Weight = 500,
                        Alignment = TextAlign.Center,
                        Padding = 0
                    }
                }
            };

            AddChild(_thumb);
            AddChild(_caption);
            AddChild(_readyBtn);
            AddChild(_starBtn);
            AddChild(_indexBadge);

            Events.OnMouseEnter += _ =>
            {
                if (!_active)
                {
                    Style.BackColor = Theme.Hover;
                    Style.Border.Color = Theme.HairlineStrong;
                    InvalidatePaint();
                }
            };
            Events.OnMouseLeave += _ =>
            {
                if (!_active)
                {
                    Style.BackColor = Theme.Section;
                    Style.Border.Color = CardBorderColor();
                    InvalidatePaint();
                }
            };

            Events.OnMouseUp += (_, args) =>
            {
                if (args.Button != (int)MouseButton.Right) return;
                _owner.OnCardClicked(_index);
                _owner.OnCardContextMenu(_index, args.Global.X, args.Global.Y);
                args.Handled = true;
            };
            Events.OnClick += (_, args) =>
            {
                if (args.Button != (int)MouseButton.Left) return;
                var now = DateTime.UtcNow;
                if (now - _lastClickTime < DoubleClickThreshold)
                {
                    _lastClickTime = DateTime.MinValue;
                    _owner.OnCardDoubleClicked(_index);
                }
                else
                {
                    _lastClickTime = now;
                    _owner.OnCardClicked(_index);
                }
                args.Handled = true;
            };
        }

        public void Rebind(int index, PhotoDocument doc, bool active)
        {
            _index = index;
            _doc = doc;
            _active = active;
            _isReady = doc.IsReady;
            _isFavorite = doc.IsFavorite;
            Name = $"GalleryCard_{index}";

            Style.BackColor = active ? Theme.Selected : Theme.Section;
            Style.Border.Width = active ? 2f : 1f;
            Style.Border.Color = active ? Theme.Accent : CardBorderColor();
            Style.Shadow = active
                ? new ShadowStyle(0, 3f, 6, 6, new SKColor(255, 153, 51, 90))
                : new ShadowStyle(0, 1.5f, 3, 3, new SKColor(0, 0, 0, 70));

            _caption.Name = $"{Name}_Caption";
            _caption.Text = doc.Name;
            _caption.Style.Text.Color = active ? Theme.Accent : Theme.Text;
            _caption.InvalidatePaint();

            _thumb.Name = $"{Name}_Thumb";
            _thumb.SetImage(GetValidImage());

            _indexBadge.Name = $"{Name}_Badge";
            _indexBadge.Text = $"#{index + 1}";
            _indexBadge.InvalidatePaint();

            _readyBtn.Name = $"{Name}_Ready";
            UpdateReadyStyle();
            _starBtn.Name = $"{Name}_Star";
            UpdateStarStyle();

            InvalidateLayout();
            InvalidatePaint();
        }

        private SKImage? GetValidImage()
        {
            if (_doc == null) return null;
            if (_doc.Look != null && _doc.Look.Handle != IntPtr.Zero) return _doc.Look;
            if (_doc.Preview != null && _doc.Preview.Handle != IntPtr.Zero) return _doc.Preview;
            if (_doc.Thumb != null && _doc.Thumb.Handle != IntPtr.Zero) return _doc.Thumb;
            return null;
        }

        public void SetActive(bool active)
        {
            _active = active;
            Style.BackColor = active ? Theme.Selected : Theme.Section;
            Style.Border.Width = active ? 2f : 1f;
            Style.Border.Color = active ? Theme.Accent : CardBorderColor();
            Style.Shadow = active
                ? new ShadowStyle(0, 3f, 6, 6, new SKColor(255, 153, 51, 90))
                : new ShadowStyle(0, 1.5f, 3, 3, new SKColor(0, 0, 0, 70));
            _caption.Style.Text.Color = active ? Theme.Accent : Theme.Text;
            InvalidatePaint();
        }

        public void SetReady(bool ready)
        {
            _isReady = ready;
            UpdateReadyStyle();
        }

        public void SetFavorite(bool favorite)
        {
            _isFavorite = favorite;
            UpdateStarStyle();
        }

        public void RefreshThumb()
        {
            _thumb.SetImage(GetValidImage());
        }

        private SKColor CardBorderColor()
        {
            if (_isReady) return Theme.SuccessSoft;
            if (_isFavorite) return Theme.FavoriteSoft;
            return Theme.Hairline;
        }

        private void UpdateReadyStyle()
        {
            _readyBtn.BackgroundSvg = _isReady ? IconStore.LoadSvg("check") : null;
            _readyBtn.Style.BackColor = _isReady ? Theme.Success : new SKColor(0, 0, 0, 150);
            _readyBtn.Style.Border.Color = _isReady ? Theme.Success : Theme.HairlineSubtle;
            _readyBtn.Style.Border.Roundness = 11f;
            _readyBtn.InvalidatePaint();
            if (!_active)
            {
                Style.Border.Color = CardBorderColor();
                InvalidatePaint();
            }
        }

        private void UpdateStarStyle()
        {
            _starBtn.BackgroundSvg = _isFavorite ? IconStore.LoadSvg("star_filled") : null;
            _starBtn.BackgroundImageTintColor = Theme.Favorite;
            _starBtn.IsClickthrough = !_isFavorite;
            _starBtn.Style.BackColor = SKColors.Transparent;
            _starBtn.Style.Border.Width = 0;
            _starBtn.Style.Border.Color = SKColors.Transparent;
            _starBtn.InvalidatePaint();
            if (!_active)
            {
                Style.Border.Color = CardBorderColor();
                InvalidatePaint();
            }
        }

        protected override void LayoutChildren()
        {
            float ox = Transform.Computed.X;
            float oy = Transform.Computed.Y;
            float w = Math.Max(1f, Transform.Width);
            float h = Math.Max(1f, Transform.Height);

            float pad = 6f;
            float captionH = 24f;
            float thumbH = Math.Max(10f, h - captionH - pad * 2f);

            _thumb.Transform.SetAbsoluteFrame(ox + pad, oy + pad, w - pad * 2f, thumbH);
            _caption.Transform.SetAbsoluteFrame(ox + pad, oy + pad + thumbH, w - pad * 2f, captionH);
            _readyBtn.Transform.SetAbsoluteFrame(ox + pad + 4f, oy + pad + 4f, 22f, 22f);
            _starBtn.Transform.SetAbsoluteFrame(ox + pad + 4f, oy + pad + thumbH - 22f, 20f, 20f);
            _indexBadge.Transform.SetAbsoluteFrame(ox + w - pad - 34f, oy + pad + 4f, 30f, 20f);
        }
    }

    private sealed class GalleryThumbWell : VisualElement
    {
        private static readonly SKPaint SmoothPaint = new()
        {
            FilterQuality = SKFilterQuality.Medium,
            IsAntialias = true
        };

        private readonly Func<SKImage?>? _imageProvider;
        private SKImage? _image;

        public GalleryThumbWell(SKImage? image, Func<SKImage?>? imageProvider = null)
        {
            _image = image;
            _imageProvider = imageProvider;
            Style = new ElementStyle
            {
                BackColor = Theme.Well,
                Border = new BorderStyle
                {
                    Width = 1,
                    Color = Theme.HairlineSubtle,
                    Roundness = Theme.RadiusSm
                }
            };
            Overflow = OverflowMode.Clip;
        }

        public void SetImage(SKImage? img)
        {
            _image = img;
            InvalidatePaint();
        }

        protected override void OnAfterStyleDraw(List<DrawCommand> cmds)
        {
            if (_image == null || _image.Handle == IntPtr.Zero)
            {
                _image = _imageProvider?.Invoke();
            }
            if (_image == null || _image.Handle == IntPtr.Zero) return;
            float w = Transform.Computed.Width;
            float h = Transform.Computed.Height;
            var dest = Contain(w, h, _image.Width, _image.Height);
            cmds.Add(new DrawSkImageCommand(_image, dest, SmoothPaint));
        }

        private static SKRect Contain(float boxW, float boxH, int imgW, int imgH)
        {
            if (imgW <= 0 || imgH <= 0) return new SKRect(0, 0, boxW, boxH);
            float scale = Math.Min(boxW / imgW, boxH / imgH);
            float dw = imgW * scale;
            float dh = imgH * scale;
            float x = (boxW - dw) * 0.5f;
            float y = (boxH - dh) * 0.5f;
            return new SKRect(x, y, x + dw, y + dh);
        }
    }
}
