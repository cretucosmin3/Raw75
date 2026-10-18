using System;
using System.Collections.Generic;
using Blossom.Animations;
using Blossom.Core;
using Blossom.Core.Input;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Blossom.Hosting;
using Blossom.Primitives;
using Blossom.Reactive;
using Raw75.Develop;
using SkiaSharp;

namespace Raw75.Controls;

/// <summary>Horizontal session strip: thumbs, click to select, horizontally scrollable, virtualized.</summary>
public class Filmstrip : VisualElement
{
    public const float FilterRailW = 56f;

    private const float ThumbW = 96f;
    private const float Gap = 6f;
    private const float FilterBtnW = 44f;
    private const float FilterBtnH = 28f;

    private readonly VisualElement _filterRail;
    private readonly Stack _filterStack;
    private readonly IconButton _btnFilterAll;
    private readonly IconButton _btnFilterReady;
    private readonly IconButton _btnFilterHeart;
    private readonly FilmstripScroller _scroller;

    private readonly Dictionary<int, Cell> _activeCells = new();
    private readonly Stack<Cell> _cellPool = new();
    private readonly List<int> _recycledKeys = new();
    private readonly List<int> _filteredIndices = new();
    private readonly Signal<PhotoFilter> _filter = new(PhotoFilter.All);

    private IReadOnlyList<PhotoDocument> _docs = Array.Empty<PhotoDocument>();
    private int _activeIndex = -1;
    private int _lastMinIdx = -1;
    private int _lastMaxIdx = -1;
    private float _lastOriginX = float.NaN;
    private float _lastOriginY = float.NaN;
    private float _lastHeight = float.NaN;

    private bool _isPanning;
    private float _panStartX;
    private float _panStartScroll;

    public event Action<int>? Selected;
    public event Action<int, bool, bool>? SelectionRequested;
    public event Action<PhotoFilter>? FilterChanged;
    public event Action<int, float, float>? ContextMenuRequested;

    private readonly HashSet<int> _selectedIndices = new();

    public PhotoFilter ActiveFilter => _filter.Value;

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

        if ((_filter.Peek() & PhotoFilter.Ready) != 0)
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

