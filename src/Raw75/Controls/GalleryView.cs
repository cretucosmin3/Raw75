using System;
using System.Collections.Generic;
using Blossom.Core;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Raw75.Develop;
using Silk.NET.Input;
using SkiaSharp;

namespace Raw75.Controls;

public enum GalleryFilter
{
    All,
    ReadyOnly,
    UnmarkedOnly
}

/// <summary>
/// Full-canvas lightbox / gallery grid view:
/// Displays a multi-column contact sheet with ready checkboxes, filter tabs,
/// and double-click to open in develop view.
/// </summary>
public sealed class GalleryView : ScrollContainer
{
    private const float TopBarH = 46f;
    private const float CardW = 200f;
    private const float CardH = 210f;
    private const float Gap = 12f;
    private const float Pad = 16f;

    private readonly VisualElement _headerBar;
    private readonly IconButton _btnFilterAll;
    private readonly IconButton _btnFilterReady;
    private readonly IconButton _btnFilterUnmarked;
    private readonly IconButton _btnMarkAll;
    private readonly IconButton _btnClearAll;
    private readonly VisualElement _countLabel;

    private readonly List<GalleryCard> _cards = new();
    private IReadOnlyList<PhotoDocument> _docs = Array.Empty<PhotoDocument>();
    private int _activeIndex = -1;
    private GalleryFilter _filter = GalleryFilter.All;

    public event Action<int>? PhotoSelected;
    public event Action<int>? PhotoDoubleClicked;
    public event Action<int, bool>? ReadyToggled;
    public event Action? BatchReadyChanged;

    public GalleryFilter ActiveFilter => _filter;

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
        _btnFilterAll.Clicked += () => SetFilter(GalleryFilter.All);

        _btnFilterReady = new IconButton("Ready (0)", "check");
        _btnFilterReady.Clicked += () => SetFilter(GalleryFilter.ReadyOnly);

        _btnFilterUnmarked = new IconButton("Unmarked (0)");
        _btnFilterUnmarked.Clicked += () => SetFilter(GalleryFilter.UnmarkedOnly);

        _btnMarkAll = new IconButton("Mark All Ready", "check");
        _btnMarkAll.Clicked += MarkAllReady;

