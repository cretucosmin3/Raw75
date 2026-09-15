using System;
using System.Collections.Generic;
using Blossom.Core;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Raw75.Controls;
using Silk.NET.Input;
using SkiaSharp;

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

/// <summary>Modal overlay: format, JPEG quality, long-edge size, Export / Cancel.</summary>
public sealed class ExportDialog : VisualElement
{
    private static readonly string[] Formats = { "JPEG", "PNG", "WebP", "TIFF" };
    private static readonly int[] LongEdges = { 0, 1920, 2048, 2560, 3840, 5120 };

    private readonly VisualElement _card;
    private readonly VisualElement _title;
    private readonly VisualElement _fileLabel;
    private readonly VisualElement _scopeLabel;
    private readonly IconButton[] _scopeBtns;
    private readonly VisualElement _formatLabel;
    private readonly IconButton[] _formatBtns;
    private readonly VisualElement _qualityLabel;
    private readonly QualitySlider _slider;
    private readonly VisualElement _edgeLabel;
    private readonly IconButton[] _edgeBtns;
    private readonly IconButton _exportBtn;
    private readonly IconButton _cancelBtn;

    private ExportScope _scope = ExportScope.Active;
    private string _format = "JPEG";
    private int _quality = 95;
    private int _longEdge;
    private int _nativeW;
    private int _nativeH;

    public string SuggestedName { get; private set; } = "";

    public event Action<ExportRequest>? Confirmed;

    public ExportDialog()
    {
        Name = "ExportDialog";
        Visible = false;
        ZIndex = 1000;
        ReceivesKeyboard = true;
        Transform = new Transform(0, 0, 800, 600)
        {
            Anchor = Anchor.Left | Anchor.Right | Anchor.Top | Anchor.Bottom
        };
        Style = new ElementStyle
        {
            BackColor = new SKColor(0, 0, 0, 160)
        };

        _card = Box("Card", Theme.Panel, 6f);
        _title = Label("Title", "Export Photos", Theme.Text, 18, 700, TextAlign.Left);
        _fileLabel = Label("File", "", Theme.TextDim, 13, 400, TextAlign.Left);
        _scopeLabel = Label("ScopeLbl", "Export Scope", Theme.TextDim, 12, 500, TextAlign.Left);

        _scopeBtns = new IconButton[3];
        _scopeBtns[0] = new IconButton("Active (1)");
        _scopeBtns[0].Clicked += () => SetScope(ExportScope.Active);
        _scopeBtns[1] = new IconButton("Ready (0)", "check");
        _scopeBtns[1].Clicked += () => SetScope(ExportScope.Ready);
        _scopeBtns[2] = new IconButton("All (0)");
        _scopeBtns[2].Clicked += () => SetScope(ExportScope.All);

        _formatLabel = Label("FormatLbl", "Format", Theme.TextDim, 12, 500, TextAlign.Left);
        _qualityLabel = Label("QualityLbl", "Quality  95", Theme.TextDim, 12, 500, TextAlign.Left);
        _edgeLabel = Label("EdgeLbl", "Long edge  Full", Theme.TextDim, 12, 500, TextAlign.Left);

        _formatBtns = new IconButton[Formats.Length];
        for (int i = 0; i < Formats.Length; i++)
        {
            string fmt = Formats[i];
            var btn = new IconButton(fmt);
            btn.Clicked += () => SetFormat(fmt);
            _formatBtns[i] = btn;
        }

        _slider = new QualitySlider();
        _slider.Value = _quality;
        _slider.Changed += q =>
        {
            _quality = q;
            _qualityLabel.Text = $"Quality  {q}";
        };

        _edgeBtns = new IconButton[LongEdges.Length];
        for (int i = 0; i < LongEdges.Length; i++)
        {
            int edge = LongEdges[i];
            string caption = EdgeCaption(edge);
            var btn = new IconButton(caption);
            btn.Clicked += () => SetLongEdge(edge);
            _edgeBtns[i] = btn;
        }

        _exportBtn = new IconButton("Export", "export", primary: true);
        _exportBtn.Clicked += Confirm;
        _cancelBtn = new IconButton("Cancel");
        _cancelBtn.Clicked += Close;

        AddChild(_card);
        _card.AddChild(_title);
        _card.AddChild(_fileLabel);
        _card.AddChild(_scopeLabel);
        foreach (var b in _scopeBtns)
            _card.AddChild(b);
        _card.AddChild(_formatLabel);
        foreach (var b in _formatBtns)
            _card.AddChild(b);
        _card.AddChild(_qualityLabel);
        _card.AddChild(_slider);
        _card.AddChild(_edgeLabel);
        foreach (var b in _edgeBtns)
            _card.AddChild(b);
        _card.AddChild(_exportBtn);
        _card.AddChild(_cancelBtn);

        Events.OnClick += (_, args) =>
        {
            if (!_card.Transform.Computed.RectF.Contains(args.Global.X, args.Global.Y))
            {
                args.Handled = true;
                Close();
            }
        };

        Events.OnKeyDown += k =>
        {
            if (!Visible)
                return;
            if ((Key)k == Key.Escape)
                Close();
            else if ((Key)k == Key.Enter)
                Confirm();
        };

        SetFormat("JPEG");
        SetLongEdge(0);
        SetScope(ExportScope.Active);
    }

