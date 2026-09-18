using System;
using Blossom.Core;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Raw75.Controls;
using Raw75.Io;
using Silk.NET.Input;
using SkiaSharp;

namespace Raw75.Views;

/// <summary>Blocks the develop chrome until a workspace folder is chosen.</summary>
public sealed class WorkspaceGate : VisualElement
{
    private readonly VisualElement _card;
    private readonly VisualElement _title;
    private readonly VisualElement _body;
    private readonly IconButton _choose;
    private readonly IconButton _last;
    private readonly VisualElement _lastPath;
    private string? _lastRoot;

    public event Action<string>? FolderPicked;

    public WorkspaceGate()
    {
        Name = "WorkspaceGate";
        Visible = false;
        ZIndex = 900;
        ReceivesKeyboard = true;
        Transform = new Transform(0, 0, 800, 600)
        {
            Anchor = Anchor.Left | Anchor.Right | Anchor.Top | Anchor.Bottom
        };
        Style = new ElementStyle { BackColor = new SKColor(10, 10, 12, 230) };

        _card = new VisualElement
        {
            Name = "GateCard",
            Style = new ElementStyle
            {
                BackColor = Theme.Panel,
                Border = new BorderStyle { Width = 1, Color = Theme.Hairline, Roundness = Theme.Radius }
            }
        };
        _title = Label("GateTitle", "Open a folder", Theme.Text, 20, 700);
        _body = Label("GateBody",
            "Pick the folder that holds your photos. Raw75 will list everything in it and keep previews in .raw75 next to the files.",
            Theme.TextSecondary, 13.5f, 400);
        _choose = new IconButton("Choose folder", primary: true);
        _choose.Clicked += Pick;
        _last = new IconButton("Open last folder");
        _last.Clicked += () =>
        {
            if (!string.IsNullOrEmpty(_lastRoot))
                FolderPicked?.Invoke(_lastRoot);
        };
        _lastPath = Label("GateLast", "", Theme.TextDim, 12, 400);

        AddChild(_card);
        _card.AddChild(_title);
        _card.AddChild(_body);
        _card.AddChild(_choose);
        _card.AddChild(_last);
        _card.AddChild(_lastPath);

        Events.OnKeyDown += k =>
        {
            if (!Visible)
                return;
            if ((Key)k == Key.Enter || (Key)k == Key.O)
                Pick();
        };
    }

    public void RefreshTheme()
    {
        if (_card.Style != null)
        {
            _card.Style.BackColor = Theme.Panel;
            if (_card.Style.Border != null)
            {
                _card.Style.Border.Color = Theme.Hairline;
                _card.Style.Border.Roundness = Theme.Radius;
            }
        }
        if (_title.Style?.Text != null) _title.Style.Text.Color = Theme.Text;
        if (_body.Style?.Text != null) _body.Style.Text.Color = Theme.TextSecondary;
        if (_lastPath.Style?.Text != null) _lastPath.Style.Text.Color = Theme.TextDim;
        InvalidatePaint();
    }

    public void Show()
    {
        _lastRoot = WorkspaceStore.LoadLastRoot();
        bool hasLast = !string.IsNullOrEmpty(_lastRoot);
        _last.Visible = hasLast;
        _lastPath.Visible = hasLast;
        _lastPath.Text = hasLast ? _lastRoot! : "";
        CoverView();
        Visible = true;
        ParentView?.SetActiveKeyboardElement(this);
        InvalidateLayout();
        ForceLayoutSubtree();
        InvalidatePaint();
    }

    public void Hide()
    {
        if (!Visible)
            return;
        if (ParentView?.ActiveKeyboardElement == this)
            ParentView.SetActiveKeyboardElement(null);
        Visible = false;
        InvalidatePaint();
    }

    internal void Pick()
    {
        string? folder = null;
        try { folder = FileDialogs.OpenFolder(); }
        catch (Exception ex) { Blossom.Log.Warning(ex.Message); }
        if (string.IsNullOrWhiteSpace(folder))
            return;
        FolderPicked?.Invoke(folder);
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
        const float cw = 500f;
        const float ch = 264f;
        float x = ox + Math.Max(0, (w - cw) / 2f);
        float y = oy + Math.Max(0, (h - ch) / 2f);
        _card.Transform.SetAbsoluteFrame(x, y, cw, ch);
        _title.Transform.SetAbsoluteFrame(x + 24, y + 22, cw - 48, 28);
        _body.Transform.SetAbsoluteFrame(x + 24, y + 58, cw - 48, 56);
        _choose.Transform.SetAbsoluteFrame(x + 24, y + 130, 184, 36);
        _last.Transform.SetAbsoluteFrame(x + 24 + 184 + 12, y + 130, 184, 36);
        _lastPath.Transform.SetAbsoluteFrame(x + 24, y + 184, cw - 48, 40);
    }

    private static VisualElement Label(string name, string text, SKColor color, float size, int weight)
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
                    Alignment = TextAlign.Left,
                    Padding = 0,
                    Overflow = TextOverflow.Ellipsis,
                    MaxLines = name == "GateBody" ? 3 : 1
                }
            }
        };
    }

}
