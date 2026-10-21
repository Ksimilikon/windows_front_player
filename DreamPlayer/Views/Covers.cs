// Cover images: bytes come from the core (`idx_get_track_cover` — the cover
// file or the one embedded in the audio tags), fetched off the UI thread and
// cached per track. List rows get small decoded thumbnails (CoverThumb) with a
// cache of their own; a playlist shows its cover file or, without one, the
// cover of its first song.
//
// A cover can change without its track changing (a synced file written over
// the local one, an embedded picture): `Invalidate` drops what was cached for
// the tracks / playlists behind the given files and tells the visible
// thumbnails to reload.

using DreamPlayer.Core;
using DreamPlayer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace DreamPlayer.Views;

public static class Covers
{
    private const int CacheSize = 64;
    private const int ThumbCacheSize = 600;
    /// the fetch itself is cached: concurrent requests (bottom bar, song tab,
    /// SMTC) for one track share a single core call. UI thread only.
    private static readonly Dictionary<string, Task<byte[]?>> Cache = [];
    private static readonly LinkedList<string> Order = new();
    /// decoded thumbnails (`null` — no cover), by `<cover key>|<px>`.
    private static readonly Dictionary<string, Task<BitmapImage?>> Thumbs = [];
    private static readonly LinkedList<string> ThumbOrder = new();
    /// thumbnails of a long list must not keep the core busy for the UI.
    private static readonly SemaphoreSlim ThumbFetches = new(2);

    /// covers changed: track keys (`Track.Key`) and playlist ids
    /// (`PlaylistId`). UI thread.
    public static event Action<IReadOnlySet<string>>? Invalidated;

    /// the cache key changes when the cover file of the track changes.
    public static string Key(Track t) => $"{t.Key}|{t.CoverArt}";

    public static string PlaylistId(PlaylistView pl) => pl.IsPool ? "pl:*" : $"pl:{pl.Name}";

    /// what a playlist shows: its cover file or the first song.
    public static string Key(PlaylistView pl) =>
        $"{PlaylistId(pl)}|{pl.CoverArt}|{(pl.Tracks.Count > 0 ? Key(pl.Tracks[0]) : "")}";

    public static Task<byte[]?> BytesAsync(Track t)
    {
        var key = Key(t);
        if (Cache.TryGetValue(key, out var cached))
            return cached;
        var fetch = Task.Run(() => CoreApi.Library.Cover(t));
        Cache[key] = fetch;
        Order.AddLast(key);
        while (Order.Count > CacheSize)
        {
            Cache.Remove(Order.First!.Value);
            Order.RemoveFirst();
        }
        return fetch;
    }

    public static void Forget(Track t) => Forget(t.Key + "|");

    private static void Forget(string prefix)
    {
        foreach (var key in Cache.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            Cache.Remove(key);
            Order.Remove(key);
        }
        foreach (var key in Thumbs.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            Thumbs.Remove(key);
            ThumbOrder.Remove(key);
        }
    }

