using System;
using Blossom.Core;
using Raw75.Develop;
using Raw75.Views;
using SkiaSharp;

namespace Raw75.Imaging;

internal sealed class PreviewLoader
{
    private readonly PhotoPane _pane;
    private readonly Action<string> _status;
    private readonly DevelopEngine _engine;
    private PhotoDocument? _doc;
    private SKImage? _shownSource;

    public PreviewLoader(PhotoPane pane, Action<string> status)
    {
        _pane = pane;
        _status = status;
        _engine = new DevelopEngine(OnEngineStatus);
    }

    public void Load(string path)
    {
        _pane.SetDroppedPath(path);
        PhotoDocument? previous = _doc;
        _doc = new PhotoDocument(path);
        _shownSource = null;
        _engine.Open(_doc);
        previous?.Dispose();
    }

    private void OnEngineStatus(string text)
    {
        SKImage? display = _doc?.Display;
        if (display != null
            && display.Handle != IntPtr.Zero
            && !ReferenceEquals(display, _shownSource))
        {
            SKImage? copy = CloneImage(display);
            if (copy != null)
            {
                _pane.SetDeveloped(copy);
                _shownSource = display;
            }
        }

        _status(text);
    }

    private static SKImage? CloneImage(SKImage src)
    {
        if (Gpu.IsReady)
        {
            using var surface = Gpu.CreateSurface(src.Width, src.Height, SKColorType.Rgba8888);
            if (surface != null)
            {
                surface.Canvas.DrawImage(src, 0, 0);
                SKImage? snap = Gpu.Snapshot(surface);
                if (snap != null && !ReferenceEquals(snap, src))
                    return snap;
            }
        }

        SKImage? raster = src.ToRasterImage();
        if (raster != null && !ReferenceEquals(raster, src))
            return raster;

        using var bmp = SKBitmap.FromImage(src);
        return bmp != null ? SKImage.FromBitmap(bmp) : null;
    }
}