        if ((_filter.Peek() & PhotoFilter.Favorite) != 0)
            RebuildFiltered(keepScroll: true);
    }

    public Filmstrip()
    {
        Name = "Filmstrip";
        Style = new ElementStyle { Border = new BorderStyle { Width = 0, Roundness = 0 } };

        _filterRail = new VisualElement
        {
            Name = "Filmstrip_FilterRail",
            Style = new ElementStyle { Border = new BorderStyle { Width = 1, Roundness = 0 } }
        };

        _filterStack = new Stack
        {
            Name = "Filmstrip_FilterStack",
            Orientation = Orientation.Vertical,
            Gap = 4f
        };

        _btnFilterAll = FilterButton(new IconButton("All") { FontSize = 11f }, PhotoFilter.All);
        _btnFilterAll.Clicked += () => SetFilter(PhotoFilter.All);

        _btnFilterReady = FilterButton(IconButton.Icon("check"), PhotoFilter.Ready);
        _btnFilterReady.Clicked += () => ToggleFilterFlag(PhotoFilter.Ready);

        _btnFilterHeart = FilterButton(IconButton.Icon("heart_filled"), PhotoFilter.Favorite);
        _btnFilterHeart.Clicked += () => ToggleFilterFlag(PhotoFilter.Favorite);

        _filterRail.AddChild(_filterStack);
        AddChild(_filterRail);

        _scroller = new FilmstripScroller(this)
        {
            Name = "Filmstrip_Scroller",
            OverflowX = OverflowMode.Scroll,
            OverflowY = OverflowMode.Clip,
            ScrollbarVisibilityX = ScrollbarVisibility.Auto,
            ScrollbarVisibilityY = ScrollbarVisibility.Hidden,
            ScrollbarThickness = 6f,
            ScrollbarRadius = 3f,
            ScrollbarPadding = 1.5f,
            ScrollbarTrackColor = SKColors.Transparent,
            Padding = new Thickness(8, 6, 8, 8),
            SmoothScroll = true,
            ScrollStepX = (ThumbW + Gap) * 1.5f,
            Style = new ElementStyle { Border = new BorderStyle { Width = 0 } }
        };

        _scroller.Events.OnMouseDown += (_, args) =>
        {
            if (args.Button == PointerButton.Middle)
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

        this.Bind(_ => ApplyTheme());
    }

    /// <summary>Adds a rail button whose toggled state follows the active filter.</summary>
    private IconButton FilterButton(IconButton btn, PhotoFilter flag)
    {
        btn.MinWidth = btn.MaxWidth = FilterBtnW;
        btn.MinHeight = btn.MaxHeight = FilterBtnH;
        btn.Bind(b => b.Toggled = flag == PhotoFilter.All
            ? _filter.Value == PhotoFilter.All
            : (_filter.Value & flag) != 0);
        _filterStack.AddChild(btn);
        return btn;
    }

    private void ApplyTheme()
    {
        Style.BackColor = Theme.Filmstrip;
        _filterRail.Style.BackColor = Theme.BottomBar;
        _filterRail.Style.Border.Color = Theme.Hairline;

        _btnFilterReady.IconColor = Theme.Success;
        _btnFilterReady.ToggledBackColor = Theme.Success.WithAlpha(36);
        _btnFilterReady.ToggledBorderColor = Theme.SuccessSoft;
        _btnFilterHeart.IconColor = Theme.Favorite;
        _btnFilterHeart.ToggledBackColor = Theme.Favorite.WithAlpha(32);
        _btnFilterHeart.ToggledBorderColor = Theme.FavoriteSoft;

        _scroller.Style.BackColor = Theme.Filmstrip;
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
        _lastMinIdx = -1;
        _lastMaxIdx = -1;

        UpdateFilteredIndices();
        UpdateContentSize();
        UpdateVirtualCells(force: true);
        EnsureVisible(activeIndex);
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
        SyncCellSelection();
        EnsureVisible(activeIndex);
        InvalidatePaint();
    }

    public void SetFilter(PhotoFilter filter)
    {
        if (_filter.Peek() == filter) return;
        _filter.Value = filter;
        RebuildFiltered(keepScroll: false);
        FilterChanged?.Invoke(filter);
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

        // The filter stack sizes itself; centre it in the rail.
        float stackH = _filterStack.GetPreferredSize(FilterBtnW, 0f).Height;
        float stackY = oy + Math.Max(6f, (h - stackH) * 0.5f);
        _filterStack.Transform.SetAbsoluteFrame(ox + (FilterRailW - FilterBtnW) * 0.5f, stackY, FilterBtnW, stackH);

        float scrollerW = Math.Max(0f, w - FilterRailW);
        _scroller.Transform.SetAbsoluteFrame(ox + FilterRailW, oy, scrollerW, h);

        UpdateContentSize();
        base.LayoutChildren();
        UpdateVirtualCells(force: true);
    }

    private void ToggleFilterFlag(PhotoFilter flag) => SetFilter(_filter.Peek() ^ flag);

    private void RebuildFiltered(bool keepScroll)
    {
        float oldScroll = ScrollX;
        UpdateFilteredIndices();
        _lastMinIdx = -1;
        _lastMaxIdx = -1;
        UpdateContentSize();
        ScrollX = keepScroll ? oldScroll : 0f;
        UpdateVirtualCells(force: true);
        EnsureVisible(_activeIndex);
        InvalidatePaint();
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

    private int VisualIndexOf(int docIndex) => _filteredIndices.IndexOf(docIndex);

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
                    cell.Rebind(docIdx, _docs[docIdx], docIdx == _activeIndex, _selectedIndices.Contains(docIdx));
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
                    cell = ThumbTile.Create(() => new Cell(this));
                    _scroller.AddChild(cell);
                }

                cell.Rebind(docIdx, _docs[docIdx], docIdx == _activeIndex, _selectedIndices.Contains(docIdx));
                cell.Transform.SetAbsoluteFrame(cellAbsX, cellAbsY, ThumbW, cellH);
                cell.Visible = true;
                _activeCells[vis] = cell;
            }
        }
    }

    private void SyncCellSelection()
    {
        foreach (var cell in _activeCells.Values)
            cell.SetSelection(cell.Index == _activeIndex, _selectedIndices.Contains(cell.Index));
    }

    private void OnCellClicked(int index, bool ctrl, bool shift)
    {
        if (SelectionRequested != null)
        {
            SelectionRequested.Invoke(index, ctrl, shift);
        }
        else
        {
            OnCellSelected(index);
        }
    }

    private void OnCellSelected(int index)
    {
        _activeIndex = index;
        _selectedIndices.Clear();
        _selectedIndices.Add(index);
        SyncCellSelection();
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

    /// <summary>
    /// Filmstrip thumb: photo inset in a hairline frame, read-only ready / favourite (heart) marks, selection pip.
    /// Marks are toggled from the photo view (the tile is too small for click targets).
    /// </summary>
    private sealed class Cell : ThumbTile
    {
        private const float Inset = 3f;
        private const float Mark = 12f;
        private const float Pip = 14f;

        private readonly Filmstrip _owner;

        public Cell(Filmstrip owner)
        {
            _owner = owner;
        }

        protected override string NamePrefix => "FilmstripCell";

        protected override SKRect PhotoRect => new(Inset, Inset, W - Inset, H - Inset);

        protected override void OnPrimaryClick(bool ctrl, bool shift) => _owner.OnCellClicked(Index, ctrl, shift);

        protected override void OnContextMenu(float x, float y)
        {
            _owner.OnCellSelected(Index);
            _owner.ContextMenuRequested?.Invoke(Index, x, y);
        }

        protected override bool InteractiveMarks => false;

        protected override void PaintOverlay(SKCanvas c)
        {
            float r = Mark * 0.5f;
            if (Doc?.IsReady == true)
            {
                ThumbInk.Disc(c, 6f + r, 6f + r, r, Theme.Success);
                ThumbInk.Check(c, 6f + r, 6f + r, Mark * 0.62f, Theme.Text, 1.6f);
            }
            if (Doc?.IsFavorite == true)
            {
                float heartY = H - 6f - r;
                ThumbInk.Disc(c, 6f + r, heartY, r, Theme.Scrim);
                ThumbInk.Heart(c, 6f + r, heartY, Mark * 0.9f, Theme.Favorite, filled: true);
            }

            // Multi-select pip; the active thumb already carries the full accent ring.
            if (!IsSelected || IsActive) return;
            float cx = W - 6f - Pip * 0.5f, cy = 6f + Pip * 0.5f;
            ThumbInk.Disc(c, cx, cy, Pip * 0.5f, Theme.Accent);
            ThumbInk.Check(c, cx, cy, Pip * 0.62f, Theme.ButtonTextOnAccent, 1.8f);
        }
    }
}

