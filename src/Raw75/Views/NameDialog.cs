using System;
using Blossom.Core;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Raw75.Controls;
using Silk.NET.Input;
using SkiaSharp;

namespace Raw75.Views;

/// <summary>Small modal to name a preset.</summary>
public sealed class NameDialog : VisualElement
{
    private readonly VisualElement _card;
    private readonly VisualElement _title;
    private readonly VisualElement _field;
    private readonly IconButton _save;
    private readonly IconButton _cancel;
    private string _value = "";

    public event Action<string>? Confirmed;

    public NameDialog()
    {
        Name = "NameDialog";
        Visible = false;
        ZIndex = 1100;
        ReceivesKeyboard = true;
        Transform = new Transform(0, 0, 800, 600)
        {
            Anchor = Anchor.Left | Anchor.Right | Anchor.Top | Anchor.Bottom
        };
        Style = new ElementStyle { BackColor = new SKColor(0, 0, 0, 160) };

        _card = new VisualElement
        {
            Name = "NameCard",
            Style = new ElementStyle
            {
                BackColor = Theme.Panel,
                Border = new BorderStyle { Width = 1, Color = Theme.Hairline, Roundness = Theme.Radius }
            }
        };
        _title = new VisualElement
        {
            Name = "NameTitle",
            Text = "Preset name",
            IsClickthrough = true,
            Style = new ElementStyle
            {
                Text = new TextStyle { Color = Theme.Text, Size = 15, Weight = 700, Alignment = TextAlign.Left }
            }
        };
        _field = new VisualElement
        {
            Name = "NameField",
            Style = new ElementStyle
            {
                BackColor = Theme.Track,
                Border = new BorderStyle { Width = 1, Color = Theme.Accent, Roundness = 4 },
                Text = new TextStyle { Color = Theme.Text, Size = 14, Weight = 500, Alignment = TextAlign.Left, Padding = 8 }
            }
        };
        _save = new IconButton("Save", primary: true);
        _save.Clicked += Confirm;
        _cancel = new IconButton("Cancel");
        _cancel.Clicked += Close;

        AddChild(_card);
        _card.AddChild(_title);
        _card.AddChild(_field);
        _card.AddChild(_save);
        _card.AddChild(_cancel);

        Events.OnClick += (_, e) =>
        {
            if (!_card.Transform.Computed.RectF.Contains(e.Global.X, e.Global.Y))
            {
                e.Handled = true;
                Close();
            }
        };
        Events.OnKeyDown += k =>
        {
            if (!Visible) return;
            var key = (Key)k;
            if (key == Key.Escape) { Close(); return; }
            if (key == Key.Enter) { Confirm(); return; }
            if (key == Key.Backspace && _value.Length > 0)
            {
                _value = _value[..^1];
                SyncField();
            }
        };
        Events.OnKeyType += ch =>
        {
            if (!Visible) return;
            if (char.IsControl(ch)) return;
            if (_value.Length >= 40) return;
            if ("<>:\"/\\|?*".Contains(ch)) return;
            _value += ch;
            SyncField();
        };
    }

    public void Open(string seed)
    {
        _value = seed ?? "";
        SyncField();
        float w = ParentView?.Width ?? Transform.Width;
        float h = ParentView?.Height ?? Transform.Height;
        if (w < 1f) w = 1f;
        if (h < 1f) h = 1f;
        Transform.SetAbsoluteFrame(0, 0, w, h);
        Transform.Anchor = Anchor.Left | Anchor.Right | Anchor.Top | Anchor.Bottom;
        Visible = true;
        ParentView?.SetActiveKeyboardElement(this);
        InvalidateLayout();
        InvalidatePaint();
    }

    public void Close()
    {
        if (!Visible) return;
        if (ParentView?.ActiveKeyboardElement == this)
            ParentView.SetActiveKeyboardElement(null);
        Visible = false;
        InvalidatePaint();
    }

    private void Confirm()
    {
        string name = _value.Trim();
        if (name.Length == 0)
            return;
        Confirmed?.Invoke(name);
        Close();
    }

    private void SyncField()
    {
        _field.Text = _value.Length == 0 ? "Type a name…" : _value;
        _field.Style.Text.Color = _value.Length == 0 ? Theme.TextDim : Theme.Text;
        InvalidatePaint();
    }

    protected override void LayoutChildren()
    {
        float ox = Transform.Computed.X;
        float oy = Transform.Computed.Y;
        float w = Math.Max(1f, Transform.Computed.Width);
        float h = Math.Max(1f, Transform.Computed.Height);
        const float cw = 420f;
        const float ch = 168f;
        float x = ox + Math.Max(0, (w - cw) / 2f);
        float y = oy + Math.Max(0, (h - ch) / 2f);
        _card.Transform.SetAbsoluteFrame(x, y, cw, ch);
        _title.Transform.SetAbsoluteFrame(x + 18, y + 16, cw - 36, 24);
        _field.Transform.SetAbsoluteFrame(x + 18, y + 52, cw - 36, 36);
        _save.Transform.SetAbsoluteFrame(x + cw - 18 - 96, y + ch - 48, 96, 32);
        _cancel.Transform.SetAbsoluteFrame(x + cw - 18 - 96 - 10 - 96, y + ch - 48, 96, 32);
    }

}
