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
    /// packet format version the frontend writes (`codec::Version::V0_13`:
    /// deleted playlists; songs with their dates, db metadata and covers;
    /// playlist entry ids and removed entries, so an import merges playlists).
    public const uint PacketVersion = 4;

    /// a non-null pointer for an empty C string array (0 elements are read).
    private static readonly nint* EmptyList = (nint*)NativeMemory.AllocZeroed((nuint)sizeof(nint));

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
        EngineStatus.Ok or IdxStatus.Ok or CdcStatus.Ok or InitStatus.Ok or ConStatus.Ok or SyncStatus.Ok => LogLevel.Ok,
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

        /// songs that are in no playlist (the orphan collector); `null` on
        /// error.
        public static List<Track>? Orphans(OrphanFilter filter)
        {
            FFITracksList list = default;
            var st = Run(A, $"idx_get_orphan_tracks({filter})", () => Native.IdxGetOrphanTracks(filter, out list));
            if (st != IdxStatus.Ok)
                return null;
            var result = new List<Track>((int)list.Len);
            var items = (FFITrackMetadata*)list.Ptr;
            for (nuint i = 0; i < list.Len; i++)
                result.Add(Track.FromFfi(items[i]));
            Native.IdxFreeTracksList(list);
            CoreLog.Debug(A, $"idx_get_orphan_tracks: {result.Count} track(s)");
            return result;
        }

        /// the mark "ignore orphanhood": the collector stops listing the song.
        public static IdxStatus SetIgnoreOrphan(long id, bool ignore) =>
            Run(A, $"idx_set_track_ignore_orphan({id}, {ignore})", () => Native.IdxSetTrackIgnoreOrphan(id, ignore));

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
        /// no cover or an error. `quiet` — list thumbnails (one call per row).
        public static byte[]? Cover(Track t, bool quiet = false)
        {
            nint p = 0;
            nuint len = 0;
            var id = t.Id ?? 0;
            var path = t.Id is null ? t.Path : null;
            var st = Run(A, $"idx_get_track_cover({id}, {Q(path)})", () => Native.IdxGetTrackCover(id, path, out p, out len), quiet);
            if (st != IdxStatus.Ok || p == 0)
                return null;
            var bytes = new ReadOnlySpan<byte>((void*)p, checked((int)len)).ToArray();
            Native.IdxFreeBytes(p, len);
            CoreLog.Debug(A, $"idx_get_track_cover: {Format.Size(bytes.Length)}");
            return bytes;
        }

        public static IdxStatus IndexDir(string? dir) =>
            Run(A, $"idx_index_dir({Q(dir)})", () => Native.IdxIndexDir(dir));

        /// writes the tags into the audio file of the track, then into the db
        /// (the file is dated as changed). A null field — leave as is; lists
        /// are rewritten entirely (empty — clear), an empty album clears it.
        /// `WriteFailed` — the file couldn't be written, the db is unchanged.
        public static IdxStatus SetMeta(long id, string? title, IReadOnlyList<string>? artists,
            string? album, IReadOnlyList<string>? genres)
        {
            using var a = artists is null ? null : new Utf8Array(artists);
            using var g = genres is null ? null : new Utf8Array(genres);
            return Run(A, $"idx_set_track_meta({id}, {Q(title)}, {List(artists)}, {Q(album)}, {List(genres)})",
                () => Native.IdxSetTrackMeta(id, title, Ptr(a), a?.Len ?? 0, album, Ptr(g), g?.Len ?? 0));

            static string List(IReadOnlyList<string>? l) => l is null ? "null" : $"[{string.Join(", ", l)}]";
            // null — leave the list as is, so an empty one (clear) still needs a pointer.
            static nint* Ptr(Utf8Array? arr) => arr is null ? null : arr.Len == 0 ? EmptyList : arr.Ptr;
        }

        public static IdxStatus SetCover(long id, string? path) =>
            Run(A, $"idx_set_track_cover({id}, {Q(path)})", () => Native.IdxSetTrackCover(id, path));

        /// writes the picture (png / jpg) into the tags of the track's audio
        /// file as the front cover; the core dates the file as changed, so it
        /// syncs again. The separate cover file of the track stays as it is.
        public static IdxStatus EmbedCover(long id, string imagePath) =>
            Run(A, $"idx_embed_track_cover({id}, {Q(imagePath)})", () => Native.IdxEmbedTrackCover(id, imagePath));

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
                st = Run(A, $"cdc_pack_tracks(v{PacketVersion}, {arr.Length} id(s), audio={f.Audio}, names={f.Names}, covers={f.Covers})",
                    () => Native.CdcPackTracks(PacketVersion, idsPtr, (nuint)arr.Length, f.Audio, f.Names, f.Covers, out p, out len));
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

    // ───────────────────────── device pairing (con) ─────────────────────────

    public static class Con
    {
        private const string A = "con";

        /// public key of this device (32 bytes); `null` on error.
        public static byte[]? PubKey()
        {
            var key = new byte[32];
            ConStatus st;
            fixed (byte* p = key)
            {
                var ptr = p;
                st = Run(A, "con_get_pub_key()", () => Native.ConGetPubKey(ptr));
            }
            return st == ConStatus.Ok ? key : null;
        }

        /// a new connection entry brought to `Connected` (the transport is up).
        public static (ConStatus, ulong) Open()
        {
            ulong id = 0;
            var st = Run(A, "con_add()", () => Native.ConAdd(out id));
            if (st != ConStatus.Ok)
                return (st, 0);
            var entry = id;
            st = Run(A, $"con_connect({entry})", () => Native.ConConnect(entry));
            if (st != ConStatus.Ok)
                Run(A, $"con_remove({entry})", () => Native.ConRemove(entry));
            return (st, id);
        }

        /// drops a connection entry (the transport is down).
        public static void Close(ulong id)
        {
            Run(A, $"con_disconnect({id})", () => Native.ConDisconnect(id));
            Run(A, $"con_remove({id})", () => Native.ConRemove(id));
        }

        /// every device this one has paired with; `null` on error.
        public static List<PairedDevice>? Paired()
        {
            FFIConPairedList list = default;
            var st = Run(A, "con_get_paired()", () => Native.ConGetPaired(out list));
            if (st != ConStatus.Ok)
                return null;
            var result = new List<PairedDevice>((int)list.Len);
            var items = (FFIConPaired*)list.Ptr;
            for (nuint i = 0; i < list.Len; i++)
                result.Add(PairedDevice.FromFfi(items[i]));
            Native.ConFreePairedList(list);
            return result;
        }

        public static ConStatus Rename(long pairedId, string name) =>
            Run(A, $"con_rename({pairedId}, {Q(name)})", () => Native.ConRename(pairedId, name));

        public static ConStatus Forget(long pairedId) =>
            Run(A, $"con_forget({pairedId})", () => Native.ConForget(pairedId));
    }

    // ───────────────────────── library sync (sync) ─────────────────────────

    /// a session event (a copy of `FFISyncEvent`).
    public readonly record struct SyncEvent(
        SyncEventKind Kind, WireStatus Status, bool ByPeer, long PairedId, uint Songs, uint Playlists);

    /// one file of a received data portion: points into the session memory.
    public sealed class SyncFile
    {
        internal nint Data;
        internal nuint DataLen;

        public required ImportFileKind Kind { get; init; }
        /// suggested OS-safe name.
        public required string Name { get; init; }
        /// a local song with the same name to write over (`null` — anywhere).
        public string? Replace { get; init; }
        public long Length => (long)DataLen;

        /// a read-only view of the bytes; valid until `ImportDone` / `Cancel`.
        public Stream OpenRead() => new UnmanagedMemoryStream((byte*)Data, (long)DataLen);
    }

    /// a sync session over one connection entry (`sync_*`): a state machine
    /// without I/O, driven by one thread at a time (the sync worker).
    /// Polling calls are quiet in the log.
    public sealed class SyncSession : IDisposable
    {
        private const string A = "sync";
        private nint _s;

        private SyncSession(nint s) => _s = s;

        private static (SyncStatus, SyncSession?) Wrap(SyncStatus st, nint s) =>
            st == SyncStatus.Ok && s != 0 ? (st, new SyncSession(s)) : (st, null);

        /// two-way library sync; `host` — this side listened.
        public static (SyncStatus, SyncSession?) New(ulong con, bool host)
        {
            nint s = 0;
            var st = Run(A, $"sync_new({con}, host={host})", () => Native.SyncNew(con, host, out s));
            return Wrap(st, s);
        }

        /// one-way send of a selection with the export flags.
        public static (SyncStatus, SyncSession?) NewSend(ulong con, bool host, ExportMode what,
            IReadOnlyList<string> playlists, IReadOnlyList<long> ids, PackFlags f)
        {
            nint s = 0;
            using var names = new Utf8Array(what == ExportMode.Playlists ? playlists : []);
            var arr = what == ExportMode.Tracks ? ids.ToArray() : [];
            var kind = (SyncWhat)what;
            SyncStatus st;
            fixed (long* p = arr)
            {
                var idsPtr = p;
                st = Run(A, $"sync_new_send({con}, host={host}, {kind}, {names.Len} name(s), {arr.Length} id(s), audio={f.Audio}, names={f.Names}, covers={f.Covers})",
                    () => Native.SyncNewSend(con, host, kind, names.Ptr, names.Len, idsPtr, (nuint)arr.Length,
                        f.Audio, f.Names, f.Covers, out s));
            }
            return Wrap(st, s);
        }

        /// one-way receive; `force` — accept outdated playlists too.
        public static (SyncStatus, SyncSession?) NewReceive(ulong con, bool host, bool force)
        {
            nint s = 0;
            var st = Run(A, $"sync_new_receive({con}, host={host}, force={force})",
                () => Native.SyncNewReceive(con, host, force, out s));
            return Wrap(st, s);
        }

        private nint S => _s != 0 ? _s : throw new ObjectDisposedException(nameof(SyncSession));

        /// bytes received from the peer.
        public void Feed(ReadOnlySpan<byte> data)
        {
            var s = S;
            var len = data.Length;
            fixed (byte* p = data)
            {
                var ptr = p;
                Run(A, $"sync_feed({len} B)", () => Native.SyncFeed(s, ptr, (nuint)len), quiet: true);
            }
        }

        /// bytes to send to the peer (empty — nothing now).
        public byte[] TakeOutput()
        {
            var s = S;
            nint p = 0;
            nuint len = 0;
            var st = Run(A, "sync_take_output()", () => Native.SyncTakeOutput(s, out p, out len), quiet: true);
            if (st != SyncStatus.Ok || p == 0)
                return [];
            var bytes = new ReadOnlySpan<byte>((void*)p, checked((int)len)).ToArray();
            Native.SyncFreeBytes(p, len);
            return bytes;
        }

        /// the next event (`null` — none now).
        public SyncEvent? PollEvent()
        {
            var s = S;
            FFISyncEvent ev = default;
            var st = Run(A, "sync_poll_event()", () => Native.SyncPollEvent(s, out ev), quiet: true);
            if (st != SyncStatus.Ok || ev.Kind == SyncEventKind.None)
                return null;
            CoreLog.Debug(A, $"event {ev.Kind} status={ev.Status} by_peer={ev.ByPeer != 0} paired={ev.PairedId} songs={ev.Songs} playlists={ev.Playlists}");
            return new SyncEvent(ev.Kind, ev.Status, ev.ByPeer != 0, ev.PairedId, ev.Songs, ev.Playlists);
        }

        /// the session is over (`Done` or `Failed`).
        public bool Finished => _s == 0 || Native.SyncFinished(_s);

        /// files of the waiting import (after an `Import` event).
        public IReadOnlyList<SyncFile> ImportFiles()
        {
            var s = S;
            FFISyncFiles files = default;
            var st = Run(A, "sync_import_files()", () => Native.SyncImportFiles(s, out files));
            if (st != SyncStatus.Ok || files.Ptr == 0)
                return [];
            var list = new List<SyncFile>((int)files.Len);
            var items = (FFISyncFile*)files.Ptr;
            for (nuint i = 0; i < files.Len; i++)
            {
                list.Add(new SyncFile
                {
                    Kind = items[i].Kind,
                    Name = Native.Str(items[i].Name) ?? $"file{i}",
                    Replace = Native.Str(items[i].Replace),
                    Data = items[i].Data,
                    DataLen = items[i].DataLen,
                });
            }
            return list;
        }

        /// the import files are written: `paths[i]` — where file `i` went
        /// (null — not written). The portion is saved and the peer answered.
        public SyncStatus ImportDone(IReadOnlyList<string?> paths)
        {
            var s = S;
            using var arr = new Utf8Array(paths);
            return Run(A, $"sync_import_done({paths.Count} path(s), {paths.Count(p => p is null)} skipped)",
                () => Native.SyncImportDone(s, arr.Ptr, arr.Len));
        }

        /// breaks the session off: the `Abort` for the peer is in `TakeOutput`.
        public void Cancel()
        {
            var s = S;
            Run(A, "sync_cancel()", () => Native.SyncCancel(s));
        }

        public void Dispose()
        {
            if (_s == 0)
                return;
            var s = _s;
            _s = 0;
            Native.SyncFree(s);
            CoreLog.Debug(A, "sync_free()");
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