/// <summary>Which mark a <see cref="ThumbBadge"/> toggles.</summary>
internal enum ThumbMark
{
    Ready,
    Favorite
}

/// <summary>
/// Shared thumbnail tile for the filmstrip and the gallery: a <see cref="Control"/> that paints the
/// photo inside a crisp hairline frame (active = 2px accent ring, multi-selected = accent wash and a
/// softer ring, hover = stronger hairline and a lifted shadow) and hosts the ready / favourite badges.
/// Tiles are pooled by their owners, so <see cref="Rebind"/> carries all per-photo state.
/// </summary>
internal abstract class ThumbTile : Control
{

    private float _hover;

    protected ThumbBadge ReadyBadge { get; }
    protected ThumbBadge FavoriteBadge { get; }

    public int Index { get; private set; } = -1;
    public PhotoDocument? Doc { get; private set; }
    protected bool IsActive { get; private set; }
    protected bool IsSelected { get; private set; }

    /// <summary>Outer corner radius of the tile.</summary>
    protected virtual float CornerRadius => Theme.RadiusSm;

    /// <summary>Photo well in local coordinates.</summary>
    protected abstract SKRect PhotoRect { get; }

    protected abstract string NamePrefix { get; }

    protected ThumbTile()
    {
        Cursor = UiCursor.Hand;
        Style = new ElementStyle
        {
            Border = new BorderStyle { Width = 0 },
            Shadow = new ShadowStyle(0, 1f, 1.5f, 1.5f, SKColors.Transparent)
        };

        ReadyBadge = new ThumbBadge(ThumbMark.Ready) { Name = "Thumb_Ready" };
        ReadyBadge.Clicked += () => ToggleMark(ReadyBadge);
        FavoriteBadge = new ThumbBadge(ThumbMark.Favorite) { Name = "Thumb_Favorite" };
        FavoriteBadge.Clicked += () => ToggleMark(FavoriteBadge);
        AddChild(ReadyBadge);
        AddChild(FavoriteBadge);

        Events.OnMouseUp += (_, args) =>
        {
            if (args.Button != PointerButton.Right || Doc == null) return;
            OnContextMenu(args.Global.X, args.Global.Y);
            args.Handled = true;
        };
        Events.OnClick += (_, args) =>
        {
            if (args.Button != PointerButton.Left || Doc == null) return;
            var host = ParentView?.Events;
            bool ctrl = Events.IsControlDown || Events.IsKeyDown(UiKey.SuperLeft) || Events.IsKeyDown(UiKey.SuperRight) ||
                        (host != null && (host.IsControlDown || host.IsKeyDown(UiKey.SuperLeft) || host.IsKeyDown(UiKey.SuperRight)));
            bool shift = Events.IsShiftDown || (host?.IsShiftDown ?? false);
            OnPrimaryClick(ctrl, shift);
            args.Handled = true;
        };

        this.Bind(_ => ApplyChrome());
    }

