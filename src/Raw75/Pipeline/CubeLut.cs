using System;
using System.Globalization;
using System.IO;
using SkiaSharp;

namespace Raw75.Pipeline;

/// <summary>
/// Adobe <c>.cube</c> 3D LUT → 2D unwrap (width = size*size, height = size).
/// Layout: x = r + b*size, y = g. Red varies fastest in the file.
/// </summary>
public static class CubeLut
{
    public static SKImage? LoadCubeAs2d(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            string text = File.ReadAllText(path);
            if (!TryParse(text, out int size, out float[] rgb))
                return null;

            int width = size * size;
            int height = size;
            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            using var bmp = new SKBitmap(info);
            IntPtr ptr = bmp.GetPixels();
            if (ptr == IntPtr.Zero)
                return null;

            unsafe
            {
                byte* dst = (byte*)ptr;
                int stride = bmp.RowBytes;
                int i = 0;
                for (int bz = 0; bz < size; bz++)
                {
                    for (int gy = 0; gy < size; gy++)
                    {
                        for (int rx = 0; rx < size; rx++)
                        {
                            float rf = rgb[i++];
                            float gf = rgb[i++];
                            float bf = rgb[i++];
                            int x = rx + bz * size;
                            int y = gy;
                            byte* p = dst + y * stride + x * 4;
                            p[0] = ToByte(rf);
                            p[1] = ToByte(gf);
                            p[2] = ToByte(bf);
                            p[3] = 255;
                        }
                    }
                }
            }

            bmp.SetImmutable();
            return SKImage.FromBitmap(bmp);
        }
        catch
        {
            return null;
        }
    }

    private static byte ToByte(float v)
    {
        int i = (int)MathF.Round(v * 255f);
        if (i < 0) return 0;
        if (i > 255) return 255;
        return (byte)i;
    }

    private static bool TryParse(string text, out int size, out float[] rgb)
    {
        size = 0;
        rgb = Array.Empty<float>();
        string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.None);
        int n = 0;
        float[]? data = null;
        int di = 0;

        for (int li = 0; li < lines.Length; li++)
        {
            string raw = lines[li].Trim();
            if (raw.Length == 0 || raw[0] == '#')
                continue;

            int sp = raw.IndexOf(' ');
            string head = sp < 0 ? raw : raw.Substring(0, sp);
            if (head.Equals("TITLE", StringComparison.OrdinalIgnoreCase) ||
                head.Equals("DOMAIN_MIN", StringComparison.OrdinalIgnoreCase) ||
                head.Equals("DOMAIN_MAX", StringComparison.OrdinalIgnoreCase) ||
                head.Equals("LUT_1D_SIZE", StringComparison.OrdinalIgnoreCase) ||
                head.Equals("LUT_3D_INPUT_RANGE", StringComparison.OrdinalIgnoreCase))
                continue;

            if (head.Equals("LUT_3D_SIZE", StringComparison.OrdinalIgnoreCase))
            {
                string rest = sp < 0 ? "" : raw.Substring(sp + 1).Trim();
                if (!int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out size))
                    return false;
                if (size < 2 || size > 256)
                    return false;
                n = size * size * size;
                data = new float[n * 3];
                continue;
            }

            if (data == null)
                continue;

            string[] tok = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            for (int t = 0; t < tok.Length; t++)
            {
                if (!float.TryParse(tok[t], NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                    return false;
                if (di >= data.Length)
                    return false;
                data[di++] = v;
            }
        }

        if (data == null || n == 0 || di != data.Length)
            return false;

        rgb = data;
        return true;
    }
}
