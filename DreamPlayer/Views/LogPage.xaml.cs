// LOG tab: the frontend log (CoreLog) with a level filter and search, plus the
// core's own panic log (`<data>/core_panic.log`).

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using DreamPlayer.Core;
using DreamPlayer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace DreamPlayer.Views;

public sealed partial class LogPage : UserControl, ITabPage
{
    /// filter: the minimal level shown.
    private static readonly LogLevel[] Levels = [LogLevel.Debug, LogLevel.Info, LogLevel.Warn, LogLevel.Error];

    private readonly ObservableCollection<LogEntry> _visible = [];

    public LogPage()
    {
        InitializeComponent();
        LevelBox.Items.Add(L.T("log.level_all"));
        LevelBox.Items.Add(L.T("log.level_info"));
        LevelBox.Items.Add(L.T("log.level_warn"));
        LevelBox.Items.Add(L.T("log.level_error"));
        LevelBox.SelectedIndex = 1;
        Entries.ItemsSource = _visible;
        CoreLog.Entries.CollectionChanged += OnEntries;
        Loaded += (_, _) => ScrollToEnd();
        SyncPanics();
    }

    public void OnClosed() => CoreLog.Entries.CollectionChanged -= OnEntries;

    private static string PanicLog => Path.Combine(App.DataDir, "core_panic.log");

    private LogLevel MinLevel => Levels[Math.Max(0, LevelBox.SelectedIndex)];

    private bool Matches(LogEntry e) =>
        e.Level >= MinLevel
        && (string.IsNullOrWhiteSpace(Search.Text)
            || e.Text.Contains(Search.Text, StringComparison.CurrentCultureIgnoreCase)
            || e.Area.Contains(Search.Text, StringComparison.CurrentCultureIgnoreCase));

    private void Rebuild()
    {
        _visible.Clear();
        foreach (var e in CoreLog.Entries.Where(Matches))
            _visible.Add(e);
        ScrollToEnd();
    }

    private void OnEntries(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                foreach (var entry in e.NewItems!.OfType<LogEntry>().Where(Matches))
                {
                    _visible.Add(entry);
                    if (entry.Area == "panic")
                        SyncPanics();
                }
                ScrollToEnd();
                break;
            // the oldest entries are trimmed from the top.
            case NotifyCollectionChangedAction.Remove:
                foreach (var entry in e.OldItems!.OfType<LogEntry>())
                    _visible.Remove(entry);
                break;
            default:
                Rebuild();
                break;
        }
    }

    private void ScrollToEnd()
    {
        if (FollowToggle.IsChecked == true && _visible.Count > 0)
            Entries.ScrollIntoView(_visible[^1]);
    }

    private void SyncPanics()
    {
        var count = CoreApi.PanicCount;
        var logged = File.Exists(PanicLog);
        PanicBar.IsOpen = count > 0 || logged;
        PanicBar.Severity = count > 0 ? InfoBarSeverity.Error : InfoBarSeverity.Warning;
        PanicBar.Message = count > 0 ? L.F("log.panics_now", count) : L.T("log.panics_file");
    }

    // ───────────────────────── events ─────────────────────────

    private void LevelBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => Rebuild();
    private void Search_TextChanged(object sender, TextChangedEventArgs e) => Rebuild();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var rows = Entries.SelectedItems.Count > 0 ? Entries.SelectedItems.OfType<LogEntry>() : _visible;
        var package = new DataPackage();
        package.SetText(string.Join(Environment.NewLine, rows));
        Clipboard.SetContent(package);
        ShellView.Current?.Notify(L.T("log.copied"), LogLevel.Ok);
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (CoreLog.FilePath is { } path)
            Ui.OpenPath(path);
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => CoreLog.Clear();

    private async void ShowPanics_Click(object sender, RoutedEventArgs e)
    {
        string text;
        try
        {
            text = File.Exists(PanicLog) ? await File.ReadAllTextAsync(PanicLog) : L.T("log.no_panic_file");
        }
        catch (IOException ex)
        {
            text = ex.Message;
        }
        await Ui.ShowTextAsync(XamlRoot, PanicLog, text);
    }
}