    /// <summary>
    /// Builds a tile outside any running effect, so its theme binding is owned by the tile alone
    /// (a pooled tile can be created while a caller's effect is running and must outlive it).
    /// </summary>
    public static T Create<T>(Func<T> factory) where T : ThumbTile => EffectScope.Record(factory).Result;

    /// <summary>Thin neutral scrollbars for the thumbnail scrollers; call from a theme binding.</summary>
    public static void StyleScrollbars(ScrollContainer scroller)
    {
        scroller.ScrollbarThumbColor = Theme.HairlineStrong;
        scroller.ScrollbarThumbHoverColor = Theme.TextDim;
        scroller.ScrollbarThumbDragColor = Theme.TextSecondary;
        scroller.InvalidatePaint();
    }

    protected abstract void OnPrimaryClick(bool ctrl, bool shift);
    protected abstract void OnContextMenu(float x, float y);
    protected virtual void OnReadyToggled(bool ready) { }
    protected virtual void OnFavoriteToggled(bool favorite) { }

    /// <summary>False hides the clickable badges; the tile may paint its own read-only marks.</summary>
    protected virtual bool InteractiveMarks => true;

    /// <summary>Per-photo state beyond the shared marks (captions, badges).</summary>
    protected virtual void OnRebind(PhotoDocument doc) { }

    /// <summary>Drawn above the photo and frame.</summary>
    protected virtual void PaintOverlay(SKCanvas c) { }

    public void Rebind(int index, PhotoDocument doc, bool active, bool selected)
    {
        Index = index;
        Doc = doc;
        IsActive = active;
        IsSelected = selected;
        Name = $"{NamePrefix}_{index}";
        ReadyBadge.On = doc.IsReady;
        FavoriteBadge.On = doc.IsFavorite;
        OnRebind(doc);
        SyncBadges();
        ApplyChrome();
    }

    public void SetSelection(bool active, bool selected)
    {
        if (IsActive == active && IsSelected == selected) return;
        IsActive = active;
        IsSelected = selected;
        ApplyChrome();
    }

    public void SetReady(bool ready)
    {
        ReadyBadge.On = ready;
        SyncBadges();
        InvalidatePaint();
    }

    public void SetFavorite(bool favorite)
    {
        FavoriteBadge.On = favorite;
        SyncBadges();
        InvalidatePaint();
    }

    public void RefreshThumb() => InvalidatePaint();

    protected SKImage? Image
    {
        get
        {
            var doc = Doc;
            if (doc == null) return null;
            if (doc.Look != null && doc.Look.Handle != IntPtr.Zero) return doc.Look;
            if (doc.Preview != null && doc.Preview.Handle != IntPtr.Zero) return doc.Preview;
            if (doc.Thumb != null && doc.Thumb.Handle != IntPtr.Zero) return doc.Thumb;
            return null;
        }
    }

    protected override void OnHoverChanged()
    {
        _hover = IsHovered ? 1f : 0f;
        ApplyChrome();
        SyncBadges();
    }

    private void ToggleMark(ThumbBadge badge)
    {
        if (Doc == null) return;
        bool next = !badge.On;
        badge.On = next;
        if (badge.Mark == ThumbMark.Ready)
        {
            Doc.IsReady = next;
            OnReadyToggled(next);
        }
        else
        {
            Doc.IsFavorite = next;
            OnFavoriteToggled(next);
        }
        SyncBadges();
    }

    /// <summary>Set marks always show; unset ones appear on hover as click targets.</summary>
    private void SyncBadges()
    {
        ReadyBadge.Visible = InteractiveMarks && (ReadyBadge.On || IsHovered);
        FavoriteBadge.Visible = InteractiveMarks && (FavoriteBadge.On || IsHovered);
    }

