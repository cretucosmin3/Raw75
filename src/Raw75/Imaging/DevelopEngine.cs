using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Blossom;
using Raw75.Develop;
using Raw75.Io;
using Raw75.Pipeline;
using SkiaSharp;

namespace Raw75.Imaging;

public sealed class DevelopEngine
{
    private readonly Action<string> _status;
    private int _generation;
    private int _invalidateSeq;
    private int _hiResBusy;
    private int _viewSeq;
    private SKRect _pendingAabb;
    private int _pendingSw;
    private int _pendingSh;
    private int _busy;
    private int _liveBusy;
    private int _livePending;
    private int _histBusy;
    private int _histPending;
    private PhotoDocument? _current;

    public event Action<PhotoDocument>? Updated;
    public event Action<PhotoDocument>? LiveUpdated;
    public event Action<PhotoDocument>? HistogramUpdated;
    public event Action<PhotoDocument>? ViewportUpdated;
    public event Action<bool>? BusyChanged;
    public event Action<int, int>? ThumbProgress;
    public event Action<PhotoDocument>? ThumbLoaded;

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
        // Preview pipe only (darkroom ROI/proxy). Never CPU-develop native.
        RasterBuffer src = doc.SourceRgba;
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

    /// <summary>
    /// Decode small thumbs for catalog photos that have no Preview/Thumb yet.
    /// Does not bump the open generation, so the active decode keeps going.
    /// </summary>
    public void FillMissingThumbs(IReadOnlyList<PhotoDocument> docs)
    {
        if (docs == null || docs.Count == 0)
            return;
        var copy = new PhotoDocument[docs.Count];
        for (int i = 0; i < docs.Count; i++)
            copy[i] = docs[i];

        Task.Run(() =>
        {
            int missing = 0;
            for (int i = 0; i < copy.Length; i++)
            {
                if (!copy[i].IsDisposed && copy[i].Preview == null && copy[i].Thumb == null)
                    missing++;
            }

            if (missing == 0)
            {
                Browser.Post(() => ThumbProgress?.Invoke(copy.Length, copy.Length));
                return;
            }

            int processed = 0;
            for (int i = 0; i < copy.Length; i++)
            {
                PhotoDocument doc = copy[i];
                if (doc.IsDisposed || doc.Preview != null || doc.Thumb != null)
                    continue;

                try
                {
                    string dir = WorkspaceStore.EntryDir(doc.Path);
                    string lookPath = Path.Combine(dir, "look.jpg");
                    string prevPath = Path.Combine(dir, "preview.jpg");
                    string thumbPath = Path.Combine(dir, "thumb.jpg");
                    string? cachedPath = File.Exists(lookPath) ? lookPath : (File.Exists(prevPath) ? prevPath : (File.Exists(thumbPath) ? thumbPath : null));
                    if (cachedPath != null)
                    {
                        SKImage? cached = WorkspaceStore.LoadJpeg(cachedPath);
                        if (cached != null)
                        {
                            int nCached = Interlocked.Increment(ref processed);
                            PhotoDocument targetCached = doc;
                            SKImage shotCached = cached;
                            Browser.Post(() =>
                            {
                                if (targetCached.IsDisposed || targetCached.Preview != null || targetCached.Thumb != null)
                                    return;
                                Assign(targetCached, thumb: shotCached);
                                ThumbLoaded?.Invoke(targetCached);
                                ThumbProgress?.Invoke(nCached, missing);
                            });
                            continue;
                        }
                    }

                    RasterBuffer buf;
                    Develop.PhotoMetadata? meta = null;
                    if (RawDecoder.IsRawPath(doc.Path))
                        buf = RawDecoder.DecodeThumbnail(doc.Path, out meta);
                    else
                    {
                        RasterBuffer? raster = RawDecoder.TryDecodeRaster(doc.Path, out meta);
                        if (raster == null)
                        {
                            int cur = Interlocked.Increment(ref processed);
                            Browser.Post(() => ThumbProgress?.Invoke(cur, missing));
                            continue;
                        }
                        buf = raster.Value;
                    }

                    buf = RawDecoder.Limit(buf, 640);
                    WorkspaceStore.SaveThumb(doc, buf);
                    int n = Interlocked.Increment(ref processed);
                    PhotoDocument target = doc;
                    RasterBuffer shot = buf;
                    Develop.PhotoMetadata? shotMeta = meta;
                    Browser.Post(() =>
                    {
                        if (target.IsDisposed || target.Preview != null || target.Thumb != null)
                            return;
                        try
                        {
                            if (shotMeta != null)
                            {
                                target.Metadata ??= shotMeta;
                                if (!string.IsNullOrEmpty(shotMeta.CameraName) && string.IsNullOrEmpty(target.Camera))
                                    target.Camera = shotMeta.CameraName;
                            }
                            SKImage image = RawDecoder.Upload(shot, out _);
                            Assign(target, thumb: image);
                            ThumbLoaded?.Invoke(target);
                            ThumbProgress?.Invoke(n, missing);
                        }
                        catch (Exception ex)
                        {
                            Log.Warning("Thumb upload " + target.Name + ": " + ex.Message);
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Warning("Thumb " + doc.Name + ": " + ex.Message);
                    int cur = Interlocked.Increment(ref processed);
                    Browser.Post(() => ThumbProgress?.Invoke(cur, missing));
                }
            }

            Browser.Post(() =>
            {
                ThumbProgress?.Invoke(missing, missing);
            });
        });
    }

    public void RequestViewport(PhotoDocument doc, SKRect sourceAabb, int screenW, int screenH)
    {
        if (doc == null || doc.IsDisposed)
            return;
        Volatile.Write(ref _current, doc);
        if (sourceAabb.Width < 1e-5f || sourceAabb.Height < 1e-5f)
            return;

        _pendingAabb = sourceAabb;
        _pendingSw = Math.Max(1, screenW);
        _pendingSh = Math.Max(1, screenH);

        if (doc.HiResRgba.HasPixels && TileCovers(doc, sourceAabb, _pendingSw, _pendingSh))
            return;

        int seq = Interlocked.Increment(ref _viewSeq);
        int gen = Volatile.Read(ref _generation);

        if (doc.HiResRgba.HasPixels)
        {
            Task.Run(() => BuildViewport(doc, sourceAabb, _pendingSw, _pendingSh, gen, seq));
            return;
        }

        EnsureHiRes(doc);
    }

    public void ClearViewport(PhotoDocument doc)
    {
        if (doc == null)
            return;
        SKImage? old = doc.ViewportTile;
        doc.ViewportTile = null;
        doc.TileW = doc.TileH = 0;
        ViewportUpdated?.Invoke(doc);
        if (old != null)
            RetireIfUnused(doc, old);
    }

    public void EnsureHiRes(PhotoDocument doc)
    {
        if (doc == null || doc.HiResRgba.Width > 0)
            return;
        if (Interlocked.CompareExchange(ref _hiResBusy, 1, 0) != 0)
            return;

        int gen = Volatile.Read(ref _generation);
        Log.Info("Loading full-resolution source for viewport tiles…");
        _status("Loading full resolution…");
        Task.Run(() =>
        {
            try
            {
                RasterBuffer buf = RawDecoder.DecodeRasterFull(doc.Path)
                    ?? RawDecoder.DecodeFull(doc.Path);
                Browser.Post(() =>
                {
                    if (Stale(gen) || doc.IsDisposed || !ReferenceEquals(doc, Volatile.Read(ref _current)))
                        return;
                    doc.HiResRgba = buf;
                    doc.NativeWidth = buf.Width;
                    doc.NativeHeight = buf.Height;
                    _status(doc.Name + "  ·  full " + buf.Width + "×" + buf.Height);
                    ViewportUpdated?.Invoke(doc);
                    int seq = Volatile.Read(ref _viewSeq);
                    Task.Run(() => BuildViewport(doc, _pendingAabb, _pendingSw, _pendingSh, gen, seq));
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

    private void BuildViewport(PhotoDocument doc, SKRect aabb, int screenW, int screenH, int gen, int seq)
    {
        try
        {
            if (Stale(gen) || seq != Volatile.Read(ref _viewSeq) || !doc.HiResRgba.HasPixels)
                return;
            if (aabb.Width < 1e-5f || aabb.Height < 1e-5f)
                return;

            RasterBuffer src = doc.HiResRgba;
            float padX = aabb.Width * 0.2f;
            float padY = aabb.Height * 0.2f;
            float x0 = Math.Clamp(aabb.Left - padX, 0f, 1f);
            float y0 = Math.Clamp(aabb.Top - padY, 0f, 1f);
            float x1 = Math.Clamp(aabb.Right + padX, 0f, 1f);
            float y1 = Math.Clamp(aabb.Bottom + padY, 0f, 1f);
            if (x1 <= x0 || y1 <= y0)
                return;

            if (TileCovers(doc, aabb, screenW, screenH))
                return;

            int px = (int)Math.Floor(x0 * src.Width);
            int py = (int)Math.Floor(y0 * src.Height);
            int pw = Math.Max(1, (int)Math.Ceiling(x1 * src.Width) - px);
            int ph = Math.Max(1, (int)Math.Ceiling(y1 * src.Height) - py);
            int cap = Math.Clamp(Math.Max(screenW, screenH) * 2, 1024, 2048);
            RasterBuffer crop = RawDecoder.SampleRegion(src, px, py, pw, ph, cap);
            if (!crop.HasPixels)
                return;
            if (seq != Volatile.Read(ref _viewSeq) || Stale(gen))
                return;

            float nx = px / (float)src.Width;
            float ny = py / (float)src.Height;
            float nw = pw / (float)src.Width;
            float nh = ph / (float)src.Height;

            Browser.Post(() =>
            {
                if (Stale(gen) || seq != Volatile.Read(ref _viewSeq) || doc.IsDisposed)
                    return;
                try
                {
                    SKImage image = RawDecoder.Upload(crop, out _);
                    SKImage? old = doc.ViewportTile;
                    doc.ViewportTile = image;
                    doc.TileX = nx;
                    doc.TileY = ny;
                    doc.TileW = nw;
                    doc.TileH = nh;
                    ViewportUpdated?.Invoke(doc);
                    RetireIfUnused(doc, old);
                    Log.Info($"Viewport tile {image.Width}x{image.Height} src=({nx:0.00},{ny:0.00},{nw:0.00},{nh:0.00})");
                }
                catch (Exception ex)
                {
                    Log.Warning("Viewport tile upload: " + ex.Message);
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warning("Viewport tile: " + ex.Message);
        }
    }

    private static bool TileCovers(PhotoDocument doc, SKRect need, int screenW, int screenH)
    {
        if (doc.ViewportTile == null || doc.ViewportTile.Handle == IntPtr.Zero || doc.TileW < 1e-5f)
            return false;
        if (need.Left < doc.TileX || need.Top < doc.TileY
            || need.Right > doc.TileX + doc.TileW || need.Bottom > doc.TileY + doc.TileH)
            return false;

        float visW = Math.Max(1e-5f, need.Width);
        float visH = Math.Max(1e-5f, need.Height);
        float pxPerSrcX = doc.ViewportTile.Width / doc.TileW;
        float pxPerSrcY = doc.ViewportTile.Height / doc.TileH;
        float haveX = pxPerSrcX * visW;
        float haveY = pxPerSrcY * visH;
        return haveX >= screenW * 0.9f && haveY >= screenH * 0.9f;
    }

    private void OpenBackground(PhotoDocument doc, int gen)
    {
        try
        {
            if (Stale(gen))
                return;

            RasterBuffer? fullRaster = RawDecoder.DecodeRasterFull(doc.Path);
            if (fullRaster != null)
            {
                doc.Metadata = RawDecoder.ReadMetadata(doc.Path);
                if (doc.Metadata != null && !string.IsNullOrEmpty(doc.Metadata.CameraName))
                    doc.Camera = doc.Metadata.CameraName;
                RasterBuffer proxy = RawDecoder.Limit(fullRaster.Value,
                    RawDecoder.ProxyLongEdge(fullRaster.Value.Width, fullRaster.Value.Height));
                Publish(doc, gen, proxy, asThumb: true, asProxy: true, kind: "raster",
                    nativeW: fullRaster.Value.Width, nativeH: fullRaster.Value.Height);
                return;
            }

            // Don't ExportThumbnail here — LibRaw SIGSEGV'd on Bitmap thumb dispose
            // right after this log when switching gallery photos. DecodePreview is next
            // and Publish already supplies a proxy/thumb-sized image.

            if (Stale(gen))
                return;

            RasterBuffer preview = RawDecoder.DecodePreview(doc.Path, out int nw, out int nh, out var meta);
            doc.Metadata = meta;
            if (meta != null && !string.IsNullOrEmpty(meta.CameraName))
                doc.Camera = meta.CameraName;
            Publish(doc, gen, preview, asThumb: false, asProxy: true, kind: "preview", nativeW: nw, nativeH: nh);
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

    private void Publish(PhotoDocument doc, int gen, RasterBuffer raster, bool asThumb, bool asProxy, string kind,
        int nativeW = 0, int nativeH = 0)
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
                SKImage image = RawDecoder.Upload(raster, out _);
                Log.Info($"Upload {kind} SKImage ok {image.Width}x{image.Height}");

                if (asProxy)
                {
                    doc.SourceRgba = raster;
                    doc.SourceLinear = raster.HasLinear;
                    doc.LiveRgba = RawDecoder.Limit(raster, 256);
                    AtmosphereEstimator.Estimate(raster, out float airR, out float airG, out float airB, out float depthMax);
                    doc.Settings.AtmosphereR = airR;
                    doc.Settings.AtmosphereG = airG;
                    doc.Settings.AtmosphereB = airB;
                    doc.Settings.AtmosphereDepthMax = depthMax;
                    if (nativeW > 0)
                    {
                        doc.NativeWidth = nativeW;
                        doc.NativeHeight = nativeH;
                    }
                    else if (doc.NativeWidth <= 0)
                    {
                        doc.NativeWidth = raster.Width;
                        doc.NativeHeight = raster.Height;
                    }

                    if (doc.SourceLinear)
                    {
                        if (!doc.HasSavedSettings && Math.Abs(doc.Settings.Exposure) < 0.001f)
                        {
                            float autoEv = AutoExposureEstimator.Estimate(doc.LiveRgba.HasPixels ? doc.LiveRgba : raster);
                            doc.Settings.Exposure = Math.Max(0.5f, autoEv);
                            doc.Settings.BaseExposure = doc.Settings.Exposure;
                        }
                        else if (doc.Settings.BaseExposure == 0f)
                        {
                            float autoEv = AutoExposureEstimator.Estimate(doc.LiveRgba.HasPixels ? doc.LiveRgba : raster);
                            doc.Settings.BaseExposure = Math.Max(0.5f, autoEv);
                        }
                    }
                }

                Log.Info($"Assign {kind}");
                if (asThumb)
                    Assign(doc, thumb: image);
                if (asProxy)
                    Assign(doc, proxy: image);
                if (asProxy || doc.Display == null)
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
                if (asProxy && (doc.Preview == null || doc.Look == null))
                    ScheduleLooks(doc, doc.Settings.Clone());
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

            int cap = preview ? 900 : 2048;
            if (Math.Max(src.Width, src.Height) > cap)
                src = RawDecoder.Limit(src, cap);

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
                    ScheduleLooks(doc, settings);
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

    private void ScheduleLooks(PhotoDocument doc, DevelopSettings settings)
    {
        RasterBuffer src = doc.SourceRgba.HasPixels ? doc.SourceRgba : doc.LiveRgba;
        if (!src.HasPixels)
            return;
        src = RawDecoder.Limit(src, 720);
        Task.Run(() => BakeLooks(doc, settings, src));
    }

    private void BakeLooks(PhotoDocument doc, DevelopSettings settings, RasterBuffer src)
    {
        try
        {
            if (!src.HasPixels)
                return;
            RasterBuffer lookBuf = DevelopCpu.Apply(src, settings, fast: true);
            RasterBuffer prevBuf = RawDecoder.Limit(lookBuf, 640);
            byte[]? lookJpeg = WorkspaceStore.EncodeJpeg(lookBuf, 85);
            byte[]? prevJpeg = WorkspaceStore.EncodeJpeg(prevBuf, 82);
            WorkspaceStore.SaveLooks(doc, settings, prevJpeg, lookJpeg);

            Browser.Post(() =>
            {
                try
                {
                    SKImage preview = RawDecoder.Upload(prevBuf, out _);
                    SKImage look = RawDecoder.Upload(lookBuf, out _);
                    SKImage? oldP = doc.Preview;
                    SKImage? oldL = doc.Look;
                    doc.Preview = preview;
                    doc.Look = look;
                    Updated?.Invoke(doc);
                    RetireIfUnused(doc, oldP);
                    RetireIfUnused(doc, oldL);
                }
                catch (Exception ex)
                {
                    Log.Warning("Look upload: " + ex.Message);
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warning("Bake looks: " + ex.Message);
        }
    }

    /// <summary>
    /// Develop the native (or already-loaded hi-res) source at crop size and encode.
    /// Preview <see cref="PhotoDocument.Display"/> is proxy-sized and must not be the export source.
    /// Does not attach the native buffer to the document.
    /// </summary>
    internal static (int Width, int Height) WriteExport(
        string path,
        DevelopSettings settings,
        RasterBuffer hiRes,
        string dest,
        string format,
        int jpegQuality,
        int longEdge,
        Action<string>? status,
        Develop.PhotoMetadata? metadata = null)
    {
        RasterBuffer src = hiRes.HasPixels
            ? hiRes
            : (RawDecoder.DecodeRasterFull(path) ?? RawDecoder.DecodeFull(path));
        if (!src.HasPixels)
            throw new InvalidOperationException("Could not decode photo for export.");

        metadata ??= RawDecoder.ReadMetadata(path);

        status?.Invoke($"Developing export {src.Width}×{src.Height}…");
        RasterBuffer developed = DevelopCpu.Apply(src, settings ?? new DevelopSettings(), fast: false);
        if (!developed.HasPixels || developed.Rgba == null)
            throw new InvalidOperationException("Develop produced no pixels.");

        status?.Invoke($"Writing {developed.Width}×{developed.Height}…");
        Log.Info($"Export develop {src.Width}x{src.Height} → {developed.Width}x{developed.Height} {dest}");
        return Exporter.Export(developed, dest, format, jpegQuality, longEdge, sourcePath: path, metadata: metadata);
    }

    private bool Stale(int gen) => gen != Volatile.Read(ref _generation);

    private static void Assign(PhotoDocument doc, SKImage? thumb = null, SKImage? proxy = null, SKImage? display = null)
    {
        if (thumb != null)
        {
            SKImage? old = doc.Thumb;
            doc.Thumb = thumb;
            RetireIfUnused(doc, old);
        }

        if (proxy != null)
        {
            SKImage? old = doc.Proxy;
            doc.Proxy = proxy;
            RetireIfUnused(doc, old);
        }

        if (display != null)
        {
            SKImage? old = doc.Display;
            doc.Display = display;
            RetireIfUnused(doc, old);
        }
    }

    private static void RetireIfUnused(PhotoDocument doc, SKImage? image)
    {
        if (image == null)
            return;
        if (ReferenceEquals(image, doc.Thumb) || ReferenceEquals(image, doc.Proxy)
            || ReferenceEquals(image, doc.Display) || ReferenceEquals(image, doc.Preview)
            || ReferenceEquals(image, doc.Look) || ReferenceEquals(image, doc.ViewportTile))
            return;
        GpuRetain.Retire(image);
    }
}
