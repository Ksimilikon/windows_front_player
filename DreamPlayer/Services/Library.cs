// The library as the interface sees it: the "all songs" pool + db playlists.
// Playlist tracks are assembled from the pool by id (one pool query), like the
// TUI does. Reloaded after every db change.

using DreamPlayer.Core;

namespace DreamPlayer.Services;

public sealed class PlaylistView
{
    public required string Name { get; init; }
    public required IReadOnlyList<Track> Tracks { get; init; }
    /// the shared "all songs" pool (first in the list, can't be edited / deleted).
    public bool IsPool { get; init; }
    public string? CoverArt { get; init; }
    public ulong? CreatedAt { get; init; }
    public ulong? UpdatedAt { get; init; }

    public int Count => Tracks.Count;
    public string DisplayName => IsPool ? L.T("lib.all_songs") : Name;
    public string CountText => L.F("lib.tracks_count", Count);
    public string TotalDuration => Format.Duration((ulong)Tracks.Sum(t => (long)t.DurationS));
}

public sealed class Library
{
    public static Library Instance { get; } = new();

    public IReadOnlyList<Track> Pool { get; private set; } = [];
    /// `[0]` — the pool, then db playlists by name.
    public IReadOnlyList<PlaylistView> Playlists { get; private set; } = [];

    /// raised after every reload (on the thread that reloaded).
    public event Action? Changed;

    public PlaylistView? Find(string name, bool pool = false) =>
        Playlists.FirstOrDefault(p => p.IsPool == pool && (pool || p.Name == name));

    public bool Exists(string name) => Playlists.Any(p => !p.IsPool && p.Name == name);

    public Track? TrackById(long id) => Pool.FirstOrDefault(t => t.Id == id);

    /// deletes a song from the device: its file (already gone is fine), then
    /// its library row. `null` — done, otherwise what failed.
    public static string? DeleteFromDevice(Track t)
    {
        if (t.Path is { } path)
        {
            try
            {
                File.Delete(path);
                CoreLog.Info("lib", $"deleted file {path}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                CoreLog.Warn("lib", $"failed to delete {path}: {e.Message}");
                return $"{path}: {e.Message}";
            }
        }
        if (t.Id is { } id && CoreApi.Library.Remove(id) is var st && st != IdxStatus.Ok)
            return L.F("msg.row_kept", t.Title, CoreApi.Describe(st));
        return null;
    }

    /// re-reads the pool and playlists from the core. `false` — the core
    /// failed (the previous state is kept).
    public bool Reload()
    {
        var pool = CoreApi.Library.Tracks();
        var infos = CoreApi.Library.Playlists();
        if (pool is null || infos is null)
            return false;
        var byId = pool.Where(t => t.Id is not null).ToDictionary(t => t.Id!.Value);

        var playlists = new List<PlaylistView>
        {
            new() { Name = "", Tracks = pool, IsPool = true },
        };
        foreach (var info in infos.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var tracks = info.TrackIds
                .Select(id => byId.GetValueOrDefault(id))
                .OfType<Track>()
                .ToList();
            var missing = info.TrackIds.Count - tracks.Count;
            if (missing > 0)
                CoreLog.Warn("lib", $"playlist \"{info.Name}\": {missing} track id(s) not in the pool");
            playlists.Add(new PlaylistView
            {
                Name = info.Name ?? L.T("lib.unnamed"),
                Tracks = tracks,
                CoverArt = info.CoverArt,
                CreatedAt = info.CreatedAt,
                UpdatedAt = info.UpdatedAt,
            });
        }
        Pool = pool;
        Playlists = playlists;
        CoreLog.Info("lib", $"library loaded: {pool.Count} track(s), {playlists.Count - 1} playlist(s)");
        Changed?.Invoke();
        return true;
    }
}
