using System;
using System.Collections.Generic;

namespace Raw75.Develop;

public sealed class Session : IDisposable
{
    public List<PhotoDocument> Documents { get; } = new();
    public int ActiveIndex { get; private set; } = -1;

    public PhotoDocument? Active =>
        ActiveIndex >= 0 && ActiveIndex < Documents.Count ? Documents[ActiveIndex] : null;

    public event Action? Changed;

    public PhotoDocument Add(string path)
    {
        var doc = new PhotoDocument(path);
        Documents.Add(doc);
        ActiveIndex = Documents.Count - 1;
        Changed?.Invoke();
        return doc;
    }

    public void Select(int index)
    {
        if (index < 0 || index >= Documents.Count) return;
        ActiveIndex = index;
        Changed?.Invoke();
    }

    public void Close(int index)
    {
        if (index < 0 || index >= Documents.Count) return;
        Documents[index].Dispose();
        Documents.RemoveAt(index);
        if (ActiveIndex >= Documents.Count)
            ActiveIndex = Documents.Count - 1;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        foreach (var d in Documents)
            d.Dispose();
        Documents.Clear();
        ActiveIndex = -1;
    }
}
