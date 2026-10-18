// DEVICES tab (the TUI SYNC tab): two-way library sync with another device
// and the paired device list (rename — the name is for viewing only, forget).
// Both devices end up with every playlist and the songs in playlists; the
// listening side gives its library first, then takes the other one.

using System.Collections.ObjectModel;
using DreamPlayer.Core;
using DreamPlayer.Services;
using DreamPlayer.Services.Sync;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace DreamPlayer.Views;

public sealed partial class SyncPage : UserControl, ITabPage
{
    private readonly ObservableCollection<LogEntry> _messages = [];
    private SyncWorker? _worker;

    public SyncPage()
    {
        InitializeComponent();
        Messages.ItemsSource = _messages;
        OwnKey.Text = CoreApi.Initialized && CoreApi.Con.PubKey() is { } key
            ? Format.Fingerprint(key)
            : L.T("sync.own_key_unavailable");
        Report(L.T("sync.ready"), LogLevel.Info);
        StartButton.IsEnabled = CoreApi.Initialized;
        ReloadPaired();
    }

    public void OnClosed() => _worker?.Cancel();

    private void Report(string text, LogLevel level) => DispatcherQueue.TryEnqueue(() =>
    {
        _messages.Add(new LogEntry(DateTime.Now, level, "sync", text));
        Messages.ScrollIntoView(_messages[^1]);
    });

    private void ReloadPaired()
    {
        var keep = (PairedList.SelectedItem as PairedDevice)?.Id;
        var paired = CoreApi.Initialized ? CoreApi.Con.Paired() : null;
        PairedList.ItemsSource = paired ?? [];
        PairedList.SelectedItem = paired?.FirstOrDefault(p => p.Id == keep);
        PairedEmpty.Text = paired switch
        {
            null => L.T("sync.paired_failed"),
            [] => L.T("sync.paired_none"),
            _ => "",
        };
        PairedEmpty.Visibility = Ui.VisibleText(PairedEmpty.Text);
        SyncButtons();
    }

    private void SyncButtons()
    {
        var sel = PairedList.SelectedItem is PairedDevice;
        RenameButton.IsEnabled = sel;
        ForgetButton.IsEnabled = sel;
    }

    private async Task StartAsync()
    {
        if (Connection.Target(out var error) is not { } target)
        {
            Report(error ?? "", LogLevel.Warn);
            return;
        }
        var worker = SyncWorker.Start(target, new SyncMode.Sync(), AppSettings.Current.MusicDir, Report);
        if (worker is null)
            return;
        _worker = worker;
        SetRunning(true);
        try
        {
            await ShellView.Current!.FinishSyncAsync(worker);
        }
        finally
        {
            SetRunning(false);
            // a new device may have been paired.
            ReloadPaired();
        }
    }

    private void SetRunning(bool running)
    {
        StartButton.IsEnabled = !running && CoreApi.Initialized;
        CancelButton.IsEnabled = running;
        Connection.IsEnabled = !running;
        Progress.IsActive = running;
    }

    private async Task RenameAsync(PairedDevice p)
    {
        var name = await Ui.PromptAsync(XamlRoot, L.T("sync.rename_title"), L.T("sync.rename_hint"), p.Name);
        if (name is null || name.Trim().Length == 0 || name.Trim() == p.Name)
            return;
        var st = CoreApi.Con.Rename(p.Id, name.Trim());
        if (st == ConStatus.Ok)
            Report(L.F("sync.renamed", p.Id, name.Trim()), LogLevel.Ok);
        else
            Report(L.F("sync.rename_failed", CoreApi.Describe(st)), LogLevel.Error);
        ReloadPaired();
    }

    private async Task ForgetAsync(PairedDevice p)
    {
        if (!await Ui.ConfirmAsync(XamlRoot, L.T("confirm.forget_title"), L.F("confirm.forget_text", p.Name, p.Fingerprint),
                L.T("sync.forget"), destructive: true))
            return;
        var st = CoreApi.Con.Forget(p.Id);
        if (st == ConStatus.Ok)
            Report(L.F("sync.forgotten", p.Id), LogLevel.Ok);
        else
            Report(L.F("sync.forget_failed", CoreApi.Describe(st)), LogLevel.Error);
        ReloadPaired();
    }

    // ───────────────────────── events ─────────────────────────

    private async void Start_Click(object sender, RoutedEventArgs e) => await StartAsync();

    private void Cancel_Click(object sender, RoutedEventArgs e) => _worker?.Cancel();

    private void Reload_Click(object sender, RoutedEventArgs e) => ReloadPaired();

    private async void SaveLog_Click(object sender, RoutedEventArgs e) => await Ui.SaveLogAsync(_messages, "dream_player_sync");

    private void PairedList_SelectionChanged(object sender, SelectionChangedEventArgs e) => SyncButtons();

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (PairedList.SelectedItem is PairedDevice p)
            await RenameAsync(p);
    }

    private async void Forget_Click(object sender, RoutedEventArgs e)
    {
        if (PairedList.SelectedItem is PairedDevice p)
            await ForgetAsync(p);
    }

    private void PairedList_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (Ui.ContextItem<PairedDevice>(e) is not { } p)
            return;
        PairedList.SelectedItem = p;
        Ui.ShowMenu(Ui.Menu(
            new(L.T("sync.rename"), "", () => _ = RenameAsync(p)),
            null,
            new(L.T("sync.forget"), "", () => _ = ForgetAsync(p), Destructive: true)), PairedList, e);
    }
}
