// SETTINGS tab. The language switch rebuilds the shell (x:Bind texts are set
// once per page), the output device switch keeps playback going.

using DreamPlayer.Core;
using DreamPlayer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace DreamPlayer.Views;

public sealed partial class SettingsPage : UserControl, ITabPage
{
    private static readonly (string Code, string Name)[] Languages = [("ru", "Русский"), ("en", "English")];

    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();
        var s = AppSettings.Current;
        foreach (var (_, name) in Languages)
            LanguageBox.Items.Add(name);
        LanguageBox.SelectedIndex = Math.Max(0, Array.FindIndex(Languages, l => l.Code == s.Language));
        SpeedSlider.Value = s.Speed;
        SyncSpeedText();
        SeekStepBox.Value = s.SeekStep;
        MusicDirBox.Text = s.MusicDir;
        VerboseSwitch.IsOn = s.VerboseLog;
        LoadDevices();
        SyncLibraryInfo();
        FillAbout();
        Library.Instance.Changed += SyncLibraryInfo;
        _loading = false;
    }

    public void OnClosed() => Library.Instance.Changed -= SyncLibraryInfo;

    private void LoadDevices()
    {
        var was = _loading;
        _loading = true;
        DeviceBox.Items.Clear();
        if (CoreApi.Initialized)
        {
            var current = CoreApi.Engine.OutputDevice();
            foreach (var d in CoreApi.Engine.OutputDevices())
                DeviceBox.Items.Add(d);
            DeviceBox.SelectedItem = DeviceBox.Items.OfType<string>().FirstOrDefault(d => d == current);
        }
        DeviceBox.IsEnabled = DeviceBox.Items.Count > 0;
        _loading = was;
    }

    private void SyncLibraryInfo()
    {
        var lib = Library.Instance;
        LibraryInfo.Text = L.F("settings.library_info", lib.Pool.Count, Math.Max(0, lib.Playlists.Count - 1),
            lib.Pool.Count(t => t.Invalid));
    }

    private void SyncSpeedText() => SpeedText.Text = L.F("settings.speed", SpeedSlider.Value.ToString("0.00"));

    private void FillAbout()
    {
        var core = Path.Combine(AppContext.BaseDirectory, "dream_player.dll");
        var rows = new (string, string)[]
        {
            ("settings.core_status", App.CoreLoadError is { } err
                ? L.F("settings.core_missing", err)
                : App.CoreStatus is { } st ? CoreApi.Describe(st) : "—"),
            ("settings.core_lib", File.Exists(core) ? core : "—"),
            ("settings.data_dir", App.DataDir),
            ("settings.config_dir", App.ConfigDir),
            ("settings.panics", CoreApi.PanicCount.ToString()),
        };
        for (var i = 0; i < rows.Length; i++)
        {
            AboutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var key = new TextBlock { Text = L.T(rows[i].Item1), Foreground = Ui.Muted };
            var value = new TextBlock { Text = rows[i].Item2, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(key, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            AboutGrid.Children.Add(key);
            AboutGrid.Children.Add(value);
        }
    }

    // ───────────────────────── events ─────────────────────────

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageBox.SelectedIndex < 0)
            return;
        var code = Languages[LanguageBox.SelectedIndex].Code;
        if (code == AppSettings.Current.Language)
            return;
        AppSettings.Current.Language = code;
        AppSettings.Save();
        L.Language = code;
        CoreLog.Info("app", $"interface language: {code}");
        // after the current event: the shell (and this page) are replaced.
        DispatcherQueue.TryEnqueue(() => App.Window?.Rebuild(ShellView.SettingsKey));
    }

    private void DeviceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DeviceBox.SelectedItem is not string name)
            return;
        if (!Player.Instance.SwitchDevice(name))
            LoadDevices();
    }

    private void RefreshDevices_Click(object sender, RoutedEventArgs e) => LoadDevices();

    private void SpeedSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        SyncSpeedText();
        if (!_loading && CoreApi.Initialized)
            Player.Instance.SetSpeed((float)e.NewValue);
    }

    private void ResetSpeed_Click(object sender, RoutedEventArgs e) => SpeedSlider.Value = 1;

    private void SeekStepBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(args.NewValue))
            return;
        AppSettings.Current.SeekStep = (int)Math.Clamp(args.NewValue, 1, 120);
        AppSettings.Save();
    }

    private void MusicDirBox_LostFocus(object sender, RoutedEventArgs e) => SetMusicDir(MusicDirBox.Text.Trim());

    private async void BrowseMusicDir_Click(object sender, RoutedEventArgs e)
    {
        if (await Ui.PickFolderAsync() is not { } dir)
            return;
        MusicDirBox.Text = dir;
        if (SetMusicDir(dir) && ShellView.Current is { } shell)
            await shell.IndexMusicDirAsync();
    }

    /// `true` — the directory changed.
    private static bool SetMusicDir(string dir)
    {
        if (dir.Length == 0 || dir == AppSettings.Current.MusicDir)
            return false;
        AppSettings.Current.MusicDir = dir;
        AppSettings.Save();
        CoreLog.Info("app", $"music directory: {dir}");
        return true;
    }

    private async void Rescan_Click(object sender, RoutedEventArgs e)
    {
        SetMusicDir(MusicDirBox.Text.Trim());
        if (ShellView.Current is { } shell)
            await shell.IndexMusicDirAsync();
    }

    private async void ScanFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ShellView.Current is { } shell)
            await shell.ScanFolderAsync();
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        if (ShellView.Current is { } shell)
            await shell.AddFilesAsync();
    }

    private async void RemoveInvalid_Click(object sender, RoutedEventArgs e)
    {
        if (ShellView.Current is { } shell)
            await shell.RemoveInvalidAsync();
    }

    private void VerboseSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        CoreLog.Verbose = AppSettings.Current.VerboseLog = VerboseSwitch.IsOn;
        AppSettings.Save();
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e) => ShellView.Current?.OpenLog();
}