    /// the files were written anew (sync, import, an embedded cover): drops
    /// the covers of the library tracks / playlists that use them — and of the
    /// playlists showing such a track — and reloads the visible ones. Call
    /// after the library was re-read.
    public static void Invalidate(IReadOnlySet<string> paths)
    {
        if (paths.Count == 0)
            return;
        var lib = Library.Instance;
        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in lib.Pool)
        {
            if ((t.Path is { } p && paths.Contains(p)) || (t.CoverArt is { } c && paths.Contains(c)))
            {
                changed.Add(t.Key);
                Forget(t);
            }
        }
        foreach (var pl in lib.Playlists)
        {
            var first = pl.Tracks.Count > 0 ? pl.Tracks[0].Key : null;
            if ((pl.CoverArt is { } c && paths.Contains(c)) || (first is not null && changed.Contains(first)))
            {
                changed.Add(PlaylistId(pl));
                Forget(PlaylistId(pl) + "|");
            }
        }
        if (changed.Count == 0)
            return;
        CoreLog.Debug("ui", $"covers changed: {changed.Count} track(s) / playlist(s)");
        Invalidated?.Invoke(changed);
    }

    /// a decoded image (`decodeSize` — target pixel width, 0 — original).
    /// `null` — no cover or not a picture the system can decode.
    public static async Task<BitmapImage?> ImageAsync(Track t, int decodeSize) =>
        await DecodeAsync(await BytesAsync(t), decodeSize, t.Title);

    /// a list thumbnail of a track; a cached one comes back completed.
    public static Task<BitmapImage?> ThumbAsync(Track t, int px) =>
        Thumb($"{Key(t)}|{px}", () => FetchThumbAsync(t), px, t.Title);

    /// a list thumbnail of a playlist: its cover file, else its first song.
    public static Task<BitmapImage?> ThumbAsync(PlaylistView pl, int px)
    {
        if (pl.CoverArt is { } file)
            return Thumb($"{Key(pl)}|{px}", () => ReadFileAsync(file), px, pl.DisplayName);
        return pl.Tracks.Count > 0 ? ThumbAsync(pl.Tracks[0], px) : Task.FromResult<BitmapImage?>(null);
    }

    private static Task<BitmapImage?> Thumb(string key, Func<Task<byte[]?>> fetch, int px, string what)
    {
        if (Thumbs.TryGetValue(key, out var cached))
        {
            ThumbOrder.Remove(key);
            ThumbOrder.AddLast(key);
            return cached;
        }
        var task = LoadThumbAsync(fetch, px, what);
        Thumbs[key] = task;
        ThumbOrder.AddLast(key);
        while (ThumbOrder.Count > ThumbCacheSize)
        {
            Thumbs.Remove(ThumbOrder.First!.Value);
            ThumbOrder.RemoveFirst();
        }
        return task;
    }

    private static async Task<BitmapImage?> LoadThumbAsync(Func<Task<byte[]?>> fetch, int px, string what) =>
        await DecodeAsync(await fetch(), px, what);

    private static Task<byte[]?> FetchThumbAsync(Track t)
    {
        // the bar / song tab may have the bytes already.
        if (Cache.TryGetValue(Key(t), out var cached))
            return cached;
        return Task.Run(async () =>
        {
            await ThumbFetches.WaitAsync();
            try
            {
                return CoreApi.Library.Cover(t, quiet: true);
            }
            finally
            {
                ThumbFetches.Release();
            }
        });
    }

    private static Task<byte[]?> ReadFileAsync(string path) => Task.Run(() =>
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            CoreLog.Debug("ui", $"cover {path} not read: {e.Message}");
            return null;
        }
    });

    private static async Task<BitmapImage?> DecodeAsync(byte[]? bytes, int decodeSize, string what)
    {
        if (bytes is null || bytes.Length == 0)
            return null;
        try
        {
            using var stream = await ToStreamAsync(bytes);
            var image = new BitmapImage();
            if (decodeSize > 0)
                image.DecodePixelWidth = decodeSize;
            await image.SetSourceAsync(stream);
            return image;
        }
        catch (Exception e)
        {
            CoreLog.Warn("ui", $"cover of \"{what}\" not decoded: {e.Message}");
            return null;
        }
    }

    public static async Task<InMemoryRandomAccessStream> ToStreamAsync(byte[] bytes)
    {
        var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        return stream;
    }
}

/// a square cover thumbnail of a list row: `Track` or `Playlist` (a note
/// glyph while there is no picture). Reloads when its item changes or its
/// cover is invalidated.
public sealed partial class CoverThumb : UserControl
{
    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(object), typeof(CoverThumb), new PropertyMetadata(null, (d, _) => ((CoverThumb)d).Reload(false)));

    public static readonly DependencyProperty PlaylistProperty = DependencyProperty.Register(
        nameof(Playlist), typeof(object), typeof(CoverThumb), new PropertyMetadata(null, (d, _) => ((CoverThumb)d).Reload(false)));

    private readonly Image _image = new() { Stretch = Stretch.UniformToFill };
    private readonly FontIcon _placeholder;
    private string? _key;

    public CoverThumb()
    {
        Width = Height = 40;
        IsTabStop = false;
        _placeholder = new FontIcon { FontSize = 16, Foreground = Ui.Muted };
        Content = new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = Ui.Res("DpSurface2"),
            Child = new Grid { Children = { _placeholder, _image } },
        };
        Loaded += (_, _) =>
        {
            Covers.Invalidated += OnInvalidated;
            Reload(false);
        };
        Unloaded += (_, _) => Covers.Invalidated -= OnInvalidated;
    }

    // typed `object` for XAML: its type info can't construct a PlaylistView.

    /// a `Core.Track`.
    public object? Track
    {
        get => GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    /// a `Services.PlaylistView`.
    public object? Playlist
    {
        get => GetValue(PlaylistProperty);
        set => SetValue(PlaylistProperty, value);
    }

    private void OnInvalidated(IReadOnlySet<string> changed)
    {
        var mine = Track is Track t ? changed.Contains(t.Key)
            : Playlist is PlaylistView pl && (changed.Contains(Covers.PlaylistId(pl))
                                              || (pl.Tracks.Count > 0 && changed.Contains(pl.Tracks[0].Key)));
        if (mine)
            Reload(true);
    }

    private async void Reload(bool force)
    {
        var t = Track as Track;
        var pl = Playlist as PlaylistView;
        _placeholder.Glyph = pl is not null ? "\uE8FD" : "\uE8D6";
        var key = t is not null ? Covers.Key(t) : pl is not null ? Covers.Key(pl) : null;
        if (key == _key && !force)
            return;
        _key = key;
        if (key is null)
        {
            Show(null);
            return;
        }
        // twice the size: sharp on scaled displays too.
        var px = (int)Math.Ceiling(Width * 2);
        var load = t is not null ? Covers.ThumbAsync(t, px) : Covers.ThumbAsync(pl!, px);
        if (!load.IsCompleted)
            Show(null);
        var image = await load;
        if (_key == key)
            Show(image);
    }

    private void Show(BitmapImage? image)
    {
        _image.Source = image;
        _placeholder.Visibility = Ui.Hidden(image is not null);
    }
}
