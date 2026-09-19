using System;
using System.Collections.Generic;
using Blossom.Core;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Raw75.Develop;
using Silk.NET.Input;
using SkiaSharp;

namespace Raw75.Controls;

/// <summary>Horizontal session strip: thumbs, click to select, horizontally scrollable, virtualized.</summary>
public class Filmstrip : VisualElement
{
    public const float FilterRailW = 56f;

    private const float ThumbW = 96f;
    private const float Gap = 6f;

    private readonly VisualElement _filterRail;
    private readonly IconButton _btnFilterAll;
    private readonly IconButton _btnFilterReady;
    private readonly IconButton _btnFilterStar;
    private readonly FilmstripScroller _scroller;

    private readonly Dictionary<int, Cell> _activeCells = new();
    private readonly Stack<Cell> _cellPool = new();
    private readonly List<int> _recycledKeys = new();
    private readonly List<int> _filteredIndices = new();

    private IReadOnlyList<PhotoDocument> _docs = Array.Empty<PhotoDocument>();
    private int _activeIndex = -1;
    private PhotoFilter _filter = PhotoFilter.All;
    private int _lastMinIdx = -1;
    private int _lastMaxIdx = -1;
    private float _lastOriginX = float.NaN;
    private float _lastOriginY = float.NaN;
    private float _lastHeight = float.NaN;

    private bool _isPanning;
    private float _panStartX;
    private float _panStartScroll;

    public event Action<int>? Selected;
    public event Action<int, bool>? ReadyToggled;
    public event Action<int, bool>? FavoriteToggled;
    public event Action<PhotoFilter>? FilterChanged;
    public event Action<int, float, float>? ContextMenuRequested;

    public PhotoFilter ActiveFilter => _filter;

    public float ScrollX
    {
        get => _scroller.ScrollX;
        set => _scroller.ScrollX = value;
    }

    public void SetReady(int index, bool ready)
    {
        if (index >= 0 && index < _docs.Count)
            _docs[index].IsReady = ready;

        foreach (var cell in _activeCells.Values)
        {
            if (cell.Index == index)
            {
                cell.SetReady(ready);
                break;
            }
        }

        if ((_filter & PhotoFilter.Ready) != 0)
            RebuildFiltered(keepScroll: true);
    }

    public void SetFavorite(int index, bool favorite)
    {
        if (index >= 0 && index < _docs.Count)
            _docs[index].IsFavorite = favorite;

        foreach (var cell in _activeCells.Values)
        {
            if (cell.Index == index)
            {
                cell.SetFavorite(favorite);
                break;
            }
        }

        if ((_filter & PhotoFilter.Favorite) != 0)
            RebuildFiltered(keepScroll: true);
    }

