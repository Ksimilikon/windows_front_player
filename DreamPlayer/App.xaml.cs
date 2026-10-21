// Startup: directories, settings, frontend log, core init, the window. The
// core is brought up once per process (`init`); the music directory is indexed
// in the background after the window shows (like the TUI does before its loop).

using DreamPlayer.Core;
using DreamPlayer.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace DreamPlayer;

public partial class App : Application
{
    public static MainWindow? Window { get; private set; }
    public static DispatcherQueue UI { get; private set; } = null!;

    /// `<LocalAppData>/DreamPlayer`: core data / cache and frontend settings.
    public static string LocalDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DreamPlayer");

    public static string ConfigDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DreamPlayer");

    public static string DataDir => Path.Combine(LocalDir, "data");
    public static string CacheDir => Path.Combine(LocalDir, "cache");
    /// received covers that are files of their own (not embedded into the
    /// audio): song covers and playlist covers from imports and syncs.
    public static string CoversDir => Path.Combine(LocalDir, "covers");

    /// the core init status (`null` — the core library failed to load at all).
    public static InitStatus? CoreStatus { get; private set; }
    public static string? CoreLoadError { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            CoreLog.Error("app", $"unhandled exception: {e.Exception}");
            e.Handled = true;
            Window?.Shell?.Notify(L.F("msg.unhandled", e.Exception.Message), LogLevel.Error);
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        UI = DispatcherQueue.GetForCurrentThread();
        AppSettings.Load(Path.Combine(LocalDir, "frontend.json"));
        L.Language = AppSettings.Current.Language;
        CoreLog.Init(a => UI.TryEnqueue(() => a()));
        CoreLog.Verbose = AppSettings.Current.VerboseLog;

        InitCore();

        Window = new MainWindow();
        Window.Activate();

        if (CoreApi.Initialized)
            _ = Window.Shell!.IndexMusicDirAsync();
    }

    private static void InitCore()
    {
        foreach (var dir in new[] { DataDir, CacheDir, ConfigDir })
            Directory.CreateDirectory(dir);
        var music = AppSettings.Current.MusicDir;
        if (string.IsNullOrWhiteSpace(music))
            music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        try
        {
            CoreStatus = CoreApi.Init(CacheDir, ConfigDir, DataDir, music);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            CoreLoadError = e.Message;
            CoreLog.Error("init", $"core library failed to load: {e.Message}");
            return;
        }
        if (!CoreApi.Initialized)
            return;
        Player.Instance.Init();
        Library.Instance.Reload();
    }
}
