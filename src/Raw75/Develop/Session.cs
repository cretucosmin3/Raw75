using System;
using System.Collections.Generic;

namespace Raw75.Develop;

public sealed class Session : IDisposable
{
    public List<PhotoDocument> Documents { get; } = new();
    public int ActiveIndex { get; private set; } = -1;
    public int CacheLimit { get; set; } = 1;

    public PhotoDocument? Active =>
        ActiveIndex >= 0 && ActiveIndex < Documents.Count ? Documents[ActiveIndex] : null;

    public event Action? Changed;

    public PhotoDocument Add(string path, bool select = true)
    {
        for (int i = 0; i < Documents.Count; i++)
        {
            if (string.Equals(Documents[i].Path, path, StringComparison.OrdinalIgnoreCase))
            {
                if (select)
                    Select(i);
                return Documents[i];
            }
        }

        var doc = new PhotoDocument(path);
        Documents.Add(doc);
        if (select)
        {
            ActiveIndex = Documents.Count - 1;
            EvictWorking();
            Changed?.Invoke();
        }
        return doc;
    }

    public void Clear()
    {
        foreach (var d in Documents)
            d.Dispose();
        Documents.Clear();
        ActiveIndex = -1;
        Changed?.Invoke();
    }

    public void Select(int index)
    {
        if (index < 0 || index >= Documents.Count) return;
        ActiveIndex = index;
        EvictWorking();
        Changed?.Invoke();
    }

    public void EvictWorking()
    {
        int keep = Math.Max(1, CacheLimit);
        for (int i = 0; i < Documents.Count; i++)
        {
            if (i == ActiveIndex)
                continue;
            int dist = Math.Abs(i - ActiveIndex);
            if (dist < keep)
                continue;
            Documents[i].UnloadWorking();
        }
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
