// PLAYLISTS tab: playlist list (pool first) + tracks of the selected one with
// search and sorting. Playing starts the playlist in the shown sort order
// (search filters only the view) from the double-clicked track.

using CommunityToolkit.Mvvm.ComponentModel;
using DreamPlayer.Core;
using DreamPlayer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace DreamPlayer.Views;

public sealed partial class PlaylistRow : ObservableObject
{
    public PlaylistRow(PlaylistView view) => View = view;

    public PlaylistView View { get; }

    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    public override string ToString() => $"{View.DisplayName}, {View.CountText}";
}

public sealed partial class PlaylistsPage : UserControl, ITabPage
{
    private readonly Player _p = Player.Instance;
    private readonly Library _lib = Library.Instance;
    private List<PlaylistRow> _rows = [];
    private PlaylistView? _selected;
    private bool _refreshing;

    public PlaylistsPage()
    {
        InitializeComponent();
        foreach (var key in TrackFilter.Keys)
            SortBox.Items.Add(new ComboBoxItem { Content = TrackFilter.Label(key), Tag = key });
        SortBox.SelectedIndex = 0;

        _lib.Changed += Refresh;
        _p.PropertyChanged += OnPlayerChanged;
        Refresh();
    }

    public void OnClosed()
    {
        _lib.Changed -= Refresh;
        _p.PropertyChanged -= OnPlayerChanged;
    }

    private SortKey Sort => SortBox.SelectedItem is ComboBoxItem { Tag: SortKey k } ? k : SortKey.Order;

    // ───────────────────────── data ─────────────────────────

