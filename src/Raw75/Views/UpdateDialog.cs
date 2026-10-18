using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Blossom;
using Blossom.Core.Input;
using Blossom.Core.Visual;
using Blossom.Primitives;
using Blossom.Desktop;
using Blossom.Reactive;
using Raw75.Controls;
using Raw75.Update;

namespace Raw75.Views;

/// <summary>
/// Offers an available release: what it brings (its GitHub release notes), then downloads, installs and asks the
/// workspace to restart into it. Development builds can't install themselves and get the release page instead.
/// </summary>
public sealed partial class UpdateDialog : ModalDialog
{
    private const int MaxNoteLines = 8;

    private enum Stage { Ready, Downloading, Installing, Restarting, Installed, Failed }

    private readonly Signal<Stage> _stage = new(Stage.Ready);
    private readonly Signal<float> _progress = new(0f);
    private readonly Signal<string?> _error = new(null);
    private readonly Signal<bool> _busy = new(false);
    private readonly RichBox _subtitle;
    private readonly Stack _notes;
    private UpdateInfo? _update;
    private CancellationTokenSource? _cts;

    /// <summary>Raised once the new version is in place; the workspace saves its state and restarts.</summary>
    public event Action? RestartRequested;

    public UpdateDialog() : base("UpdateDialog", zIndex: 1150, cardWidth: 460f)
    {
        bool canInstall = SelfUpdate.PackageName is not null;

        var later = new IconButton("Later");
        later.Clicked += Close;
        later.Bind(b =>
        {
            b.Visible = !_busy.Value;
            b.Caption = _stage.Value == Stage.Installed ? "Close" : "Later";
        });

        var cancel = new IconButton("Cancel");
        cancel.Clicked += () => _cts?.Cancel();
        cancel.Bind(b => b.Visible = _stage.Value == Stage.Downloading);

        var go = new IconButton(canInstall ? "Update" : "Open release page", canInstall ? "download" : "export", primary: true);
        go.Clicked += () =>
        {
            if (canInstall) StartUpdate();
            else OpenReleasePage();
        };
        go.Bind(b =>
        {
            b.Visible = !_busy.Value && _stage.Value != Stage.Installed;
            b.Caption = _stage.Value == Stage.Failed ? "Retry" : canInstall ? "Update" : "Open release page";
        });

        var status = Label("", TextRole.Dim);
        status.Bind(l =>
        {
            l.Text = _stage.Value switch
            {
                Stage.Downloading => $"Downloading… {_progress.Value * 100f:0}%",
                Stage.Installing => "Installing…",
                Stage.Restarting => "Restarting Raw75…",
                Stage.Installed => "Installed. Restart Raw75 to use the new version.",
                Stage.Failed => _error.Value ?? "The update failed.",
                _ => canInstall ? "Raw75 restarts once the update is installed." : "This build can't update itself: get the new release from GitHub.",
            };
            l.Style.Text.Color = _stage.Value == Stage.Failed ? Theme.RedChannel : RoleColor(TextRole.Dim);
        });

        _notes = new Stack
        {
            Name = "UpdateNotes",
            Orientation = Orientation.Vertical,
            Gap = 4f
        };

        var body = Body(gap: 8f);
        body.AddChild(Label("What's new", TextRole.Section));
        body.AddChild(_notes);
        var statusRow = new Stack { Name = "UpdateStatus", Padding = new Thickness(0, 8, 0, 0) };
        statusRow.AddChild(status);
        body.AddChild(statusRow);

        Card.AddChild(Header("Update available", "", out _subtitle));
        Card.AddChild(Separator());
        Card.AddChild(body);
        Card.AddChild(new ProgressLine(_progress, _busy));
        Card.AddChild(Footer(null, cancel, later, go));
    }

    // Never dismissed by accident while files are being replaced.
    protected override bool DismissOnScrim => !_busy.Peek();

