// Frontend log: how the frontend talks to the core. Every FFI call made through
// CoreApi lands here (call, arguments, status, time), plus core callbacks
// (track end), core panics and frontend-level events (import / export steps,
// playback decisions). Kept in memory for the log tab and appended to
// `<LocalAppData>/DreamPlayer/logs/frontend.log`.

using System.Collections.ObjectModel;
using System.Text;

namespace DreamPlayer.Services;

public enum LogLevel
{
    Debug,
    Info,
    Ok,
    Warn,
    Error,
}

public sealed record LogEntry(DateTime Time, LogLevel Level, string Area, string Text)
{
    public string TimeText => Time.ToString("HH:mm:ss.fff");
    public string LevelText => Level switch
    {
        LogLevel.Debug => "DBG",
        LogLevel.Info => "INF",
        LogLevel.Ok => "OK ",
        LogLevel.Warn => "WRN",
        _ => "ERR",
    };

    public override string ToString() => $"{Time:yyyy-MM-dd HH:mm:ss.fff} {LevelText} [{Area}] {Text}";
}

public static class CoreLog
{
    private const int MemoryLimit = 5000;
    private const long FileLimit = 2 * 1024 * 1024;

    private static readonly object FileLock = new();
    private static string? _file;
    private static Action<Action>? _post;

    /// in-memory entries for the log tab; changed only on the UI thread.
    public static ObservableCollection<LogEntry> Entries { get; } = [];

    /// log successful polling calls too (position / duration / pause state —
    /// several per second). Off by default.
    public static bool Verbose { get; set; }

    public static string? FilePath => _file;

    /// `post` runs an action on the UI thread; `dir` — where the log file goes.
    public static void Init(Action<Action> post, string dir)
    {
        _post = post;
        Directory.CreateDirectory(dir);
        _file = System.IO.Path.Combine(dir, "frontend.log");
        Write(LogLevel.Info, "app", $"--- session start, pid {Environment.ProcessId} ---");
    }

    public static void Debug(string area, string text) => Write(LogLevel.Debug, area, text);
    public static void Info(string area, string text) => Write(LogLevel.Info, area, text);
    public static void Ok(string area, string text) => Write(LogLevel.Ok, area, text);
    public static void Warn(string area, string text) => Write(LogLevel.Warn, area, text);
    public static void Error(string area, string text) => Write(LogLevel.Error, area, text);

    public static void Write(LogLevel level, string area, string text)
    {
        var entry = new LogEntry(DateTime.Now, level, area, text);
        AppendFile(entry);
        if (_post is { } post)
            post(() => AddEntry(entry));
        else
            AddEntry(entry);
    }

    public static void Clear() => Entries.Clear();

    private static void AddEntry(LogEntry entry)
    {
        Entries.Add(entry);
        while (Entries.Count > MemoryLimit)
            Entries.RemoveAt(0);
    }

    private static void AppendFile(LogEntry entry)
    {
        if (_file is null)
            return;
        lock (FileLock)
        {
            try
            {
                var info = new FileInfo(_file);
                if (info.Exists && info.Length > FileLimit)
                    File.Move(_file, _file + ".1", overwrite: true);
                File.AppendAllText(_file, entry + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException)
            {
                // the log must never break the app.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
