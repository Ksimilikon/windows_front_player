// Cover images: bytes come from the core (`idx_get_track_cover` — the cover
// file or the one embedded in the audio tags), fetched off the UI thread and
// cached per track.

using DreamPlayer.Core;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace DreamPlayer.Views;

public static class Covers
{
    private const int CacheSize = 64;
    /// the fetch itself is cached: concurrent requests (bottom bar, song tab,
    /// SMTC) for one track share a single core call. UI thread only.
    private static readonly Dictionary<string, Task<byte[]?>> Cache = [];
    private static readonly LinkedList<string> Order = new();

    /// the cache key changes when the cover file of the track changes.
    private static string Key(Track t) => $"{t.Key}|{t.CoverArt}";

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

    public static void Forget(Track t)
    {
        foreach (var key in Cache.Keys.Where(k => k.StartsWith(t.Key + "|", StringComparison.Ordinal)).ToList())
        {
            Cache.Remove(key);
            Order.Remove(key);
        }
    }

    /// a decoded image (`decodeSize` — target pixel width, 0 — original).
    /// `null` — no cover or not a picture the system can decode.
    public static async Task<BitmapImage?> ImageAsync(Track t, int decodeSize)
    {
        var bytes = await BytesAsync(t);
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
            Services.CoreLog.Warn("ui", $"cover of \"{t.Title}\" not decoded: {e.Message}");
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
