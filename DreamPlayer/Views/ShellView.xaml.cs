// The shell: tab management, the menu, the now-playing bar, master volume,
// messages and library-wide actions (scan, add files, remove invalid).
//
// Tabs:
// - PLAYLISTS — permanent;
// - NOW PLAYING — exists while a queue is loaded; closing it stops playback;
// - SONG — closes by itself as soon as another tab is selected;
// - editors, import, export, settings, log — opened on demand, closable.

using System.ComponentModel;
using DreamPlayer.Core;
using DreamPlayer.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace DreamPlayer.Views;

/// optional hooks of a tab page.
public interface ITabPage
{
    /// the tab was closed: drop subscriptions.
    void OnClosed() { }
}

public sealed partial class ShellView : UserControl
{
    public const string PlaylistsKey = "playlists";
    public const string NowKey = "now";
    public const string SongKey = "song";
    public const string ImportKey = "import";
    public const string ExportKey = "export";
    public const string SettingsKey = "settings";
    public const string LogKey = "log";

    private static readonly TimeSpan ToastShort = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ToastLong = TimeSpan.FromSeconds(10);

    private readonly Player _p = Player.Instance;
    private TabViewItem? _lastSelected;
    private DateTime _toastUntil = DateTime.MaxValue;
    private bool _seeking;
    private bool _syncing;
    private int _busy;
    private string? _coverKey;

    public static ShellView? Current => App.Window?.Shell;

