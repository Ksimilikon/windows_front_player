// A playlist tab (not playing): the tracks of one playlist, followed through
// library reloads by name; the tab closes when the playlist is gone. Playing
// it hands the tab over to the playing playlist (ShellView.BecomeNowPlaying),
// so a playlist tab and the playing tab of the same playlist can both exist
// only when the playing one was started elsewhere.

using DreamPlayer.Services;
using Microsoft.UI.Xaml.Controls;

namespace DreamPlayer.Views;

public sealed partial class PlaylistPage : UserControl, ITabPage
{
    private readonly Library _lib = Library.Instance;
    private readonly bool _pool;
    private readonly string _name;

    public PlaylistPage(PlaylistView pl)
    {
        InitializeComponent();
        _pool = pl.IsPool;
        _name = pl.Name;
        Tracks.SetPlaylist(pl);
        Tracks.Starting += () => ShellView.Current?.BecomeNowPlaying(this);
        _lib.Changed += OnLibraryChanged;
    }

    /// the tab key of a playlist.
    public static string KeyOf(PlaylistView pl) => pl.IsPool ? "pl:*" : $"pl:{pl.Name}";

    public void OnClosed() => _lib.Changed -= OnLibraryChanged;

    private void OnLibraryChanged()
    {
        if (_lib.Find(_name, _pool) is { } pl)
            Tracks.SetPlaylist(pl);
        else
            DispatcherQueue.TryEnqueue(() => ShellView.Current?.CloseTab(this));
    }
}