    private void ApplyChrome()
    {
        float hover = _hover;
        Style.BackColor = Anim.Lerp(Theme.Section, Theme.Hover, hover * 0.6f);
        Style.Border.Roundness = CornerRadius;

        // Resting tiles sit flat; hover lifts them on a slightly deeper, offset shadow.
        var shadow = Style.Shadow!;
        if (Theme.UseShadows)
        {
            shadow.OffsetY = 1f + hover * 1.5f;
            shadow.SpreadX = shadow.SpreadY = 1.5f + hover * 2.5f;
            shadow.Color = new SKColor(0, 0, 0, (byte)(Theme.ShadowAlpha * (0.35f + 0.65f * hover)));
        }
        else
        {
            shadow.Color = SKColors.Transparent;
        }
        InvalidatePaint();
    }

    protected override void Paint(SKCanvas c)
    {
        float r = CornerRadius;
        var bounds = SKRect.Create(0, 0, W, H);

        // Selection wash sits under the photo so the image itself is never tinted.
        if (IsSelected || IsActive)
            ThumbInk.FillRound(c, bounds, r, Theme.AccentWash);

        var photo = PhotoRect;
        if (photo.Width > 2f && photo.Height > 2f)
            ThumbInk.Photo(c, Image, photo, Math.Max(0f, r - 1.5f));

        PaintOverlay(c);

        if (IsActive)
        {
            ThumbInk.StrokeRound(c, Inflate(bounds, -1f), Math.Max(0f, r - 1f), Theme.Accent, 2f);
        }
        else if (IsSelected)
        {
            ThumbInk.StrokeRound(c, Inflate(bounds, -0.75f), Math.Max(0f, r - 0.75f), Theme.Accent.WithAlpha(150), 1.5f);
        }
        else
        {
            var edge = Anim.Lerp(Theme.Hairline, Theme.HairlineStrong, _hover);
            ThumbInk.StrokeRound(c, Inflate(bounds, -0.5f), Math.Max(0f, r - 0.5f), edge, 1f);
        }
    }

    private static SKRect Inflate(SKRect r, float d)
    {
        r.Inflate(d, d);
        return r;
    }
}

/// <summary>
/// Round ready / favourite badge on a thumbnail. Set marks are solid (Success disc with a check,
/// Favorite heart on a scrim); unset ones are a faint outline the tile reveals on hover.
/// </summary>
internal sealed class ThumbBadge : ButtonBase
{
    private bool _on;

    public ThumbMark Mark { get; }

    public bool On
    {
        get => _on;
        set => SetAndPaint(ref _on, value);
    }

    public ThumbBadge(ThumbMark mark)
    {
        Mark = mark;
        ReceivesKeyboard = false;
        ZIndex = 5;
        Style = new ElementStyle { BackColor = SKColors.Transparent };
        // A badge owns its click: the tile underneath must not also select.
        Events.OnClick += (_, e) => { if (e.Button == PointerButton.Left) e.Handled = true; };
    }

    protected override void Paint(SKCanvas c)
    {
        float cx = W * 0.5f, cy = H * 0.5f;
        float r = Math.Min(W, H) * 0.5f - 0.5f;
        var ink = IsHovered ? Theme.Text : Theme.TextSecondary;

        if (Mark == ThumbMark.Ready)
        {
            if (_on)
            {
                ThumbInk.Disc(c, cx, cy, r, IsHovered ? Theme.Lighten(Theme.Success, 18) : Theme.Success);
                ThumbInk.Check(c, cx, cy, r * 1.25f, Theme.Text, 2f);
            }
            else
            {
                ThumbInk.Disc(c, cx, cy, r, Theme.Scrim);
                ThumbInk.Ring(c, cx, cy, r - 0.5f, Theme.HairlineStrong);
                ThumbInk.Check(c, cx, cy, r * 1.15f, ink, 1.6f);
            }
            return;
        }

        ThumbInk.Disc(c, cx, cy, r, Theme.Scrim);
        if (_on)
            ThumbInk.Heart(c, cx, cy, r * 1.3f, IsHovered ? Theme.Lighten(Theme.Favorite, 20) : Theme.Favorite, filled: true);
        else
            ThumbInk.Heart(c, cx, cy, r * 1.2f, ink, filled: false);
    }
}

