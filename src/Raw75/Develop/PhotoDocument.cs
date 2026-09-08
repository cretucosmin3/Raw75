using System;
using Raw75.Imaging;
using SkiaSharp;

namespace Raw75.Develop;

public sealed class PhotoDocument : IDisposable
{
    public string Path { get; }
    public string Name { get; }
    public string Camera { get; set; } = "";
    public DevelopSettings Settings { get; } = new();
    public UndoStack Undo { get; } = new();

    /// <summary>Display image (developed 8-bit blit). Owned.</summary>
    public SKImage? Display { get; set; }

    /// <summary>Filmstrip / navigator thumb. Owned.</summary>
    public SKImage? Thumb { get; set; }

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

    public float[] HistogramR { get; } = new float[256];
    public float[] HistogramG { get; } = new float[256];
    public float[] HistogramB { get; } = new float[256];
    public float[] HistogramY { get; } = new float[256];

    public PhotoDocument(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
    }

    public void Dispose()
    {
        SKImage? a = Display;
        SKImage? b = Thumb;
        SKImage? c = Proxy;
        Display = null;
        Thumb = null;
        Proxy = null;
        RasterKeep = null;
        if (a != null)
            GpuRetain.Retire(a);
        if (b != null && !ReferenceEquals(b, a))
            GpuRetain.Retire(b);
        if (c != null && !ReferenceEquals(c, a) && !ReferenceEquals(c, b))
            GpuRetain.Retire(c);
        SourceRgba = default;
        HiResRgba = default;
        LiveRgba = default;
    }
}
