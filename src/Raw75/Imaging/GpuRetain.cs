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
