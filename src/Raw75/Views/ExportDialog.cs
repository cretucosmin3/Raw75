using System;
using Blossom.Core.Input;
using Blossom.Core.Visual;
using Blossom.Reactive;
using Raw75.Controls;

namespace Raw75.Views;

public enum ExportScope
{
    Active,
    Ready,
    All
}

public sealed class ExportRequest
{
    public string Format = "JPEG";
    public int Quality;
    public int LongEdge;
    public ExportScope Scope = ExportScope.Active;
}

/// <summary>Modal export options: scope, format, JPEG/WebP quality, long-edge size, Export / Cancel.</summary>
public sealed class ExportDialog : ModalDialog
{
    private const int DefaultQuality = 95;
    private static readonly string[] Formats = { "JPEG", "PNG", "WebP", "TIFF" };
    private static readonly ExportScope[] Scopes = { ExportScope.Active, ExportScope.Ready, ExportScope.All };
    private static readonly int[] LongEdges = { 0, 1920, 2048, 2560, 3840, 5120 };

    /// <summary>Per-open facts the readouts describe.</summary>
    private readonly record struct Facts(int Ready, int Total, int NativeW, int NativeH);

    private readonly RichBox _subtitle;
    private readonly SliderRow _quality;

    private readonly Signal<ExportScope> _scope = new(ExportScope.Active);
    private readonly Signal<string> _format = new("JPEG");
    private readonly Signal<int> _longEdge = new(0);
    private readonly Signal<Facts> _facts = new(new Facts(0, 1, 0, 0));

    public string SuggestedName { get; private set; } = "";

    public event Action<ExportRequest>? Confirmed;

    public ExportDialog() : base("ExportDialog", zIndex: 1000, cardWidth: 460f)
    {
        var scopeValue = Label("", TextRole.Value);
        scopeValue.BindText(() => ScopeCaption(_scope.Value, _facts.Value));
        var scopeSeg = Segmented(_scope, Scopes, "Active", "Ready", "All");

        var formatSeg = Segmented(_format, Formats, Formats);
        _quality = new SliderRow("Quality", 1f, 100f, "0")
        {
            DefaultValue = DefaultQuality,
            Value = DefaultQuality
        };
        _quality.Bind(q =>
        {
            bool lossy = _format.Value is "JPEG" or "WebP";
            q.Enabled = lossy;
            q.Opacity = lossy ? 1f : 0.4f;
        });

        var edgeValue = Label("", TextRole.Value);
        edgeValue.BindText(() => EdgeCaption(_longEdge.Value, _facts.Value));
        var edgeSeg = Segmented(_longEdge, LongEdges, "Full", "1920", "2048", "2560", "3840", "5120");

        var cancel = new IconButton("Cancel");
        cancel.Clicked += Close;
        var export = new IconButton("Export", "export", primary: true);
        export.Clicked += Confirm;

        var body = Body();
        body.AddChild(ReadoutField("Photos", scopeValue, scopeSeg));
        body.AddChild(Field("Format", formatSeg, _quality));
        body.AddChild(ReadoutField("Long edge", edgeValue, edgeSeg));

        Card.AddChild(Header("Export", "", out _subtitle));
        Card.AddChild(Separator());
        Card.AddChild(body);
        Card.AddChild(Separator());
        Card.AddChild(Footer(null, cancel, export));
    }

    public void Open(string suggestedName, int nativeW = 0, int nativeH = 0, int readyCount = 0, int totalCount = 1)
    {
        SuggestedName = suggestedName ?? "";
        string name = string.IsNullOrWhiteSpace(SuggestedName) ? "Export photo" : SuggestedName;
        _subtitle.Text = nativeW > 0 && nativeH > 0 ? $"{name}  ·  {nativeW} × {nativeH}" : name;

        ReactiveEngine.Batch(() =>
        {
            _facts.Value = new Facts(readyCount, totalCount, nativeW, nativeH);
            _scope.Value = readyCount > 0 ? ExportScope.Ready : ExportScope.Active;
        });
        ShowModal();
    }

    private void Confirm()
    {
        Confirmed?.Invoke(new ExportRequest
        {
            Format = _format.Peek(),
            Quality = (int)MathF.Round(_quality.Value),
            LongEdge = _longEdge.Peek(),
            Scope = _scope.Peek()
        });
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

    private static string ScopeCaption(ExportScope scope, Facts b) => scope switch
    {
        ExportScope.Ready => b.Ready == 0 ? "None marked ready" : Photos(b.Ready),
        ExportScope.All => Photos(b.Total),
        _ => "Current photo"
    };

    private static string Photos(int n) => n == 1 ? "1 photo" : $"{n} photos";

    private static string EdgeCaption(int edge, Facts b)
    {
        if (edge > 0)
            return $"{edge} px";
        return b.NativeW > 0 && b.NativeH > 0 ? $"Original  {b.NativeW} × {b.NativeH}" : "Original size";
    }
}
