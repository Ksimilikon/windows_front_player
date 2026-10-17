// Search and sorting for track lists (playlists tab, now playing, editors).

using DreamPlayer.Core;

namespace DreamPlayer.Services;

public enum SortKey
{
    /// the playlist's own order.
    Order,
    Title,
    Artist,
    Album,
    Duration,
    Listens,
}

public static class TrackFilter
{
    public static readonly SortKey[] Keys = Enum.GetValues<SortKey>();

    /// every word of the query must occur in title / artists / album / genres
    /// / label (case-insensitive).
    public static bool Matches(Track t, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;
        var hay = $"{t.Title}\n{t.ArtistsLine}\n{t.Album}\n{t.GenresLine}\n{t.Label}";
        return query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(w => hay.Contains(w, StringComparison.CurrentCultureIgnoreCase));
    }

    public static IEnumerable<Track> Sort(IEnumerable<Track> tracks, SortKey key, bool desc)
    {
        if (key == SortKey.Order)
            return desc ? tracks.Reverse() : tracks;
        var cmp = StringComparer.CurrentCultureIgnoreCase;
        IOrderedEnumerable<Track> sorted = key switch
        {
            SortKey.Title => tracks.OrderBy(t => t.Title, cmp),
            SortKey.Artist => tracks.OrderBy(t => t.ArtistsLine, cmp).ThenBy(t => t.Album, cmp),
            SortKey.Album => tracks.OrderBy(t => t.Album ?? "", cmp).ThenBy(t => t.Title, cmp),
            SortKey.Duration => tracks.OrderBy(t => t.DurationS),
            _ => tracks.OrderBy(t => t.ListenCount),
        };
        return desc ? sorted.Reverse() : sorted;
    }

    public static string Label(SortKey key) => L.T($"sort.{key.ToString().ToLowerInvariant()}");
}
