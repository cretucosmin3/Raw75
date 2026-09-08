using System;
using System.Threading;
using System.Threading.Tasks;
using Blossom;
using Raw75.Develop;
using Raw75.Pipeline;
using SkiaSharp;

namespace Raw75.Imaging;

public sealed class DevelopEngine
{
    private readonly Action<string> _status;
    private int _generation;
    private int _invalidateSeq;
    private int _hiResBusy;
    private int _busy;
    private int _liveBusy;
    private int _livePending;
    private int _histBusy;
    private int _histPending;
    private PhotoDocument? _current;

    public event Action<PhotoDocument>? Updated;
    public event Action<PhotoDocument>? LiveUpdated;
    public event Action<PhotoDocument>? HistogramUpdated;
    public event Action<bool>? BusyChanged;

    public bool IsBusy => Volatile.Read(ref _busy) > 0;

    public DevelopEngine(Action<string> status)
    {
        _status = status ?? (_ => { });
    }

    public void Open(PhotoDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        int gen = Interlocked.Increment(ref _generation);
        Volatile.Write(ref _current, doc);

        Browser.Post(() =>
        {
            if (Stale(gen))
                return;
            _status($"Opening {doc.Name}…");
        });

        Task.Run(() => OpenBackground(doc, gen));
    }

    public void Invalidate(PhotoDocument doc, bool preview = true)
    {
        if (doc == null)
            return;
        if (!doc.SourceRgba.HasPixels)
            return;

        int openGen = Volatile.Read(ref _generation);
        int seq = Interlocked.Increment(ref _invalidateSeq);

        if (preview)
        {
            SetBusy(false);
            KickLive(doc, openGen);
            return;
        }

        var settings = doc.Settings.Clone();
        RasterBuffer src = doc.HiResRgba.HasPixels
            ? doc.HiResRgba
            : doc.SourceRgba;
        SetBusy(true);
        Task.Run(() => ApplyBackground(doc, settings, src, openGen, seq, preview: false));
        RequestHistogram(doc);
    }

    public void RequestHistogram(PhotoDocument doc)
    {
        if (doc == null || (!doc.LiveRgba.HasPixels && !doc.SourceRgba.HasPixels))
            return;
        Volatile.Write(ref _current, doc);
        Interlocked.Exchange(ref _histPending, 1);
        if (Interlocked.CompareExchange(ref _histBusy, 1, 0) != 0)
            return;
        int gen = Volatile.Read(ref _generation);
        Task.Run(() => HistogramLoop(doc, gen));
    }

