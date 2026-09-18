using System;
using Raw75.Develop;

namespace Raw75.Controls;

[Flags]
public enum PhotoFilter
{
    All = 0,
    Ready = 1,
    Favorite = 2
}

public static class PhotoFilterMatch
{
    public static bool Matches(PhotoDocument doc, PhotoFilter filter)
    {
        if (doc == null) return false;
        if (filter == PhotoFilter.All) return true;
        if ((filter & PhotoFilter.Ready) != 0 && !doc.IsReady) return false;
        if ((filter & PhotoFilter.Favorite) != 0 && !doc.IsFavorite) return false;
        return true;
    }
}
