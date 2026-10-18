// Safe, logged wrapper over Native. Every call goes to CoreLog as
// `function(args) → Status (time)`: errors red, decisions (not found, outdated)
// yellow. Core buffers are copied into managed objects and freed right away
// with the paired `*_free_*`. Polling calls (position, pause state, ...) are
// "quiet": logged only on error or with CoreLog.Verbose.

using System.Diagnostics;
using System.Runtime.InteropServices;
using DreamPlayer.Services;

namespace DreamPlayer.Core;

public static unsafe class CoreApi
{
    /// packet format version the frontend writes (`codec::Version::V0_1`).
    public const uint PacketVersion = 1;

    private static ulong _panics;
    private static int _busy;

    public static bool Initialized { get; private set; }

    /// a long core operation (indexing, packing, import) runs on a background
    /// thread and may hold the core lock for a while: pollers skip their tick
    /// meanwhile instead of blocking the UI thread on the lock.
    public static bool Busy => Volatile.Read(ref _busy) > 0;

    /// runs `f` on a background thread with `Busy` raised.
    public static Task<T> RunLong<T>(Func<T> f) => Task.Run(() =>
    {
        Interlocked.Increment(ref _busy);
        try
        {
            return f();
        }
        finally
        {
            Interlocked.Decrement(ref _busy);
        }
    });

    // ───────────────────────── logging core ─────────────────────────

    private static TStatus Run<TStatus>(string area, string call, Func<TStatus> f, bool quiet = false)
        where TStatus : struct, Enum
    {
        var sw = Stopwatch.StartNew();
        TStatus st;
        try
        {
            st = f();
        }
        catch (DllNotFoundException e)
        {
            CoreLog.Error(area, $"{call} → core library not found: {e.Message}");
            throw;
        }
        catch (EntryPointNotFoundException e)
        {
            CoreLog.Error(area, $"{call} → missing in the core library (old build?): {e.Message}");
            throw;
        }
        sw.Stop();
        var level = Classify(st);
        if (!quiet || level >= LogLevel.Warn || CoreLog.Verbose)
        {
            var text = $"{call} → {st} ({sw.Elapsed.TotalMilliseconds:0.0} ms)";
            if (level >= LogLevel.Warn)
                text += $": {Describe(st)}";
            CoreLog.Write(quiet && level < LogLevel.Warn ? LogLevel.Debug : level, area, text);
        }
        if (st.ToString() == "Panic")
            CheckPanics();
        return st;
    }

    private static LogLevel Classify<TStatus>(TStatus st) where TStatus : struct, Enum => (object)st switch
    {
        EngineStatus.Ok or IdxStatus.Ok or CdcStatus.Ok or InitStatus.Ok => LogLevel.Ok,
        EngineStatus.ConfigWriteFailed or IdxStatus.NotFound or CdcStatus.Outdated => LogLevel.Warn,
        _ => LogLevel.Error,
    };

    /// human-readable (localized) description of any core status.
    public static string Describe<TStatus>(TStatus st) where TStatus : struct, Enum =>
        L.TOr($"st.{typeof(TStatus).Name}.{st}", st.ToString());

    private static string Q(string? s) => s is null ? "null" : $"\"{s}\"";

    /// logs core panics that happened since the last check (also the ones on
    /// core threads that no status reported). Call periodically.
    public static void CheckPanics()
    {
        if (!Initialized)
            return;
        var count = Native.CorePanicCount();
        if (count == _panics)
            return;
        var p = Native.CoreLastPanic();
        var msg = Native.Str(p) ?? "?";
        Native.IdxFreeString(p);
        CoreLog.Error("panic", $"core panic #{count} ({count - _panics} new): {msg}");
        _panics = count;
    }

    public static ulong PanicCount => Initialized ? Native.CorePanicCount() : 0;

    // ───────────────────────── init ─────────────────────────

