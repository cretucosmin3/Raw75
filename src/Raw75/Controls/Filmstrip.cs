using System;
using System.Collections.Generic;
using Blossom.Core;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Raw75.Develop;
using Silk.NET.Input;
using SkiaSharp;

namespace Raw75.Controls;

/// <summary>Horizontal session strip: thumbs, names, click to select, optional close, horizontally scrollable, virtualized.</summary>
public class Filmstrip : ScrollContainer
{
    private const float ThumbW = 96f;
    private const float Gap = 6f;
    private const float NameH = 18f;

    private readonly Dictionary<int, Cell> _activeCells = new();
    private readonly Stack<Cell> _cellPool = new();
    private readonly List<int> _recycledKeys = new();

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
    public event Action<int>? CloseRequested;
    public event Action<int, bool>? ReadyToggled;

    public void SetReady(int index, bool ready)
    {
        if (index >= 0 && index < _docs.Count)
        {
            _docs[index].IsReady = ready;
            if (_activeCells.TryGetValue(index, out var cell))
                cell.SetReady(ready);
        }
    }

    public Filmstrip()
    {
        Name = "Filmstrip";
        OverflowX = OverflowMode.Scroll;
        OverflowY = OverflowMode.Clip;
        ScrollbarVisibilityX = ScrollbarVisibility.Auto;
        ScrollbarVisibilityY = ScrollbarVisibility.Hidden;
        ScrollbarThickness = 8f;
        ScrollbarRadius = 4f;
        ScrollbarPadding = 1.5f;
        ScrollbarThumbColor = new SKColor(160, 160, 160, 140);
        ScrollbarThumbHoverColor = new SKColor(200, 200, 200, 200);
        ScrollbarThumbDragColor = Theme.Accent;
        ScrollbarTrackColor = SKColors.Transparent;
        Padding = new Thickness(8, 6, 8, 8);
        SmoothScroll = true;
        ScrollStepX = (ThumbW + Gap) * 1.5f;
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

        Events.OnMouseDown += (_, args) =>
        {
            if (args.Button == (int)MouseButton.Middle)
            {
                _isPanning = true;
                _panStartX = args.Global.X;
                _panStartScroll = ScrollX;
                CapturePointer();
                args.Handled = true;
            }
        };
        Events.OnMouseMove += (_, args) =>
        {
            if (_isPanning)
            {
                float dx = args.Global.X - _panStartX;
                ScrollX = _panStartScroll - dx;
                args.Handled = true;
            }
        };
        Events.OnMouseUp += (_, args) =>
        {
            if (_isPanning)
            {
                _isPanning = false;
                ReleasePointer();
                args.Handled = true;
            }
        };
    }

    public void Bind(IReadOnlyList<PhotoDocument> docs, int activeIndex)
    {
        _docs = docs ?? Array.Empty<PhotoDocument>();
        _activeIndex = activeIndex;
        _lastMinIdx = -1;
        _lastMaxIdx = -1;

        float w = Math.Max(1f, Transform.Computed.Width > 0 ? Transform.Computed.Width : Transform.Width);
        float h = Math.Max(1f, Transform.Height > 0 ? Transform.Height : Theme.FilmH);
        float stride = ThumbW + Gap;
        float totalW = _docs.Count > 0 ? (Padding.Left + _docs.Count * stride - Gap + Padding.Right) : w;

        SetContentSize(Math.Max(totalW, w), h);
        UpdateVirtualCells(force: true);
        EnsureVisible(activeIndex);
        InvalidatePaint();
    }

