// Playlist editor (create / edit), the TUI NEW_PLAYLIST / MOD_<name> tab:
// name + cover, the assembled list (drag-n-drop order, Delete removes), the
// pool with search, sorting and "hide added". Saving writes the playlist by
// name; a rename saves under the new name and removes the old one.

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DreamPlayer.Core;
using DreamPlayer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace DreamPlayer.Views;

public sealed partial class PoolRow : ObservableObject
{
    public PoolRow(Track track) => Track = track;

    public Track Track { get; }

    [ObservableProperty]
    public partial bool IsAdded { get; set; }

    public override string ToString() => Track.ToString();
}

public sealed partial class PlaylistEditorPage : UserControl, ITabPage
{
    /// the edited playlist (`null` — a new one).
    private readonly PlaylistView? _original;
    private readonly ObservableCollection<Track> _assembled = [];
    private List<PoolRow> _pool = [];

    public PlaylistEditorPage(PlaylistView? pl)
    {
        InitializeComponent();
        _original = pl;
        NameBox.Text = pl?.Name ?? "";
        CoverBox.Text = pl?.CoverArt ?? "";
        foreach (var t in pl?.Tracks ?? [])
            _assembled.Add(t);
        AssembledList.ItemsSource = _assembled;
        _assembled.CollectionChanged += (_, _) => SyncAssembled();

        foreach (var key in TrackFilter.Keys.Where(k => k != SortKey.Order))
            SortBox.Items.Add(new ComboBoxItem { Content = TrackFilter.Label(key), Tag = key });
        SortBox.SelectedIndex = 0;

        Library.Instance.Changed += OnLibraryChanged;
        RebuildPool();
        SyncAssembled();
        Loaded += (_, _) => NameBox.Focus(FocusState.Programmatic);
    }

    public void OnClosed() => Library.Instance.Changed -= OnLibraryChanged;

    private SortKey Sort => SortBox.SelectedItem is ComboBoxItem { Tag: SortKey k } ? k : SortKey.Title;

    private bool Filtering => !string.IsNullOrWhiteSpace(AssembledSearch.Text);

    // ───────────────────────── views ─────────────────────────

    private void OnLibraryChanged() => RebuildPool();

    private void RebuildPool()
    {
        _pool = Library.Instance.Pool.Select(t => new PoolRow(t)).ToList();
        SyncAdded();
        ShowPool();
    }

    private void SyncAdded()
    {
        var added = _assembled.Select(t => t.Key).ToHashSet();
        foreach (var row in _pool)
            row.IsAdded = added.Contains(row.Track.Key);
    }

    private void ShowPool()
    {
        var hide = HideAdded.IsChecked == true;
        var byKey = _pool.ToDictionary(r => r.Track.Key);
        var visible = TrackFilter.Sort(_pool.Select(r => r.Track), Sort, SortDesc.IsChecked == true)
            .Where(t => TrackFilter.Matches(t, PoolSearch.Text))
            .Select(t => byKey[t.Key])
            .Where(r => !(hide && r.IsAdded))
            .ToList();
        PoolList.ItemsSource = visible;
        PoolTitle.Text = L.F("editor.pool_title", visible.Count, _pool.Count);
    }

    private void SyncAssembled()
    {
        var total = (ulong)_assembled.Sum(t => (long)t.DurationS);
        AssembledTitle.Text = L.F("editor.assembled_title", _assembled.Count, Format.Duration(total));
        SyncAdded();
        if (HideAdded.IsChecked == true)
            ShowPool();
        if (Filtering)
            ShowAssembled();
    }

    private void ShowAssembled()
    {
        if (!Filtering)
        {
            AssembledList.ItemsSource = _assembled;
            AssembledList.CanReorderItems = true;
            AssembledList.CanDragItems = true;
            return;
        }
        AssembledList.CanReorderItems = false;
        AssembledList.CanDragItems = false;
        AssembledList.ItemsSource = _assembled.Where(t => TrackFilter.Matches(t, AssembledSearch.Text)).ToList();
    }

    // ───────────────────────── edit ─────────────────────────