    public static InitStatus Init(string cache, string config, string data, string music)
    {
        // works before `init` and is missing in cores with the old (status-less)
        // ABI: such a core throws EntryPointNotFoundException here, before an
        // `init` with a mismatched signature is called.
        var panics = Native.CorePanicCount();
        var st = Run("init", $"init(cache={Q(cache)}, config={Q(config)}, data={Q(data)}, music={Q(music)}, need_db=true)",
            () => Native.Init(cache, config, data, music, true));
        Initialized = st is InitStatus.Ok or InitStatus.AlreadyInitialized;
        _panics = panics;
        return st;
    }

    // ───────────────────────── engine ─────────────────────────

    public static class Engine
    {
        private const string A = "engine";

        /// plays a track: by id if it is in the db, otherwise by path.
        public static EngineStatus Play(Track t)
        {
            TrackEndSignal.Reset();
            if (t.Id is { } id)
                return Run(A, $"engine_add_track_id({id}, on_end)", () => Native.EngineAddTrackId(id, TrackEndSignal.Pointer));
            if (t.Path is { } path)
                return Run(A, $"engine_add_track_path({Q(path)}, on_end)", () => Native.EngineAddTrackPath(path, TrackEndSignal.Pointer));
            CoreLog.Error(A, $"track \"{t.Title}\" has neither id nor path");
            return EngineStatus.TrackNotFound;
        }

        public static EngineStatus PlayPause() => Run(A, "engine_play_pause()", Native.EnginePlayPause);
        public static EngineStatus Resume() => Run(A, "engine_play()", Native.EnginePlay);
        public static EngineStatus Pause() => Run(A, "engine_pause()", Native.EnginePause);
        public static EngineStatus Stop() => Run(A, "engine_stop()", Native.EngineStop);
        public static EngineStatus SetSpeed(float v) => Run(A, $"engine_set_speed({v:0.00})", () => Native.EngineSetSpeed(v));
        public static EngineStatus Seek(ulong secs) => Run(A, $"engine_set_seek({secs})", () => Native.EngineSetSeek(secs));

        /// quiet: a slider drag calls it on every step (the db write that
        /// follows is logged).
        public static EngineStatus SetTrackVolume(float v) =>
            Run(A, $"engine_set_volume_track({v:0.00})", () => Native.EngineSetVolumeTrack(v), quiet: true);

        public static EngineStatus SetMasterVolume(float v) =>
            Run(A, $"engine_set_volume_master({v:0.00})", () => Native.EngineSetVolumeMaster(v));

        public static ulong Position()
        {
            ulong v = 0;
            Run(A, "engine_get_seek()", () => Native.EngineGetSeek(out v), quiet: true);
            return v;
        }

        public static ulong Duration()
        {
            ulong v = 0;
            Run(A, "engine_get_duration()", () => Native.EngineGetDuration(out v), quiet: true);
            return v;
        }

        public static bool IsPaused()
        {
            var v = false;
            Run(A, "engine_is_pause()", () => Native.EngineIsPause(out v), quiet: true);
            return v;
        }

        public static bool IsEmpty()
        {
            var v = true;
            var st = Run(A, "engine_is_empty()", () => Native.EngineIsEmpty(out v), quiet: true);
            return st != EngineStatus.Ok || v;
        }

        public static float TrackVolume()
        {
            var v = 1f;
            Run(A, "engine_get_volume_track()", () => Native.EngineGetVolumeTrack(out v), quiet: true);
            return v;
        }

        public static float MasterVolume()
        {
            var v = 1f;
            var st = Run(A, "engine_get_volume_master()", () => Native.EngineGetVolumeMaster(out v));
            return st == EngineStatus.Ok ? v : 1f;
        }

        public static IReadOnlyList<string> OutputDevices()
        {
            FFIStringList list = default;
            var st = Run(A, "engine_get_output_devices()", () => Native.EngineGetOutputDevices(out list));
            if (st != EngineStatus.Ok)
                return [];
            var result = Native.Strs(list.Ptr, list.Len);
            Native.EngineFreeOutputDevices(list);
            return result;
        }

