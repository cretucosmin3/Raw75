using System;
using System.Collections.Generic;
using Blossom.Core;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Raw75.Develop;
using Silk.NET.Input;
using SkiaSharp;

namespace Raw75.Controls;

/// <summary>Horizontal session strip: thumbs, names, click to select, optional close.</summary>
public class Filmstrip : VisualElement
{
    private const float ThumbW = 84f;
    private const float Gap = 6f;
    private const float NameH = 16f;

    private readonly List<Cell> _cells = new();

    public event Action<int>? Selected;
    public event Action<int>? CloseRequested;

    public Filmstrip()
    {
        Name = "Filmstrip";
        OverflowX = OverflowMode.Clip;
        OverflowY = OverflowMode.Clip;
        Padding = new Thickness(8, 6, 8, 6);
        Style = new ElementStyle
        {
            BackColor = Theme.Filmstrip,
            Border = new BorderStyle
            {
                Width = 0,
                Color = Theme.Hairline,
                Roundness = 0
            }
        };
    }

    public void Bind(IReadOnlyList<PhotoDocument> docs, int activeIndex)
    {
        docs ??= Array.Empty<PhotoDocument>();

        var previous = _cells.ToArray();
        _cells.Clear();
        ClearChildren();
        for (int i = 0; i < previous.Length; i++)
            previous[i].Dispose();

        for (int i = 0; i < docs.Count; i++)
        {
            var cell = new Cell(this, i, docs[i], i == activeIndex);
            _cells.Add(cell);
            AddChild(cell);
        }

        InvalidateLayout();
        InvalidatePaint();
    }

    public override SKSize GetPreferredSize(float maxWidth, float maxHeight)
    {
        float w = maxWidth > 0 ? maxWidth : (Transform.Width > 0 ? Transform.Width : 400f);
        float h = Theme.FilmH;
        if (maxHeight > 0) h = Math.Min(h, maxHeight);
        return new SKSize(w, h);
    }

    protected override void LayoutChildren()
    {
        float originX = Transform.Computed.X;
        float originY = Transform.Computed.Y;
        float h = Math.Max(1f, Transform.Height);
        float cellH = Math.Max(1f, h - Padding.Vertical);
        float x = Padding.Left;

        for (int i = 0; i < _cells.Count; i++)
        {
            var cell = _cells[i];
            cell.Transform.SetAbsoluteFrame(originX + x, originY + Padding.Top, ThumbW, cellH);
            x += ThumbW + Gap;
        }
    }

    private void OnCellSelected(int index)
    {
        for (int i = 0; i < _cells.Count; i++)
            _cells[i].SetActive(i == index);
        Selected?.Invoke(index);
    }

    private sealed class Cell : VisualElement
    {
        private readonly Filmstrip _owner;
        private readonly int _index;
        private readonly ThumbWell _thumb;
        private readonly VisualElement _caption;
        private readonly VisualElement _close;

        public Cell(Filmstrip owner, int index, PhotoDocument doc, bool active)
        {
            _owner = owner;
            _index = index;
            Name = $"FilmstripCell_{index}";
            Cursor = StandardCursor.Hand;
            Style = new ElementStyle
            {
                BackColor = active ? Theme.Selected : SKColors.Transparent,
                Border = new BorderStyle
                {
                    Width = 1,
                    Color = active ? Theme.Accent : Theme.Hairline,
                    Roundness = Theme.RadiusSm
                }
            };

            _thumb = new ThumbWell(doc.Thumb)
            {
                Name = $"{Name}_Thumb",
                IsClickthrough = true
            };

            _caption = new VisualElement
            {
                Name = $"{Name}_Name",
                Text = doc.Name,
                IsClickthrough = true,
                Style = new ElementStyle
                {
                    BackColor = SKColors.Transparent,
                    Text = new TextStyle
                    {
                        Color = Theme.TextDim,
                        Size = 10,
                        Weight = 400,
                        Alignment = TextAlign.Center,
                        Padding = 2
                    }
                }
            };

            _close = new VisualElement
            {
                Name = $"{Name}_Close",
                Text = "×",
                Style = new ElementStyle
                {
                    BackColor = new SKColor(0, 0, 0, 140),
                    Border = new BorderStyle { Width = 0, Roundness = 2 },
                    Text = new TextStyle
                    {
                        Color = Theme.Text,
                        Size = 11,
                        Weight = 600,
                        Alignment = TextAlign.Center,
                        Padding = 0
                    }
                }
            };
            _close.Cursor = StandardCursor.Hand;
            _close.Events.OnClick += (_, args) =>
            {
                if (args.Button != (int)MouseButton.Left) return;
                _owner.CloseRequested?.Invoke(_index);
                args.Handled = true;
            };

            AddChild(_thumb);
            AddChild(_caption);
            AddChild(_close);

            Events.OnClick += (_, args) =>
            {
                if (args.Button != (int)MouseButton.Left) return;
                _owner.OnCellSelected(_index);
                args.Handled = true;
            };
        }

        public void SetActive(bool active)
        {
            Style.BackColor = active ? Theme.Selected : SKColors.Transparent;
            Style.Border.Color = active ? Theme.Accent : Theme.Hairline;
            InvalidatePaint();
        }

        protected override void LayoutChildren()
        {
            float originX = Transform.Computed.X;
            float originY = Transform.Computed.Y;
            float w = Math.Max(1f, Transform.Width);
            float h = Math.Max(1f, Transform.Height);
            float thumbH = Math.Max(8f, h - NameH - 4f);

            _thumb.Transform.SetAbsoluteFrame(originX + 4, originY + 4, w - 8, thumbH);
            _caption.Transform.SetAbsoluteFrame(originX + 2, originY + 4 + thumbH, w - 4, NameH);
            _close.Transform.SetAbsoluteFrame(originX + w - 16, originY + 4, 12, 12);
        }
    }

    private sealed class ThumbWell : VisualElement
    {
        private readonly SKImage? _image;

        public ThumbWell(SKImage? image)
        {
            _image = image;
            Style = new ElementStyle { BackColor = Theme.PhotoWell };
            Overflow = OverflowMode.Clip;
        }

        protected override void OnAfterStyleDraw(List<DrawCommand> cmds)
        {
            if (_image == null || _image.Handle == IntPtr.Zero) return;
            float w = Transform.Computed.Width;
            float h = Transform.Computed.Height;
            var dest = Contain(w, h, _image.Width, _image.Height);
            cmds.Add(new DrawSkImageCommand(_image, dest));
        }

        private static SKRect Contain(float boxW, float boxH, int imgW, int imgH)
        {
            if (imgW <= 0 || imgH <= 0) return new SKRect(0, 0, boxW, boxH);
            float scale = Math.Min(boxW / imgW, boxH / imgH);
            float dw = imgW * scale;
            float dh = imgH * scale;
            float x = (boxW - dw) * 0.5f;
            float y = (boxH - dh) * 0.5f;
            return new SKRect(x, y, x + dw, y + dh);
        }
    }
}
