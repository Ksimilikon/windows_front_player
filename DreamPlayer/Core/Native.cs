// Raw C ABI of the player core (`dream_player.dll` / `libdream_player.so`).
// Mirrors `api/src/*.rs` of rs_dream_player one to one: struct layouts, status
// discriminants and signatures must match the Rust side exactly. Everything
// here is unsafe and unlogged — the rest of the app goes through CoreApi.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DreamPlayer.Core;

// ───────────────────────────── statuses ─────────────────────────────
// repr(u32) on the Rust side; discriminants are part of the ABI.

public enum InitStatus : uint
{
    Ok = 0,
    NullPointer = 1,
    InvalidUtf8 = 2,
    AlreadyInitialized = 3,
    DbFailed = 4,
    AudioFailed = 5,
    Panic = 6,
}

public enum EngineStatus : uint
{
    Ok = 0,
    NotInitialized = 1,
    NullPointer = 2,
    InvalidUtf8 = 3,
    NoDatabase = 4,
    ReadFailed = 5,
    TrackNotFound = 6,
    LoadFailed = 7,
    PlaybackFailed = 8,
    SeekFailed = 9,
    DeviceFailed = 10,
    ConfigWriteFailed = 11,
    Panic = 12,
}

public enum IdxStatus : uint
{
    Ok = 0,
    NotInitialized = 1,
    NullPointer = 2,
    InvalidUtf8 = 3,
    NoDatabase = 4,
    NotFound = 5,
    ReadFailed = 6,
    WriteFailed = 7,
    IndexFailed = 8,
    Panic = 9,
}

public enum CdcStatus : uint
{
    Ok = 0,

    // codec::CodecError (1..=99)
    BadStartWord = 1,
    UnsupportedVersion = 2,
    UnexpectedEof = 3,
    TrailingBytes = 4,
    InvalidUtf8 = 5,
    TooManyRows = 6,
    NameTooLong = 7,
    DataTooLong = 8,
    DuplicateSection = 9,
    CountMismatch = 10,

    // import decisions (100..=199)
    Outdated = 100,

    // core errors (200..)
    NullPointer = 200,
    NoDatabase = 201,
    TrackNotFound = 202,
    InvalidHash = 203,
    ReadFailed = 204,
    PlaylistNotFound = 205,
    WriteFailed = 206,
    InvalidArgument = 207,
    NotInitialized = 208,
    Panic = 209,
}

public enum ImportFileKind : uint
{
    Song = 1,
    Cover = 2,
}

// ───────────────────────────── structs ─────────────────────────────
// Rust `bool` is one byte: kept as `byte` so the structs stay blittable.

[StructLayout(LayoutKind.Sequential)]
internal struct FFITrackMetadataParams
{
    public ulong DurationS;
    public uint SampleRate;
    public uint Bitrate;
    public long ListenCount;
    public nint CoverArt; // maybe null
}

[StructLayout(LayoutKind.Sequential)]
internal struct FFITrackMetadata
{
    public byte HasId;
    public long Id;
    public nint Path; // maybe null

    public nint Title;
    public nint Artists;
    public nuint ArtistsLen;

    public nint Album; // maybe null
    public nint Genres;
    public nuint GenresLen;

    public byte HasParams;
    public FFITrackMetadataParams Params;

    public float Volume;
    public byte Invalid;
    public nint Color;     // maybe null
    public nint UserLabel; // maybe null
}