    /// rebuilds the playlist list (keeping the selection by name).
    private void Refresh()
    {
        var keep = _selected;
        var query = PlaylistSearch.Text;
        _rows = _lib.Playlists
            .Where(p => string.IsNullOrWhiteSpace(query) || p.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .Select(p => new PlaylistRow(p) { IsPlaying = IsPlaying(p) })
            .ToList();
        _refreshing = true;
        PlaylistList.ItemsSource = _rows;
        var sel = _rows.FirstOrDefault(r => keep is not null && r.View.IsPool == keep.IsPool && r.View.Name == keep.Name)
            ?? _rows.FirstOrDefault();
        PlaylistList.SelectedItem = sel;
        _refreshing = false;
        _selected = sel?.View;
        ShowTracks();
    }

    private bool IsPlaying(PlaylistView p) =>
        _p.IsActive && _p.FromPool == p.IsPool && (p.IsPool || _p.PlaylistName == p.Name);

    private void OnPlayerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Player.IsActive) or nameof(Player.PlaylistName))
        {
            foreach (var row in _rows)
                row.IsPlaying = IsPlaying(row.View);
        }
    }

    /// all tracks of the playlist in the chosen order (no search filter).
    private List<Track> Ordered(PlaylistView pl) => TrackFilter.Sort(pl.Tracks, Sort, SortDesc.IsChecked == true).ToList();

    private void ShowTracks()
    {
        var pl = _selected;
        PlaylistTitle.Text = pl?.DisplayName ?? "";
        PlaylistInfo.Text = pl is null
            ? ""
            : $"{pl.CountText} · {pl.TotalDuration}" + (pl.UpdatedAt is { } u ? $" · {L.F("playlists.updated", Format.UnixDate(u))}" : "");
        var editable = pl is { IsPool: false };
        EditButton.IsEnabled = editable;
        DeleteButton.IsEnabled = editable;
        ExportButton.IsEnabled = editable;
        PlayButton.IsEnabled = pl is { Count: > 0 };
        ShuffleButton.IsEnabled = pl is { Count: > 0 };

        var tracks = pl is null
            ? []
            : Ordered(pl).Where(t => TrackFilter.Matches(t, TrackSearch.Text)).ToList();
        TrackList.ItemsSource = tracks;
        EmptyText.Text = pl switch
        {
            null => L.T("playlists.none"),
            { Count: 0 } when pl.IsPool => L.T("playlists.pool_empty"),
            { Count: 0 } => L.T("playlists.empty"),
            _ when tracks.Count == 0 => L.T("common.nothing_found"),
            _ => "",
        };
        EmptyText.Visibility = Ui.VisibleText(EmptyText.Text);
    }

    // ───────────────────────── actions ─────────────────────────

    private void Play(PlaylistView pl, Track? from = null)
    {
        var order = Ordered(pl);
        var start = from is null ? 0 : Math.Max(0, order.IndexOf(from));
        _p.Start(pl, order, start);
    }

    private void ShufflePlay(PlaylistView pl)
    {
        _p.Shuffle = true;
        var order = Ordered(pl);
        if (order.Count > 0)
            _p.Start(pl, order, Random.Shared.Next(order.Count));
    }

    private async void Delete(PlaylistView pl)
    {
        if (pl.IsPool)
            return;
        if (!await Ui.ConfirmAsync(XamlRoot, L.T("confirm.delete_playlist_title"),
                L.F("confirm.delete_playlist_text", pl.Name), L.T("common.delete"), destructive: true))
            return;
        var st = CoreApi.Library.RemovePlaylist(pl.Name);
        if (st == IdxStatus.Ok)
        {
            ShellView.Current?.Notify(L.F("msg.playlist_deleted", pl.Name), LogLevel.Ok);
            ShellView.Current?.ReloadLibrary();
        }
        else
        {
            ShellView.Current?.Notify(L.F("msg.playlist_delete_failed", CoreApi.Describe(st)), LogLevel.Error);
        }
    }

    private async void RemoveFromLibrary(IReadOnlyList<Track> tracks)
    {
        var ids = tracks.Select(t => t.Id).OfType<long>().ToList();
        if (ids.Count == 0)
            return;
        var text = ids.Count == 1
            ? L.F("confirm.remove_track_text", Ui.TrackLine(tracks[0]))
            : L.F("confirm.remove_tracks_text", ids.Count);
        if (!await Ui.ConfirmAsync(XamlRoot, L.T("confirm.remove_track_title"), text, L.T("common.remove"), destructive: true))
            return;
        var failed = ids.Count(id => CoreApi.Library.Remove(id) != IdxStatus.Ok);
        ShellView.Current?.ReloadLibrary();
        ShellView.Current?.Notify(
            failed == 0 ? L.F("msg.tracks_removed", ids.Count) : L.F("msg.tracks_remove_failed", failed),
            failed == 0 ? LogLevel.Ok : LogLevel.Error);
    }

    private void RemoveFromPlaylist(PlaylistView pl, IReadOnlyList<Track> tracks)
    {
        var drop = tracks.Select(t => t.Key).ToHashSet();
        var ids = pl.Tracks.Where(t => !drop.Contains(t.Key)).Select(t => t.Id).OfType<long>().ToList();
        var st = CoreApi.Library.SavePlaylist(pl.Name, ids, pl.CoverArt);
        if (st == IdxStatus.Ok)
        {
            ShellView.Current?.Notify(L.F("msg.removed_from_playlist", tracks.Count, pl.Name), LogLevel.Ok);
            ShellView.Current?.ReloadLibrary();
        }
        else
        {
            ShellView.Current?.Notify(L.F("msg.playlist_save_failed", CoreApi.Describe(st)), LogLevel.Error);
        }
    }

    /// the orphan collector mark ("keep though in no playlist").
    private static void SetKeep(IReadOnlyList<Track> tracks, bool keep)
    {
        var ids = tracks.Select(t => t.Id).OfType<long>().ToList();
        var failed = ids.Count(id => CoreApi.Library.SetIgnoreOrphan(id, keep) != IdxStatus.Ok);
        ShellView.Current?.ReloadLibrary();
        if (failed > 0)
            ShellView.Current?.Notify(L.F("msg.keep_failed", failed), LogLevel.Error);
        else if (ids.Count == 1)
            ShellView.Current?.Notify(L.F(keep ? "msg.kept_one" : "msg.unkept_one", tracks[0].Title), LogLevel.Ok);
        else if (ids.Count > 1)
            ShellView.Current?.Notify(L.F(keep ? "msg.kept_many" : "msg.unkept_many", ids.Count), LogLevel.Ok);
    }

    private List<Track> SelectedTracks() => TrackList.SelectedItems.OfType<Track>().ToList();

    // ───────────────────────── events ─────────────────────────

    private void New_Click(object sender, RoutedEventArgs e) => ShellView.Current?.OpenPlaylistEditor(null);

    private void PlaylistSearch_TextChanged(object sender, TextChangedEventArgs e) => Refresh();

    private void PlaylistList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing)
            return;
        _selected = (PlaylistList.SelectedItem as PlaylistRow)?.View;
        ShowTracks();
    }

    private void PlaylistList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (Ui.TappedItem<PlaylistRow>(e) is { } row)
            Play(row.View);
    }

    private void PlaylistList_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (Ui.ContextItem<PlaylistRow>(e) is not { } row)
            return;
        PlaylistList.SelectedItem = row;
        var pl = row.View;
        Ui.ShowMenu(Ui.Menu(
            new(L.T("playlists.play"), "", () => Play(pl), pl.Count > 0),
            new(L.T("playlists.shuffle_play"), "", () => ShufflePlay(pl), pl.Count > 0),
            null,
            new(L.T("playlists.edit"), "", () => ShellView.Current?.OpenPlaylistEditor(pl), !pl.IsPool),
            new(L.T("playlists.export"), "", () => ShellView.Current?.OpenExport(ExportMode.Playlists, [pl.Name]), !pl.IsPool),
            null,
            new(L.T("playlists.delete"), "", () => Delete(pl), !pl.IsPool, Destructive: true)), PlaylistList, e);
    }

    private void TrackSearch_TextChanged(object sender, TextChangedEventArgs e) => ShowTracks();
    private void Sort_Changed(object sender, SelectionChangedEventArgs e) => ShowTracks();
    private void SortDesc_Click(object sender, RoutedEventArgs e) => ShowTracks();

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is { } pl)
            Play(pl);
    }

    private void ShufflePlay_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is { } pl)
            ShufflePlay(pl);
    }

    private void Edit_Click(object sender, RoutedEventArgs e) => ShellView.Current?.OpenPlaylistEditor(_selected);

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is { IsPool: false } pl)
            ShellView.Current?.OpenExport(ExportMode.Playlists, [pl.Name]);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is { } pl)
            Delete(pl);
    }

    private void TrackList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_selected is { } pl && Ui.TappedItem<Track>(e) is { } t)
            Play(pl, t);
    }

    private void TrackList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && _selected is { } pl && TrackList.SelectedItem is Track t)
        {
            Play(pl, t);
            e.Handled = true;
        }
    }

    private void TrackList_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (_selected is not { } pl || Ui.ContextItem<Track>(e) is not { } t)
            return;
        if (!TrackList.SelectedItems.Contains(t))
            TrackList.SelectedItem = t;
        var selected = SelectedTracks();
        var many = selected.Count > 1;
        var items = new List<Ui.Item?>
        {
            new(L.T("track.play_from_here"), "", () => Play(pl, t)),
            new(L.T("track.open_song"), "", () => ShellView.Current?.OpenSong(t)),
            new(L.T("track.edit_meta"), "", () => ShellView.Current?.OpenTrackEditor(t), !many && t.Id is not null),
            new(L.T("track.show_in_folder"), "", () => Ui.ShowInFolder(t.Path), !many && t.Path is not null),
            new(L.T("track.export"), "", () => ShellView.Current?.OpenExport(ExportMode.Tracks, null, selected.Select(x => x.Id).OfType<long>())),
            null,
            new(many ? L.F("track.remove_from_playlist_n", selected.Count) : L.T("track.remove_from_playlist"),
                "", () => RemoveFromPlaylist(pl, selected), !pl.IsPool, Destructive: true),
            new(many ? L.F("track.remove_from_library_n", selected.Count) : L.T("track.remove_from_library"),
                "", () => RemoveFromLibrary(selected), Destructive: true),
        };
        // the pool is the whole library: there a song can also go from the device.
        if (pl.IsPool)
        {
            var kept = selected.All(x => x.IgnoreOrphan);
            items.Add(null);
            items.Add(new(L.T(kept ? "orphans.unkeep" : "orphans.keep"), kept ? "" : "",
                () => SetKeep(selected, !kept), selected.Any(x => x.Id is not null)));
            items.Add(new(many ? L.F("track.delete_from_device_n", selected.Count) : L.T("track.delete_from_device"),
                "", () => _ = ShellView.Current?.DeleteFromDeviceAsync(selected), Destructive: true));
        }
        Ui.ShowMenu(Ui.Menu([.. items]), TrackList, e);
    }
}
