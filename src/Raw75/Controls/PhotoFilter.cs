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
        bool wantReady = (filter & PhotoFilter.Ready) != 0;
        bool wantFavorite = (filter & PhotoFilter.Favorite) != 0;
        if (wantReady && wantFavorite)
            return doc.IsReady || doc.IsFavorite;
        if (wantReady)
            return doc.IsReady;
        if (wantFavorite)
            return doc.IsFavorite;
        return true;
    }
}
