// EXPORT tab: what (library / playlists / tracks), flags, the selection list
// (checkboxes, search keeps the marks), the target file from a save dialog.
// The same selection and flags can be sent straight to another device
// instead (it receives on its import tab).

using System.Collections.ObjectModel;
using DreamPlayer.Core;
using DreamPlayer.Services;
using DreamPlayer.Services.Sync;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DreamPlayer.Views;

/// a row of the selection list: a playlist (by name) or a track (by id).
public sealed record ExportItem(string Title, string Subtitle, string? Playlist, long? TrackId)
{
    public override string ToString() => $"{Title}, {Subtitle}";
}

public sealed partial class ExportPage : UserControl, ITabPage
{
    private readonly ObservableCollection<LogEntry> _messages = [];
    /// marked items survive search changes.
    private readonly HashSet<ExportItem> _checked = [];
    private List<ExportItem> _all = [];
    private bool _syncing;
    /// a running send to another device.
    private SyncWorker? _worker;

    public ExportPage()
    {
        InitializeComponent();
        Messages.ItemsSource = _messages;
        ModeBox.Items.Add(L.T("export.mode_library"));
        ModeBox.Items.Add(L.T("export.mode_playlists"));
        ModeBox.Items.Add(L.T("export.mode_tracks"));
        AudioSwitch.IsOn = AppSettings.Current.ExportAudio;
        NamesSwitch.IsOn = AppSettings.Current.ExportNames;
        CoversSwitch.IsOn = AppSettings.Current.ExportCovers;
        ModeBox.SelectedIndex = 0;
        Library.Instance.Changed += Rebuild;
        Report(L.T("export.ready"), LogLevel.Info);
    }

    public void OnClosed()
    {
        Library.Instance.Changed -= Rebuild;
        _worker?.Cancel();
    }

    private ExportMode Mode => (ExportMode)Math.Max(0, ModeBox.SelectedIndex);

    /// opens the tab with a ready selection (from a context menu).
    public void Preselect(ExportMode mode, IEnumerable<string>? playlists, IEnumerable<long>? ids)
    {
        ModeBox.SelectedIndex = (int)mode;
        Rebuild();
        var names = playlists?.ToHashSet() ?? [];
        var tracks = ids?.ToHashSet() ?? [];
        _checked.Clear();
        foreach (var item in _all.Where(i => (i.Playlist is { } n && names.Contains(n)) || (i.TrackId is { } id && tracks.Contains(id))))
            _checked.Add(item);
        Show();
    }

    private void Report(string text, LogLevel level) => DispatcherQueue.TryEnqueue(() =>
    {
        _messages.Add(new LogEntry(DateTime.Now, level, "export", text));
        Messages.ScrollIntoView(_messages[^1]);
    });

    private void Rebuild()
    {
        var lib = Library.Instance;
        _all = Mode switch
        {
            ExportMode.Playlists => lib.Playlists
                .Where(p => !p.IsPool)
                .Select(p => new ExportItem(p.Name, $"{p.CountText} · {p.TotalDuration}", p.Name, null))
                .ToList(),
            ExportMode.Tracks => lib.Pool
                .Where(t => t.Id is not null)
                .OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
                .Select(t => new ExportItem(t.Title, $"{t.ArtistsLine} · {t.DurationText}", null, t.Id))
                .ToList(),
            _ => [],
        };
        _checked.IntersectWith(_all);
        Show();
    }