    public Filmstrip()
    {
        Name = "Filmstrip";
        Style = new ElementStyle
        {
            BackColor = Theme.Filmstrip,
            Border = new BorderStyle
            {
                Width = 0,
                Color = Theme.Hairline,
                Roundness = 0
            }
        };

        _filterRail = new VisualElement
        {
            Name = "Filmstrip_FilterRail",
            Style = new ElementStyle
            {
                BackColor = Theme.BottomBar,
                Border = new BorderStyle
                {
                    Width = 1,
                    Color = Theme.Hairline,
                    Roundness = 0
                }
            }
        };

        _btnFilterAll = new IconButton("All");
        _btnFilterAll.FontSize = 11f;
        _btnFilterAll.Toggled = true;
        _btnFilterAll.Clicked += () => SetFilter(PhotoFilter.All);

        _btnFilterReady = IconButton.Icon("check");
        _btnFilterReady.Clicked += () => ToggleFilterFlag(PhotoFilter.Ready);

        _btnFilterStar = new IconButton("⭐");
        _btnFilterStar.FontSize = 14f;
        _btnFilterStar.Clicked += () => ToggleFilterFlag(PhotoFilter.Favorite);

        _filterRail.AddChild(_btnFilterAll);
        _filterRail.AddChild(_btnFilterReady);
        _filterRail.AddChild(_btnFilterStar);
        AddChild(_filterRail);

        _scroller = new FilmstripScroller(this)
        {
            Name = "Filmstrip_Scroller",
            OverflowX = OverflowMode.Scroll,
            OverflowY = OverflowMode.Clip,
            ScrollbarVisibilityX = ScrollbarVisibility.Auto,
            ScrollbarVisibilityY = ScrollbarVisibility.Hidden,
            ScrollbarThickness = 8f,
            ScrollbarRadius = 4f,
            ScrollbarPadding = 1.5f,
            ScrollbarThumbColor = new SKColor(160, 160, 160, 140),
            ScrollbarThumbHoverColor = new SKColor(200, 200, 200, 200),
            ScrollbarThumbDragColor = Theme.Accent,
            ScrollbarTrackColor = SKColors.Transparent,
            Padding = new Thickness(8, 6, 8, 8),
            SmoothScroll = true,
            ScrollStepX = (ThumbW + Gap) * 1.5f,
            Style = new ElementStyle
            {
                BackColor = Theme.Filmstrip,
                Border = new BorderStyle { Width = 0 }
            }
        };

        _scroller.Events.OnMouseDown += (_, args) =>
        {
            if (args.Button == (int)MouseButton.Middle)
            {
                _isPanning = true;
                _panStartX = args.Global.X;
                _panStartScroll = _scroller.ScrollX;
                _scroller.CapturePointer();
                args.Handled = true;
            }
        };
        _scroller.Events.OnMouseMove += (_, args) =>
        {
            if (_isPanning)
            {
                float dx = args.Global.X - _panStartX;
                _scroller.ScrollX = _panStartScroll - dx;
                args.Handled = true;
            }
        };
        _scroller.Events.OnMouseUp += (_, args) =>
        {
            if (_isPanning)
            {
                _isPanning = false;
                _scroller.ReleasePointer();
                args.Handled = true;
            }
        };

        AddChild(_scroller);
    }

    public void RefreshTheme()
    {
        Style.BackColor = Theme.Filmstrip;
        if (Style.Border != null)
            Style.Border.Color = Theme.Hairline;
        _scroller.Style.BackColor = Theme.Filmstrip;
        _scroller.ScrollbarThumbDragColor = Theme.Accent;
        if (_filterRail.Style != null)
        {
            _filterRail.Style.BackColor = Theme.BottomBar;
            if (_filterRail.Style.Border != null)
                _filterRail.Style.Border.Color = Theme.Hairline;
        }
        foreach (var cell in _activeCells.Values)
            cell.RefreshTheme();
        foreach (var cell in _cellPool)
            cell.RefreshTheme();
        InvalidatePaint();
    }

    public void Bind(IReadOnlyList<PhotoDocument> docs, int activeIndex)
    {
        _docs = docs ?? Array.Empty<PhotoDocument>();
        _activeIndex = activeIndex;
        _lastMinIdx = -1;
        _lastMaxIdx = -1;

        UpdateFilteredIndices();
        ApplyFilterChrome();
        UpdateContentSize();
        UpdateVirtualCells(force: true);
        EnsureVisible(activeIndex);
        InvalidatePaint();
    }

    public void SetFilter(PhotoFilter filter)
    {
        if (_filter == filter) return;
        _filter = filter;
        ApplyFilterChrome();
        RebuildFiltered(keepScroll: false);
        FilterChanged?.Invoke(_filter);
    }

    public void EnsureVisible(int index)
    {
        int vis = VisualIndexOf(index);
        if (vis < 0) return;
        float cellLeft = _scroller.Padding.Left + vis * (ThumbW + Gap);
        float cellRight = cellLeft + ThumbW;
        float viewW = _scroller.Transform.Computed.Width > 0 ? _scroller.Transform.Computed.Width : _scroller.Transform.Width;
        if (viewW <= 0) return;

        if (cellLeft < _scroller.ScrollX + _scroller.Padding.Left)
        {
            _scroller.AnimateScrollTo(Math.Max(0, cellLeft - _scroller.Padding.Left), 0);
        }
        else if (cellRight > _scroller.ScrollX + viewW - _scroller.Padding.Right)
        {
            _scroller.AnimateScrollTo(Math.Max(0, cellRight - viewW + _scroller.Padding.Right), 0);
        }
    }

