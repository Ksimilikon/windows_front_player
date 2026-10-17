// The window: hosts the shell (rebuilt on a language switch), owns the UI
// timer that drives the player and the system media integration — both outlive
// shell rebuilds.

using DreamPlayer.Core;
using DreamPlayer.Services;
using DreamPlayer.Views;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace DreamPlayer;

public sealed partial class MainWindow : Window
{
    private readonly DispatcherQueueTimer _timer;
    private readonly MediaKeys? _media;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Dream Player";
        AppWindow.Resize(new SizeInt32(1280, 820));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 900;
            presenter.PreferredMinimumHeight = 600;
        }
        var bar = AppWindow.TitleBar;
        bar.BackgroundColor = Windows.UI.Color.FromArgb(255, 0x12, 0x12, 0x12);
        bar.InactiveBackgroundColor = bar.BackgroundColor;
        bar.ForegroundColor = Colors.White;
        bar.ButtonBackgroundColor = bar.BackgroundColor;
        bar.ButtonInactiveBackgroundColor = bar.BackgroundColor;
        bar.ButtonForegroundColor = Colors.White;
        bar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(255, 0x2A, 0x2A, 0x2A);

        Rebuild();

        if (CoreApi.Initialized)
            _media = MediaKeys.TryCreate(DispatcherQueue);

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(200);
        _timer.Tick += (_, _) =>
        {
            if (CoreApi.Initialized && !CoreApi.Busy)
                Player.Instance.Tick();
            Shell?.OnTick();
        };
        _timer.Start();

        Closed += (_, _) =>
        {
            _timer.Stop();
            // flushes a pending per-track volume to the db.
            Player.Instance.Stop();
            _media?.Dispose();
            CoreLog.Info("app", "--- window closed ---");
        };
    }

    public ShellView? Shell { get; private set; }

    /// (re)creates the shell, e.g. after the interface language changed.
    /// `reopen` — a tab key to open again in the new shell.
    public void Rebuild(string? reopen = null)
    {
        Shell?.Detach();
        Shell = new ShellView();
        Host.Children.Clear();
        Host.Children.Add(Shell);
        if (reopen == ShellView.SettingsKey)
            Shell.OpenSettings();
    }
}
