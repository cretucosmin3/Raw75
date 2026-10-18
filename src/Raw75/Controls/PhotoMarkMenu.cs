using System;
using Blossom.Animations;
using Blossom.Core;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Blossom.Primitives;
using Blossom.Reactive;
using SkiaSharp;

namespace Raw75.Controls;

/// <summary>
/// Right-click popup for a gallery / filmstrip photo: toggle Ready and Favorite, and merge the
/// selection to HDR. A floating Overlay card (a vertical <see cref="Stack"/> of 28px items) over a
/// transparent full-view catcher that closes the menu on any click outside the card.
/// </summary>
public sealed class PhotoMarkMenu : VisualElement
{
    private const float CardW = 200f;
    private const float Inset = 4f;
    private const float EdgeMargin = 8f;

    private readonly Stack _card;
    private readonly Signal<bool> _ready = new(false);
    private readonly Signal<bool> _favorite = new(false);
    private readonly Signal<bool> _showHdrMerge = new(false);

    private int _index = -1;
    private float _cardX;
    private float _cardY;

    public event Action<int, bool>? ReadyToggled;
    public event Action<int, bool>? FavoriteToggled;
    public event Action? HdrMergeRequested;

    public PhotoMarkMenu()
    {
        Name = "PhotoMarkMenu";
        Visible = false;
        ZIndex = 1200;
        Style = new ElementStyle { BackColor = SKColors.Transparent };

        _card = new Stack
        {
            Name = "PhotoMarkMenu_Card",
            Orientation = Orientation.Vertical,
            Padding = new Thickness(Inset),
            Style = new ElementStyle
            {
                Border = new BorderStyle { Width = 1 },
                Shadow = new ShadowStyle(0, 8f, 18f, 18f, SKColors.Transparent)
            }
        };

        var ready = new MenuItem("PhotoMarkMenu_Ready");
        ready.Bind(i =>
        {
            bool on = _ready.Value;
            i.Set(on ? "Unmark Ready" : "Mark Ready", "check", on ? Theme.Success : null, on ? "X" : "P");
        });
        ready.Clicked += () => Pick(() => ReadyToggled?.Invoke(_index, !_ready.Peek()));

        var favorite = new MenuItem("PhotoMarkMenu_Favorite");
        favorite.Bind(i =>
        {
            bool on = _favorite.Value;
            i.Set(on ? "Remove from Favorites" : "Add to Favorites", on ? "heart_filled" : "heart", on ? Theme.Favorite : null, null);
        });
        favorite.Clicked += () => Pick(() => FavoriteToggled?.Invoke(_index, !_favorite.Peek()));

        var separator = new MenuSeparator();
        separator.BindVisible(() => _showHdrMerge.Value);

        var hdr = new MenuItem("PhotoMarkMenu_HdrMerge");
        hdr.Set("Merge HDR…", "stack", null, "Ctrl+M");
        hdr.BindVisible(() => _showHdrMerge.Value);
        hdr.Clicked += () => Pick(() => HdrMergeRequested?.Invoke());

        _card.AddChild(ready);
        _card.AddChild(favorite);
        _card.AddChild(separator);
        _card.AddChild(hdr);
        AddChild(_card);

        // Any press outside the card dismisses; presses on the card's own padding do not.
        Events.OnMouseDown += (_, args) =>
        {
            Close();
            args.Handled = true;
        };
        Events.OnClick += (_, args) => args.Handled = true;
        _card.Events.OnMouseDown += (_, args) => args.Handled = true;
        _card.Events.OnClick += (_, args) => args.Handled = true;

        this.Bind(_ => ApplyTheme());
    }

    public void Open(int index, bool ready, bool favorite, float x, float y, float viewW, float viewH, bool showHdrMerge = false)
    {
        _index = index;
        ReactiveEngine.Batch(() =>
        {
            _ready.Value = ready;
            _favorite.Value = favorite;
            _showHdrMerge.Value = showHdrMerge;
        });

        float h = _card.GetPreferredSize(CardW, 0f).Height;
        _cardX = Math.Clamp(x, EdgeMargin, Math.Max(EdgeMargin, viewW - CardW - EdgeMargin));
        _cardY = Math.Clamp(y, EdgeMargin, Math.Max(EdgeMargin, viewH - h - EdgeMargin));

        Transform.SetAbsoluteFrame(0, 0, viewW, viewH);
        Visible = true;
        InvalidateLayout();
        InvalidatePaint();
    }

    public void Close()
    {
        if (!Visible) return;
        Visible = false;
        _index = -1;
        InvalidatePaint();
    }

    private void Pick(Action action)
    {
        action();
        Close();
    }

    private void ApplyTheme()
    {
        _card.Style.BackColor = Theme.Overlay;
        _card.Style.Border.Color = Theme.HairlineStrong;
        _card.Style.Border.Roundness = Theme.Radius;
        // Popups float well above the chrome: always a deep, soft shadow.
        if (_card.Style.Shadow != null)
            _card.Style.Shadow.Color = new SKColor(0, 0, 0, (byte)Math.Min(255, Theme.ShadowAlpha + 90));
        InvalidatePaint();
    }

