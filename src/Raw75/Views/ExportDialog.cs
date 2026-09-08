using System;
using System.Collections.Generic;
using Blossom.Core;
using Blossom.Core.Visual;
using Silk.NET.Input;
using SkiaSharp;

namespace Raw75.Views;

public sealed class ExportRequest
{
    public string Format = "JPEG";
    public int Quality;
    public int LongEdge;
}

/// <summary>Modal overlay: format, JPEG quality, long-edge size, Export / Cancel.</summary>
public sealed class ExportDialog : VisualElement
{
    private static readonly string[] Formats = { "JPEG", "PNG", "WebP", "TIFF" };
    private static readonly int[] LongEdges = { 0, 1920, 2048, 2560, 3840, 5120 };

    private readonly VisualElement _card;
    private readonly VisualElement _title;
    private readonly VisualElement _fileLabel;
    private readonly VisualElement _formatLabel;
    private readonly VisualElement[] _formatBtns;
    private readonly VisualElement _qualityLabel;
    private readonly QualitySlider _slider;
    private readonly VisualElement _edgeLabel;
    private readonly VisualElement[] _edgeBtns;
    private readonly VisualElement _exportBtn;
    private readonly VisualElement _cancelBtn;

    private string _format = "JPEG";
    private int _quality = 90;
    private int _longEdge;

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
        _title = Label("Title", "Export", Theme.Text, 16, 700, TextAlign.Left);
        _fileLabel = Label("File", "", Theme.TextDim, 12, 400, TextAlign.Left);
        _formatLabel = Label("FormatLbl", "Format", Theme.TextDim, 11, 500, TextAlign.Left);
        _qualityLabel = Label("QualityLbl", "Quality  90", Theme.TextDim, 11, 500, TextAlign.Left);
        _edgeLabel = Label("EdgeLbl", "Long edge  Full", Theme.TextDim, 11, 500, TextAlign.Left);

        _formatBtns = new VisualElement[Formats.Length];
        for (int i = 0; i < Formats.Length; i++)
        {
            string fmt = Formats[i];
            var btn = Chip("Fmt_" + fmt, fmt);
            btn.Events.OnClick += (_, args) =>
            {
                args.Handled = true;
                SetFormat(fmt);
            };
            _formatBtns[i] = btn;
        }

        _slider = new QualitySlider();
        _slider.Value = _quality;
        _slider.Changed += q =>
        {
            _quality = q;
            _qualityLabel.Text = $"Quality  {q}";
        };

        _edgeBtns = new VisualElement[LongEdges.Length];
        for (int i = 0; i < LongEdges.Length; i++)
        {
            int edge = LongEdges[i];
            string caption = EdgeCaption(edge);
            var btn = Chip("Edge_" + caption, caption);
            btn.Events.OnClick += (_, args) =>
            {
                args.Handled = true;
                SetLongEdge(edge);
            };
            _edgeBtns[i] = btn;
        }

        _exportBtn = Chip("Export", "Export");
        _exportBtn.Events.OnClick += (_, args) =>
        {
            args.Handled = true;
            Confirm();
        };

        _cancelBtn = Chip("Cancel", "Cancel");
        _cancelBtn.Events.OnClick += (_, args) =>
        {
            args.Handled = true;
            Close();
        };