    public void EnsureVisible(int index)
    {
        if (index < 0 || index >= _docs.Count) return;
        float cellLeft = Padding.Left + index * (ThumbW + Gap);
        float cellRight = cellLeft + ThumbW;
        float viewW = Transform.Computed.Width > 0 ? Transform.Computed.Width : Transform.Width;
        if (viewW <= 0) return;

        if (cellLeft < ScrollX + Padding.Left)
        {
            AnimateScrollTo(Math.Max(0, cellLeft - Padding.Left), 0);
        }
        else if (cellRight > ScrollX + viewW - Padding.Right)
        {
            AnimateScrollTo(Math.Max(0, cellRight - viewW + Padding.Right), 0);
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

    protected override void OnScrollOffsetChanged()
    {
        base.OnScrollOffsetChanged();
        UpdateVirtualCells(force: false);
    }

    protected override void LayoutChildren()
    {
        float w = Math.Max(1f, Transform.Computed.Width);
        float h = Math.Max(1f, Transform.Height);
        float stride = ThumbW + Gap;
        float totalW = _docs.Count > 0 ? (Padding.Left + _docs.Count * stride - Gap + Padding.Right) : w;
        SetContentSize(Math.Max(totalW, w), h);

        base.LayoutChildren();
        UpdateVirtualCells(force: true);
    }

    private void UpdateVirtualCells(bool force = false)
    {
        if (_docs.Count == 0)
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

        float originX = Transform.Computed.X;
        float originY = Transform.Computed.Y;
        float w = Math.Max(1f, Transform.Computed.Width > 0 ? Transform.Computed.Width : Transform.Width);
        float h = Math.Max(1f, Transform.Height > 0 ? Transform.Height : Theme.FilmH);
        float cellH = Math.Max(1f, h - Padding.Vertical);
        float stride = ThumbW + Gap;

        float scrollX = ScrollX;
        int minIdx = (int)Math.Floor((scrollX - Padding.Left - ThumbW) / stride) - 2;
        int maxIdx = (int)Math.Ceiling((scrollX + w - Padding.Left) / stride) + 2;

        minIdx = Math.Clamp(minIdx, 0, _docs.Count - 1);
        maxIdx = Math.Clamp(maxIdx, 0, _docs.Count - 1);

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

        // Recycle cells that are now outside the visible window
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

        // Place or rebind cells within the visible window
        for (int i = minIdx; i <= maxIdx; i++)
        {
            float cellAbsX = originX + Padding.Left + i * stride;
            float cellAbsY = originY + Padding.Top;

            if (_activeCells.TryGetValue(i, out var cell))
            {
                if (force)
                {
                    cell.Rebind(i, _docs[i], i == _activeIndex);
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
                    AddChild(cell);
                }

                cell.Rebind(i, _docs[i], i == _activeIndex);
                cell.Transform.SetAbsoluteFrame(cellAbsX, cellAbsY, ThumbW, cellH);
                cell.Visible = true;
                _activeCells[i] = cell;
            }
        }
    }

    private void OnCellSelected(int index)
    {
        _activeIndex = index;
        foreach (var pair in _activeCells)
            pair.Value.SetActive(pair.Key == index);
        EnsureVisible(index);
        Selected?.Invoke(index);
    }

    public override void Dispose()
    {
        base.Dispose();
        foreach (var cell in _activeCells.Values)
            cell.Dispose();
        _activeCells.Clear();

        while (_cellPool.Count > 0)
            _cellPool.Pop().Dispose();
    }

    private sealed class Cell : VisualElement
    {
        private readonly Filmstrip _owner;
        private int _index;
        private readonly ThumbWell _thumb;
        private readonly RichBox _caption;
        private readonly VisualElement _ready;
        private readonly VisualElement _close;
        private PhotoDocument? _doc;
        private bool _active;
        private bool _isReady;

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

            _caption = new RichBox
            {
                Name = "Cell_Name",
                Text = "",
                IsClickthrough = true,
                Overflow = OverflowMode.Clip,
                Style = new ElementStyle
                {
                    BackColor = SKColors.Transparent,
                    Text = new TextStyle
                    {
                        Color = Theme.TextDim,
                        Size = 11.5f,
                        Weight = 400,
                        Alignment = TextAlign.Center,
                        Padding = 2,
                        Overflow = TextOverflow.Ellipsis,
                        MaxLines = 1
                    }
                }
            };

            _ready = new VisualElement
            {
                Name = "Cell_Ready",
                Cursor = StandardCursor.Hand,
                BackgroundImageScale = ImageScaleMode.Contain,
                BackgroundImageTintBlendMode = SKBlendMode.SrcIn,
                BackgroundImageTintColor = SKColors.White,
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

            _close = new VisualElement
            {
                Name = "Cell_Close",
                BackgroundSvg = IconStore.LoadSvg("cross"),
                BackgroundImageScale = ImageScaleMode.Contain,
                BackgroundImageTintBlendMode = SKBlendMode.SrcIn,
                BackgroundImageTintColor = Theme.Text,
                Padding = new Thickness(2.5f),
                Style = new ElementStyle
                {
                    BackColor = new SKColor(0, 0, 0, 160),
                    Border = new BorderStyle { Width = 1, Color = Theme.HairlineSubtle, Roundness = 6 }
                }
            };
            _close.Cursor = StandardCursor.Hand;
            _close.Events.OnMouseEnter += _ =>
            {
                _close.Style.BackColor = new SKColor(220, 50, 40, 220);
                _close.BackgroundImageTintColor = SKColors.White;
                _close.InvalidatePaint();
            };
            _close.Events.OnMouseLeave += _ =>
            {
                _close.Style.BackColor = new SKColor(0, 0, 0, 160);
                _close.BackgroundImageTintColor = Theme.Text;
                _close.InvalidatePaint();
            };
            _close.Events.OnClick += (_, args) =>
            {
                if (args.Button != (int)MouseButton.Left) return;
                _owner.CloseRequested?.Invoke(_index);
                args.Handled = true;
            };

            AddChild(_thumb);
            AddChild(_caption);
            AddChild(_ready);
            AddChild(_close);

            Events.OnMouseEnter += _ =>
            {
                if (!_active)
                {
                    Style.BackColor = Theme.Hover;
                    Style.Border.Color = Theme.HairlineStrong;
                    _caption.Style.Text.Color = Theme.Text;
                    InvalidatePaint();
                }
            };
            Events.OnMouseLeave += _ =>
            {
                if (!_active)
                {
                    Style.BackColor = Theme.Section;
                    Style.Border.Color = _isReady ? Theme.SuccessSoft : Theme.Hairline;
                    _caption.Style.Text.Color = Theme.TextDim;
                    InvalidatePaint();
                }
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
            Name = $"FilmstripCell_{index}";

            Style.BackColor = active ? Theme.Selected : Theme.Section;
            Style.Border.Width = active ? 1.5f : 1f;
            Style.Border.Color = active ? Theme.Accent : (_isReady ? Theme.SuccessSoft : Theme.Hairline);
            Style.Border.Roundness = Theme.RadiusSm;
            Style.Shadow = active
                ? new ShadowStyle(0, 2f, 4, 4, new SKColor(255, 153, 51, 80))
                : new ShadowStyle(0, 1.5f, 2, 2, new SKColor(0, 0, 0, 60));

            _caption.Name = $"{Name}_Name";
            _caption.Text = doc.Name;
            _caption.Style.Text.Color = active ? Theme.Accent : Theme.TextDim;
            _caption.InvalidatePaint();

            _thumb.Name = $"{Name}_Thumb";
            _thumb.SetImage(GetValidImage());

            _ready.Name = $"{Name}_Ready";
            UpdateReadyStyle();

            _close.Name = $"{Name}_Close";
            _close.Style.BackColor = new SKColor(0, 0, 0, 160);
            _close.BackgroundImageTintColor = Theme.Text;

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
            Style.Border.Color = active ? Theme.Accent : (_isReady ? Theme.SuccessSoft : Theme.Hairline);
            Style.Shadow = active ? new ShadowStyle(0, 2f, 4, 4, new SKColor(255, 153, 51, 80)) : new ShadowStyle(0, 1.5f, 2, 2, new SKColor(0, 0, 0, 60));
            _caption.Style.Text.Color = active ? Theme.Accent : Theme.TextDim;
            InvalidatePaint();
        }

        public void SetReady(bool ready)
        {
            _isReady = ready;
            UpdateReadyStyle();
        }

        public void RefreshThumb()
        {
            _thumb.SetImage(GetValidImage());
        }

        private void UpdateReadyStyle()
        {
            _ready.BackgroundSvg = _isReady ? IconStore.LoadSvg("check") : null;
            _ready.Style.BackColor = _isReady ? Theme.Success : new SKColor(0, 0, 0, 140);
            _ready.Style.Border.Color = _isReady ? Theme.Success : Theme.HairlineSubtle;
            _ready.Style.Border.Roundness = 9f;
            _ready.InvalidatePaint();
            if (!_active)
            {
                Style.Border.Color = _isReady ? Theme.SuccessSoft : Theme.Hairline;
                InvalidatePaint();
            }
        }

        protected override void LayoutChildren()
        {
            float originX = Transform.Computed.X;
            float originY = Transform.Computed.Y;
            float w = Math.Max(1f, Transform.Width);
            float h = Math.Max(1f, Transform.Height);
            float thumbH = Math.Max(8f, h - NameH - 4f);

            _thumb.Transform.SetAbsoluteFrame(originX + 4, originY + 4, w - 8, thumbH);
            _caption.Transform.SetAbsoluteFrame(originX + 2, originY + 4 + thumbH, w - 4, NameH);
            _ready.Transform.SetAbsoluteFrame(originX + 6, originY + 6, 18, 18);
            _close.Transform.SetAbsoluteFrame(originX + w - 20, originY + 6, 14, 14);
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
                Border = new BorderStyle
                {
                    Width = 1,
                    Color = Theme.HairlineSubtle,
                    Roundness = 2
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
