using System;
using Blossom.Core;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using SkiaSharp;

namespace Raw75.Controls;

/// <summary>
/// Right-click popup: toggle Ready and Favorite on a gallery / filmstrip photo.
/// </summary>
public sealed class PhotoMarkMenu : VisualElement
{
    private const float CardW = 168f;
    private const float CardH = 84f;
    private const float BtnH = 32f;
    private const float Pad = 8f;

    private readonly VisualElement _card;
    private readonly IconButton _btnReady;
    private readonly IconButton _btnFavorite;

    private int _index = -1;

    public event Action<int, bool>? ReadyToggled;
    public event Action<int, bool>? FavoriteToggled;

    public PhotoMarkMenu()
    {
        Name = "PhotoMarkMenu";
        Visible = false;
        ZIndex = 1200;
        Style = new ElementStyle { BackColor = SKColors.Transparent };

        _card = new VisualElement
        {
            Name = "PhotoMarkMenu_Card",
            Style = new ElementStyle
            {
                BackColor = Theme.Overlay,
                Border = new BorderStyle
                {
                    Width = 1,
                    Color = Theme.HairlineStrong,
                    Roundness = Theme.Radius
                },
                Shadow = new ShadowStyle(0, 4f, 10, 10, new SKColor(0, 0, 0, 160))
            }
        };

        _btnReady = new IconButton("Ready", "check");
        _btnFavorite = new IconButton("⭐ Favorite", "star");

        _btnReady.Clicked += () =>
        {
            bool next = !_btnReady.Toggled;
            ReadyToggled?.Invoke(_index, next);
            Close();
        };
        _btnFavorite.Clicked += () =>
        {
            bool next = !_btnFavorite.Toggled;
            FavoriteToggled?.Invoke(_index, next);
            Close();
        };

        _card.AddChild(_btnReady);
        _card.AddChild(_btnFavorite);
        AddChild(_card);

        Events.OnMouseDown += (_, args) =>
        {
            Close();
            args.Handled = true;
        };
        Events.OnClick += (_, args) =>
        {
            Close();
            args.Handled = true;
        };
        _card.Events.OnClick += (_, args) =>
        {
            args.Handled = true;
        };
    }

    public void Open(int index, bool ready, bool favorite, float x, float y, float viewW, float viewH)
    {
        _index = index;
        _btnReady.Toggled = ready;
        _btnReady.Caption = ready ? "Unmark Ready" : "Mark Ready";
        _btnFavorite.Toggled = favorite;
        _btnFavorite.Caption = favorite ? "Unstar" : "⭐ Favorite";
        _btnFavorite.SetIcon(favorite ? "star_filled" : "star");

        float cardX = Math.Clamp(x, 8f, Math.Max(8f, viewW - CardW - 8f));
        float cardY = Math.Clamp(y, 8f, Math.Max(8f, viewH - CardH - 8f));

        Transform.SetAbsoluteFrame(0, 0, viewW, viewH);
        _card.Transform.SetAbsoluteFrame(cardX, cardY, CardW, CardH);
        LayoutCard();
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

    protected override void LayoutChildren()
    {
        LayoutCard();
    }

    private void LayoutCard()
    {
        float ox = _card.Transform.Computed.X;
        float oy = _card.Transform.Computed.Y;
        float innerW = CardW - Pad * 2f;
        _btnReady.Transform.SetAbsoluteFrame(ox + Pad, oy + Pad, innerW, BtnH);
        _btnFavorite.Transform.SetAbsoluteFrame(ox + Pad, oy + Pad + BtnH + 4f, innerW, BtnH);
    }
}