        AddChild(_card);
        _card.AddChild(_title);
        _card.AddChild(_fileLabel);
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
        StyleExportButtons();
    }

    public void Open(string suggestedName)
    {
        SuggestedName = suggestedName ?? "";
        _fileLabel.Text = string.IsNullOrWhiteSpace(SuggestedName)
            ? "Export photo"
            : SuggestedName;
        Visible = true;
        ParentView?.SetActiveKeyboardElement(this);
        InvalidateLayout();
        ForceLayoutSubtree();
        InvalidatePaint();
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

    private void Confirm()
    {
        Confirmed?.Invoke(new ExportRequest
        {
            Format = _format,
            Quality = _quality,
            LongEdge = _longEdge
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
            ApplyChip(_formatBtns[i], Formats[i] == format);
        InvalidatePaint();
    }

    private void SetLongEdge(int edge)
    {
        _longEdge = edge;
        _edgeLabel.Text = "Long edge  " + EdgeCaption(edge);
        for (int i = 0; i < _edgeBtns.Length; i++)
            ApplyChip(_edgeBtns[i], LongEdges[i] == edge);
        InvalidatePaint();
    }

    private void StyleExportButtons()
    {
        _exportBtn.Style.BackColor = Theme.Accent;
        _exportBtn.Style.Text.Color = SKColors.White;
        _exportBtn.Style.Border.Color = Theme.Accent;
        _cancelBtn.Style.BackColor = Theme.PanelAlt;
        _cancelBtn.Style.Text.Color = Theme.Text;
        _cancelBtn.Style.Border.Color = Theme.Hairline;
    }

    protected override void LayoutChildren()
    {
        float originX = Transform.Computed.X;
        float originY = Transform.Computed.Y;
        float w = Math.Max(1f, Transform.Computed.Width);
        float h = Math.Max(1f, Transform.Computed.Height);

        const float pad = 18f;
        const float cardW = 440f;
        const float cardH = 332f;
        float cardX = originX + Math.Max(0, (w - cardW) / 2f);
        float cardY = originY + Math.Max(0, (h - cardH) / 2f);
        _card.Transform.SetAbsoluteFrame(cardX, cardY, cardW, cardH);

        float x = cardX + pad;
        float y = cardY + pad;
        float inner = cardW - pad * 2f;

        _title.Transform.SetAbsoluteFrame(x, y, inner, 24f);
        y += 26f;
        _fileLabel.Transform.SetAbsoluteFrame(x, y, inner, 18f);
        y += 26f;

        _formatLabel.Transform.SetAbsoluteFrame(x, y, inner, 16f);
        y += 20f;
        LayoutRow(_formatBtns, x, y, inner, 28f, 6f);
        y += 40f;

        _qualityLabel.Transform.SetAbsoluteFrame(x, y, inner, 16f);
        y += 20f;
        _slider.Transform.SetAbsoluteFrame(x, y, inner, 22f);
        y += 34f;

        _edgeLabel.Transform.SetAbsoluteFrame(x, y, inner, 16f);
        y += 20f;
        LayoutRow(_edgeBtns, x, y, inner, 26f, 5f);
        y += 44f;

        const float btnW = 96f;
        const float btnH = 32f;
        _exportBtn.Transform.SetAbsoluteFrame(cardX + cardW - pad - btnW, y, btnW, btnH);
        _cancelBtn.Transform.SetAbsoluteFrame(cardX + cardW - pad - btnW * 2 - 10f, y, btnW, btnH);
    }

    private static void LayoutRow(VisualElement[] items, float x, float y, float width, float height, float gap)
    {
        int n = items.Length;
        float cell = n > 0 ? (width - gap * (n - 1)) / n : width;
        for (int i = 0; i < n; i++)
            items[i].Transform.SetAbsoluteFrame(x + i * (cell + gap), y, cell, height);
    }

    private static string EdgeCaption(int edge) => edge <= 0 ? "Full" : edge.ToString();

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
        return new VisualElement
        {
            Name = name,
            Text = text,
            IsClickthrough = true,
            Style = new ElementStyle
            {
                BackColor = SKColors.Transparent,
                Text = new TextStyle
                {
                    Color = color,
                    Size = size,
                    Weight = weight,
                    Alignment = align,
                    Padding = 2
                }
            }
        };
    }

    private static VisualElement Chip(string name, string text)
    {
        var el = new VisualElement
        {
            Name = name,
            Text = text,
            Cursor = StandardCursor.Hand,
            Style = new ElementStyle
            {
                BackColor = Theme.PanelAlt,
                Border = new BorderStyle
                {
                    Width = 1,
                    Color = Theme.Hairline,
                    Roundness = 4
                },
                Text = new TextStyle
                {
                    Color = Theme.Text,
                    Size = 12,
                    Weight = 500,
                    Alignment = TextAlign.Center,
                    Padding = 0
                }
            }
        };
        return el;
    }

    private static void ApplyChip(VisualElement el, bool selected)
    {
        el.Style.BackColor = selected ? Theme.Selected : Theme.PanelAlt;
        el.Style.Border.Color = selected ? Theme.Accent : Theme.Hairline;
        el.Style.Text.Color = selected ? Theme.Text : Theme.TextDim;
    }

    private sealed class QualitySlider : VisualElement
    {
        private int _value = 90;

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
            Style = new ElementStyle
            {
                BackColor = Theme.Track,
                Border = new BorderStyle
                {
                    Width = 1,
                    Color = Theme.Hairline,
                    Roundness = 4
                }
            };

            Events.OnMouseDown += (_, args) =>
            {
                if (args.Button != 0)
                    return;
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
                if (!HasPointerCapture)
                    return;
                ReleasePointer();
                args.Handled = true;
            };
        }

        private void SetFromX(float globalX)
        {
            var local = PointToClient(globalX, 0);
            float w = Math.Max(1f, Transform.Computed.Width);
            Value = (int)Math.Round(Math.Clamp(local.X / w, 0f, 1f) * 99f + 1f);
        }

        protected override void OnAfterStyleDraw(List<DrawCommand> cmds)
        {
            cmds.Add(new DrawCallbackCommand(canvas =>
            {
                float w = Transform.Computed.Width;
                float h = Transform.Computed.Height;
                float t = (_value - 1) / 99f;
                float fillW = Math.Max(2f, w * t);
                using var fill = new SKPaint { Color = Theme.TrackFill, IsAntialias = true };
                canvas.DrawRect(new SKRect(0, 0, fillW, h), fill);
                using var handle = new SKPaint { Color = Theme.Handle, IsAntialias = true };
                float hx = Math.Clamp(fillW, 4f, w - 4f);
                canvas.DrawRect(new SKRect(hx - 3f, 2f, hx + 3f, h - 2f), handle);
            }));
        }
    }
}