/// <summary>
/// Shared paints and glyphs for thumbnail chrome. Tiles repaint while scrolling, so paints are
/// reused instead of allocated per draw (painting is single-threaded).
/// </summary>
internal static class ThumbInk
{
    private static readonly SKPaint FillPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private static readonly SKPaint StrokePaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke };
    private static readonly SKPaint ImagePaint = new() { IsAntialias = true };

    // Glyphs in a 24-unit box, centred on (12, 12).
    private static readonly SKPath CheckPath = BuildPolyline(false, 20, 6, 9, 17, 4, 12);
    private static readonly SKPath HeartPath = SKPath.ParseSvgPathData("M20.84 4.61a5.5 5.5 0 0 0-7.78 0L12 5.67l-1.06-1.06a5.5 5.5 0 0 0-7.78 7.78l1.06 1.06L12 21.23l7.78-7.78 1.06-1.06a5.5 5.5 0 0 0 0-7.78z");

    public static void FillRound(SKCanvas c, SKRect rect, float radius, SKColor color)
    {
        FillPaint.Color = color;
        c.DrawRoundRect(rect, radius, radius, FillPaint);
    }

    public static void StrokeRound(SKCanvas c, SKRect rect, float radius, SKColor color, float width)
    {
        StrokePaint.Color = color;
        StrokePaint.StrokeWidth = width;
        StrokePaint.StrokeJoin = SKStrokeJoin.Miter;
        c.DrawRoundRect(rect, radius, radius, StrokePaint);
    }

    public static void Disc(SKCanvas c, float cx, float cy, float r, SKColor color)
    {
        FillPaint.Color = color;
        c.DrawCircle(cx, cy, r, FillPaint);
    }

    public static void Ring(SKCanvas c, float cx, float cy, float r, SKColor color)
    {
        StrokePaint.Color = color;
        StrokePaint.StrokeWidth = 1f;
        c.DrawCircle(cx, cy, r, StrokePaint);
    }

    public static void Check(SKCanvas c, float cx, float cy, float size, SKColor color, float strokeWidth)
        => Glyph(c, CheckPath, cx, cy, size, color, filled: false, strokeWidth);

    public static void Heart(SKCanvas c, float cx, float cy, float size, SKColor color, bool filled)
        => Glyph(c, HeartPath, cx, cy, size, color, filled, 1.3f);

    /// <summary>Photo contain-fit in a recessed well, clipped to the well's rounded corners.</summary>
    public static void Photo(SKCanvas c, SKImage? image, SKRect well, float radius)
    {
        FillRound(c, well, radius, Theme.Well);
        if (image == null || image.Handle == IntPtr.Zero || image.Width <= 0 || image.Height <= 0) return;

        float scale = Math.Min(well.Width / image.Width, well.Height / image.Height);
        float dw = image.Width * scale;
        float dh = image.Height * scale;
        var dest = SKRect.Create(well.Left + (well.Width - dw) * 0.5f, well.Top + (well.Height - dh) * 0.5f, dw, dh);

        c.Save();
        if (radius > 0f)
        {
            using var clip = new SKRoundRect(well, radius);
            c.ClipRoundRect(clip, SKClipOperation.Intersect, true);
        }
        c.DrawImage(image, dest, Sampling.Smooth, ImagePaint);
        c.Restore();
    }

    private static void Glyph(SKCanvas c, SKPath path, float cx, float cy, float size, SKColor color, bool filled, float strokeWidth)
    {
        float s = size / 24f;
        c.Save();
        c.Translate(cx, cy);
        c.Scale(s);
        c.Translate(-12f, -12f);
        var paint = filled ? FillPaint : StrokePaint;
        paint.Color = color;
        if (!filled)
        {
            StrokePaint.StrokeWidth = strokeWidth / s;
            StrokePaint.StrokeCap = SKStrokeCap.Round;
            StrokePaint.StrokeJoin = SKStrokeJoin.Round;
        }
        c.DrawPath(path, paint);
        c.Restore();
    }

    private static SKPath BuildPolyline(bool close, params float[] xy)
    {
        using var path = new SKPathBuilder();
        path.MoveTo(xy[0], xy[1]);
        for (int i = 2; i + 1 < xy.Length; i += 2)
            path.LineTo(xy[i], xy[i + 1]);
        if (close) path.Close();
        return path.Detach();
    }
}