    private void Add(IEnumerable<Track> tracks)
    {
        var have = _assembled.Select(t => t.Key).ToHashSet();
        foreach (var t in tracks.Where(t => t.Id is not null && have.Add(t.Key)))
            _assembled.Add(t);
    }

    private void RemoveSelected()
    {
        foreach (var t in AssembledList.SelectedItems.OfType<Track>().ToList())
            _assembled.Remove(t);
    }

    private async void Save()
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            ShellView.Current?.Notify(L.T("msg.name_required"), LogLevel.Warn);
            NameBox.Focus(FocusState.Programmatic);
            return;
        }
        var renamed = _original is not null && _original.Name != name;
        if ((_original is null || renamed) && Library.Instance.Exists(name)
            && !await Ui.ConfirmAsync(XamlRoot, L.T("confirm.overwrite_title"), L.F("confirm.overwrite_text", name),
                L.T("common.overwrite"), destructive: true))
            return;

        var cover = string.IsNullOrWhiteSpace(CoverBox.Text) ? null : CoverBox.Text.Trim();
        if (cover is not null && !File.Exists(cover))
            ShellView.Current?.Notify(L.F("msg.cover_missing", cover), LogLevel.Warn);
        var ids = _assembled.Select(t => t.Id).OfType<long>().ToList();
        var st = CoreApi.Library.SavePlaylist(name, ids, cover);
        if (st != IdxStatus.Ok)
        {
            ShellView.Current?.Notify(L.F("msg.playlist_save_failed", CoreApi.Describe(st)), LogLevel.Error);
            return;
        }
        if (renamed && CoreApi.Library.RemovePlaylist(_original!.Name) != IdxStatus.Ok)
            ShellView.Current?.Notify(L.F("msg.old_playlist_kept", _original.Name), LogLevel.Warn);
        else
            ShellView.Current?.Notify(L.F("msg.playlist_saved", name), LogLevel.Ok);
        ShellView.Current?.ReloadLibrary();
        ShellView.Current?.CloseTab(this);
    }

    // ───────────────────────── events ─────────────────────────

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e) =>
        SaveButton.IsEnabled = NameBox.Text.Trim().Length > 0;

    private async void BrowseCover_Click(object sender, RoutedEventArgs e)
    {
        if (await Ui.PickFileAsync(Ui.ImageTypes) is { } path)
            CoverBox.Text = path;
    }

    private void ClearCover_Click(object sender, RoutedEventArgs e) => CoverBox.Text = "";
    private void Cancel_Click(object sender, RoutedEventArgs e) => ShellView.Current?.CloseTab(this);
    private void Save_Click(object sender, RoutedEventArgs e) => Save();

    private void RemoveSelected_Click(object sender, RoutedEventArgs e) => RemoveSelected();

    /// sorts the assembled list once by the pool sort settings.
    private void SortAssembled_Click(object sender, RoutedEventArgs e)
    {
        var sorted = TrackFilter.Sort(_assembled.ToList(), Sort, SortDesc.IsChecked == true).ToList();
        _assembled.Clear();
        foreach (var t in sorted)
            _assembled.Add(t);
    }

    private void AssembledSearch_TextChanged(object sender, TextChangedEventArgs e) => ShowAssembled();

    private void AssembledList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (Ui.TappedItem<Track>(e) is { } t)
            _assembled.Remove(t);
    }

    private void AssembledList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Delete)
        {
            RemoveSelected();
            e.Handled = true;
        }
    }

    private void AddSelected_Click(object sender, RoutedEventArgs e) =>
        Add(PoolList.SelectedItems.OfType<PoolRow>().Select(r => r.Track).ToList());

    private void PoolSearch_TextChanged(object sender, TextChangedEventArgs e) => ShowPool();
    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowPool();
    private void PoolOption_Click(object sender, RoutedEventArgs e) => ShowPool();

    private void PoolList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (Ui.TappedItem<PoolRow>(e) is { } row)
            Add([row.Track]);
    }

    private void PoolList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            Add(PoolList.SelectedItems.OfType<PoolRow>().Select(r => r.Track).ToList());
            e.Handled = true;
        }
    }
}
