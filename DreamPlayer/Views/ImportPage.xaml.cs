// IMPORT tab: packet file + target directory (default — the music directory)
// + force. An outdated packet is offered to be applied with force.

using System.Collections.ObjectModel;
using DreamPlayer.Core;
using DreamPlayer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DreamPlayer.Views;

public sealed partial class ImportPage : UserControl, ITabPage
{
    private readonly ObservableCollection<LogEntry> _messages = [];

    public ImportPage()
    {
        InitializeComponent();
        Messages.ItemsSource = _messages;
        SaveToBox.Text = AppSettings.Current.ImportSaveTo ?? AppSettings.Current.MusicDir;
        Report(L.T("import.ready"), LogLevel.Info);
        Inputs_Changed(this, null!);
    }

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

    private void Inputs_Changed(object sender, TextChangedEventArgs e) =>
        RunButton.IsEnabled = CoreApi.Initialized
            && PacketBox.Text.Trim().Length > 0
            && SaveToBox.Text.Trim().Length > 0;

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
