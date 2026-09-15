using System;
using Blossom;
using Blossom.Core;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Raw75.Controls;
using Silk.NET.Input;
using SkiaSharp;

namespace Raw75.Views;

/// <summary>Small modal to name a preset with active caret and text editing.</summary>
public sealed class NameDialog : VisualElement
{
    private readonly VisualElement _card;
    private readonly VisualElement _title;
    private readonly VisualElement _field;
    private readonly VisualElement _caret;
    private readonly IconButton _save;
    private readonly IconButton _cancel;

    private string _value = "";
    private int _caretIndex = 0;
    private System.Threading.Timer? _blinkTimer;
    private bool _caretVisible = true;

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
                Text = new TextStyle { Color = Theme.Text, Size = 17, Weight = 700, Alignment = TextAlign.Left }
            }
        };
        _field = new VisualElement
        {
            Name = "NameField",
            Cursor = StandardCursor.IBeam,
            Style = new ElementStyle
            {
                BackColor = Theme.Track,
                Border = new BorderStyle { Width = 1, Color = Theme.Accent, Roundness = 4 },
                Text = new TextStyle { Color = Theme.Text, Size = 15, Weight = 500, Alignment = TextAlign.Left, Padding = 10 }
            }
        };
        _caret = new VisualElement
        {
            Name = "NameCaret",
            IsClickthrough = true,
            Visible = false,
            Style = new ElementStyle
            {
                BackColor = Theme.Accent,
                Border = new BorderStyle { Width = 0, Color = SKColors.Transparent, Roundness = 1 }
            }
        };

        _save = new IconButton("Save", primary: true);
        _save.Clicked += Confirm;
        _cancel = new IconButton("Cancel");
        _cancel.Clicked += Close;

        AddChild(_card);
        _card.AddChild(_title);
        _card.AddChild(_field);
        _card.AddChild(_caret);
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

        _field.Events.OnClick += (_, e) =>
        {
            float localX = e.Global.X - (_field.Transform.Computed.X + 10f);
            _caretIndex = GetCharIndexAt(localX);
            SyncField();
        };

        Events.OnKeyDown += k =>
        {
            if (!Visible) return;
            var key = (Key)k;
            bool isCtrl = Events.IsControlDown;

            if (key == Key.Escape) { Close(); return; }
            if (key == Key.Enter || key == Key.KeypadEnter) { Confirm(); return; }

            if (key == Key.Backspace || (int)key == 259 || (int)key == 8 || (int)key == 127)
            {
                if (isCtrl && _caretIndex > 0)
                {
                    int prevWord = FindPreviousWordBoundary(_caretIndex);
                    int count = _caretIndex - prevWord;
                    _value = _value.Remove(prevWord, count);
                    _caretIndex = prevWord;
                    SyncField();
                }
                else
                {
                    Backspace();
                }
                return;
            }

            if (key == Key.Delete || (int)key == 261) { Delete(); return; }
            if (key == Key.Left) { MoveLeft(); return; }
            if (key == Key.Right) { MoveRight(); return; }
            if (key == Key.Home) { MoveHome(); return; }
            if (key == Key.End) { MoveEnd(); return; }
            if (isCtrl && key == Key.V) { HandlePaste(); return; }
            if (isCtrl && key == Key.A) { MoveEnd(); return; }
        };

        Events.OnKeyType += ch =>
        {
            if (!Visible) return;
            if (ch == '\b' || ch == (char)8 || ch == 127 || ch == (char)127)
            {
                Backspace();
                return;
            }
            if (char.IsControl(ch)) return;
            if ("<>:\"/\\|?*".Contains(ch)) return;
            InsertText(ch.ToString());
        };
    }

    public void Open(string seed)
    {
        _value = seed ?? "";
        _caretIndex = _value.Length;
        float w = ParentView?.Width ?? Transform.Width;
        float h = ParentView?.Height ?? Transform.Height;
        if (w < 1f) w = 1f;
        if (h < 1f) h = 1f;
        Transform.SetAbsoluteFrame(0, 0, w, h);
        Transform.Anchor = Anchor.Left | Anchor.Right | Anchor.Top | Anchor.Bottom;
        Visible = true;
        ParentView?.SetActiveKeyboardElement(this);
        InvalidateLayout();
        SyncField();
        StartBlink();
        InvalidatePaint();
    }

    public void Close()
    {
        if (!Visible) return;
        StopBlink();
        if (ParentView?.ActiveKeyboardElement == this)
            ParentView.SetActiveKeyboardElement(null);
        Visible = false;
        InvalidatePaint();
    }

    internal void InsertText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (_value.Length >= 40) return;

        int available = 40 - _value.Length;
        if (text.Length > available)
            text = text[..available];

        _value = _value.Insert(_caretIndex, text);
        _caretIndex += text.Length;
        SyncField();
    }

    internal void Backspace()
    {
        if (_caretIndex > 0 && _value.Length > 0)
        {
            _value = _value.Remove(_caretIndex - 1, 1);
            _caretIndex--;
            SyncField();
        }
    }

    internal void Delete()
    {
        if (_caretIndex < _value.Length)
        {
            _value = _value.Remove(_caretIndex, 1);
            SyncField();
        }
    }

    internal void MoveLeft()
    {
        if (_caretIndex > 0)
        {
            _caretIndex--;
            SyncField();
        }
    }

    internal void MoveRight()
    {
        if (_caretIndex < _value.Length)
        {
            _caretIndex++;
            SyncField();
        }
    }

    internal void MoveHome()
    {
        _caretIndex = 0;
        SyncField();
    }

    internal void MoveEnd()
    {
        _caretIndex = _value.Length;
        SyncField();
    }

    internal void Confirm()
    {
        string name = _value.Trim();
        if (name.Length == 0)
            return;
        Confirmed?.Invoke(name);
        Close();
    }

    private void HandlePaste()
    {
        try
        {
            string paste = Browser.GetClipboardText();
            if (string.IsNullOrEmpty(paste)) return;
            paste = paste.Replace("\r", "").Replace("\n", " ").Trim();
            foreach (char c in "<>:\"/\\|?*")
                paste = paste.Replace(c.ToString(), "");
            InsertText(paste);
        }
        catch { }
    }

    private int FindPreviousWordBoundary(int fromIdx)
    {
        if (fromIdx <= 0) return 0;
        int idx = fromIdx - 1;
        while (idx > 0 && char.IsWhiteSpace(_value[idx])) idx--;
        while (idx > 0 && !char.IsWhiteSpace(_value[idx - 1])) idx--;
        return Math.Max(0, idx);
    }

    private int GetCharIndexAt(float localX)
    {
        if (string.IsNullOrEmpty(_value) || localX <= 0f)
            return 0;

        using var paint = new SKPaint
        {
            Typeface = Theme.GetTypeface(500),
            TextSize = 15f,
            IsAntialias = true
        };

        float currentX = 0f;
        for (int i = 0; i < _value.Length; i++)
        {
            float charW = paint.MeasureText(_value[i].ToString());
            if (localX < currentX + charW / 2f)
                return i;
            currentX += charW;
        }
        return _value.Length;
    }

    private void SyncField()
    {
        _caretIndex = Math.Clamp(_caretIndex, 0, _value.Length);
        _field.Text = _value.Length == 0 ? "Type a name…" : _value;
        _field.Style.Text.Color = _value.Length == 0 ? Theme.TextDim : Theme.Text;
        _field.InvalidatePaint();

        UpdateCaretPosition();
        ResetBlink();
        InvalidatePaint();
    }

    private void UpdateCaretPosition()
    {
        float textW = 0f;
        if (_value.Length > 0 && _caretIndex > 0)
        {
            using var paint = new SKPaint
            {
                Typeface = Theme.GetTypeface(500),
                TextSize = 15f,
                IsAntialias = true
            };
            int safeLen = Math.Min(_caretIndex, _value.Length);
            textW = paint.MeasureText(_value[..safeLen]);
        }

        float fx = _field.Transform.Computed.X;
        float fy = _field.Transform.Computed.Y;
        const float padLeft = 10f;
        const float caretW = 2f;
        const float caretH = 20f;
        float caretX = fx + padLeft + textW;
        float caretY = fy + Math.Max(0f, (_field.Transform.Computed.Height - caretH) / 2f);

        _caret.Transform.SetAbsoluteFrame(caretX, caretY, caretW, caretH);
        _caret.Visible = Visible && _caretVisible;
    }

    private void ResetBlink()
    {
        _caretVisible = true;
        _caret.Visible = Visible;
        _blinkTimer?.Change(530, 530);
    }

    private void StartBlink()
    {
        _blinkTimer?.Dispose();
        _caretVisible = true;
        _caret.Visible = Visible;
        _blinkTimer = new System.Threading.Timer(_ =>
        {
            if (!Visible) return;
            Browser.Post(() =>
            {
                if (!Visible) return;
                _caretVisible = !_caretVisible;
                _caret.Visible = _caretVisible;
                _caret.InvalidatePaint();
                _field.InvalidatePaint();
                InvalidatePaint();
            });
        }, null, 530, 530);
    }

    private void StopBlink()
    {
        _blinkTimer?.Dispose();
        _blinkTimer = null;
        _caretVisible = false;
        _caret.Visible = false;
    }

    protected override void LayoutChildren()
    {
        float ox = Transform.Computed.X;
        float oy = Transform.Computed.Y;
        float w = Math.Max(1f, Transform.Computed.Width);
        float h = Math.Max(1f, Transform.Computed.Height);
        const float cw = 440f;
        const float ch = 180f;
        float x = ox + Math.Max(0, (w - cw) / 2f);
        float y = oy + Math.Max(0, (h - ch) / 2f);
        _card.Transform.SetAbsoluteFrame(x, y, cw, ch);
        _title.Transform.SetAbsoluteFrame(x + 18, y + 16, cw - 36, 26);
        _field.Transform.SetAbsoluteFrame(x + 18, y + 54, cw - 36, 40);
        _save.Transform.SetAbsoluteFrame(x + cw - 18 - 100, y + ch - 48, 100, 34);
        _cancel.Transform.SetAbsoluteFrame(x + cw - 18 - 100 - 10 - 100, y + ch - 48, 100, 34);
        UpdateCaretPosition();
    }
}
