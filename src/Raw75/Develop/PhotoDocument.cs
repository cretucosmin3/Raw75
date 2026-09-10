using System;
using Raw75.Imaging;
using Raw75.Views;
using SkiaSharp;

namespace Raw75.Develop;

public sealed class PhotoDocument : IDisposable
{
    public string Path { get; }
    public string Name { get; }
    public string Camera { get; set; } = "";
    public bool IsReady { get; set; }
    public DevelopSettings Settings { get; } = new();
    public UndoStack Undo { get; } = new();

    /// <summary>Display image (developed 8-bit blit). Owned.</summary>
    public SKImage? Display { get; set; }

    /// <summary>Filmstrip / navigator thumb. Owned.</summary>
    public SKImage? Thumb { get; set; }

    /// <summary>Small developed preview (crop + rotate + look) for the filmstrip.</summary>
    public SKImage? Preview { get; set; }

    /// <summary>Larger developed look, kept across unload so click-back is already oriented.</summary>
    public SKImage? Look { get; set; }

    public ZoomMode SavedZoomMode { get; set; } = ZoomMode.Fit;
    public float SavedZoom { get; set; } = 1f;
    public float SavedPanX { get; set; }
    public float SavedPanY { get; set; }
    public bool SavedFreeZoom { get; set; }
    public bool HasSavedView { get; set; }

    /// <summary>Working GPU proxy (F16 if possible). Owned.</summary>
    public SKImage? Proxy { get; set; }

    /// <summary>Pixel owner for <see cref="Proxy"/> / <see cref="Display"/> when Skia aliases the bitmap.</summary>
    public SKBitmap? RasterKeep { get; set; }

    /// <summary>Source RGBA for CPU develop. Not owned by Skia.</summary>
    internal RasterBuffer SourceRgba { get; set; }

    /// <summary>Full-res RGBA loaded on first 1:1 / zoom-in.</summary>
    internal RasterBuffer HiResRgba { get; set; }

    /// <summary>Small raster for live slider drags.</summary>
    internal RasterBuffer LiveRgba { get; set; }

    /// <summary>Working buffer is scene-linear (16-bit RAW), not display-referred 8-bit.</summary>
    internal bool SourceLinear { get; set; }

    public int SourceWidth { get; set; }
    public int SourceHeight { get; set; }
    public int NativeWidth { get; set; }
    public int NativeHeight { get; set; }

    /// <summary>Visible-region hi-res tile in source space (undeveloped linear).</summary>
    public SKImage? ViewportTile { get; set; }
    public float TileX { get; set; }
    public float TileY { get; set; }
    public float TileW { get; set; }
    public float TileH { get; set; }

    public float[] HistogramR { get; } = new float[256];
    public float[] HistogramG { get; } = new float[256];
    public float[] HistogramB { get; } = new float[256];
    public float[] HistogramY { get; } = new float[256];

    public bool IsDisposed { get; private set; }

    public PhotoDocument(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
    }

    public void UnloadWorking()
    {
        SKImage? a = Display;
        SKImage? c = Proxy;
        SKImage? tile = ViewportTile;
        Display = null;
        Proxy = null;
        ViewportTile = null;
        TileW = TileH = 0;
        RasterKeep = null;
        if (a != null && !ReferenceEquals(a, Thumb) && !ReferenceEquals(a, Preview) && !ReferenceEquals(a, Look))
            GpuRetain.Retire(a);
        if (c != null && !ReferenceEquals(c, a) && !ReferenceEquals(c, Thumb) && !ReferenceEquals(c, Preview) && !ReferenceEquals(c, Look))
            GpuRetain.Retire(c);
        if (tile != null && !ReferenceEquals(tile, a) && !ReferenceEquals(tile, c)
            && !ReferenceEquals(tile, Thumb) && !ReferenceEquals(tile, Preview) && !ReferenceEquals(tile, Look))
            GpuRetain.Retire(tile);
        SourceRgba = default;
        HiResRgba = default;
        LiveRgba = default;
        SourceLinear = false;
    }

    public void Dispose()
    {
        IsDisposed = true;
        UnloadWorking();
        SKImage? b = Thumb;
        SKImage? p = Preview;
        SKImage? look = Look;
        Thumb = null;
        Preview = null;
        Look = null;
        if (b != null)
            GpuRetain.Retire(b);
        if (p != null && !ReferenceEquals(p, b))
            GpuRetain.Retire(p);
        if (look != null && !ReferenceEquals(look, b) && !ReferenceEquals(look, p))
            GpuRetain.Retire(look);
    }
}