    protected override bool HandleKey(UiKey key) => !_busy.Peek() && base.HandleKey(key);

    public void Open(UpdateInfo update)
    {
        _update = update;
        _subtitle.Text = $"Raw75 {update.Version} is out — you have {AppVersion.Current}.";
        _stage.Value = Stage.Ready;
        _progress.Value = 0f;

        foreach (var child in new List<VisualElement>(_notes.Children))
        {
            _notes.RemoveChild(child);
            child.Dispose();
        }
        var lines = NoteLines(update.Notes);
        if (lines.Count == 0)
            lines.Add("See the release page for the full list of changes.");
        foreach (string line in lines)
            _notes.AddChild(Label(line, TextRole.Body));

        ShowModal();
        InvalidateLayout();
    }

    public override void Close()
    {
        if (_busy.Peek()) return;
        base.Close();
    }

    private async void StartUpdate()
    {
        if (_update is not { } update || _busy.Peek()) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        ReactiveEngine.Batch(() =>
        {
            _busy.Value = true;
            _progress.Value = 0f;
            _stage.Value = Stage.Downloading;
        });

        using UpdateInstaller installer = SelfUpdate.CreateInstaller();
        try
        {
            if (installer.CheckInstallable() is { } why)
                throw new UpdateException(why);

            var progress = new Progress<double>(p => Shell.Post(() => _progress.Value = (float)p));
            string staged = await Task.Run(() => installer.DownloadAsync(update, progress, ct), ct);

            _stage.Value = Stage.Installing;
            await Task.Run(() => installer.Apply(staged));

            _stage.Value = Stage.Restarting;
            Log.Info($"Updated to {update.Version}; restarting.");
            RestartRequested?.Invoke();
        }
        catch (OperationCanceledException)
        {
            installer.DeleteStaging();
            ReactiveEngine.Batch(() =>
            {
                _busy.Value = false;
                _stage.Value = Stage.Ready;
            });
        }
        catch (Exception ex)
        {
            Log.Error($"Update failed: {ex}");
            ReactiveEngine.Batch(() =>
            {
                _busy.Value = false;
                _error.Value = ex is UpdateException ? ex.Message : $"The update failed: {ex.Message}";
                _stage.Value = Stage.Failed;
            });
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>The files are in place but the new process could not be started: ask for a manual restart.</summary>
    public void RestartFailed()
    {
        ReactiveEngine.Batch(() =>
        {
            _busy.Value = false;
            _stage.Value = Stage.Installed;
        });
    }

    private void OpenReleasePage()
    {
        if (_update != null)
            SelfUpdate.OpenUrl(_update.PageUrl);
        Close();
    }

    /// <summary>
    /// GitHub's generated release notes as short plain lines: bullets become lines ("Gallery refresh (#7)"),
    /// links become their text, and the contributor / full-changelog footer is dropped.
    /// </summary>
    internal static List<string> NoteLines(string markdown)
    {
        var lines = new List<string>();
        foreach (string raw in (markdown ?? "").Replace("\r", "").Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("## New Contributors", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("**Full Changelog**", StringComparison.OrdinalIgnoreCase))
                break;
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            line = Bullet().Replace(line, "");
            line = PullRef().Replace(line, " (#$1)");
            line = Link().Replace(line, "$1");
            line = line.Replace("**", "").Replace("`", "").Trim();
            if (line.Length == 0) continue;
            lines.Add("•  " + line);
            if (lines.Count == MaxNoteLines)
            {
                lines.Add("…and more on the release page.");
                break;
            }
        }
        return lines;
    }

    [GeneratedRegex(@"^[*\-+]\s+")]
    private static partial Regex Bullet();

    // " by @user in https://github.com/owner/repo/pull/12" -> " (#12)"
    [GeneratedRegex(@"\s+by\s+@[\w-]+\s+in\s+https://github\.com/\S+/pull/(\d+)")]
    private static partial Regex PullRef();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex Link();
}
