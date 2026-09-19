using System;
using System.IO;

namespace Raw75.Develop;

/// <summary>
/// Photographic EXIF & sensor metadata (Camera, Lens, Exposure parameters, Capture time).
/// </summary>
public sealed class PhotoMetadata
{
    public string CameraMake { get; set; } = "";
    public string CameraModel { get; set; } = "";
    public string LensModel { get; set; } = "";
    public float FocalLength { get; set; }
    public float FocalLengthIn35mm { get; set; }
    public float ShutterSpeed { get; set; }
    public float Aperture { get; set; }
    public float Iso { get; set; }
    public DateTime? CaptureTime { get; set; }
    public double? GpsLatitude { get; set; }
    public double? GpsLongitude { get; set; }
    public double? GpsAltitude { get; set; }
    public string Artist { get; set; } = "";
    public string Description { get; set; } = "";
    public string Copyright { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public int Orientation { get; set; } = 1;
    public long FileSizeBytes { get; set; }

    public bool HasGps => GpsLatitude.HasValue && GpsLongitude.HasValue;

    public string FormattedGps
    {
        get
        {
            if (!HasGps) return "—";
            double lat = GpsLatitude!.Value;
            double lon = GpsLongitude!.Value;
            char latRef = lat >= 0 ? 'N' : 'S';
            char lonRef = lon >= 0 ? 'E' : 'W';
            double absLat = Math.Abs(lat);
            double absLon = Math.Abs(lon);

            int latD = (int)Math.Floor(absLat);
            double latMrem = (absLat - latD) * 60.0;
            int latM = (int)Math.Floor(latMrem);
            double latS = (latMrem - latM) * 60.0;

            int lonD = (int)Math.Floor(absLon);
            double lonMrem = (absLon - lonD) * 60.0;
            int lonM = (int)Math.Floor(lonMrem);
            double lonS = (lonMrem - lonM) * 60.0;

            return $"{latD}°{latM}'{latS:0.#}\"{latRef}  {lonD}°{lonM}'{lonS:0.#}\"{lonRef}";
        }
    }

    public string FormattedLocation
    {
        get
        {
            if (!HasGps) return "—";
            string coords = FormattedGps;
            if (GpsAltitude.HasValue)
            {
                string altStr = GpsAltitude.Value >= 0
                    ? $"{GpsAltitude.Value:0.#} m"
                    : $"-{Math.Abs(GpsAltitude.Value):0.#} m";
                return $"{coords} ({altStr})";
            }
            return coords;
        }
    }

    public string CameraName
    {
        get
        {
            string mk = CameraMake.Trim();
            string md = CameraModel.Trim();
            if (string.IsNullOrEmpty(mk)) return string.IsNullOrEmpty(md) ? "Unknown Camera" : md;
            if (string.IsNullOrEmpty(md)) return mk;
            if (md.StartsWith(mk, StringComparison.OrdinalIgnoreCase)) return md;
            return $"{mk} {md}";
        }
    }

    public string LensName
    {
        get
        {
            string l = LensModel.Trim();
            return string.IsNullOrEmpty(l) ? "Unknown Lens" : l;
        }
    }

    public string FormattedShutter
    {
        get
        {
            if (ShutterSpeed <= 0.000001f) return "—";
            if (ShutterSpeed >= 1.0f)
            {
                return ShutterSpeed % 1.0f == 0 ? $"{ShutterSpeed:0}s" : $"{ShutterSpeed:0.0}s";
            }
            float denom = 1.0f / ShutterSpeed;
            int roundedDenom = (int)Math.Round(denom);
            return $"1/{roundedDenom}s";
        }
    }

    public string FormattedAperture
    {
        get
        {
            if (Aperture <= 0.01f) return "—";
            return $"ƒ/{Aperture:0.#}";
        }
    }

    public string FormattedIso
    {
        get
        {
            if (Iso <= 0.1f) return "—";
            return $"ISO {Iso:0}";
        }
    }

    public string FormattedFocal
    {
        get
        {
            if (FocalLength <= 0.1f) return "—";
            if (FocalLengthIn35mm > 0.1f && Math.Abs(FocalLengthIn35mm - FocalLength) > 1.0f)
            {
                return $"{FocalLength:0.#}mm ({FocalLengthIn35mm:0}mm eq)";
            }
            return $"{FocalLength:0.#}mm";
        }
    }

    public string FormattedExposure
    {
        get
        {
            var parts = new System.Collections.Generic.List<string>(4);
            if (ShutterSpeed > 0.000001f) parts.Add(FormattedShutter);
            if (Aperture > 0.01f) parts.Add(FormattedAperture);
            if (Iso > 0.1f) parts.Add(FormattedIso);
            if (FocalLength > 0.1f) parts.Add(FormattedFocal);
            return parts.Count > 0 ? string.Join("   ", parts) : "No exposure EXIF";
        }
    }

    public string FormattedDimensions
    {
        get
        {
            if (Width <= 0 || Height <= 0) return "—";
            double mp = (double)Width * Height / 1_000_000.0;
            return $"{Width} × {Height}  ({mp.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} MP)";
        }
    }

    public string FormattedFileSize
    {
        get
        {
            if (FileSizeBytes <= 0) return "—";
            if (FileSizeBytes >= 1024L * 1024L * 1024L)
            {
                double gb = FileSizeBytes / (1024.0 * 1024.0 * 1024.0);
                return $"{gb.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} GB";
            }
            if (FileSizeBytes >= 1024L * 1024L)
            {
                double mb = FileSizeBytes / (1024.0 * 1024.0);
                return $"{mb.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} MB";
            }
            if (FileSizeBytes >= 1024L)
            {
                double kb = FileSizeBytes / 1024.0;
                return $"{kb.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} KB";
            }
            return $"{FileSizeBytes} B";
        }
    }

    public string FormattedDateTime
    {
        get
        {
            return CaptureTime.HasValue
                ? CaptureTime.Value.ToString("yyyy-MM-dd  HH:mm", System.Globalization.CultureInfo.InvariantCulture)
                : "—";
        }
    }
}
