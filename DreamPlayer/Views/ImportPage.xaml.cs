// IMPORT tab: packet file + target directory (default — the music directory)
// + force. An outdated packet is offered to be applied with force. The same
// import can come straight from another device (it sends from its export tab).

using System.Collections.ObjectModel;
using DreamPlayer.Core;
using DreamPlayer.Services;
using DreamPlayer.Services.Sync;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DreamPlayer.Views;

public sealed partial class ImportPage : UserControl, ITabPage
{
    private readonly ObservableCollection<LogEntry> _messages = [];
    /// a running receive from another device.
    private SyncWorker? _worker;

    public ImportPage()
    {
        InitializeComponent();
        Messages.ItemsSource = _messages;
        SaveToBox.Text = AppSettings.Current.ImportSaveTo ?? AppSettings.Current.MusicDir;
        Report(L.T("import.ready"), LogLevel.Info);
        Inputs_Changed(this, null!);
        ReceiveButton.IsEnabled = CoreApi.Initialized;
    }

    public void OnClosed() => _worker?.Cancel();

    private void Report(string text, LogLevel level)
    {
        // Transfer reports from a worker thread.
        DispatcherQueue.TryEnqueue(() =>
        {
            _messages.Add(new LogEntry(DateTime.Now, level, "import", text));
            Messages.ScrollIntoView(_messages[^1]);
        });
    }

    private async Task RunAsync(bool force)
    {
        var packet = PacketBox.Text.Trim();
        var saveTo = SaveToBox.Text.Trim();
        AppSettings.Current.ImportSaveTo = saveTo;
        AppSettings.Save();

        RunButton.IsEnabled = false;
        Progress.IsActive = true;
        ImportResult result;
        try
        {
            result = await CoreApi.RunLong(() => Transfer.Import(packet, saveTo, force, Report));
        }
        finally
        {
            RunButton.IsEnabled = true;
            Progress.IsActive = false;
        }

        switch (result)
        {
            case ImportResult.Ok:
                ShellView.Current?.ReloadLibrary();
                ShellView.Current?.Notify(L.T("msg.import_done"), LogLevel.Ok);
                break;
            case ImportResult.Outdated when !force:
                if (await Ui.ConfirmAsync(XamlRoot, L.T("confirm.outdated_title"), L.T("confirm.outdated_text"),
                        L.T("confirm.outdated_yes"), destructive: true))
                    await RunAsync(force: true);
                break;
            default:
                ShellView.Current?.Notify(L.T("msg.import_failed"), LogLevel.Error);
                break;
        }
    }

    /// receives from another device that sends from its export tab: FORCE
    /// applies, a received song replaces a local file with the same name,
    /// other files go to the target folder.
    private async Task ReceiveAsync()
    {
        var saveTo = SaveToBox.Text.Trim();
        if (saveTo.Length == 0)
        {
            Report(L.T("import.no_save_to"), LogLevel.Warn);
            return;
        }
        if (Connection.Target(out var error) is not { } target)
        {
            Report(error ?? "", LogLevel.Warn);
            return;
        }
        AppSettings.Current.ImportSaveTo = saveTo;
        AppSettings.Save();
        var force = ForceSwitch.IsOn;
        Report(L.F("import.receiving", saveTo) + (force ? " (force)" : ""), LogLevel.Info);
        var worker = SyncWorker.Start(target, new SyncMode.Receive(force), saveTo, Report);
        if (worker is null)
            return;
        _worker = worker;
        SetReceiving(true);
        try
        {
            await ShellView.Current!.FinishSyncAsync(worker);
        }
        finally
        {
            SetReceiving(false);
        }
    }

    private void SetReceiving(bool running)
    {
        ReceiveButton.IsEnabled = !running && CoreApi.Initialized;
        CancelReceiveButton.IsEnabled = running;
        Connection.IsEnabled = !running;
        ReceiveProgress.IsActive = running;
    }

    private void Inputs_Changed(object sender, TextChangedEventArgs e) =>
        RunButton.IsEnabled = CoreApi.Initialized
            && PacketBox.Text.Trim().Length > 0
            && SaveToBox.Text.Trim().Length > 0;

    private async void Receive_Click(object sender, RoutedEventArgs e) => await ReceiveAsync();

    private void CancelReceive_Click(object sender, RoutedEventArgs e) => _worker?.Cancel();

    private async void SaveLog_Click(object sender, RoutedEventArgs e) => await Ui.SaveLogAsync(_messages, "dream_player_import");

    private async void BrowsePacket_Click(object sender, RoutedEventArgs e)
    {
        if (await Ui.PickFileAsync(Transfer.Extension, "*") is { } path)
            PacketBox.Text = path;
    }

    private async void BrowseSaveTo_Click(object sender, RoutedEventArgs e)
    {
        if (await Ui.PickFolderAsync() is { } path)
            SaveToBox.Text = path;
    }

    private async void Run_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(ForceSwitch.IsOn);
}
