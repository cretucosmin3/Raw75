using System;
using Blossom.Core;
using Blossom.Core.Input;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Blossom.Primitives;
using Blossom.Reactive;
using Raw75.Controls;
using SkiaSharp;

namespace Raw75.Views;

/// <summary>Small modal that names a preset: one text field, Cancel / Save.</summary>
public sealed class NameDialog : ModalDialog
{
    private const int MaxNameLength = 40;

    private readonly TextField _field;
    private readonly Signal<string> _name = new("");

    public event Action<string>? Confirmed;

    public NameDialog() : base("NameDialog", zIndex: 1100, cardWidth: 400f)
    {
        _field = new TextField("Preset name")
        {
            MaxLength = MaxNameLength,
            CharFilter = IsFileNameChar
        };
        _field.Changed += text => _name.Value = text;
        _field.Submitted += Confirm;
        _field.Escaped += Close;

        var cancel = new IconButton("Cancel");
        cancel.Clicked += Close;
        var save = new IconButton("Save", primary: true);
        save.Clicked += Confirm;
        save.Bind(b => b.Enabled = _name.Value.Trim().Length > 0);

        var body = Body();
        body.AddChild(Field("Name", _field));

        Card.AddChild(Header("Save preset", "Keeps the current adjustments for reuse on other photos."));
        Card.AddChild(Separator());
        Card.AddChild(body);
        Card.AddChild(Separator());
        Card.AddChild(Footer(null, cancel, save));

        // Typing after a click on the card (which takes focus off the field) still lands in the field.
        Events.OnTextInput += e =>
        {
            if (!Visible) return;
            ParentView?.SetActiveKeyboardElement(_field);
            _field.InsertText(e.Text);
            e.Handled = true;
        };
    }

    public void Open(string seed)
    {
        _field.Text = seed ?? "";
        _name.Value = _field.Text;
        ShowModal(_field);
        _field.SelectAll();
    }

    private void Confirm()
    {
        string name = _field.Text.Trim();
        if (name.Length == 0)
            return;
        Confirmed?.Invoke(name);
        Close();
    }

    protected override bool HandleKey(UiKey key)
    {
        if (key is UiKey.Enter or UiKey.KeypadEnter)
        {
            Confirm();
            return true;
        }
        return base.HandleKey(key);
    }
}