        _btnClearAll = new IconButton("Clear Ready", "cross");
        _btnClearAll.Clicked += ClearAllReady;

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
        _headerBar.AddChild(_btnFilterUnmarked);
        _headerBar.AddChild(_btnMarkAll);
        _headerBar.AddChild(_btnClearAll);
        _headerBar.AddChild(_countLabel);
        AddChild(_headerBar);
    }

    public void Bind(IReadOnlyList<PhotoDocument> docs, int activeIndex)
    {
        _docs = docs ?? Array.Empty<PhotoDocument>();
        _activeIndex = activeIndex;

        var prev = _cards.ToArray();
        _cards.Clear();
        for (int i = 0; i < prev.Length; i++)
        {
            RemoveChild(prev[i]);
            prev[i].Dispose();
        }

        for (int i = 0; i < _docs.Count; i++)
        {
            var card = new GalleryCard(this, i, _docs[i], i == _activeIndex);
            _cards.Add(card);
            AddChild(card);
        }

        UpdateFilterCounts();
        ApplyFilterVisibility();
        InvalidateLayout();
        InvalidatePaint();
    }

    public void SetSelected(int index)
    {
        _activeIndex = index;
        for (int i = 0; i < _cards.Count; i++)
            _cards[i].SetActive(_cards[i].Index == index);
        InvalidatePaint();
    }

    public void SetReady(int index, bool ready)
    {
        for (int i = 0; i < _cards.Count; i++)
        {
            if (_cards[i].Index == index)
            {
                _cards[i].SetReady(ready);
                break;
            }
        }
        UpdateFilterCounts();
        ApplyFilterVisibility();
    }

    public void RefreshThumbs()
    {
        for (int i = 0; i < _cards.Count; i++)
            _cards[i].RefreshThumb();
        InvalidatePaint();
    }

    public void SetFilter(GalleryFilter filter)
    {
        _filter = filter;
        _btnFilterAll.Toggled = filter == GalleryFilter.All;
        _btnFilterReady.Toggled = filter == GalleryFilter.ReadyOnly;
        _btnFilterUnmarked.Toggled = filter == GalleryFilter.UnmarkedOnly;
        ApplyFilterVisibility();
        InvalidateLayout();
        InvalidatePaint();
    }

    private void UpdateFilterCounts()
    {
        int total = _docs.Count;
        int ready = 0;
        for (int i = 0; i < total; i++)
            if (_docs[i].IsReady) ready++;
        int unmarked = total - ready;

        _btnFilterAll.Caption = $"All ({total})";
        _btnFilterReady.Caption = $"Ready ({ready})";
        _btnFilterUnmarked.Caption = $"Unmarked ({unmarked})";
        _countLabel.Text = $"{total} photos · {ready} ready";
    }

    private void ApplyFilterVisibility()
    {
        for (int i = 0; i < _cards.Count; i++)
        {
            var card = _cards[i];
            bool visible = _filter switch
            {
                GalleryFilter.ReadyOnly => card.Doc.IsReady,
                GalleryFilter.UnmarkedOnly => !card.Doc.IsReady,
                _ => true
            };
            card.Visible = visible;
        }
    }

    private void MarkAllReady()
    {
        for (int i = 0; i < _docs.Count; i++)
        {
            _docs[i].IsReady = true;
            if (i < _cards.Count) _cards[i].SetReady(true);
        }
        UpdateFilterCounts();
        ApplyFilterVisibility();
        BatchReadyChanged?.Invoke();
    }

    private void ClearAllReady()
    {
        for (int i = 0; i < _docs.Count; i++)
        {
            _docs[i].IsReady = false;
            if (i < _cards.Count) _cards[i].SetReady(false);
        }
        UpdateFilterCounts();
        ApplyFilterVisibility();
        BatchReadyChanged?.Invoke();
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
        _btnFilterUnmarked.Transform.SetAbsoluteFrame(hx, hy, 126f, btnH); hx += 134f;

        _btnMarkAll.Transform.SetAbsoluteFrame(hx, hy, 144f, btnH); hx += 148f;
        _btnClearAll.Transform.SetAbsoluteFrame(hx, hy, 118f, btnH);

        _countLabel.Transform.SetAbsoluteFrame(ox + w - Pad - 200f, hy + 6f, 200f, 20f);

        // Grid layout for cards
        float startY = TopBarH + Pad;
        float availW = Math.Max(1f, w - Pad * 2f);
        int cols = Math.Max(1, (int)((availW + Gap) / (CardW + Gap)));
        float actualCellW = (availW - (cols - 1) * Gap) / cols;

        int visibleIndex = 0;
        for (int i = 0; i < _cards.Count; i++)
        {
            var card = _cards[i];
            if (!card.Visible) continue;

            int col = visibleIndex % cols;
            int row = visibleIndex / cols;

            float cx = ox + Pad + col * (actualCellW + Gap);
            float cy = oy + startY + row * (CardH + Gap);

            card.Transform.SetAbsoluteFrame(cx, cy, actualCellW, CardH);
            visibleIndex++;
        }

        int totalRows = (visibleIndex + cols - 1) / cols;
        float totalH = startY + totalRows * (CardH + Gap) + Pad;
        SetContentSize(w, Math.Max(totalH, h));
        base.LayoutChildren();
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
        if (_filter != GalleryFilter.All)
            ApplyFilterVisibility();
    }

    public sealed class GalleryCard : VisualElement
    {
        private static readonly TimeSpan DoubleClickThreshold = TimeSpan.FromMilliseconds(300);

        private readonly GalleryView _owner;
        public int Index { get; }
        public PhotoDocument Doc { get; }
        private bool _active;
        private bool _isReady;
        private DateTime _lastClickTime = DateTime.MinValue;

        private readonly GalleryThumbWell _thumb;
        private readonly VisualElement _caption;
        private readonly VisualElement _readyBtn;
        private readonly VisualElement _indexBadge;

        public GalleryCard(GalleryView owner, int index, PhotoDocument doc, bool active)
        {
            _owner = owner;
            Index = index;
            Doc = doc;
            _active = active;
            _isReady = doc.IsReady;

            Name = $"GalleryCard_{index}";
            Cursor = StandardCursor.Hand;
            Style = new ElementStyle
            {
                BackColor = active ? Theme.Selected : Theme.Section,
                Border = new BorderStyle
                {
                    Width = active ? 2f : 1f,
                    Color = active ? Theme.Accent : (_isReady ? new SKColor(255, 153, 51, 140) : Theme.Hairline),
                    Roundness = Theme.Radius
                },
                Shadow = active
                    ? new ShadowStyle(0, 3f, 6, 6, new SKColor(255, 153, 51, 90))
                    : new ShadowStyle(0, 1.5f, 3, 3, new SKColor(0, 0, 0, 70))
            };

            _thumb = new GalleryThumbWell(doc.Preview ?? doc.Thumb)
            {
                Name = $"{Name}_Thumb",
                IsClickthrough = true
            };

            _caption = new VisualElement
            {
                Name = $"{Name}_Caption",
                Text = doc.Name,
                IsClickthrough = true,
                Style = new ElementStyle
                {
                    BackColor = SKColors.Transparent,
                    Text = new TextStyle
                    {
                        Color = active ? Theme.Accent : Theme.Text,
                        Size = 12,
                        Weight = 500,
                        Alignment = TextAlign.Center,
                        Padding = 4
                    }
                }
            };

            _readyBtn = new VisualElement
            {
                Name = $"{Name}_Ready",
                Cursor = StandardCursor.Hand,
                BackgroundImageScale = ImageScaleMode.Contain,
                BackgroundImageTintBlendMode = SKBlendMode.SrcIn,
                BackgroundImageTintColor = SKColors.White,
                BackgroundSvg = _isReady ? IconStore.LoadSvg("check") : null,
                Style = new ElementStyle
                {
                    BackColor = _isReady ? Theme.Accent : new SKColor(0, 0, 0, 170),
                    Border = new BorderStyle
                    {
                        Width = 1,
                        Color = _isReady ? Theme.Accent : Theme.HairlineSubtle,
                        Roundness = 4
                    }
                }
            };
            _readyBtn.Events.OnClick += (_, args) =>
            {
                if (args.Button != (int)MouseButton.Left) return;
                _isReady = !_isReady;
                Doc.IsReady = _isReady;
                UpdateReadyStyle();
                _owner.OnCardReadyToggled(Index, _isReady);
                args.Handled = true;
            };

            _indexBadge = new VisualElement
            {
                Name = $"{Name}_Badge",
                Text = $"#{index + 1}",
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
                    Style.Border.Color = _isReady ? new SKColor(255, 153, 51, 140) : Theme.Hairline;
                    InvalidatePaint();
                }
            };

            Events.OnClick += (_, args) =>
            {
                if (args.Button != (int)MouseButton.Left) return;
                var now = DateTime.UtcNow;
                if (now - _lastClickTime < DoubleClickThreshold)
                {
                    _lastClickTime = DateTime.MinValue;
                    _owner.OnCardDoubleClicked(Index);
                }
                else
                {
                    _lastClickTime = now;
                    _owner.OnCardClicked(Index);
                }
                args.Handled = true;
            };
        }

        public void SetActive(bool active)
        {
            _active = active;
            Style.BackColor = active ? Theme.Selected : Theme.Section;
            Style.Border.Width = active ? 2f : 1f;
            Style.Border.Color = active ? Theme.Accent : (_isReady ? new SKColor(255, 153, 51, 140) : Theme.Hairline);
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

        public void RefreshThumb()
        {
            _thumb.SetImage(Doc.Preview ?? Doc.Thumb);
        }

        private void UpdateReadyStyle()
        {
            _readyBtn.BackgroundSvg = _isReady ? IconStore.LoadSvg("check") : null;
            _readyBtn.Style.BackColor = _isReady ? Theme.Accent : new SKColor(0, 0, 0, 170);
            _readyBtn.Style.Border.Color = _isReady ? Theme.Accent : Theme.HairlineSubtle;
            _readyBtn.InvalidatePaint();
            if (!_active)
            {
                Style.Border.Color = _isReady ? new SKColor(255, 153, 51, 140) : Theme.Hairline;
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
            _indexBadge.Transform.SetAbsoluteFrame(ox + w - pad - 34f, oy + pad + 4f, 30f, 20f);
        }
    }

    private sealed class GalleryThumbWell : VisualElement
    {
        private SKImage? _image;

        public GalleryThumbWell(SKImage? image)
        {
            _image = image;
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
            if (_image == null || _image.Handle == IntPtr.Zero) return;
            float w = Transform.Computed.Width;
            float h = Transform.Computed.Height;
            var dest = Contain(w, h, _image.Width, _image.Height);
            cmds.Add(new DrawSkImageCommand(_image, dest));
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
