// Frontend settings (not the core config — the core keeps the master volume
// itself). Stored as JSON in `<LocalAppData>/DreamPlayer/frontend.json`.

using System.Globalization;
using System.Text.Json;

namespace DreamPlayer.Services;

public enum RepeatMode
{
    Off,
    All,
    One,
}

public sealed class AppSettings
{
    /// "ru" / "en".
    public string Language { get; set; } =
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? "ru" : "en";

    public string MusicDir { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
    public bool Shuffle { get; set; }
    public RepeatMode Repeat { get; set; } = RepeatMode.All;
    public float Speed { get; set; } = 1f;
    public bool VerboseLog { get; set; }
    /// seconds the bottom panel buttons seek by.
    public int SeekStep { get; set; } = 10;

    // last used import / export options
    public string? ImportSaveTo { get; set; }
    public bool ExportAudio { get; set; } = true;
    public bool ExportNames { get; set; } = true;
    public bool ExportCovers { get; set; } = true;

    // ───────────────────────── storage ─────────────────────────

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static string? _path;

    public static AppSettings Current { get; private set; } = new();

    public static void Load(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // a broken settings file must not break startup.
            CoreLog.Warn("app", $"settings not loaded ({e.Message}), defaults used");
            Current = new();
        }
    }

    public static void Save()
    {
        if (_path is null)
            return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(Current, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            CoreLog.Warn("app", $"settings not saved: {e.Message}");
        }
    }
}
