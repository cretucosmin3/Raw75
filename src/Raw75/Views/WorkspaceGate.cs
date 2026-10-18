using System;
using Blossom.Core.Input;
using Blossom.Reactive;
using Raw75.Controls;
using Raw75.Io;
using SkiaSharp;

namespace Raw75.Views;

/// <summary>Blocks the develop chrome until a workspace folder is chosen.</summary>
public sealed class WorkspaceGate : ModalDialog
{
    private readonly Signal<string?> _lastRoot = new(null);

    public event Action<string>? FolderPicked;

    public WorkspaceGate() : base("WorkspaceGate", zIndex: 900, cardWidth: 460f)
    {
        var lastPath = Label("", TextRole.Body);
        lastPath.BindText(() => _lastRoot.Value ?? "");

        var last = new IconButton("Open last folder");
        last.Clicked += () =>
        {
            string? root = _lastRoot.Peek();
            if (!string.IsNullOrEmpty(root))
                FolderPicked?.Invoke(root);
        };
        var choose = new IconButton("Choose folder…", "folder", primary: true);
        choose.Clicked += Pick;

        var body = Body();
        body.AddChild(Field("Last folder", lastPath));
        var bodyRule = Separator();

        Card.AddChild(Header("Open a folder",
            "Pick the folder that holds your photos. Raw75 lists everything in it and keeps previews in .raw75 next to the files.",
            out _, subtitleLines: 2));
        Card.AddChild(Separator());
        Card.AddChild(body);
        Card.AddChild(bodyRule);
        Card.AddChild(Footer(null, last, choose));

        // The last-folder section and its shortcut only exist when a previous workspace is on record.
        this.Bind(_ =>
        {
            bool hasLast = !string.IsNullOrEmpty(_lastRoot.Value);
            body.Visible = hasLast;
            bodyRule.Visible = hasLast;
            last.Visible = hasLast;
            Card.InvalidateLayout();
            InvalidateLayout();
        });
    }

    /// <summary>The workspace behind is empty, so the gate covers it almost fully.</summary>
    protected override SKColor ScrimColor => Theme.Canvas.WithAlpha(236);

    protected override bool DismissOnScrim => false;

    public void Show()
    {
        _lastRoot.Value = WorkspaceStore.LoadLastRoot();
        ShowModal();
    }

    public void Hide() => HideModal();

    private void Pick()
    {
        string? folder = null;
        try { folder = FileDialogs.OpenFolder(); }
        catch (Exception ex) { Blossom.Log.Warning(ex.Message); }
        if (string.IsNullOrWhiteSpace(folder))
            return;
        FolderPicked?.Invoke(folder);
    }

    protected override bool HandleKey(UiKey key)
    {
        if (key is UiKey.Enter or UiKey.KeypadEnter or UiKey.O)
        {
            Pick();
            return true;
        }
        // No Escape: there is nothing to go back to until a folder is open.
        return false;
    }
}