    public void Open(string suggestedName, int nativeW = 0, int nativeH = 0, int readyCount = 0, int totalCount = 1)
    {
        SuggestedName = suggestedName ?? "";
        _nativeW = nativeW;
        _nativeH = nativeH;
        string name = string.IsNullOrWhiteSpace(SuggestedName) ? "Export photo" : SuggestedName;
        _fileLabel.Text = nativeW > 0 && nativeH > 0
            ? $"{name}  ·  {nativeW}×{nativeH}"
            : name;

        _scopeBtns[0].Caption = "Active (1)";
        _scopeBtns[1].Caption = $"Ready ({readyCount})";
        _scopeBtns[2].Caption = $"All ({totalCount})";
        SetScope(readyCount > 0 ? ExportScope.Ready : ExportScope.Active);

        SetLongEdge(_longEdge);
        CoverView();
        Visible = true;
        ParentView?.SetActiveKeyboardElement(this);
        InvalidateLayout();
        ForceLayoutSubtree();
        InvalidatePaint();
    }

    private void SetScope(ExportScope scope)
    {
        _scope = scope;
        _scopeBtns[0].Toggled = scope == ExportScope.Active;
        _scopeBtns[1].Toggled = scope == ExportScope.Ready;
        _scopeBtns[2].Toggled = scope == ExportScope.All;
        InvalidatePaint();
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

    public void Close()
    {
        if (!Visible)
            return;
        if (ParentView?.ActiveKeyboardElement == this)
            ParentView.SetActiveKeyboardElement(null);
        Visible = false;
        InvalidatePaint();
    }

    internal void Confirm()
    {
        Confirmed?.Invoke(new ExportRequest
        {
            Format = _format,
            Quality = _quality,
            LongEdge = _longEdge,
            Scope = _scope
        });
        Close();
    }

    private void SetFormat(string format)
    {
        _format = format;
        bool jpegLike = format is "JPEG" or "WebP";
        _slider.Interactive = jpegLike;
        _slider.Opacity = jpegLike ? 1f : 0.45f;
        _qualityLabel.Text = jpegLike ? $"Quality  {_quality}" : "Quality  n/a";
        for (int i = 0; i < _formatBtns.Length; i++)
            _formatBtns[i].Toggled = Formats[i] == format;
        InvalidatePaint();
    }

    private void SetLongEdge(int edge)
    {
        _longEdge = edge;
        _edgeLabel.Text = "Long edge  " + EdgeCaption(edge);
        for (int i = 0; i < _edgeBtns.Length; i++)
            _edgeBtns[i].Toggled = LongEdges[i] == edge;
        InvalidatePaint();
    }

    protected override void LayoutChildren()
    {
        float originX = Transform.Computed.X;
        float originY = Transform.Computed.Y;
        float w = Math.Max(1f, Transform.Computed.Width);
        float h = Math.Max(1f, Transform.Computed.Height);

        const float pad = 20f;
        const float cardW = 480f;
        const float cardH = 436f;
        float cardX = originX + Math.Max(0, (w - cardW) / 2f);
        float cardY = originY + Math.Max(0, (h - cardH) / 2f);
        _card.Transform.SetAbsoluteFrame(cardX, cardY, cardW, cardH);

        float x = cardX + pad;
        float y = cardY + pad;
        float inner = cardW - pad * 2f;

        _title.Transform.SetAbsoluteFrame(x, y, inner, 26f);
        y += 28f;
        _fileLabel.Transform.SetAbsoluteFrame(x, y, inner, 20f);
        y += 26f;

        _scopeLabel.Transform.SetAbsoluteFrame(x, y, inner, 18f);
        y += 22f;
        LayoutRow(_scopeBtns, x, y, inner, 32f, 6f);
        y += 42f;

        _formatLabel.Transform.SetAbsoluteFrame(x, y, inner, 18f);
        y += 22f;
        LayoutRow(_formatBtns, x, y, inner, 32f, 6f);
        y += 44f;

        _qualityLabel.Transform.SetAbsoluteFrame(x, y, inner, 18f);
        y += 22f;
        _slider.Transform.SetAbsoluteFrame(x, y, inner, 30f);
        y += 40f;

        _edgeLabel.Transform.SetAbsoluteFrame(x, y, inner, 18f);
        y += 22f;
        LayoutRow(_edgeBtns, x, y, inner, 30f, 6f);
        y += 46f;

        const float btnW = 104f;
        const float btnH = 34f;
        _exportBtn.Transform.SetAbsoluteFrame(cardX + cardW - pad - btnW, y, btnW, btnH);
        _cancelBtn.Transform.SetAbsoluteFrame(cardX + cardW - pad - btnW * 2 - 10f, y, btnW, btnH);
    }

    private static void LayoutRow(IconButton[] items, float x, float y, float width, float height, float gap)
    {
        int n = items.Length;
        float cell = n > 0 ? (width - gap * (n - 1)) / n : width;
        for (int i = 0; i < n; i++)
            items[i].Transform.SetAbsoluteFrame(x + i * (cell + gap), y, cell, height);
    }

    private string EdgeCaption(int edge)
    {
        if (edge <= 0)
            return _nativeW > 0 && _nativeH > 0 ? $"Full {_nativeW}×{_nativeH}" : "Full";
        return edge.ToString();
    }

    private static VisualElement Box(string name, SKColor fill, float round)
    {
        return new VisualElement
        {
            Name = name,
            Style = new ElementStyle
            {
                BackColor = fill,
                Border = new BorderStyle
                {
                    Width = 1,
                    Color = Theme.Hairline,
                    Roundness = round
                }
            }
        };
    }

    private static VisualElement Label(string name, string text, SKColor color, float size, int weight, TextAlign align)
    {
        return new RichBox
        {
            Name = name,
            Text = text,
            IsClickthrough = true,
            Overflow = OverflowMode.Visible,
            TextOverflow = TextOverflow.Visible,
            Style = new ElementStyle
            {
                BackColor = SKColors.Transparent,
                Text = new TextStyle
                {
                    Color = color,
                    Size = size,
                    Weight = weight,
                    Alignment = align,
                    Padding = 2,
                    Overflow = TextOverflow.Visible,
                    MaxLines = 1
                }
            }
        };
    }

    private sealed class QualitySlider : VisualElement
    {
        private int _value = 90;
        private bool _hovered;
        private bool _dragging;

        public int Value
        {
            get => _value;
            set
            {
                int v = Math.Clamp(value, 1, 100);
                if (_value == v)
                    return;
                _value = v;
                InvalidatePaint();
                Changed?.Invoke(_value);
            }
        }

        public event Action<int>? Changed;

        public QualitySlider()
        {
            Name = "QualitySlider";
            Cursor = StandardCursor.HResize;
            Style = new ElementStyle { BackColor = SKColors.Transparent };

            Events.OnMouseEnter += _ => { _hovered = true; InvalidatePaint(); };
            Events.OnMouseLeave += _ => { _hovered = false; InvalidatePaint(); };
            Events.OnMouseDown += (_, args) =>
            {
                if (args.Button != 0)
                    return;
                _dragging = true;
                CapturePointer();
                SetFromX(args.Global.X);
                args.Handled = true;
            };
            Events.OnMouseMove += (_, args) =>
            {
                if (!HasPointerCapture)
                    return;
                SetFromX(args.Global.X);
                args.Handled = true;
            };
            Events.OnMouseUp += (_, args) =>
            {
                _dragging = false;
                if (HasPointerCapture)
                    ReleasePointer();
                args.Handled = true;
                InvalidatePaint();
            };
        }

        private void SetFromX(float globalX)
        {
            var local = PointToClient(globalX, 0);
            float w = Math.Max(1f, Transform.Computed.Width);
            float thumbRadius = Theme.ThumbW * 0.5f;
            float travel = Math.Max(1f, w - Theme.ThumbW);
            float t = Math.Clamp((local.X - thumbRadius) / travel, 0f, 1f);
            Value = (int)Math.Round(t * 99f + 1f);
        }

        protected override void OnAfterStyleDraw(List<DrawCommand> cmds)
        {
            cmds.Add(new DrawCallbackCommand(canvas =>
            {
                float t = (_value - 1) / 99f;
                SliderChrome.Draw(canvas, Transform.Computed.Width, Transform.Computed.Height,
                    t, 0f, bipolar: false, _hovered, _dragging);
            }));
        }
    }
}