[StructLayout(LayoutKind.Sequential)]
internal struct FFITracksList
{
    public nint Ptr;
    public nuint Len;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FFIStringList
{
    public nint Ptr;
    public nuint Len;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FFIPlaylist
{
    public nint Name; // null = anonymous
    public nint Tracks; // i64 ids
    public nuint TracksLen;
    public nint CoverArt; // maybe null
    public byte HasUpdatedAt;
    public ulong UpdatedAt;
    public byte HasCreatedAt;
    public ulong CreatedAt;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FFIPlaylistsList
{
    public nint Ptr;
    public nuint Len;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FFIImportFile
{
    public ImportFileKind Kind;
    public nint Name;
    public nint Data;
    public nuint DataLen;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FFIImportFiles
{
    public nint Ptr;
    public nuint Len;
}

// ───────────────────────────── functions ─────────────────────────────

internal static unsafe partial class Native
{
    /// resolved as `dream_player.dll` on Windows, `libdream_player.so` on Linux.
    private const string Lib = "dream_player";

    // ── init / panics ──

    [LibraryImport(Lib, EntryPoint = "init", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial InitStatus Init(string cache, string config, string data, string music,
        [MarshalAs(UnmanagedType.U1)] bool needDb);

    [LibraryImport(Lib, EntryPoint = "core_panic_count")]
    internal static partial ulong CorePanicCount();

    [LibraryImport(Lib, EntryPoint = "core_last_panic")]
    internal static partial nint CoreLastPanic();

    // ── engine ──

    [LibraryImport(Lib, EntryPoint = "engine_add_track_id")]
    internal static partial EngineStatus EngineAddTrackId(long id, delegate* unmanaged[Cdecl]<void> onEnd);

    [LibraryImport(Lib, EntryPoint = "engine_add_track_path", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial EngineStatus EngineAddTrackPath(string path, delegate* unmanaged[Cdecl]<void> onEnd);

    [LibraryImport(Lib, EntryPoint = "engine_play_pause")]
    internal static partial EngineStatus EnginePlayPause();

    [LibraryImport(Lib, EntryPoint = "engine_play")]
    internal static partial EngineStatus EnginePlay();

    [LibraryImport(Lib, EntryPoint = "engine_pause")]
    internal static partial EngineStatus EnginePause();

    [LibraryImport(Lib, EntryPoint = "engine_stop")]
    internal static partial EngineStatus EngineStop();

    [LibraryImport(Lib, EntryPoint = "engine_set_speed")]
    internal static partial EngineStatus EngineSetSpeed(float speed);

    [LibraryImport(Lib, EntryPoint = "engine_set_seek")]
    internal static partial EngineStatus EngineSetSeek(ulong seekSec);

    [LibraryImport(Lib, EntryPoint = "engine_get_seek")]
    internal static partial EngineStatus EngineGetSeek(out ulong pos);

    [LibraryImport(Lib, EntryPoint = "engine_get_duration")]
    internal static partial EngineStatus EngineGetDuration(out ulong duration);

    [LibraryImport(Lib, EntryPoint = "engine_is_pause")]
    internal static partial EngineStatus EngineIsPause([MarshalAs(UnmanagedType.U1)] out bool paused);

    [LibraryImport(Lib, EntryPoint = "engine_is_empty")]
    internal static partial EngineStatus EngineIsEmpty([MarshalAs(UnmanagedType.U1)] out bool empty);

    [LibraryImport(Lib, EntryPoint = "engine_set_volume_track")]
    internal static partial EngineStatus EngineSetVolumeTrack(float volume);

    [LibraryImport(Lib, EntryPoint = "engine_set_volume_master")]
    internal static partial EngineStatus EngineSetVolumeMaster(float volume);

    [LibraryImport(Lib, EntryPoint = "engine_get_volume_track")]
    internal static partial EngineStatus EngineGetVolumeTrack(out float volume);

    [LibraryImport(Lib, EntryPoint = "engine_get_volume_master")]
    internal static partial EngineStatus EngineGetVolumeMaster(out float volume);

    [LibraryImport(Lib, EntryPoint = "engine_get_output_devices")]
    internal static partial EngineStatus EngineGetOutputDevices(out FFIStringList list);

    [LibraryImport(Lib, EntryPoint = "engine_get_output_device")]
    internal static partial EngineStatus EngineGetOutputDevice(out nint name);

    [LibraryImport(Lib, EntryPoint = "engine_set_output_device", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial EngineStatus EngineSetOutputDevice(string name);

    [LibraryImport(Lib, EntryPoint = "engine_free_output_devices")]
    internal static partial void EngineFreeOutputDevices(FFIStringList list);

    // ── index (db) ──

    [LibraryImport(Lib, EntryPoint = "idx_get_playlists")]
    internal static partial IdxStatus IdxGetPlaylists(out FFIPlaylistsList list);

    [LibraryImport(Lib, EntryPoint = "idx_get_tracks")]
    internal static partial IdxStatus IdxGetTracks(out FFITracksList list);

    [LibraryImport(Lib, EntryPoint = "idx_get_track", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IdxStatus IdxGetTrack(long id, string? path, out FFITrackMetadata meta);

    [LibraryImport(Lib, EntryPoint = "idx_get_track_cover", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IdxStatus IdxGetTrackCover(long id, string? path, out nint ptr, out nuint len);

    [LibraryImport(Lib, EntryPoint = "idx_index_dir", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IdxStatus IdxIndexDir(string? dir);

    [LibraryImport(Lib, EntryPoint = "idx_set_track_meta", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IdxStatus IdxSetTrackMeta(long id, string? title, nint* artists, nuint artistsLen);

    [LibraryImport(Lib, EntryPoint = "idx_set_track_album", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IdxStatus IdxSetTrackAlbum(long id, string? album);

    [LibraryImport(Lib, EntryPoint = "idx_set_track_genres")]
    internal static partial IdxStatus IdxSetTrackGenres(long id, nint* genres, nuint genresLen);

    [LibraryImport(Lib, EntryPoint = "idx_set_track_cover", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IdxStatus IdxSetTrackCover(long id, string? coverPath);

    [LibraryImport(Lib, EntryPoint = "idx_set_track_color", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IdxStatus IdxSetTrackColor(long id, string? color);

    [LibraryImport(Lib, EntryPoint = "idx_set_track_label", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IdxStatus IdxSetTrackLabel(long id, string? label);

    [LibraryImport(Lib, EntryPoint = "idx_set_track_invalid")]
    internal static partial IdxStatus IdxSetTrackInvalid(long id, [MarshalAs(UnmanagedType.U1)] bool invalid);

    [LibraryImport(Lib, EntryPoint = "idx_add_tracks")]
    internal static partial IdxStatus IdxAddTracks(nint* paths, nuint len, out ulong added);

    [LibraryImport(Lib, EntryPoint = "idx_set_track_volume")]
    internal static partial IdxStatus IdxSetTrackVolume(long id, float volume);

    [LibraryImport(Lib, EntryPoint = "idx_set_track_path", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IdxStatus IdxSetTrackPath(long id, string path);

    [LibraryImport(Lib, EntryPoint = "idx_remove_track")]
    internal static partial IdxStatus IdxRemoveTrack(long id);

    [LibraryImport(Lib, EntryPoint = "idx_remove_invalid_tracks")]
    internal static partial IdxStatus IdxRemoveInvalidTracks(out ulong removed);

    [LibraryImport(Lib, EntryPoint = "idx_save_playlist", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IdxStatus IdxSavePlaylist(string name, long* trackIds, nuint trackIdsLen, string? cover);

    [LibraryImport(Lib, EntryPoint = "idx_remove_playlist", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IdxStatus IdxRemovePlaylist(string name);

    // ── memory ──

    [LibraryImport(Lib, EntryPoint = "idx_free_string")]
    internal static partial void IdxFreeString(nint s);

    [LibraryImport(Lib, EntryPoint = "idx_free_track_metadata")]
    internal static partial void IdxFreeTrackMetadata(FFITrackMetadata meta);

    [LibraryImport(Lib, EntryPoint = "idx_free_tracks_list")]
    internal static partial void IdxFreeTracksList(FFITracksList list);

    [LibraryImport(Lib, EntryPoint = "idx_free_playlists_list")]
    internal static partial void IdxFreePlaylistsList(FFIPlaylistsList list);

    [LibraryImport(Lib, EntryPoint = "idx_free_bytes")]
    internal static partial void IdxFreeBytes(nint ptr, nuint len);

    [LibraryImport(Lib, EntryPoint = "cdc_free_bytes")]
    internal static partial void CdcFreeBytes(nint ptr, nuint len);

    // ── packets (cdc) ──

    [LibraryImport(Lib, EntryPoint = "cdc_pack_library")]
    internal static partial CdcStatus CdcPackLibrary(uint version,
        [MarshalAs(UnmanagedType.U1)] bool withAudio,
        [MarshalAs(UnmanagedType.U1)] bool withNames,
        [MarshalAs(UnmanagedType.U1)] bool withCovers,
        out nint outPtr, out nuint outLen);

    [LibraryImport(Lib, EntryPoint = "cdc_pack_tracks")]
    internal static partial CdcStatus CdcPackTracks(uint version, long* ids, nuint idsLen,
        [MarshalAs(UnmanagedType.U1)] bool withAudio,
        [MarshalAs(UnmanagedType.U1)] bool withNames,
        out nint outPtr, out nuint outLen);

    [LibraryImport(Lib, EntryPoint = "cdc_pack_playlists")]
    internal static partial CdcStatus CdcPackPlaylists(uint version, nint* names, nuint namesLen,
        [MarshalAs(UnmanagedType.U1)] bool withAudio,
        [MarshalAs(UnmanagedType.U1)] bool withNames,
        [MarshalAs(UnmanagedType.U1)] bool withCovers,
        out nint outPtr, out nuint outLen);

    [LibraryImport(Lib, EntryPoint = "cdc_import_prepare")]
    internal static partial CdcStatus CdcImportPrepare(byte* bytes, nuint len,
        [MarshalAs(UnmanagedType.U1)] bool force,
        out nint session, out FFIImportFiles files);

    [LibraryImport(Lib, EntryPoint = "cdc_import_commit")]
    internal static partial CdcStatus CdcImportCommit(nint session, nint* paths, nuint pathsLen);

    [LibraryImport(Lib, EntryPoint = "cdc_import_cancel")]
    internal static partial void CdcImportCancel(nint session);

    // ── helpers ──

    /// copies a core-owned C string (does not free it).
    internal static string? Str(nint p) => p == 0 ? null : Marshal.PtrToStringUTF8(p);

    /// copies an array of core-owned C strings.
    internal static string[] Strs(nint arr, nuint len)
    {
        if (arr == 0 || len == 0)
            return [];
        var result = new List<string>((int)len);
        var ptrs = (nint*)arr;
        for (nuint i = 0; i < len; i++)
        {
            if (Str(ptrs[i]) is { } s)
                result.Add(s);
        }
        return [.. result];
    }
}

/// a temporary array of UTF-8 C strings for `const char* const*` arguments.
/// A null element stays null. Freed on Dispose.
internal sealed unsafe class Utf8Array : IDisposable
{
    private readonly nint[] _ptrs;
    private GCHandle _pin;

    public Utf8Array(IReadOnlyList<string?> items)
    {
        _ptrs = new nint[items.Count];
        for (var i = 0; i < items.Count; i++)
            _ptrs[i] = items[i] is { } s ? Marshal.StringToCoTaskMemUTF8(s) : 0;
        _pin = GCHandle.Alloc(_ptrs, GCHandleType.Pinned);
    }

    public nint* Ptr => _ptrs.Length == 0 ? null : (nint*)_pin.AddrOfPinnedObject();
    public nuint Len => (nuint)_ptrs.Length;

    public void Dispose()
    {
        if (!_pin.IsAllocated)
            return;
        _pin.Free();
        foreach (var p in _ptrs)
            Marshal.FreeCoTaskMem(p);
    }
}

/// the end-of-track callback the core calls from its audio thread. It only
/// raises a flag; the UI loop polls it (like the TUI does), so nothing runs on
/// the core thread.
internal static unsafe class TrackEndSignal
{
    private static int _ended;

    internal static delegate* unmanaged[Cdecl]<void> Pointer => &OnTrackEnd;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnTrackEnd() => Volatile.Write(ref _ended, 1);

    /// takes the flag (resetting it): `true` — a track played to the end.
    internal static bool Take() => Interlocked.Exchange(ref _ended, 0) == 1;

    internal static void Reset() => Volatile.Write(ref _ended, 0);
}
