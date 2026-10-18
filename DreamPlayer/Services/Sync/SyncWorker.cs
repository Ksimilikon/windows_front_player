// A running sync / send / receive (the TUI sync worker, `tui/src/sync/mod.rs`):
// brings the transport up, opens a connection entry (`con_*`) and a session
// (`sync_*`), then carries bytes between them until the session is over. The
// protocol itself — pairing, comparing libraries, confirmations — lives in
// the core; the worker only moves bytes and writes the received files: a song
// over the local file with the same name, anything else into the target
// folder (never overwriting).
//
// One worker at a time (a second one would fight over the port / device).
// Progress goes to `report` from a background thread.

using System.Threading.Channels;
using DreamPlayer.Core;

namespace DreamPlayer.Services.Sync;

/// `Complete` — the session went through to `Done`; `LibraryChanged` —
/// received data was saved (the library has to be re-read).
public readonly record struct SyncResult(bool Complete, bool LibraryChanged);

public sealed class SyncWorker
{
    private const string A = "sync";
    /// how long the loop waits for incoming bytes before it looks at the
    /// session again (output, events, cancellation).
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan LastWords = TimeSpan.FromSeconds(2);

    private static SyncWorker? _active;

    private readonly CancellationTokenSource _cancel = new();
    private readonly Report _report;
    private bool _changed;

    private SyncWorker(Report report) => _report = report;

    public Task<SyncResult> Completion { get; private set; } = Task.FromResult(default(SyncResult));

    public bool Running => !Completion.IsCompleted;

    /// a sync / send / receive is running somewhere in the app.
    public static bool AnyRunning => _active is { Running: true };

    /// starts a worker; `null` — another one is running (reported).
    public static SyncWorker? Start(SyncTarget target, SyncMode mode, string saveTo, Report report)
    {
        if (AnyRunning)
        {
            report(L.T("sync.already_running"), LogLevel.Warn);
            return null;
        }
        var w = new SyncWorker(report);
        _active = w;
        w.Completion = Task.Run(() => w.RunAsync(target, mode, saveTo));
        return w;
    }

    public void Cancel()
    {
        if (Running)
            _cancel.Cancel();
    }

    private void Log(string text, LogLevel level)
    {
        CoreLog.Write(level, A, text);
        _report(text, level);
    }

