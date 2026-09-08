using System;
using System.Collections.Generic;
using Blossom.Core.Visual;
using Silk.NET.Input;
using SkiaSharp;

namespace Raw75.Controls;

/// <summary>Collapsible Develop-panel section: disclosure header + stacked body.</summary>
public class PanelGroup : VisualElement
{
    private readonly VisualElement _header;
    private readonly List<VisualElement> _body = new();
    private bool _expanded = true;
    private string _title;

    public bool Expanded
    {
        get => _expanded;
        set
        {
            if (_expanded == value) return;
            _expanded = value;
            ApplyExpanded();
        }
    }

    public PanelGroup(string title)
    {
        Name = $"PanelGroup_{title}";
        _title = title ?? "";
        Padding = new Thickness(14, 8, 14, 14);
        Style = new ElementStyle
        {
            BackColor = Theme.Panel,
            Border = new BorderStyle
            {
                Width = 1,
                Color = Theme.Hairline,
                Roundness = Theme.Radius
            }
        };

        _header = new VisualElement
        {
            Name = $"{Name}_Header",
            Style = new ElementStyle
            {
                BackColor = SKColors.Transparent,
                Text = new TextStyle
                {
                    Color = Theme.TextDim,
                    Size = 11,
                    Weight = 600,
                    Alignment = TextAlign.Left,
                    Padding = 10
                }
            }
        };
        _header.Cursor = StandardCursor.Hand;
        _header.Events.OnClick += (_, args) =>
        {
            if (args.Button != (int)MouseButton.Left) return;
            Expanded = !Expanded;
            args.Handled = true;
        };

        AddChild(_header);
        ApplyExpanded();
    }

    public void AddBody(VisualElement child)
    {
        if (child == null) throw new ArgumentNullException(nameof(child));
        _body.Add(child);
        child.Visible = _expanded;
        AddChild(child);
        InvalidateLayout();
    }

    public override SKSize GetPreferredSize(float maxWidth, float maxHeight)
    {
        float w = maxWidth > 0 ? maxWidth : (Transform.Width > 0 ? Transform.Width : Theme.RightW);
        float h = Theme.GroupHeadH;
        if (_expanded)
        {
            float inner = Math.Max(0, w - Padding.Horizontal);
            for (int i = 0; i < _body.Count; i++)
            {
                var child = _body[i];
                if (child == null || !child.Visible) continue;
                var size = child.GetPreferredSize(inner, 0);
                h += size.Height + Theme.SliderGap;
            }
            h += Padding.Vertical;
        }

        if (maxHeight > 0) h = Math.Min(h, maxHeight);
        return new SKSize(w, h);
    }

    protected override void LayoutChildren()
    {
        float originX = Transform.Computed.X;
        float originY = Transform.Computed.Y;
        float w = Math.Max(1f, Transform.Width);

        _header.Transform.SetAbsoluteFrame(originX, originY, w, Theme.GroupHeadH);

        if (!_expanded) return;

        float y = Theme.GroupHeadH + Padding.Top;
        float innerW = Math.Max(0, w - Padding.Horizontal);
        float childX = originX + Padding.Left;

        for (int i = 0; i < _body.Count; i++)
        {
            var child = _body[i];
            if (child == null || !child.Visible) continue;

            var preferred = child.GetPreferredSize(innerW, 0);
            float childH = preferred.Height > 0 ? preferred.Height : child.Transform.Height;
            float childW = innerW;
            child.Transform.SetAbsoluteFrame(childX, originY + y, childW, childH);
            y += childH + Theme.SliderGap;
        }
    }

    private void ApplyExpanded()
    {
        _header.Text = (_expanded ? "▾  " : "▸  ") + _title.ToUpperInvariant();
        for (int i = 0; i < _body.Count; i++)
        {
            if (_body[i] != null)
                _body[i].Visible = _expanded;
        }
        InvalidateLayout();
        InvalidatePaint();
    }
}
