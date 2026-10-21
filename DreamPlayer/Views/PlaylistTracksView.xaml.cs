// The tracks of one playlist (set by the host: PLAYLISTS or a playlist tab)
// with search and sorting. Playing starts the playlist in the shown sort order
// (search filters only the view) from the double-clicked track; `Starting`
// lets a playlist tab turn into the playing one first.

using DreamPlayer.Core;
using DreamPlayer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace DreamPlayer.Views;

public sealed partial class PlaylistTracksView : UserControl
{
    private readonly Player _p = Player.Instance;
    private PlaylistView? _playlist;

    public PlaylistTracksView()
    {
        InitializeComponent();
        foreach (var key in TrackFilter.Keys)
            SortBox.Items.Add(new ComboBoxItem { Content = TrackFilter.Label(key), Tag = key });
        SortBox.SelectedIndex = 0;
        Show();
    }

    /// shows a playlist (`null` — nothing selected). A method, not a
    /// property: XAML type info can't construct a PlaylistView.
    public void SetPlaylist(PlaylistView? pl)
    {
        _playlist = pl;
        Show();
    }

    /// the view is a playlist tab itself: no "open in a tab" button.
    public bool InTab
    {
        get => OpenTabButton.Visibility != Visibility.Visible;
        set => OpenTabButton.Visibility = Ui.Hidden(value);
    }

    /// raised right before the playlist starts playing from this view.
    public event Action? Starting;

    private SortKey Sort => SortBox.SelectedItem is ComboBoxItem { Tag: SortKey k } ? k : SortKey.Order;

    /// all tracks of the playlist in the chosen order (no search filter).
    private List<Track> Ordered(PlaylistView pl) => TrackFilter.Sort(pl.Tracks, Sort, SortDesc.IsChecked == true).ToList();

    private void Show()
    {
        var pl = _playlist;
        HeaderCover.Playlist = pl;
        HeaderCover.Visibility = Ui.Visible(pl is not null);
        PlaylistTitle.Text = pl?.DisplayName ?? "";
        PlaylistInfo.Text = pl is null
            ? ""
            : $"{pl.CountText} · {pl.TotalDuration}" + (pl.UpdatedAt is { } u ? $" · {L.F("playlists.updated", Format.UnixDate(u))}" : "");
        var editable = pl is { IsPool: false };
        EditButton.IsEnabled = editable;
        DeleteButton.IsEnabled = editable;
        ExportButton.IsEnabled = editable;
        OpenTabButton.IsEnabled = pl is not null;
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

    public void Play(PlaylistView pl, Track? from = null)
    {
        var order = Ordered(pl);
        if (order.Count == 0)
            return;
        var start = from is null ? 0 : Math.Max(0, order.IndexOf(from));
        Starting?.Invoke();
        _p.Start(pl, order, start);
    }

    public void ShufflePlay(PlaylistView pl)
    {
        var order = Ordered(pl);
        if (order.Count == 0)
            return;
        _p.Shuffle = true;
        Starting?.Invoke();
        _p.Start(pl, order, Random.Shared.Next(order.Count));
    }

    public static async void Delete(PlaylistView pl, XamlRoot root)
    {
        if (pl.IsPool)
            return;
        if (!await Ui.ConfirmAsync(root, L.T("confirm.delete_playlist_title"),
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

    private static void RemoveFromPlaylist(PlaylistView pl, IReadOnlyList<Track> tracks)
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

    private void TrackSearch_TextChanged(object sender, TextChangedEventArgs e) => Show();
    private void Sort_Changed(object sender, SelectionChangedEventArgs e) => Show();
    private void SortDesc_Click(object sender, RoutedEventArgs e) => Show();

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_playlist is { } pl)
            Play(pl);
    }

    private void ShufflePlay_Click(object sender, RoutedEventArgs e)
    {
        if (_playlist is { } pl)
            ShufflePlay(pl);
    }

    private void OpenTab_Click(object sender, RoutedEventArgs e)
    {
        if (_playlist is { } pl)
            ShellView.Current?.OpenPlaylist(pl);
    }

    private void Edit_Click(object sender, RoutedEventArgs e) => ShellView.Current?.OpenPlaylistEditor(_playlist);

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_playlist is { IsPool: false } pl)
            ShellView.Current?.OpenExport(ExportMode.Playlists, [pl.Name]);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_playlist is { } pl)
            Delete(pl, XamlRoot);
    }

    private void TrackList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_playlist is { } pl && Ui.TappedItem<Track>(e) is { } t)
            Play(pl, t);
    }

    private void TrackList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && _playlist is { } pl && TrackList.SelectedItem is Track t)
        {
            Play(pl, t);
            e.Handled = true;
        }
    }

    private void TrackList_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (_playlist is not { } pl || Ui.ContextItem<Track>(e) is not { } t)
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