        public static string? OutputDevice()
        {
            nint p = 0;
            var st = Run(A, "engine_get_output_device()", () => Native.EngineGetOutputDevice(out p));
            if (st != EngineStatus.Ok)
                return null;
            var name = Native.Str(p);
            Native.IdxFreeString(p);
            return name;
        }

        public static EngineStatus SetOutputDevice(string name) =>
            Run(A, $"engine_set_output_device({Q(name)})", () => Native.EngineSetOutputDevice(name));
    }

    // ───────────────────────── library (db) ─────────────────────────

    public static class Library
    {
        private const string A = "idx";

        /// the whole track pool; `null` on error.
        public static List<Track>? Tracks()
        {
            FFITracksList list = default;
            var st = Run(A, "idx_get_tracks()", () => Native.IdxGetTracks(out list));
            if (st != IdxStatus.Ok)
                return null;
            var result = new List<Track>((int)list.Len);
            var items = (FFITrackMetadata*)list.Ptr;
            for (nuint i = 0; i < list.Len; i++)
                result.Add(Track.FromFfi(items[i]));
            Native.IdxFreeTracksList(list);
            CoreLog.Debug(A, $"idx_get_tracks: {result.Count} track(s)");
            return result;
        }

        public static List<PlaylistInfo>? Playlists()
        {
            FFIPlaylistsList list = default;
            var st = Run(A, "idx_get_playlists()", () => Native.IdxGetPlaylists(out list));
            if (st != IdxStatus.Ok)
                return null;
            var result = new List<PlaylistInfo>((int)list.Len);
            var items = (FFIPlaylist*)list.Ptr;
            for (nuint i = 0; i < list.Len; i++)
                result.Add(PlaylistInfo.FromFfi(items[i]));
            Native.IdxFreePlaylistsList(list);
            CoreLog.Debug(A, $"idx_get_playlists: {result.Count} playlist(s)");
            return result;
        }

        /// one track by id (or by path, which takes priority).
        public static Track? Get(long id, string? path = null)
        {
            FFITrackMetadata meta = default;
            var st = Run(A, $"idx_get_track({id}, {Q(path)})", () => Native.IdxGetTrack(id, path, out meta));
            if (st != IdxStatus.Ok)
                return null;
            var track = Track.FromFfi(meta);
            Native.IdxFreeTrackMetadata(meta);
            return track;
        }

        /// cover bytes to display (cover file or the embedded one); `null` —
        /// no cover or an error.
        public static byte[]? Cover(Track t)
        {
            nint p = 0;
            nuint len = 0;
            var id = t.Id ?? 0;
            var path = t.Id is null ? t.Path : null;
            var st = Run(A, $"idx_get_track_cover({id}, {Q(path)})", () => Native.IdxGetTrackCover(id, path, out p, out len));
            if (st != IdxStatus.Ok || p == 0)
                return null;
            var bytes = new ReadOnlySpan<byte>((void*)p, checked((int)len)).ToArray();
            Native.IdxFreeBytes(p, len);
            CoreLog.Debug(A, $"idx_get_track_cover: {Format.Size(bytes.Length)}");
            return bytes;
        }

        public static IdxStatus IndexDir(string? dir) =>
            Run(A, $"idx_index_dir({Q(dir)})", () => Native.IdxIndexDir(dir));

        /// `title`/`artists` null — leave as is.
        public static IdxStatus SetMeta(long id, string? title, IReadOnlyList<string>? artists)
        {
            if (artists is null)
                return Run(A, $"idx_set_track_meta({id}, {Q(title)}, null)",
                    () => Native.IdxSetTrackMeta(id, title, null, 0));
            using var arr = new Utf8Array(artists);
            return Run(A, $"idx_set_track_meta({id}, {Q(title)}, [{string.Join(", ", artists)}])",
                () => Native.IdxSetTrackMeta(id, title, arr.Ptr, arr.Len));
        }

        public static IdxStatus SetAlbum(long id, string? album) =>
            Run(A, $"idx_set_track_album({id}, {Q(album)})", () => Native.IdxSetTrackAlbum(id, album));

