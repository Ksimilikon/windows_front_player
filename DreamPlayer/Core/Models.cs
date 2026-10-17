// Owning C# copies of the core FFI structs. Strings / arrays are copied out of
// the core buffers; the buffers themselves are freed by CoreApi right away.

namespace DreamPlayer.Core;

/// track metadata (a copy of `FFITrackMetadata`).
public sealed record Track
{
    /// index row id (`null` — the track is not from the db).
    public long? Id { get; init; }
    public string? Path { get; init; }
    public string Title { get; init; } = "";
    public IReadOnlyList<string> Artists { get; init; } = [];
    public string? Album { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = [];
    public ulong DurationS { get; init; }
    public uint SampleRate { get; init; }
    public uint Bitrate { get; init; }
    public long ListenCount { get; init; }
    /// separate cover file from the db (the embedded one is fetched on demand).
    public string? CoverArt { get; init; }
    /// saved per-track volume (1.0 — default).
    public float Volume { get; init; } = 1f;
    public bool Invalid { get; init; }
    /// user color label, `#RRGGBB` when set by this frontend.
    public string? Color { get; init; }
    public string? Label { get; init; }

    public string ArtistsLine => string.Join(", ", Artists);
    public string GenresLine => string.Join(", ", Genres);
    public string DurationText => Format.Duration(DurationS);

    /// identity that survives reloads: the db id, or the path for external files.
    public string Key => Id is { } id ? $"id:{id}" : $"path:{Path}";

    internal static unsafe Track FromFfi(in FFITrackMetadata m)
    {
        var p = m.HasParams != 0 ? m.Params : default;
        return new Track
        {
            Id = m.HasId != 0 ? m.Id : null,
            Path = Native.Str(m.Path),
            Title = Native.Str(m.Title) ?? "",
            Artists = Native.Strs(m.Artists, m.ArtistsLen),
            Album = Native.Str(m.Album),
            Genres = Native.Strs(m.Genres, m.GenresLen),
            DurationS = p.DurationS,
            SampleRate = p.SampleRate,
            Bitrate = p.Bitrate,
            ListenCount = p.ListenCount,
            CoverArt = m.HasParams != 0 ? Native.Str(p.CoverArt) : null,
            Volume = m.Volume,
            Invalid = m.Invalid != 0,
            Color = Native.Str(m.Color),
            Label = Native.Str(m.UserLabel),
        };
    }
}

/// a db playlist (a copy of `FFIPlaylist`): name, track id order, cover, dates.
public sealed record PlaylistInfo(
    string? Name,
    IReadOnlyList<long> TrackIds,
    string? CoverArt,
    ulong? CreatedAt,
    ulong? UpdatedAt)
{
    internal static unsafe PlaylistInfo FromFfi(in FFIPlaylist p)
    {
        var ids = p.Tracks == 0 || p.TracksLen == 0
            ? []
            : new ReadOnlySpan<long>((void*)p.Tracks, checked((int)p.TracksLen)).ToArray();
        return new PlaylistInfo(
            Native.Str(p.Name),
            ids,
            Native.Str(p.CoverArt),
            p.HasCreatedAt != 0 ? p.CreatedAt : null,
            p.HasUpdatedAt != 0 ? p.UpdatedAt : null);
    }
}

/// what goes into an export packet.
public readonly record struct PackFlags(bool Audio, bool Names, bool Covers);

public static class Format
{
    public static string Duration(ulong secs)
    {
        var t = TimeSpan.FromSeconds(secs);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    public static string Size(long n) => n switch
    {
        < 1024 => $"{n} B",
        < 1024 * 1024 => $"{n / 1024.0:0.0} KiB",
        < 1024L * 1024 * 1024 => $"{n / (1024.0 * 1024):0.0} MiB",
        _ => $"{n / (1024.0 * 1024 * 1024):0.00} GiB",
    };

    public static string UnixDate(ulong? secs) => secs is { } s
        ? DateTimeOffset.FromUnixTimeSeconds((long)s).LocalDateTime.ToString("g")
        : "—";
}
