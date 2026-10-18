using SixLabors.ImageSharp.Metadata.Profiles.Exif;

namespace Raw75.Imaging;

internal static class ExifProfileExtensions
{
    /// <summary>ImageSharp 3 replaced <c>GetValue</c> with <c>TryGetValue</c>; null when the tag is absent.</summary>
    public static IExifValue<T>? GetValue<T>(this ExifProfile profile, ExifTag<T> tag)
        => profile.TryGetValue(tag, out IExifValue<T>? value) ? value : null;
}