    private async Task<SyncResult> RunAsync(SyncTarget target, SyncMode mode, string saveTo)
    {
        var ct = _cancel.Token;
        var complete = false;
        if (CoreApi.Con.PubKey() is { } key)
            Log(L.F("sync.this_device", Format.Fingerprint(key)), LogLevel.Info);
        try
        {
            await using var link = await ConnectAsync(target, ct);
            var (st, con) = CoreApi.Con.Open();
            if (st != ConStatus.Ok)
            {
                Log(L.F("sync.failed_open", CoreApi.Describe(st)), LogLevel.Error);
                return new SyncResult(false, false);
            }
            try
            {
                complete = await DriveAsync(link, con, mode, saveTo, ct);
            }
            finally
            {
                CoreApi.Con.Close(con);
            }
        }
        catch (OperationCanceledException)
        {
            Log(L.T("sync.cancelled"), LogLevel.Warn);
        }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or UnauthorizedAccessException
                                      or InvalidOperationException or TimeoutException or System.Runtime.InteropServices.COMException)
        {
            Log(L.F("sync.connection_error", e.Message), LogLevel.Error);
        }
        return new SyncResult(complete, _changed);
    }

    /// brings the transport up; a cancellation returns at once even if the
    /// transport is stuck in a blocking call (a link it brings up later is
    /// closed then).
    private async Task<SyncLink> ConnectAsync(SyncTarget target, CancellationToken ct)
    {
        var connecting = Transports.ConnectAsync(target, s => Log(s, LogLevel.Info), ct);
        try
        {
            return await connecting.WaitAsync(ct);
        }
        catch (OperationCanceledException) when (!connecting.IsCompleted)
        {
            _ = connecting.ContinueWith(t =>
            {
                CoreLog.Debug(A, "a cancelled connection finished in the background");
                if (t.IsCompletedSuccessfully)
                    _ = t.Result.DisposeAsync();
            }, TaskScheduler.Default);
            throw;
        }
    }

    /// carries bytes between the link and the session until it is over;
    /// `true` — the session is complete.
    private async Task<bool> DriveAsync(SyncLink link, ulong con, SyncMode mode, string saveTo, CancellationToken ct)
    {
        var host = link.Host;
        var (st, session) = mode switch
        {
            SyncMode.Send send => CoreApi.SyncSession.NewSend(con, host, send.What, send.Playlists, send.Ids, send.Flags),
            SyncMode.Receive r => CoreApi.SyncSession.NewReceive(con, host, r.Force),
            _ => CoreApi.SyncSession.New(con, host),
        };
        if (session is null)
        {
            Log(L.F("sync.failed_open", CoreApi.Describe(st)), LogLevel.Error);
            return false;
        }
        using var s = session;
        var stream = link.Stream;

        // reads run on their own task; the loop below takes what arrived.
        var incoming = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        using var stopReading = new CancellationTokenSource();
        var reader = ReadLoopAsync(stream, incoming.Writer, stopReading.Token);
        Task<bool>? waiting = null;
        var complete = false;
        try
        {
            while (true)
            {
                var output = s.TakeOutput();
                if (output.Length > 0)
                {
                    // a blocking transport (USB) ignores the token: don't
                    // wait for it once cancelled.
                    await stream.WriteAsync(output, ct).AsTask().WaitAsync(ct);
                    await stream.FlushAsync(ct).WaitAsync(ct);
                }
                while (s.PollEvent() is { } ev)
                {
                    switch (ev.Kind)
                    {
                        case SyncEventKind.Import:
                            Import(s, saveTo);
                            break;
                        case SyncEventKind.Received:
                            _changed = true;
                            break;
                        case SyncEventKind.Done:
                            complete = true;
                            break;
                    }
                    if (Describe(ev) is { } msg)
                        Log(msg.Text, msg.Level);
                }
                if (s.Finished)
                    return complete;
                if (ct.IsCancellationRequested)
                {
                    s.Cancel();
                    Log(L.T("sync.cancelled"), LogLevel.Warn);
                    return false;
                }

                // what arrived meanwhile — or wait a tick for it.
                var fed = false;
                while (incoming.Reader.TryRead(out var chunk))
                {
                    s.Feed(chunk);
                    fed = true;
                }
                if (fed)
                    continue;
                if (incoming.Reader.Completion.IsCompleted)
                {
                    await incoming.Reader.Completion; // rethrows a read error
                    throw new IOException(L.T("sync.closed_by_peer"));
                }
                waiting ??= incoming.Reader.WaitToReadAsync().AsTask();
                if (await Task.WhenAny(waiting, Task.Delay(Tick, CancellationToken.None)) == waiting)
                    waiting = null;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            s.Cancel();
            Log(L.T("sync.cancelled"), LogLevel.Warn);
            return false;
        }
        finally
        {
            // the last answer (`Ack` / `Abort`) still has to reach the peer.
            var last = s.TakeOutput();
            if (last.Length > 0)
            {
                try
                {
                    using var t = new CancellationTokenSource(LastWords);
                    await stream.WriteAsync(last, t.Token).AsTask().WaitAsync(t.Token);
                    await stream.FlushAsync(t.Token).WaitAsync(t.Token);
                }
                catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException
                                              or System.Net.Sockets.SocketException)
                {
                    CoreLog.Debug(A, $"last frame not sent: {e.Message}");
                }
            }
            stopReading.Cancel();
            // unblocks a pending read; a stuck device must not hold the
            // worker (the transport finishes closing in the background).
            try
            {
                await link.DisposeAsync().AsTask().WaitAsync(LastWords);
                await reader.WaitAsync(LastWords);
            }
            catch (Exception e)
            {
                CoreLog.Debug(A, $"closing the link: {e.Message}");
            }
        }
    }

    private static async Task ReadLoopAsync(Stream stream, ChannelWriter<byte[]> to, CancellationToken ct)
    {
        var buf = new byte[256 * 1024];
        try
        {
            while (true)
            {
                var n = await stream.ReadAsync(buf, ct);
                if (n == 0)
                    break;
                to.TryWrite(buf.AsSpan(0, n).ToArray());
            }
            to.TryComplete();
        }
        catch (Exception e)
        {
            to.TryComplete(ct.IsCancellationRequested ? null : e);
        }
    }

    /// writes the files of a received data portion and reports where.
    private void Import(CoreApi.SyncSession s, string saveTo)
    {
        var files = s.ImportFiles();
        var paths = new List<string?>(files.Count);
        foreach (var f in files)
        {
            try
            {
                using var data = f.OpenRead();
                string path;
                if (f.Kind == ImportFileKind.Song && f.Replace is { } replace)
                {
                    using (var file = new FileStream(replace, FileMode.Create, FileAccess.Write))
                        data.CopyTo(file);
                    path = replace;
                }
                else
                {
                    path = Transfer.WriteUnique(saveTo, f.Name, data);
                }
                CoreLog.Debug(A, $"wrote {f.Kind} {Format.Size(f.Length)} → {path}");
                paths.Add(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log(L.F("import.write_failed", f.Name, e.Message), LogLevel.Warn);
                paths.Add(null);
            }
        }
        var st = s.ImportDone(paths);
        if (st != SyncStatus.Ok)
            Log(L.F("sync.import_failed", CoreApi.Describe(st)), LogLevel.Error);
    }

    /// a session event -> a log line (`null` — not worth one).
    private static (string Text, LogLevel Level)? Describe(CoreApi.SyncEvent ev)
    {
        string What(string verb) => (ev.Songs, ev.Playlists) switch
        {
            (var s, 0) => L.F($"sync.{verb}_songs", s),
            (0, var p) => L.F($"sync.{verb}_playlists", p),
            var (s, p) => L.F($"sync.{verb}_both", s, p),
        };
        return ev.Kind switch
        {
            SyncEventKind.Paired => (L.F("sync.paired", PairedName(ev.PairedId)), LogLevel.Ok),
            SyncEventKind.Sent => (What("sent"), LogLevel.Info),
            SyncEventKind.Received => (What("received"), LogLevel.Info),
            SyncEventKind.PeerRejected => (L.F("sync.peer_rejected", CoreApi.Describe(ev.Status)), LogLevel.Warn),
            SyncEventKind.Done => (L.T("sync.complete"), LogLevel.Ok),
            SyncEventKind.Failed => (L.F(ev.ByPeer ? "sync.failed_peer" : "sync.failed_here", CoreApi.Describe(ev.Status)),
                LogLevel.Error),
            _ => null,
        };
    }

    private static string PairedName(long id) =>
        CoreApi.Con.Paired()?.FirstOrDefault(p => p.Id == id) is { } p
            ? $"#{id} {p.Name} [{p.Fingerprint}]"
            : $"#{id}";
}
