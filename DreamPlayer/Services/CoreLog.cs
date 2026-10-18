// Frontend log: how the frontend talks to the core. Every FFI call made through
// CoreApi lands here (call, arguments, status, time), plus core callbacks
// (track end), core panics and frontend-level events (import / export steps,
// playback decisions). Kept in memory only (nothing is written to disk by
// itself): the log tabs save it to a file on request (`Save`).

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

    private static Action<Action>? _post;

    /// in-memory entries for the log tab; changed only on the UI thread.
    public static ObservableCollection<LogEntry> Entries { get; } = [];

    /// log successful polling calls too (position / duration / pause state —
    /// several per second). Off by default.
    public static bool Verbose { get; set; }

    /// `post` runs an action on the UI thread.
    public static void Init(Action<Action> post)
    {
        _post = post;
        Write(LogLevel.Info, "app", $"--- session start, pid {Environment.ProcessId} ---");
    }

    /// writes `entries` to a text file (one line each, UTF-8).
    public static void Save(string path, IEnumerable<LogEntry> entries) =>
        File.WriteAllLines(path, entries.Select(e => e.ToString()), new UTF8Encoding(false));

    public static void Debug(string area, string text) => Write(LogLevel.Debug, area, text);
    public static void Info(string area, string text) => Write(LogLevel.Info, area, text);
    public static void Ok(string area, string text) => Write(LogLevel.Ok, area, text);
    public static void Warn(string area, string text) => Write(LogLevel.Warn, area, text);
    public static void Error(string area, string text) => Write(LogLevel.Error, area, text);

    public static void Write(LogLevel level, string area, string text)
    {
        var entry = new LogEntry(DateTime.Now, level, area, text);
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

}