    public void RefreshThumbs()
    {
        foreach (var cell in _activeCells.Values)
            cell.RefreshThumb();
        InvalidatePaint();
    }

    public void RefreshThumb(PhotoDocument doc)
    {
        if (doc == null) return;
        foreach (var cell in _activeCells.Values)
        {
            if (ReferenceEquals(cell.Doc, doc))
            {
                cell.RefreshThumb();
                break;
            }
        }
        InvalidatePaint();
    }

    public override SKSize GetPreferredSize(float maxWidth, float maxHeight)
    {
        float w = maxWidth > 0 ? maxWidth : (Transform.Width > 0 ? Transform.Width : 400f);
        float h = Theme.FilmH;
        if (maxHeight > 0) h = Math.Min(h, maxHeight);
        return new SKSize(w, h);
    }

    protected override void LayoutChildren()
    {
        float ox = Transform.Computed.X;
        float oy = Transform.Computed.Y;
        float w = Transform.Computed.Width;
        float h = Transform.Computed.Height;

        _filterRail.Transform.SetAbsoluteFrame(ox, oy, FilterRailW, h);

        float btnW = 44f;
        float btnH = 28f;
        float gap = 4f;
        float stackH = btnH * 3f + gap * 2f;
        float startY = oy + Math.Max(6f, (h - stackH) * 0.5f);
        float bx = ox + (FilterRailW - btnW) * 0.5f;

        _btnFilterAll.Transform.SetAbsoluteFrame(bx, startY, btnW, btnH);
        _btnFilterReady.Transform.SetAbsoluteFrame(bx, startY + btnH + gap, btnW, btnH);
        _btnFilterStar.Transform.SetAbsoluteFrame(bx, startY + (btnH + gap) * 2f, btnW, btnH);

        float scrollerW = Math.Max(0f, w - FilterRailW);
        _scroller.Transform.SetAbsoluteFrame(ox + FilterRailW, oy, scrollerW, h);

        UpdateContentSize();
        base.LayoutChildren();
        UpdateVirtualCells(force: true);
    }

    private void ToggleFilterFlag(PhotoFilter flag)
    {
        PhotoFilter next = _filter ^ flag;
        SetFilter(next);
    }

    private void ApplyFilterChrome()
    {
        _btnFilterAll.Toggled = _filter == PhotoFilter.All;
        _btnFilterReady.Toggled = (_filter & PhotoFilter.Ready) != 0;
        _btnFilterStar.Toggled = (_filter & PhotoFilter.Favorite) != 0;
    }

