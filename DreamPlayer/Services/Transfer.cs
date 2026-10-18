// Packet export / import (the frontend side of `cdc_*`), ported from the TUI
// EXPORT / IMPORT tabs. The core never writes files itself: on import it hands
// out the songs / covers, we write them into the target directory (never
// overwriting — `name (1).ext`, ...) and pass the paths back. Both operations
// are blocking: run them off the UI thread.

using DreamPlayer.Core;

namespace DreamPlayer.Services;

public enum ExportMode
{
    Library,
    Playlists,
    Tracks,
}

public enum ImportResult
{
    Ok,
    /// the packet is older than local playlists: retry with `force`.
    Outdated,
    Failed,
}

/// progress line for the tab's own log (also mirrored to CoreLog).
public delegate void Report(string text, LogLevel level);

public static class Transfer
{
    public const string Extension = ".dplr";

    public static string DefaultFileName() =>
        $"dream_player_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}{Extension}";

    // ───────────────────────── export ─────────────────────────

    public static bool Export(string target, ExportMode mode, IReadOnlyList<string> playlists,
        IReadOnlyList<long> trackIds, PackFlags flags, Report report)
    {
        var what = mode switch
        {
            ExportMode.Library => L.T("export.what_library"),
            ExportMode.Playlists => L.F("export.what_playlists", playlists.Count),
            _ => L.F("export.what_tracks", trackIds.Count),
        };
        Log(report, L.F("export.packing", what, flags.Audio, flags.Names, flags.Covers), LogLevel.Info);
        var (st, bytes) = mode switch
        {
            ExportMode.Library => CoreApi.Packets.PackLibrary(flags),
            ExportMode.Playlists => CoreApi.Packets.PackPlaylists(playlists, flags),
            _ => CoreApi.Packets.PackTracks(trackIds, flags with { Covers = false }),
        };
        if (bytes is null)
        {
            Log(report, L.F("export.failed", CoreApi.Describe(st)), LogLevel.Error);
            return false;
        }
        try
        {
            File.WriteAllBytes(target, bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDelete(target);
            Log(report, L.F("export.write_failed", target, e.Message), LogLevel.Error);
            return false;
        }
        Log(report, L.F("export.done", Format.Size(bytes.Length), target), LogLevel.Ok);
        return true;
    }

    // ───────────────────────── import ─────────────────────────

    public static ImportResult Import(string packet, string saveTo, bool force, Report report)
    {
        if (Directory.Exists(packet))
        {
            Log(report, L.F("import.is_dir", packet), LogLevel.Error);
            return ImportResult.Failed;
        }
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(packet);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log(report, L.F("import.read_failed", packet, e.Message), LogLevel.Error);
            return ImportResult.Failed;
        }
        Log(report, L.F("import.read", Format.Size(bytes.Length), packet) + (force ? " (force)" : ""), LogLevel.Info);

        // step 1: the core checks the packet and the dates.
        var (st, session) = CoreApi.Packets.Prepare(bytes, force);
        bytes = [];
        if (st == CdcStatus.Outdated)
        {
            Log(report, CoreApi.Describe(st), LogLevel.Warn);
            return ImportResult.Outdated;
        }
        if (session is null)
        {
            Log(report, L.F("import.rejected", CoreApi.Describe(st)), LogLevel.Error);
            return ImportResult.Failed;
        }

        using (session)
        {
            // step 2: we write the files ourselves.
            var songs = session.Files.Count(f => f.Kind == ImportFileKind.Song);
            var covers = session.Files.Count - songs;
            Log(report, L.F("import.to_write", songs, covers, saveTo), LogLevel.Info);
            var written = new List<string>(session.Files.Count);
            foreach (var f in session.Files)
            {
                try
                {
                    string path;
                    using (var data = f.OpenRead())
                        path = WriteUnique(saveTo, f.Name, data);
                    written.Add(path);
                    CoreLog.Debug("import", $"wrote {f.Kind} {Format.Size(f.Length)} → {path}");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Log(report, L.F("import.write_failed", f.Name, e.Message), LogLevel.Error);
                    RemoveAll(written);
                    return ImportResult.Failed; // the session is cancelled by Dispose
                }
            }

            // step 3: the core saves the paths.
            st = session.Commit(written);
            if (st == CdcStatus.Ok)
            {
                Log(report, L.F("import.done", songs, covers), LogLevel.Ok);
                return ImportResult.Ok;
            }
            RemoveAll(written);
            if (st == CdcStatus.Outdated)
            {
                Log(report, L.T("import.changed_meanwhile"), LogLevel.Warn);
                return ImportResult.Outdated;
            }
            Log(report, L.F("import.failed", CoreApi.Describe(st)), LogLevel.Error);
            return ImportResult.Failed;
        }
    }

    /// writes the file into `dir` without overwriting: if the name is taken —
    /// `name (1).ext`, `name (2).ext`, ...
    public static string WriteUnique(string dir, string name, Stream data)
    {
        Directory.CreateDirectory(dir);
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var n = 0; n < 10_000; n++)
        {
            var path = Path.Combine(dir, n == 0 ? name : $"{stem} ({n}){ext}");
            FileStream file;
            try
            {
                file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            }
            catch (IOException) when (File.Exists(path))
            {
                continue;
            }
            try
            {
                using (file)
                    data.CopyTo(file);
            }
            catch
            {
                TryDelete(path);
                throw;
            }
            return path;
        }
        throw new IOException($"no free name for {name} in {dir}");
    }

    private static void RemoveAll(IEnumerable<string> paths)
    {
        foreach (var p in paths)
            TryDelete(p);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            CoreLog.Warn("app", $"failed to delete {path}: {e.Message}");
        }
    }

    private static void Log(Report report, string text, LogLevel level)
    {
        CoreLog.Write(level, "transfer", text);
        report(text, level);
    }
}