    public ShellView()
    {
        InitializeComponent();

        Open(PlaylistsKey, L.T("tab.playlists"), "", () => new PlaylistsPage(), closable: false);
        if (_p.IsActive)
            Open(NowKey, NowHeader(), "", () => new NowPlayingPage(), select: false);

        _p.PropertyChanged += OnPlayerChanged;
        _p.TrackStarted += OnTrackStarted;
        _p.Stopped += OnStopped;
        _p.Notify += Notify;

        SeekSlider.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => _seeking = true), true);
        SeekSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => EndSeek()), true);
        SeekSlider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => EndSeek()), true);

        AddAccelerators();
        SyncBar();
        Loaded += (_, _) => ReportStartup();
    }

    /// the shell is being replaced (language switch): stop listening.
    public void Detach()
    {
        _p.PropertyChanged -= OnPlayerChanged;
        _p.TrackStarted -= OnTrackStarted;
        _p.Stopped -= OnStopped;
        _p.Notify -= Notify;
        foreach (var page in Tabs.TabItems.OfType<TabViewItem>().Select(t => t.Content).OfType<ITabPage>())
            page.OnClosed();
    }

    private void ReportStartup()
    {
        if (App.CoreLoadError is { } err)
            Notify(L.F("msg.core_missing", err), LogLevel.Error, sticky: true);
        else if (App.CoreStatus is { } st && st != InitStatus.Ok && st != InitStatus.AlreadyInitialized)
            Notify(L.F("msg.core_init_failed", CoreApi.Describe(st)), LogLevel.Error, sticky: true);
    }

    // ───────────────────────── tabs ─────────────────────────

    private TabViewItem? Find(string key) =>
        Tabs.TabItems.OfType<TabViewItem>().FirstOrDefault(t => (string)t.Tag == key);

    /// opens (or selects an already open) tab.
    private TabViewItem Open(string key, string header, string glyph, Func<UIElement> make,
        bool closable = true, bool select = true, int? at = null)
    {
        var tab = Find(key);
        if (tab is null)
        {
            tab = new TabViewItem
            {
                Tag = key,
                Header = header,
                IconSource = new FontIconSource { Glyph = glyph },
                IsClosable = closable,
                Content = make(),
            };
            if (at is { } i && i <= Tabs.TabItems.Count)
                Tabs.TabItems.Insert(i, tab);
            else
                Tabs.TabItems.Add(tab);
        }
        if (select)
            Tabs.SelectedItem = tab;
        return tab;
    }

    private void RemoveTab(TabViewItem tab)
    {
        if (!Tabs.TabItems.Contains(tab))
            return;
        (tab.Content as ITabPage)?.OnClosed();
        Tabs.TabItems.Remove(tab);
        if (_lastSelected == tab)
            _lastSelected = null;
    }

    public void CloseTab(string key)
    {
        if (Find(key) is { } tab)
            CloseRequested(tab);
    }

    /// closes the tab hosting `page` (an editor after save / cancel).
    public void CloseTab(UIElement page)
    {
        if (Tabs.TabItems.OfType<TabViewItem>().FirstOrDefault(t => ReferenceEquals(t.Content, page)) is { } tab)
            CloseRequested(tab);
    }

    private void CloseRequested(TabViewItem tab)
    {
        if (!tab.IsClosable)
            return;
        // closing the queue = stop (the Stopped handler removes the tab).
        if ((string)tab.Tag == NowKey && _p.IsActive)
        {
            _p.Stop();
            return;
        }
        RemoveTab(tab);
    }

    private void Tabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args) =>
        CloseRequested(args.Tab);

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = Tabs.SelectedItem as TabViewItem;
        // the song tab lives only while it is selected.
        if (_lastSelected is { } prev && prev != selected && (string)prev.Tag == SongKey)
            DispatcherQueue.TryEnqueue(() => RemoveTab(prev));
        _lastSelected = selected;
    }

    private string NowHeader() =>
        string.IsNullOrEmpty(_p.PlaylistName) ? L.T("tab.now") : L.F("tab.now_named", _p.PlaylistName);

    /// position right after PLAYLISTS / NOW PLAYING.
    private int AfterMain() => Find(NowKey) is { } now ? Tabs.TabItems.IndexOf(now) + 1 : 1;

    public void OpenNowPlaying() => Open(NowKey, NowHeader(), "", () => new NowPlayingPage(), at: 1);

    /// the song tab: `track` null — follows the playing track.
    public void OpenSong(Track? track = null)
    {
        if (track is null && _p.Current is null)
            return;
        if (Find(SongKey) is { } tab)
        {
            (tab.Content as ITabPage)?.OnClosed();
            tab.Content = new SongPage(track);
            Tabs.SelectedItem = tab;
            return;
        }
        Open(SongKey, L.T("tab.song"), "", () => new SongPage(track), at: AfterMain());
    }

    public void OpenPlaylistEditor(PlaylistView? pl)
    {
        if (pl is { IsPool: true })
        {
            Notify(L.T("msg.pool_not_editable"), LogLevel.Warn);
            return;
        }
        var key = pl is null ? "pl-new" : $"pl-edit:{pl.Name}";
        var header = pl is null ? L.T("tab.new_playlist") : L.F("tab.edit_playlist", pl.Name);
        Open(key, header, pl is null ? "" : "", () => new PlaylistEditorPage(pl));
    }

    public void OpenTrackEditor(Track t)
    {
        if (t.Id is null)
        {
            Notify(L.T("msg.not_in_library"), LogLevel.Warn);
            return;
        }
        var title = t.Title.Length > 18 ? t.Title[..18] + "…" : t.Title;
        Open($"track-edit:{t.Key}", L.F("tab.edit_track", title), "", () => new TrackEditorPage(t));
    }

    public void OpenImport() => Open(ImportKey, L.T("tab.import"), "", () => new ImportPage());

    public void OpenExport(ExportMode? mode = null, IEnumerable<string>? playlists = null, IEnumerable<long>? ids = null)
    {
        var tab = Open(ExportKey, L.T("tab.export"), "", () => new ExportPage());
        if (mode is { } m)
            ((ExportPage)tab.Content).Preselect(m, playlists, ids);
    }

    public void OpenSettings() => Open(SettingsKey, L.T("tab.settings"), "", () => new SettingsPage());

    public void OpenLog() => Open(LogKey, L.T("tab.log"), "", () => new LogPage());

    // ───────────────────────── messages ─────────────────────────

    public void Notify(string text, LogLevel level) => Notify(text, level, sticky: false);

    public void Notify(string text, LogLevel level, bool sticky)
    {
        Toast.Severity = level switch
        {
            LogLevel.Error => InfoBarSeverity.Error,
            LogLevel.Warn => InfoBarSeverity.Warning,
            LogLevel.Ok => InfoBarSeverity.Success,
            _ => InfoBarSeverity.Informational,
        };
        Toast.Message = text;
        Toast.IsOpen = true;
        _toastUntil = sticky ? DateTime.MaxValue : DateTime.UtcNow + (level >= LogLevel.Warn ? ToastLong : ToastShort);
    }

    /// called by the window timer.
    public void OnTick()
    {
        if (Toast.IsOpen && DateTime.UtcNow > _toastUntil)
            Toast.IsOpen = false;
    }

    /// runs a long core operation off the UI thread with a busy bar.
    public async Task<T> RunBusyAsync<T>(string text, Func<T> f)
    {
        _busy++;
        BusyBar.Message = text;
        BusyBar.IsOpen = true;
        try
        {
            return await CoreApi.RunLong(f);
        }
        finally
        {
            if (--_busy == 0)
                BusyBar.IsOpen = false;
        }
    }

    // ───────────────────────── library actions ─────────────────────────

    /// re-reads the library after a db change and syncs the queue.
    public void ReloadLibrary()
    {
        if (!Library.Instance.Reload())
            Notify(L.T("msg.library_reload_failed"), LogLevel.Error);
        _p.SyncWithLibrary();
    }

    private bool RequireCore()
    {
        if (CoreApi.Initialized)
            return true;
        Notify(L.T("msg.core_not_ready"), LogLevel.Error);
        return false;
    }

    public Task IndexMusicDirAsync() => IndexDirAsync(AppSettings.Current.MusicDir);

    public async Task IndexDirAsync(string dir)
    {
        if (!RequireCore())
            return;
        if (!Directory.Exists(dir))
        {
            Notify(L.F("msg.dir_missing", dir), LogLevel.Warn);
            return;
        }
        var st = await RunBusyAsync(L.F("busy.indexing", dir), () => CoreApi.Library.IndexDir(dir));
        ReloadLibrary();
        if (st == IdxStatus.Ok)
            Notify(L.F("msg.indexed", dir, Library.Instance.Pool.Count), LogLevel.Ok);
        else
            Notify(L.F("msg.index_failed", CoreApi.Describe(st)), LogLevel.Error);
    }

    public async Task ScanFolderAsync()
    {
        if (await Ui.PickFolderAsync() is { } dir)
            await IndexDirAsync(dir);
    }

    public async Task AddFilesAsync()
    {
        if (!RequireCore())
            return;
        var files = await Ui.PickFilesAsync(Ui.AudioTypes);
        if (files.Count == 0)
            return;
        var added = await RunBusyAsync(L.F("busy.adding", files.Count), () => CoreApi.Library.AddTracks(files));
        ReloadLibrary();
        if (added is { } n)
            Notify(L.F("msg.added", n, files.Count), n > 0 ? LogLevel.Ok : LogLevel.Warn);
        else
            Notify(L.T("msg.add_failed"), LogLevel.Error);
    }

    public async Task RemoveInvalidAsync()
    {
        if (!RequireCore())
            return;
        var invalid = Library.Instance.Pool.Count(t => t.Invalid);
        if (!await Ui.ConfirmAsync(XamlRoot, L.T("confirm.remove_invalid_title"),
                L.F("confirm.remove_invalid_text", invalid), L.T("common.remove"), destructive: true))
            return;
        var removed = CoreApi.Library.RemoveInvalid();
        ReloadLibrary();
        if (removed is { } n)
            Notify(L.F("msg.removed_invalid", n), LogLevel.Ok);
        else
            Notify(L.T("msg.remove_invalid_failed"), LogLevel.Error);
    }

    // ───────────────────────── menu ─────────────────────────

    private void Menu_Import(object sender, RoutedEventArgs e) => OpenImport();
    private void Menu_Export(object sender, RoutedEventArgs e) => OpenExport();
    private void Menu_NewPlaylist(object sender, RoutedEventArgs e) => OpenPlaylistEditor(null);
    private async void Menu_AddFiles(object sender, RoutedEventArgs e) => await AddFilesAsync();
    private async void Menu_ScanFolder(object sender, RoutedEventArgs e) => await ScanFolderAsync();
    private async void Menu_Rescan(object sender, RoutedEventArgs e) => await IndexMusicDirAsync();
    private async void Menu_RemoveInvalid(object sender, RoutedEventArgs e) => await RemoveInvalidAsync();
    private void Menu_Settings(object sender, RoutedEventArgs e) => OpenSettings();
    private void Menu_Log(object sender, RoutedEventArgs e) => OpenLog();
    private void Menu_Exit(object sender, RoutedEventArgs e) => App.Window?.Close();

    // ───────────────────────── player events ─────────────────────────

    private void OnTrackStarted(TrackItem item)
    {
        if (Find(NowKey) is null)
            OpenNowPlaying();
    }

    private void OnStopped()
    {
        if (Find(NowKey) is { } tab)
            RemoveTab(tab);
        // the song tab following the playing track has nothing to show.
        if (Find(SongKey) is { Content: SongPage { FollowsPlayer: true } } song)
            RemoveTab(song);
        SyncBar();
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Player.Position) or nameof(Player.Duration):
                SyncProgress();
                break;
            case nameof(Player.PlaylistName):
                if (Find(NowKey) is { } tab)
                    tab.Header = NowHeader();
                break;
            case nameof(Player.MasterVolume):
                SyncVolume();
                break;
            default:
                SyncBar();
                break;
        }
    }

    // ───────────────────────── bottom bar ─────────────────────────

    private void SyncBar()
    {
        var cur = _p.Current?.Track;
        BarTitle.Text = cur?.Title ?? L.T("bar.nothing");
        BarArtists.Text = cur?.ArtistsLine ?? "";
        NowButton.IsEnabled = cur is not null;
        foreach (var b in new Control[] { PrevButton, BackButton, PlayButton, ForwardButton, NextButton, StopButton, SeekSlider })
            b.IsEnabled = _p.IsActive;
        PlayGlyph.Glyph = _p.IsPaused ? "" : "";
        ShuffleButton.IsChecked = _p.Shuffle;
        RepeatGlyph.Glyph = _p.Repeat == RepeatMode.One ? "" : "";
        RepeatGlyph.Foreground = _p.Repeat == RepeatMode.Off ? Ui.Muted : Ui.Accent;
        var repeat = L.T($"bar.repeat_{_p.Repeat.ToString().ToLowerInvariant()}");
        ToolTipService.SetToolTip(RepeatButton, repeat);
        AutomationProperties.SetName(RepeatButton, repeat);
        SyncProgress();
        SyncVolume();
        _ = SyncCoverAsync(cur);
    }

    private async Task SyncCoverAsync(Track? t)
    {
        var key = t is null ? null : $"{t.Key}|{t.CoverArt}";
        if (key == _coverKey)
            return;
        _coverKey = key;
        BarCover.Source = null;
        if (t is null)
            return;
        var image = await Covers.ImageAsync(t, 96);
        if (_coverKey == key)
            BarCover.Source = image;
    }

    private void SyncProgress()
    {
        DurText.Text = Format.Duration(_p.Duration);
        if (_seeking)
            return;
        _syncing = true;
        SeekSlider.Maximum = Math.Max(1, _p.Duration);
        SeekSlider.Value = Math.Min(_p.Position, SeekSlider.Maximum);
        _syncing = false;
        PosText.Text = Format.Duration(_p.Position);
    }

    private void SyncVolume()
    {
        var pct = Math.Round(_p.MasterVolume * 100);
        _syncing = true;
        VolumeSlider.Value = pct;
        _syncing = false;
        VolumeText.Text = $"{pct}%";
        VolumeGlyph.Glyph = pct switch { 0 => "", < 34 => "", < 67 => "", _ => "" };
    }

    private void SeekSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncing)
            return;
        PosText.Text = Format.Duration((ulong)e.NewValue);
        // keyboard changes seek right away, a pointer drag — on release.
        if (!_seeking)
            _p.SeekTo((ulong)e.NewValue);
    }

    private void EndSeek()
    {
        if (!_seeking)
            return;
        _seeking = false;
        _p.SeekTo((ulong)SeekSlider.Value);
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncing)
            return;
        _p.RequestMasterVolume((float)(e.NewValue / 100));
        VolumeText.Text = $"{Math.Round(e.NewValue)}%";
    }

    private void VolumeSlider_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(VolumeSlider).Properties.MouseWheelDelta;
        VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + Math.Sign(delta) * 5, 0, 100);
        e.Handled = true;
    }

    private void NowButton_Click(object sender, RoutedEventArgs e) => OpenSong();
    private void Play_Click(object sender, RoutedEventArgs e) => _p.TogglePause();
    private void Prev_Click(object sender, RoutedEventArgs e) => _p.Prev();
    private void Next_Click(object sender, RoutedEventArgs e) => _p.Next();
    private void Back_Click(object sender, RoutedEventArgs e) => _p.SeekBy(-AppSettings.Current.SeekStep);
    private void Forward_Click(object sender, RoutedEventArgs e) => _p.SeekBy(AppSettings.Current.SeekStep);
    private void Stop_Click(object sender, RoutedEventArgs e) => _p.Stop();
    private void Shuffle_Click(object sender, RoutedEventArgs e) => _p.Shuffle = ShuffleButton.IsChecked == true;

    private void Repeat_Click(object sender, RoutedEventArgs e) =>
        _p.Repeat = _p.Repeat switch
        {
            RepeatMode.Off => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            _ => RepeatMode.Off,
        };

    // ───────────────────────── keyboard ─────────────────────────

    private void AddAccelerators()
    {
        void Add(VirtualKey key, VirtualKeyModifiers mods, Action action)
        {
            var acc = new KeyboardAccelerator { Key = key, Modifiers = mods };
            acc.Invoked += (_, args) =>
            {
                action();
                args.Handled = true;
            };
            KeyboardAccelerators.Add(acc);
        }

        const VirtualKeyModifiers ctrl = VirtualKeyModifiers.Control;
        const VirtualKeyModifiers ctrlShift = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift;
        Add(VirtualKey.Space, ctrl, _p.TogglePause);
        Add(VirtualKey.Right, ctrl, _p.Next);
        Add(VirtualKey.Left, ctrl, _p.Prev);
        Add(VirtualKey.Right, ctrlShift, () => _p.SeekBy(AppSettings.Current.SeekStep));
        Add(VirtualKey.Left, ctrlShift, () => _p.SeekBy(-AppSettings.Current.SeekStep));
        Add(VirtualKey.Up, ctrl, () => _p.RequestMasterVolume(_p.MasterVolume + 0.05f));
        Add(VirtualKey.Down, ctrl, () => _p.RequestMasterVolume(_p.MasterVolume - 0.05f));
        Add(VirtualKey.W, ctrl, () =>
        {
            if (Tabs.SelectedItem is TabViewItem tab)
                CloseRequested(tab);
        });
        Add(VirtualKey.L, ctrl, OpenLog);
        Add(VirtualKey.I, ctrl, OpenImport);
        Add(VirtualKey.E, ctrl, () => OpenExport());
        Add(VirtualKey.N, ctrl, () => OpenPlaylistEditor(null));
    }
}