    private void Show()
    {
        var library = Mode == ExportMode.Library;
        Items.Visibility = Ui.Hidden(library);
        Search.IsEnabled = !library;
        AllButton.IsEnabled = !library;
        NoneButton.IsEnabled = !library;
        CoversSwitch.IsEnabled = Mode != ExportMode.Tracks;
        LibraryText.Text = library
            ? L.F("export.library_info", Library.Instance.Pool.Count, Library.Instance.Playlists.Count - 1)
            : "";
        LibraryText.Visibility = Ui.Visible(library);

        var query = Search.Text;
        var visible = _all
            .Where(i => string.IsNullOrWhiteSpace(query)
                || $"{i.Title} {i.Subtitle}".Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        _syncing = true;
        Items.ItemsSource = visible;
        // the selection is set after the containers exist.
        DispatcherQueue.TryEnqueue(() =>
        {
            _syncing = true;
            foreach (var item in visible.Where(_checked.Contains))
                Items.SelectedItems.Add(item);
            _syncing = false;
        });
        _syncing = false;
        SyncTitle();
    }

    private void SyncTitle() =>
        SelectionTitle.Text = Mode == ExportMode.Library
            ? L.T("export.mode_library")
            : L.F("export.selected", _checked.Count, _all.Count);

    /// the marked selection and the flags (saved as the defaults); `null` —
    /// nothing marked (reported).
    private (ExportMode Mode, List<string> Names, List<long> Ids, PackFlags Flags)? Picked()
    {
        var mode = Mode;
        var names = _all.Where(_checked.Contains).Select(i => i.Playlist).OfType<string>().ToList();
        var ids = _all.Where(_checked.Contains).Select(i => i.TrackId).OfType<long>().ToList();
        if (mode != ExportMode.Library && names.Count + ids.Count == 0)
        {
            Report(L.T("export.nothing_selected"), LogLevel.Warn);
            return null;
        }
        // no playlists — no covers (the switch is disabled then).
        var flags = new PackFlags(AudioSwitch.IsOn, NamesSwitch.IsOn, CoversSwitch.IsOn && mode != ExportMode.Tracks);
        AppSettings.Current.ExportAudio = AudioSwitch.IsOn;
        AppSettings.Current.ExportNames = NamesSwitch.IsOn;
        AppSettings.Current.ExportCovers = CoversSwitch.IsOn;
        AppSettings.Save();
        return (mode, names, ids, flags);
    }

    /// sends the selection to another device (it receives on its import tab).
    private async Task SendAsync()
    {
        if (Picked() is not var (mode, names, ids, flags))
            return;
        if (Connection.Target(out var error) is not { } target)
        {
            Report(error ?? "", LogLevel.Warn);
            return;
        }
        var what = mode switch
        {
            ExportMode.Library => L.T("export.what_library"),
            ExportMode.Playlists => L.F("export.what_playlists", names.Count),
            _ => L.F("export.what_tracks", ids.Count),
        };
        Report(L.F("export.sending", what, flags.Audio, flags.Names, flags.Covers), LogLevel.Info);
        var worker = SyncWorker.Start(target, new SyncMode.Send(mode, names, ids, flags), AppSettings.Current.MusicDir, Report);
        if (worker is null)
            return;
        _worker = worker;
        SetSending(true);
        try
        {
            await ShellView.Current!.FinishSyncAsync(worker);
        }
        finally
        {
            SetSending(false);
        }
    }

    private void SetSending(bool running)
    {
        SendButton.IsEnabled = !running && CoreApi.Initialized;
        CancelSendButton.IsEnabled = running;
        Connection.IsEnabled = !running;
        SendProgress.IsActive = running;
    }

    private async Task RunAsync()
    {
        if (Picked() is not var (mode, names, ids, flags))
            return;
        var target = await Ui.PickSaveFileAsync(Transfer.DefaultFileName(), L.T("export.file_type"), Transfer.Extension);
        if (target is null)
            return;

        RunButton.IsEnabled = false;
        Progress.IsActive = true;
        try
        {
            var ok = await CoreApi.RunLong(() => Transfer.Export(target, mode, names, ids, flags, Report));
            ShellView.Current?.Notify(ok ? L.T("msg.export_done") : L.T("msg.export_failed"), ok ? LogLevel.Ok : LogLevel.Error);
        }
        finally
        {
            RunButton.IsEnabled = CoreApi.Initialized;
            Progress.IsActive = false;
        }
    }

    // ───────────────────────── events ─────────────────────────

    private void ModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _checked.Clear();
        Search.Text = "";
        Rebuild();
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e) => Show();

    private void Items_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing)
            return;
        foreach (var item in e.AddedItems.OfType<ExportItem>())
            _checked.Add(item);
        foreach (var item in e.RemovedItems.OfType<ExportItem>())
            _checked.Remove(item);
        SyncTitle();
    }

    private void All_Click(object sender, RoutedEventArgs e) => Items.SelectAll();

    private void None_Click(object sender, RoutedEventArgs e)
    {
        // clears hidden (filtered out) marks too.
        _checked.Clear();
        Items.SelectedItems.Clear();
        SyncTitle();
    }

    private async void Run_Click(object sender, RoutedEventArgs e) => await RunAsync();
    private async void Send_Click(object sender, RoutedEventArgs e) => await SendAsync();
    private void CancelSend_Click(object sender, RoutedEventArgs e) => _worker?.Cancel();
    private async void SaveLog_Click(object sender, RoutedEventArgs e) => await Ui.SaveLogAsync(_messages, "dream_player_export");
}
