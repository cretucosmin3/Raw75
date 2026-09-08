using System;
using System.Collections.Generic;
using Blossom;
using SkiaSharp;

namespace Raw75.Imaging;

/// <summary>
/// Defers SKImage/SKBitmap dispose until the command ledger and GPU
/// have finished the frames that still reference them.
/// </summary>
internal static class GpuRetain
{
    private readonly record struct Item(SKImage? Image, SKBitmap? Bitmap, SKShader? Shader, int Lives);

    private static readonly Dictionary<SKImage, SKBitmap> Bitmaps = new();
    private static readonly List<Item> Grave = new();
    private static bool _pumping;

    internal static void Describe(System.Text.StringBuilder sb)
    {
        sb.AppendLine($"GpuRetain live bitmaps  {Bitmaps.Count}");
        long live = 0;
        int i = 0;
        foreach (var kv in Bitmaps)
        {
            long img = MemSize.Image(kv.Key);
            long bmp = MemSize.Bitmap(kv.Value);
            live += img + bmp;
            if (i < 12)
            {
                sb.AppendLine($"  live[{i}] image {MemSize.ImageLabel(kv.Key)}  bitmap {MemSize.Bytes(bmp)}");
                i++;
            }
        }
        if (Bitmaps.Count > 12)
            sb.AppendLine($"  … {Bitmaps.Count - 12} more live bitmaps");
        sb.AppendLine($"  live bytes (image+bitmap, may alias documents)  {MemSize.Bytes(live)}");

        sb.AppendLine($"GpuRetain grave  {Grave.Count}  (deferred dispose)");
        long grave = 0;
        for (int g = 0; g < Grave.Count; g++)
        {
            Item it = Grave[g];
            long n = MemSize.Image(it.Image) + MemSize.Bitmap(it.Bitmap);
            grave += n;
            sb.AppendLine($"  grave[{g}] lives={it.Lives}  {MemSize.ImageLabel(it.Image)}  bmp {MemSize.Bytes(MemSize.Bitmap(it.Bitmap))}  shader={(it.Shader != null)}");
        }
        sb.AppendLine($"  grave bytes  {MemSize.Bytes(grave)}");
    }

    public static void Attach(SKImage image, SKBitmap bitmap)
    {
        if (image == null || bitmap == null)
            return;
        Bitmaps[image] = bitmap;
    }

    public static void Retire(SKImage? image)
    {
        if (image == null || image.Handle == IntPtr.Zero)
            return;
        Bitmaps.Remove(image, out SKBitmap? bitmap);
        Grave.Add(new Item(image, bitmap, null, 3));
        Pump();
    }

    public static void RetireShader(SKShader? shader)
    {
        if (shader == null)
            return;
        Grave.Add(new Item(null, null, shader, 2));
        Pump();
    }

    private static void Pump()
    {
        if (_pumping)
            return;
        _pumping = true;
        Browser.Post(Step);
    }

    private static void Step()
    {
        try
        {
            for (int i = Grave.Count - 1; i >= 0; i--)
            {
                Item it = Grave[i];
                int lives = it.Lives - 1;
                if (lives > 0)
                {
                    Grave[i] = it with { Lives = lives };
                    continue;
                }

                Grave.RemoveAt(i);
                try { it.Image?.Dispose(); } catch { }
                try { it.Bitmap?.Dispose(); } catch { }
                try { it.Shader?.Dispose(); } catch { }
            }
        }
        catch (Exception ex)
        {
            Log.Warning("GpuRetain: " + ex.Message);
        }
        finally
        {
            _pumping = false;
            if (Grave.Count > 0)
                Pump();
        }
    }
}