    private void RebuildFiltered(bool keepScroll)
    {
        float oldScroll = ScrollX;
        UpdateFilteredIndices();
        _lastMinIdx = -1;
        _lastMaxIdx = -1;
        UpdateContentSize();
        if (!keepScroll)
            ScrollX = 0f;
        else
            ScrollX = oldScroll;
        UpdateVirtualCells(force: true);
        EnsureVisible(_activeIndex);
        InvalidatePaint();
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

    private int VisualIndexOf(int docIndex)
    {
        for (int i = 0; i < _filteredIndices.Count; i++)
        {
            if (_filteredIndices[i] == docIndex)
                return i;
        }
        return -1;
    }

    private void UpdateContentSize()
    {
        float scrollerW = Math.Max(1f, _scroller.Transform.Computed.Width > 0 ? _scroller.Transform.Computed.Width : (_scroller.Transform.Width > 0 ? _scroller.Transform.Width : Math.Max(1f, Transform.Width - FilterRailW)));
        float h = Math.Max(1f, Transform.Height > 0 ? Transform.Height : Theme.FilmH);
        float stride = ThumbW + Gap;
        int n = _filteredIndices.Count;
        float totalW = n > 0
            ? (_scroller.Padding.Left + n * stride - Gap + _scroller.Padding.Right)
            : scrollerW;
        _scroller.SetContentSize(Math.Max(totalW, scrollerW), h);
    }

    private void UpdateVirtualCells(bool force = false)
    {
        float originX = _scroller.Transform.Computed.X;
        float originY = _scroller.Transform.Computed.Y;
        float w = Math.Max(1f, _scroller.Transform.Computed.Width > 0 ? _scroller.Transform.Computed.Width : _scroller.Transform.Width);
        float h = Math.Max(1f, _scroller.Transform.Computed.Height > 0 ? _scroller.Transform.Computed.Height : (_scroller.Transform.Height > 0 ? _scroller.Transform.Height : Theme.FilmH));
        float cellH = Math.Max(1f, h - _scroller.Padding.Vertical);
        float stride = ThumbW + Gap;
        float scrollX = _scroller.ScrollX;

        if (_filteredIndices.Count == 0)
        {
            foreach (var cell in _activeCells.Values)
            {
                cell.Visible = false;
                _cellPool.Push(cell);
            }
            _activeCells.Clear();
            _lastMinIdx = -1;
            _lastMaxIdx = -1;
            return;
        }

        float thumbsLeft = _scroller.Padding.Left;
        int minIdx = (int)Math.Floor((scrollX - thumbsLeft - ThumbW) / stride) - 2;
        int maxIdx = (int)Math.Ceiling((scrollX + w - thumbsLeft) / stride) + 2;

        minIdx = Math.Clamp(minIdx, 0, _filteredIndices.Count - 1);
        maxIdx = Math.Clamp(maxIdx, 0, _filteredIndices.Count - 1);

        bool originChanged = originX != _lastOriginX || originY != _lastOriginY || cellH != _lastHeight;
        if (!force && !originChanged && minIdx == _lastMinIdx && maxIdx == _lastMaxIdx)
        {
            return;
        }

        _lastMinIdx = minIdx;
        _lastMaxIdx = maxIdx;
        _lastOriginX = originX;
        _lastOriginY = originY;
        _lastHeight = cellH;

        _recycledKeys.Clear();
        foreach (var kvp in _activeCells)
        {
            int idx = kvp.Key;
            if (idx < minIdx || idx > maxIdx)
            {
                var cell = kvp.Value;
                cell.Visible = false;
                _cellPool.Push(cell);
                _recycledKeys.Add(idx);
            }
        }
        for (int i = 0; i < _recycledKeys.Count; i++)
            _activeCells.Remove(_recycledKeys[i]);

        for (int vis = minIdx; vis <= maxIdx; vis++)
        {
            int docIdx = _filteredIndices[vis];
            float cellAbsX = originX + _scroller.Padding.Left + vis * stride;
            float cellAbsY = originY + _scroller.Padding.Top;

            if (_activeCells.TryGetValue(vis, out var cell))
            {
                if (force)
                {
                    cell.Rebind(docIdx, _docs[docIdx], docIdx == _activeIndex);
                    cell.Transform.SetAbsoluteFrame(cellAbsX, cellAbsY, ThumbW, cellH);
                }
                else if (originChanged)
                {
                    cell.Transform.SetAbsoluteFrame(cellAbsX, cellAbsY, ThumbW, cellH);
                }
                cell.Visible = true;
            }
            else
            {
                if (_cellPool.Count > 0)
                {
                    cell = _cellPool.Pop();
                }
                else
                {
                    cell = new Cell(this);
                    _scroller.AddChild(cell);
                }

                cell.Rebind(docIdx, _docs[docIdx], docIdx == _activeIndex);
                cell.Transform.SetAbsoluteFrame(cellAbsX, cellAbsY, ThumbW, cellH);
                cell.Visible = true;
                _activeCells[vis] = cell;
            }
        }
    }

    private void OnCellSelected(int index)
    {
        _activeIndex = index;
        foreach (var pair in _activeCells)
            pair.Value.SetActive(pair.Value.Index == index);
        EnsureVisible(index);
        Selected?.Invoke(index);
    }

    public override void Dispose()
    {
        base.Dispose();
        _scroller.Dispose();
        foreach (var cell in _activeCells.Values)
            cell.Dispose();
        _activeCells.Clear();

        while (_cellPool.Count > 0)
            _cellPool.Pop().Dispose();
    }

    private sealed class FilmstripScroller : ScrollContainer
    {
        private readonly Filmstrip _owner;

        public FilmstripScroller(Filmstrip owner)
        {
            _owner = owner;
        }

        protected override void OnScrollOffsetChanged()
        {
            base.OnScrollOffsetChanged();
            _owner.UpdateVirtualCells(force: false);
        }

        protected override void LayoutChildren()
        {
            base.LayoutChildren();
            _owner.UpdateVirtualCells(force: true);
        }
    }

    private sealed class Cell : VisualElement
    {
        private readonly Filmstrip _owner;
        private int _index;
        private readonly ThumbWell _thumb;
        private readonly VisualElement _ready;
        private readonly VisualElement _star;
        private PhotoDocument? _doc;
        private bool _active;
        private bool _isReady;
        private bool _isFavorite;

        public int Index => _index;
        public PhotoDocument? Doc => _doc;

        public Cell(Filmstrip owner)
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
                    Roundness = Theme.RadiusSm
                },
                Shadow = new ShadowStyle(0, 1.5f, 2, 2, new SKColor(0, 0, 0, 60))
            };