        public static IdxStatus SetGenres(long id, IReadOnlyList<string> genres)
        {
            using var arr = new Utf8Array(genres);
            return Run(A, $"idx_set_track_genres({id}, [{string.Join(", ", genres)}])",
                () => Native.IdxSetTrackGenres(id, arr.Ptr, arr.Len));
        }

        public static IdxStatus SetCover(long id, string? path) =>
            Run(A, $"idx_set_track_cover({id}, {Q(path)})", () => Native.IdxSetTrackCover(id, path));

        public static IdxStatus SetColor(long id, string? color) =>
            Run(A, $"idx_set_track_color({id}, {Q(color)})", () => Native.IdxSetTrackColor(id, color));

        public static IdxStatus SetLabel(long id, string? label) =>
            Run(A, $"idx_set_track_label({id}, {Q(label)})", () => Native.IdxSetTrackLabel(id, label));

        public static IdxStatus SetInvalid(long id, bool invalid) =>
            Run(A, $"idx_set_track_invalid({id}, {invalid})", () => Native.IdxSetTrackInvalid(id, invalid));

        public static IdxStatus SetVolume(long id, float volume) =>
            Run(A, $"idx_set_track_volume({id}, {volume:0.00})", () => Native.IdxSetTrackVolume(id, volume));

        public static IdxStatus SetPath(long id, string path) =>
            Run(A, $"idx_set_track_path({id}, {Q(path)})", () => Native.IdxSetTrackPath(id, path));

        public static IdxStatus Remove(long id) =>
            Run(A, $"idx_remove_track({id})", () => Native.IdxRemoveTrack(id));

        /// adds files to the library; the number written, `null` on error.
        public static ulong? AddTracks(IReadOnlyList<string> paths)
        {
            using var arr = new Utf8Array(paths);
            ulong added = 0;
            var st = Run(A, $"idx_add_tracks({paths.Count} path(s))", () => Native.IdxAddTracks(arr.Ptr, arr.Len, out added));
            return st == IdxStatus.Ok ? added : null;
        }

        public static ulong? RemoveInvalid()
        {
            ulong removed = 0;
            var st = Run(A, "idx_remove_invalid_tracks()", () => Native.IdxRemoveInvalidTracks(out removed));
            return st == IdxStatus.Ok ? removed : null;
        }

        /// creates / rewrites playlist `name`. `cover` is written as is: pass
        /// the current cover to keep it (null clears it).
        public static IdxStatus SavePlaylist(string name, IReadOnlyList<long> ids, string? cover)
        {
            var arr = ids.ToArray();
            fixed (long* p = arr)
            {
                var ptr = p;
                return Run(A, $"idx_save_playlist({Q(name)}, {arr.Length} id(s), cover={Q(cover)})",
                    () => Native.IdxSavePlaylist(name, ptr, (nuint)arr.Length, cover));
            }
        }

        public static IdxStatus RemovePlaylist(string name) =>
            Run(A, $"idx_remove_playlist({Q(name)})", () => Native.IdxRemovePlaylist(name));
    }

    // ───────────────────────── packets (cdc) ─────────────────────────

    public static class Packets
    {
        private const string A = "cdc";

        public static (CdcStatus, byte[]?) PackLibrary(PackFlags f)
        {
            nint p = 0;
            nuint len = 0;
            var st = Run(A, $"cdc_pack_library(v{PacketVersion}, audio={f.Audio}, names={f.Names}, covers={f.Covers})",
                () => Native.CdcPackLibrary(PacketVersion, f.Audio, f.Names, f.Covers, out p, out len));
            return (st, TakeBytes(st, p, len));
        }

        public static (CdcStatus, byte[]?) PackTracks(IReadOnlyList<long> ids, PackFlags f)
        {
            nint p = 0;
            nuint len = 0;
            var arr = ids.ToArray();
            CdcStatus st;
            fixed (long* ptr = arr)
            {
                var idsPtr = ptr;
                st = Run(A, $"cdc_pack_tracks(v{PacketVersion}, {arr.Length} id(s), audio={f.Audio}, names={f.Names})",
                    () => Native.CdcPackTracks(PacketVersion, idsPtr, (nuint)arr.Length, f.Audio, f.Names, out p, out len));
            }
            return (st, TakeBytes(st, p, len));
        }