    protected override void LayoutChildren()
    {
        float h = _card.GetPreferredSize(CardW, 0f).Height;
        _card.Transform.SetAbsoluteFrame(Transform.Computed.X + _cardX, Transform.Computed.Y + _cardY, CardW, h);
    }

    /// <summary>Menu row: icon, label and an optional shortcut hint, over a hover wash.</summary>
    private sealed class MenuItem : ButtonBase
    {
        private const float RowH = 28f;
        private const float IconSize = 14f;
        private const float IconX = 9f;
        private const float LabelX = 32f;

        private static readonly TextPaint LabelInk = new() { TextSize = 12f };
        private static readonly TextPaint HintInk = new() { TextSize = 11f };
        private static SKTypeface? _regular;
        private static SKTypeface Regular => _regular ??= Theme.GetTypeface(400);

        private readonly VisualElement _icon;
        private float _hover;
        private string _label = "";
        private string? _hint;
        private string? _iconName;
        private SKColor? _iconColor;

        public MenuItem(string name)
        {
            Name = name;
            ReceivesKeyboard = false;
            Style = new ElementStyle { BackColor = SKColors.Transparent };

            _icon = new VisualElement
            {
                Name = $"{name}_Icon",
                IsClickthrough = true,
                Interactive = false,
                BackgroundImageScale = ImageScaleMode.Contain,
                BackgroundImageTintBlendMode = SKBlendMode.SrcIn,
                Style = new ElementStyle { BackColor = SKColors.Transparent }
            };
            AddChild(_icon);

            // An item owns its click: the card underneath only guards against dismissal.
            Events.OnClick += (_, e) => { if (e.Button == PointerButton.Left) e.Handled = true; };

            this.Bind(_ => ApplyInk());
        }

        public void Set(string label, string iconName, SKColor? iconColor, string? hint)
        {
            _label = label;
            _hint = hint;
            _iconColor = iconColor;
            if (_iconName != iconName)
            {
                _iconName = iconName;
                _icon.BackgroundSvg = IconStore.LoadSvg(iconName);
            }
            ApplyInk();
        }

        protected override void OnHoverChanged()
        {
            _hover = IsHovered ? 1f : 0f;
            ApplyInk();
        }

        public override SKSize GetPreferredSize(float maxWidth, float maxHeight)
            => new(maxWidth > 0 ? maxWidth : CardW - Inset * 2f, RowH);

        protected override void LayoutChildren()
        {
            _icon.Transform.SetAbsoluteFrame(
                Transform.Computed.X + IconX,
                Transform.Computed.Y + (Transform.Height - IconSize) * 0.5f,
                IconSize, IconSize);
        }

        private SKColor LabelColor => Anim.Lerp(Theme.TextSecondary, Theme.Text, _hover);

        private void ApplyInk()
        {
            _icon.BackgroundImageTintColor = _iconColor ?? LabelColor;
            _icon.InvalidatePaint();
            InvalidatePaint();
        }

        protected override void Paint(SKCanvas c)
        {
            float hover = _hover;
            if (hover > 0f)
            {
                float r = Math.Max(0f, Theme.RadiusSm - 1f);
                ThumbInk.FillRound(c, SKRect.Create(0, 0, W, H), r, Theme.Hairline.WithAlpha((byte)(Theme.Hairline.Alpha * hover)));
            }

            LabelInk.Typeface = Regular;
            LabelInk.Color = LabelColor;
            float baseline = Blossom.Utils.Text.Baseline(LabelInk.Font, H * 0.5f);
            c.DrawText(_label, LabelX, baseline, LabelInk);

            if (!string.IsNullOrEmpty(_hint))
            {
                HintInk.Typeface = Regular;
                HintInk.Color = Theme.TextDim;
                HintInk.TextAlign = SKTextAlign.Right;
                c.DrawText(_hint, W - 10f, Blossom.Utils.Text.Baseline(HintInk.Font, H * 0.5f), HintInk);
            }
        }
    }

    /// <summary>Hairline rule between item groups.</summary>
    private sealed class MenuSeparator : Control
    {
        private const float SepH = 9f;

        public MenuSeparator()
        {
            Name = "PhotoMarkMenu_Separator";
            Style = new ElementStyle { BackColor = SKColors.Transparent };
            this.Bind(_ => { Theme.Observe(); InvalidatePaint(); });
        }

        public override SKSize GetPreferredSize(float maxWidth, float maxHeight) => new(maxWidth, SepH);

        protected override void Paint(SKCanvas c) =>
            ThumbInk.FillRound(c, SKRect.Create(6f, MathF.Floor(H * 0.5f), W - 12f, 1f), 0f, Theme.HairlineSubtle);
    }
}