    private void HistogramLoop(PhotoDocument doc, int gen)
    {
        try
        {
            while (Interlocked.Exchange(ref _histPending, 0) != 0)
            {
                if (Stale(gen) || !ReferenceEquals(doc, Volatile.Read(ref _current)))
                    break;
                RasterBuffer src = doc.LiveRgba.HasPixels ? doc.LiveRgba : doc.SourceRgba;
                if (!src.HasPixels)
                    break;
                if (Math.Max(src.Width, src.Height) > 256)
                    src = RawDecoder.Limit(src, 256);
                DevelopSettings settings = doc.Settings.Clone();
                RasterBuffer developed = DevelopCpu.Apply(src, settings, fast: true);
                if (Stale(gen))
                    continue;
                Browser.Post(() =>
                {
                    if (Stale(gen) || !ReferenceEquals(doc, Volatile.Read(ref _current)))
                        return;
                    DevelopCpu.FillHistogram(developed, doc);
                    HistogramUpdated?.Invoke(doc);
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warning("Histogram: " + ex.Message);
        }
        finally
        {
            Volatile.Write(ref _histBusy, 0);
            PhotoDocument? current = Volatile.Read(ref _current);
            if (current != null && Volatile.Read(ref _histPending) != 0)
                RequestHistogram(current);
        }
    }

    private void KickLive(PhotoDocument doc, int gen)
    {
        Volatile.Write(ref _current, doc);
        Interlocked.Exchange(ref _livePending, 1);
        if (Interlocked.CompareExchange(ref _liveBusy, 1, 0) != 0)
            return;
        Task.Run(() => LiveLoop(doc, gen));
    }

    private void LiveLoop(PhotoDocument doc, int gen)
    {
        try
        {
            while (Interlocked.Exchange(ref _livePending, 0) != 0)
            {
                PhotoDocument? current = Volatile.Read(ref _current);
                if (current == null || Stale(gen) || !ReferenceEquals(doc, current))
                    break;

                int seq = Volatile.Read(ref _invalidateSeq);
                RasterBuffer src = doc.LiveRgba;
                if (!src.HasPixels)
                    src = doc.SourceRgba;
                if (!src.HasPixels)
                    break;
                if (Math.Max(src.Width, src.Height) > 512)
                    src = RawDecoder.Limit(src, 512);

                DevelopSettings settings = doc.Settings.Clone();
                RasterBuffer developed = DevelopCpu.Apply(src, settings, fast: true);
                if (Stale(gen) || seq != Volatile.Read(ref _invalidateSeq))
                    continue;

                Browser.Post(() =>
                {
                    if (Stale(gen) || seq != Volatile.Read(ref _invalidateSeq))
                        return;
                    try
                    {
                        SKImage image = RawDecoder.Upload(developed, out _);
                        Assign(doc, display: image);
                        LiveUpdated?.Invoke(doc);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning("Live upload: " + ex.Message);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warning("Live develop: " + ex.Message);
        }
        finally
        {
            Volatile.Write(ref _liveBusy, 0);
            PhotoDocument? current = Volatile.Read(ref _current);
            int g = Volatile.Read(ref _generation);
            if (current != null && Volatile.Read(ref _livePending) != 0)
                KickLive(current, g);
        }
    }

    public void EnsureHiRes(PhotoDocument doc)
    {
        if (doc == null || doc.HiResRgba.Width > 0)
            return;
        if (Interlocked.CompareExchange(ref _hiResBusy, 1, 0) != 0)
            return;

        int gen = Volatile.Read(ref _generation);
        Log.Info("Loading full-resolution preview…");
        _status("Loading full resolution…");
        Task.Run(() =>
        {
            try
            {
                RasterBuffer buf = RawDecoder.DecodeFull(doc.Path);
                Browser.Post(() =>
                {
                    if (Stale(gen) || !ReferenceEquals(doc, Volatile.Read(ref _current)))
                        return;
                    doc.HiResRgba = buf;
                    Invalidate(doc, preview: false);
                });
            }
            catch (Exception ex)
            {
                Log.Warning("Full-res decode failed: " + ex.Message);
                Browser.Post(() => _status("Full-res decode failed"));
            }
            finally
            {
                Volatile.Write(ref _hiResBusy, 0);
            }
        });
    }

    private void OpenBackground(PhotoDocument doc, int gen)
    {
        try
        {
            if (Stale(gen))
                return;

            RasterBuffer? raster = RawDecoder.TryDecodeRaster(doc.Path);
            if (raster != null)
            {
                Publish(doc, gen, raster.Value, asThumb: true, asProxy: true, kind: "raster");
                return;
            }

            try
            {
                Publish(doc, gen, RawDecoder.DecodeThumbnail(doc.Path), asThumb: true, asProxy: false, kind: "thumbnail");
            }
            catch (Exception ex)
            {
                Log.Warning($"Thumbnail failed: {ex.Message}");
            }

            if (Stale(gen))
                return;

            Publish(doc, gen, RawDecoder.DecodePreview(doc.Path), asThumb: false, asProxy: true, kind: "preview");
        }
        catch (Exception ex)
        {
            string flat = LibRawNative.Flatten(ex);
            Log.Error($"Could not open {doc.Path}: {flat}\n{ex}");
            if (Stale(gen))
                return;
            Browser.Post(() =>
            {
                if (!Stale(gen))
                    _status("Could not open: " + flat);
            });
        }
    }

    private void Publish(PhotoDocument doc, int gen, RasterBuffer raster, bool asThumb, bool asProxy, string kind)
    {
        if (Stale(gen))
            return;

        Browser.Post(() =>
        {
            try
            {
                if (Stale(gen))
                    return;

                Log.Info($"Upload {kind} {raster.Width}x{raster.Height} (UI SKImage)");
                SKImage image = RawDecoder.Upload(raster, out SKBitmap keep);
                Log.Info($"Upload {kind} SKImage ok {image.Width}x{image.Height}");
                if (asProxy)
                {
                    SKImage gpu = DevelopRenderer.PromoteGpu(image);
                    if (!ReferenceEquals(gpu, image))
                    {
                        Log.Info($"GPU proxy {gpu.Width}x{gpu.Height}");
                        image = gpu;
                    }
                }

                if (asProxy)
                {
                    doc.SourceRgba = raster;
                    doc.SourceLinear = raster.HasLinear;
                    doc.LiveRgba = RawDecoder.Limit(raster, 256);
                    doc.RasterKeep?.Dispose();
                    doc.RasterKeep = keep;
                }
                else if (doc.RasterKeep == null)
                {
                    doc.RasterKeep = keep;
                }
                else
                {
                    keep.Dispose();
                }

                Log.Info($"Assign {kind}");
                if (asThumb)
                    Assign(doc, thumb: image);
                if (asProxy)
                    Assign(doc, proxy: image);
                if (doc.Display == null || asProxy)
                    Assign(doc, display: image);

                if (doc.SourceWidth <= 0 || asProxy)
                {
                    doc.SourceWidth = image.Width;
                    doc.SourceHeight = image.Height;
                }

                Log.Info($"Assigned {kind}, skip develop on import");
                _status($"{doc.Name}  ·  {image.Width}×{image.Height} {kind}");
                Log.Info($"Showing {kind}");
                Updated?.Invoke(doc);
                Log.Info($"Shown {kind}");
            }
            catch (Exception ex)
            {
                Log.Error($"GPU upload failed: {ex}");
                if (!Stale(gen))
                    _status($"GPU upload failed: {ex.Message}");
            }
        });
    }

    private void ApplyBackground(
        PhotoDocument doc,
        DevelopSettings settings,
        RasterBuffer src,
        int gen,
        int seq,
        bool preview)
    {
        try
        {
            if (Stale(gen) || seq != Volatile.Read(ref _invalidateSeq))
                return;

            if (preview && Math.Max(src.Width, src.Height) > 900)
                src = RawDecoder.Limit(src, 900);

            Log.Info($"Develop CPU {src.Width}x{src.Height} preview={preview}");
            RasterBuffer developed = DevelopCpu.Apply(src, settings, fast: false);
            if (Stale(gen) || seq != Volatile.Read(ref _invalidateSeq))
                return;

            Browser.Post(() =>
            {
                if (Stale(gen) || seq != Volatile.Read(ref _invalidateSeq))
                    return;
                try
                {
                    SKImage image = RawDecoder.Upload(developed, out _);
                    Log.Info($"Develop CPU ok {image.Width}x{image.Height}");
                    Assign(doc, display: image);
                    DevelopCpu.FillHistogram(developed, doc);
                    Updated?.Invoke(doc);
                    Log.Info("Develop shown");
                }
                catch (Exception ex)
                {
                    Log.Error($"Develop upload failed: {ex}");
                }
                finally
                {
                    if (seq == Volatile.Read(ref _invalidateSeq))
                        SetBusy(false);
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error($"Develop CPU failed: {ex}");
            Browser.Post(() =>
            {
                if (seq == Volatile.Read(ref _invalidateSeq))
                    SetBusy(false);
            });
        }
    }

    private void SetBusy(bool busy)
    {
        int next = busy ? 1 : 0;
        int prev = Interlocked.Exchange(ref _busy, next);
        if (prev == next)
            return;
        Browser.Post(() => BusyChanged?.Invoke(busy));
    }

    private bool Stale(int gen) => gen != Volatile.Read(ref _generation);

    private static void Assign(PhotoDocument doc, SKImage? thumb = null, SKImage? proxy = null, SKImage? display = null)
    {
        if (thumb != null)
        {
            SKImage? old = doc.Thumb;
            doc.Thumb = thumb;
            DisposeUnowned(doc, old);
        }

        if (proxy != null)
        {
            SKImage? old = doc.Proxy;
            doc.Proxy = proxy;
            DisposeUnowned(doc, old);
        }

        if (display != null)
        {
            SKImage? old = doc.Display;
            doc.Display = display;
            DisposeUnowned(doc, old);
        }
    }

    private static void DisposeUnowned(PhotoDocument doc, SKImage? image)
    {
        // Do not SKImage.Dispose here. The command ledger and GPU still
        // hold the previous frame's image; freeing it SIGSEGVs on the next slider tick.
        _ = doc;
        _ = image;
    }
}
