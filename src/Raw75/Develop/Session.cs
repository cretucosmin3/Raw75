using System;
using System.Collections.Generic;
using Raw75.Imaging;

namespace Raw75.Develop;

public sealed class Session : IDisposable
{
    public List<PhotoDocument> Documents { get; } = new();
    public int ActiveIndex { get; private set; } = -1;
    public int CacheLimit { get; set; } = 1;

    public HashSet<int> SelectedIndices { get; } = new();

    public PhotoDocument? Active =>
        ActiveIndex >= 0 && ActiveIndex < Documents.Count ? Documents[ActiveIndex] : null;

    public event Action? Changed;
    public event Action? SelectionChanged;

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
            SelectedIndices.Clear();
            SelectedIndices.Add(ActiveIndex);
            Changed?.Invoke();
            SelectionChanged?.Invoke();
            EvictWorking();
        }
        return doc;
    }

    public void Clear()
    {
        foreach (var d in Documents)
            d.Dispose();
        Documents.Clear();
        ActiveIndex = -1;
        SelectedIndices.Clear();
        Changed?.Invoke();
        SelectionChanged?.Invoke();
    }

    public void Select(int index)
    {
        if (index < 0 || index >= Documents.Count) return;
        ActiveIndex = index;
        SelectedIndices.Clear();
        SelectedIndices.Add(index);
        Changed?.Invoke();
        SelectionChanged?.Invoke();
        EvictWorking();
    }

    public void SetActiveOnly(int index)
    {
        if (index < 0 || index >= Documents.Count) return;
        ActiveIndex = index;
        Changed?.Invoke();
        EvictWorking();
    }

    public void ToggleSelection(int index)
    {
        if (index < 0 || index >= Documents.Count) return;
        if (SelectedIndices.Contains(index))
        {
            if (SelectedIndices.Count > 1)
                SelectedIndices.Remove(index);
        }
        else
        {
            SelectedIndices.Add(index);
        }
        ActiveIndex = index;
        Changed?.Invoke();
        SelectionChanged?.Invoke();
        EvictWorking();
    }

    public void SelectRange(int fromIndex, int toIndex)
    {
        if (Documents.Count == 0) return;
        int start = Math.Clamp(Math.Min(fromIndex, toIndex), 0, Documents.Count - 1);
        int end = Math.Clamp(Math.Max(fromIndex, toIndex), 0, Documents.Count - 1);
        for (int i = start; i <= end; i++)
            SelectedIndices.Add(i);
        ActiveIndex = toIndex;
        Changed?.Invoke();
        SelectionChanged?.Invoke();
        EvictWorking();
    }

    public IReadOnlyList<PhotoDocument> GetSelectedDocuments()
    {
        var list = new List<PhotoDocument>();
        for (int i = 0; i < Documents.Count; i++)
        {
            if (SelectedIndices.Contains(i))
                list.Add(Documents[i]);
        }
        if (list.Count == 0 && Active != null)
            list.Add(Active);
        return list;
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

    public void ResetAllDocuments()
    {
        for (int i = 0; i < Documents.Count; i++)
        {
            var doc = Documents[i];
            doc.Settings.Reset();
            doc.IsReady = false;
            doc.IsFavorite = false;
            doc.Undo.Clear();
            doc.UnloadWorking();

            if (doc.Preview != null)
            {
                GpuRetain.Retire(doc.Preview);
                doc.Preview = null;
            }
            if (doc.Look != null)
            {
                GpuRetain.Retire(doc.Look);
                doc.Look = null;
            }
            if (doc.Thumb != null)
            {
                GpuRetain.Retire(doc.Thumb);
                doc.Thumb = null;
            }
        }
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