        public static (CdcStatus, byte[]?) PackPlaylists(IReadOnlyList<string> names, PackFlags f)
        {
            nint p = 0;
            nuint len = 0;
            using var arr = new Utf8Array(names);
            var st = Run(A, $"cdc_pack_playlists(v{PacketVersion}, [{string.Join(", ", names)}], audio={f.Audio}, names={f.Names}, covers={f.Covers})",
                () => Native.CdcPackPlaylists(PacketVersion, arr.Ptr, arr.Len, f.Audio, f.Names, f.Covers, out p, out len));
            return (st, TakeBytes(st, p, len));
        }

        private static byte[]? TakeBytes(CdcStatus st, nint p, nuint len)
        {
            if (st != CdcStatus.Ok)
                return null;
            if (p == 0)
                return [];
            var bytes = new ReadOnlySpan<byte>((void*)p, checked((int)len)).ToArray();
            Native.CdcFreeBytes(p, len);
            CoreLog.Debug(A, $"packet: {Format.Size(bytes.Length)}");
            return bytes;
        }

        /// import step 1: `cdc_import_prepare`. On `Ok` the session lists the
        /// files to write; it must be committed or disposed (= cancel).
        public static (CdcStatus, ImportSession?) Prepare(byte[] packet, bool force)
        {
            nint session = 0;
            FFIImportFiles files = default;
            CdcStatus st;
            fixed (byte* p = packet)
            {
                var ptr = p;
                st = Run(A, $"cdc_import_prepare({Format.Size(packet.Length)}, force={force})",
                    () => Native.CdcImportPrepare(ptr, (nuint)packet.Length, force, out session, out files));
            }
            if (st != CdcStatus.Ok || session == 0)
                return (st, null);
            return (st, new ImportSession(session, files));
        }
    }

    /// file to write for an import: points into the session memory.
    public sealed class ImportFile
    {
        internal nint Data;
        internal nuint DataLen;

        public required ImportFileKind Kind { get; init; }
        /// suggested OS-safe name (collisions are resolved by the frontend).
        public required string Name { get; init; }
        public long Length => (long)DataLen;

        /// a read-only view of the bytes; valid while the session is open.
        public Stream OpenRead() => new UnmanagedMemoryStream((byte*)Data, (long)DataLen);
    }

    /// an import between `cdc_import_prepare` and commit / cancel.
    public sealed class ImportSession : IDisposable
    {
        private nint _session;

        internal ImportSession(nint session, FFIImportFiles files)
        {
            _session = session;
            var list = new List<ImportFile>((int)files.Len);
            var items = (FFIImportFile*)files.Ptr;
            for (nuint i = 0; i < files.Len; i++)
            {
                list.Add(new ImportFile
                {
                    Kind = items[i].Kind,
                    Name = Native.Str(items[i].Name) ?? $"file{i}",
                    Data = items[i].Data,
                    DataLen = items[i].DataLen,
                });
            }
            Files = list;
            CoreLog.Debug("cdc", $"import session: {list.Count} file(s) to write");
        }

        public IReadOnlyList<ImportFile> Files { get; }

        /// step 3: `paths[i]` — where `Files[i]` was written (null — not
        /// written). Always closes the session.
        public CdcStatus Commit(IReadOnlyList<string?> paths)
        {
            if (_session == 0)
                throw new ObjectDisposedException(nameof(ImportSession));
            var s = _session;
            _session = 0;
            using var arr = new Utf8Array(paths);
            return Run("cdc", $"cdc_import_commit({paths.Count} path(s), {paths.Count(p => p is null)} skipped)",
                () => Native.CdcImportCommit(s, arr.Ptr, arr.Len));
        }

        public void Dispose()
        {
            if (_session == 0)
                return;
            var s = _session;
            _session = 0;
            Native.CdcImportCancel(s);
            CoreLog.Info("cdc", "cdc_import_cancel()");
        }
    }
}
