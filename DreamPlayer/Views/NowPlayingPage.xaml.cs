// NOW PLAYING tab: the player queue. Drag-n-drop reorders it (temporary);
// while the order differs from the db one the header shows "save order" and
// "revert". While a search is active the list shows a filtered copy and
// reordering is off.

using System.ComponentModel;
using DreamPlayer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace DreamPlayer.Views;

public sealed partial class NowPlayingPage : UserControl, ITabPage
{
    private readonly Player _p = Player.Instance;

    public NowPlayingPage()
    {
        InitializeComponent();
        Queue.ItemsSource = _p.Queue;
        _p.PropertyChanged += OnPlayerChanged;
        _p.Queue.CollectionChanged += OnQueueChanged;
        Loaded += (_, _) => ScrollToCurrent();
        Sync();
    }

    public void OnClosed()
    {
        _p.PropertyChanged -= OnPlayerChanged;
        _p.Queue.CollectionChanged -= OnQueueChanged;
    }

    private bool Filtering => !string.IsNullOrWhiteSpace(Search.Text);

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Player.OrderDirty) or nameof(Player.PlaylistName) or nameof(Player.FromPool))
            Sync();
        if (e.PropertyName == nameof(Player.Current) && Filtering is false)
            ScrollToCurrent();
    }

    private void OnQueueChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        Sync();
        if (Filtering)
            ApplyFilter();
    }

    private void Sync()
    {
        TitleText.Text = _p.PlaylistName;
        var total = (ulong)_p.Queue.Sum(i => (long)i.Track.DurationS);
        InfoText.Text = $"{L.F("lib.tracks_count", _p.Queue.Count)} · {Core.Format.Duration(total)}"
            + (_p.FromPool ? $" · {L.T("now.pool_hint")}" : "");
        EditButton.IsEnabled = !_p.FromPool;
        // "All songs" has no order to save: only revert.
        RevertButton.Visibility = Ui.Visible(_p.OrderDirty);
        SaveOrderButton.Visibility = Ui.Visible(_p.OrderDirty && !_p.FromPool);
    }

    private void ApplyFilter()
    {
        if (!Filtering)
        {
            Queue.ItemsSource = _p.Queue;
            Queue.CanReorderItems = true;
            Queue.CanDragItems = true;
            return;
        }
        Queue.CanReorderItems = false;
        Queue.CanDragItems = false;
        Queue.ItemsSource = _p.Queue.Where(i => TrackFilter.Matches(i.Track, Search.Text)).ToList();
    }

    private void ScrollToCurrent()
    {
        if (_p.Current is { } cur)
            Queue.ScrollIntoView(cur, ScrollIntoViewAlignment.Default);
    }

    private List<TrackItem> Selected() => Queue.SelectedItems.OfType<TrackItem>().ToList();

    private async void RemoveFromPlaylist(TrackItem item)
    {
        if (!await Ui.ConfirmAsync(XamlRoot, L.T("confirm.remove_from_playlist_title"),
                L.F("confirm.remove_from_playlist_text", Ui.TrackLine(item.Track), _p.PlaylistName),
                L.T("common.remove"), destructive: true))
            return;
        if (_p.RemoveFromPlaylist(item))
            ShellView.Current?.Notify(L.F("msg.removed_from_playlist", 1, _p.PlaylistName), LogLevel.Ok);
    }

    // ───────────────────────── events ─────────────────────────

    private void Search_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();
    private void ScrollToCurrent_Click(object sender, RoutedEventArgs e) => ScrollToCurrent();
    private void Revert_Click(object sender, RoutedEventArgs e) => _p.RevertOrder();
    private void SaveOrder_Click(object sender, RoutedEventArgs e) => _p.SaveOrder();

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (!_p.FromPool)
            ShellView.Current?.OpenPlaylistEditor(Library.Instance.Find(_p.PlaylistName));
    }

    private void Queue_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (Ui.TappedItem<TrackItem>(e) is { } item)
            _p.PlayItem(item);
    }

    private void Queue_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter when Queue.SelectedItem is TrackItem item:
                _p.PlayItem(item);
                e.Handled = true;
                break;
            case VirtualKey.Delete:
                foreach (var item in Selected())
                    _p.RemoveFromQueue(item);
                e.Handled = true;
                break;
        }
    }

    private void Queue_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (Ui.ContextItem<TrackItem>(e) is not { } item)
            return;
        if (!Queue.SelectedItems.Contains(item))
            Queue.SelectedItem = item;
        var selected = Selected();
        var many = selected.Count > 1;
        var t = item.Track;
        var last = _p.Queue.Count - 1;
        Ui.ShowMenu(Ui.Menu(
            new(L.T("track.play"), "", () => _p.PlayItem(item)),
            new(L.T("track.play_next"), "", () => _p.MoveAfterCurrent(item), !many && item != _p.Current),
            new(L.T("track.open_song"), "", () => ShellView.Current?.OpenSong(t)),
            new(L.T("track.edit_meta"), "", () => ShellView.Current?.OpenTrackEditor(t), !many && t.Id is not null),
            new(L.T("track.show_in_folder"), "", () => Ui.ShowInFolder(t.Path), !many && t.Path is not null),
            null,
            new(L.T("track.move_top"), "", () => _p.Move(item, 0), !many && !Filtering),
            new(L.T("track.move_bottom"), "", () => _p.Move(item, last), !many && !Filtering),
            null,
            new(many ? L.F("track.remove_from_queue_n", selected.Count) : L.T("track.remove_from_queue"),
                "", () => selected.ForEach(_p.RemoveFromQueue)),
            new(L.T("track.remove_from_playlist"), "", () => RemoveFromPlaylist(item),
                !many && !_p.FromPool, Destructive: true)), Queue, e);
    }
}
