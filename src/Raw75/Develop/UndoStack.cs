using System.Collections.Generic;

namespace Raw75.Develop;

public sealed class UndoStack
{
    private readonly List<DevelopSettings> _undo = new();
    private readonly List<DevelopSettings> _redo = new();
    private DevelopSettings _dragBase = null!;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void BeginDrag(DevelopSettings current)
    {
        _dragBase = current.Clone();
    }

    public void EndDrag(DevelopSettings current)
    {
        if (_dragBase == null) return;
        if (!_dragBase.LooksLike(current))
        {
            _undo.Add(_dragBase);
            _redo.Clear();
        }
        _dragBase = null!;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        _dragBase = null!;
    }

    public void Push(DevelopSettings before)
    {
        _undo.Add(before.Clone());
        _redo.Clear();
    }

    public DevelopSettings? Undo(DevelopSettings current)
    {
        if (_undo.Count == 0) return null;
        _redo.Add(current.Clone());
        var s = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        return s;
    }

    public DevelopSettings? Redo(DevelopSettings current)
    {
        if (_redo.Count == 0) return null;
        _undo.Add(current.Clone());
        var s = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        return s;
    }
}
