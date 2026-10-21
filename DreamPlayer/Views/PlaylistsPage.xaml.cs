// PLAYLISTS tab: playlist list (pool first) + tracks of the selected one
// (PlaylistTracksView). A playlist also opens in a tab of its own.

using CommunityToolkit.Mvvm.ComponentModel;
using DreamPlayer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

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
        _lib.Changed += Refresh;
        _p.PropertyChanged += OnPlayerChanged;
        Refresh();
    }

    public void OnClosed()
    {
        _lib.Changed -= Refresh;
        _p.PropertyChanged -= OnPlayerChanged;
    }

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
        Tracks.SetPlaylist(_selected);
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

    // ───────────────────────── events ─────────────────────────

    private void New_Click(object sender, RoutedEventArgs e) => ShellView.Current?.OpenPlaylistEditor(null);

    private void PlaylistSearch_TextChanged(object sender, TextChangedEventArgs e) => Refresh();

    private void PlaylistList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing)
            return;
        _selected = (PlaylistList.SelectedItem as PlaylistRow)?.View;
        Tracks.SetPlaylist(_selected);
    }

    private void PlaylistList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (Ui.TappedItem<PlaylistRow>(e) is { } row)
            Tracks.Play(row.View);
    }

    private void PlaylistList_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (Ui.ContextItem<PlaylistRow>(e) is not { } row)
            return;
        PlaylistList.SelectedItem = row;
        var pl = row.View;
        Ui.ShowMenu(Ui.Menu(
            new(L.T("playlists.play"), "", () => Tracks.Play(pl), pl.Count > 0),
            new(L.T("playlists.shuffle_play"), "", () => Tracks.ShufflePlay(pl), pl.Count > 0),
            new(L.T("playlists.open_tab"), "", () => ShellView.Current?.OpenPlaylist(pl)),
            null,
            new(L.T("playlists.edit"), "", () => ShellView.Current?.OpenPlaylistEditor(pl), !pl.IsPool),
            new(L.T("playlists.export"), "", () => ShellView.Current?.OpenExport(ExportMode.Playlists, [pl.Name]), !pl.IsPool),
            null,
            new(L.T("playlists.delete"), "", () => PlaylistTracksView.Delete(pl, XamlRoot), !pl.IsPool, Destructive: true)),
            PlaylistList, e);
    }
}