            _thumb = new ThumbWell(null, () => GetValidImage())
            {
                Name = "Cell_Thumb",
                IsClickthrough = true
            };

            _ready = MakeMarkButton("Cell_Ready");
            _ready.Events.OnClick += (_, args) =>
            {
                if (args.Button != (int)MouseButton.Left) return;
                if (_doc == null) return;
                _isReady = !_isReady;
                _doc.IsReady = _isReady;
                UpdateReadyStyle();
                _owner.ReadyToggled?.Invoke(_index, _isReady);
                args.Handled = true;
            };

            _star = new VisualElement
            {
                Name = "Cell_Star",
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
            _star.Events.OnClick += (_, args) =>
            {
                if (args.Button != (int)MouseButton.Left) return;
                if (_doc == null) return;
                _isFavorite = !_isFavorite;
                _doc.IsFavorite = _isFavorite;
                UpdateStarStyle();
                _owner.FavoriteToggled?.Invoke(_index, _isFavorite);
                args.Handled = true;
            };

            AddChild(_thumb);
            AddChild(_ready);
            AddChild(_star);

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
                    Style.Border.Color = CellBorderColor();
                    InvalidatePaint();
                }
            };

            Events.OnMouseUp += (_, args) =>
            {
                if (args.Button != (int)MouseButton.Right) return;
                _owner.OnCellSelected(_index);
                _owner.ContextMenuRequested?.Invoke(_index, args.Global.X, args.Global.Y);
                args.Handled = true;
            };
            Events.OnClick += (_, args) =>
            {
                if (args.Button != (int)MouseButton.Left) return;
                _owner.OnCellSelected(_index);
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
            Name = $"FilmstripCell_{index}";

            Style.BackColor = active ? Theme.Selected : Theme.Section;
            Style.Border.Width = active ? 1.5f : 1f;
            Style.Border.Color = active ? Theme.Accent : CellBorderColor();
            Style.Border.Roundness = Theme.RadiusSm;
            Style.Shadow = active
                ? new ShadowStyle(0, 2f, 4, 4, new SKColor(255, 153, 51, 80))
                : new ShadowStyle(0, 1.5f, 2, 2, new SKColor(0, 0, 0, 60));

            _thumb.Name = $"{Name}_Thumb";
            _thumb.SetImage(GetValidImage());

            _ready.Name = $"{Name}_Ready";
            UpdateReadyStyle();
            _star.Name = $"{Name}_Star";
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
            Style.Border.Width = active ? 1.5f : 1f;
            Style.Border.Color = active ? Theme.Accent : CellBorderColor();
            Theme.ApplyCardShadow(Style);
            if (active && Theme.UseShadows && Style.Shadow != null)
                Style.Shadow.Color = new SKColor(Theme.Accent.Red, Theme.Accent.Green, Theme.Accent.Blue, 70);
            InvalidatePaint();
        }

        public void RefreshTheme()
        {
            SetActive(_active);
            UpdateReadyStyle();
            UpdateStarStyle();
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

        private SKColor CellBorderColor()
        {
            if (_isReady) return Theme.SuccessSoft;
            if (_isFavorite) return Theme.FavoriteSoft;
            return Theme.Hairline;
        }

        private void UpdateReadyStyle()
        {
            _ready.BackgroundSvg = _isReady ? (_ready.BackgroundSvg ?? IconStore.LoadSvg("check")) : null;
            _ready.Style.BackColor = _isReady ? Theme.Success : new SKColor(0, 0, 0, 140);
            _ready.Style.Border.Color = _isReady ? Theme.Success : Theme.HairlineSubtle;
            _ready.Style.Border.Roundness = 9f;
            _ready.InvalidatePaint();
            if (!_active)
            {
                Style.Border.Color = CellBorderColor();
                InvalidatePaint();
            }
        }

        private void UpdateStarStyle()
        {
            _star.BackgroundSvg = _isFavorite ? (_star.BackgroundSvg ?? IconStore.LoadSvg("star_filled")) : null;
            _star.BackgroundImageTintColor = Theme.Favorite;
            _star.IsClickthrough = !_isFavorite;
            _star.Style.BackColor = SKColors.Transparent;
            _star.Style.Border.Width = 0;
            _star.Style.Border.Color = SKColors.Transparent;
            _star.InvalidatePaint();
            if (!_active)
            {
                Style.Border.Color = CellBorderColor();
                InvalidatePaint();
            }
        }

        protected override void LayoutChildren()
        {
            float originX = Transform.Computed.X;
            float originY = Transform.Computed.Y;
            float w = Math.Max(1f, Transform.Width);
            float h = Math.Max(1f, Transform.Height);

            _thumb.Transform.SetAbsoluteFrame(originX + 1, originY + 1, w - 2, h - 2);
            _ready.Transform.SetAbsoluteFrame(originX + 4, originY + 4, 18, 18);
            _star.Transform.SetAbsoluteFrame(originX + 4, originY + h - 20, 16, 16);
        }

        private static VisualElement MakeMarkButton(string name)
        {
            return new VisualElement
            {
                Name = name,
                Cursor = StandardCursor.Hand,
                BackgroundImageScale = ImageScaleMode.Contain,
                BackgroundImageTintBlendMode = SKBlendMode.SrcIn,
                BackgroundImageTintColor = SKColors.White,
                Padding = new Thickness(2f),
                Style = new ElementStyle
                {
                    BackColor = new SKColor(0, 0, 0, 140),
                    Border = new BorderStyle
                    {
                        Width = 1,
                        Color = Theme.HairlineSubtle,
                        Roundness = 9f
                    }
                }
            };
        }
    }

    private sealed class ThumbWell : VisualElement
    {
        private static readonly SKPaint SmoothPaint = new()
        {
            FilterQuality = SKFilterQuality.Medium,
            IsAntialias = true
        };

        private readonly Func<SKImage?>? _imageProvider;
        private SKImage? _image;

        public ThumbWell(SKImage? image, Func<SKImage?>? imageProvider = null)
        {
            _image = image;
            _imageProvider = imageProvider;
            Style = new ElementStyle
            {
                BackColor = Theme.Well,
                Border = new BorderStyle { Width = 0, Color = SKColors.Transparent, Roundness = Theme.RadiusSm }
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
