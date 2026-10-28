// ORPHANS tab (the TUI ORPHANS tab, `:orphans`): songs that are in no
// playlist (the core finds them). The filter switches between the ones the
// collector lists (not marked), the kept ones (marked "ignore") and all;
// "keep" sets / clears the mark; songs can be deleted from the device (file +
// library row). Re-read on every library change.

using DreamPlayer.Core;
using DreamPlayer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace DreamPlayer.Views;

public sealed partial class OrphansPage : UserControl, ITabPage
{
    private static readonly OrphanFilter[] Filters = [OrphanFilter.NotIgnored, OrphanFilter.Ignored, OrphanFilter.All];

    /// `null` — the core failed to answer.
    private List<Track>? _tracks;
    private List<Track> _shown = [];

    public OrphansPage()
    {
        InitializeComponent();
        foreach (var f in Filters)
            FilterBox.Items.Add(L.T($"orphans.filter_{f}"));
        FilterBox.SelectedIndex = 0;
        Library.Instance.Changed += Reload;
    }

    public void OnClosed() => Library.Instance.Changed -= Reload;

    private OrphanFilter Filter => Filters[Math.Max(0, FilterBox.SelectedIndex)];

    private void Reload()
    {
        _tracks = CoreApi.Initialized ? CoreApi.Library.Orphans(Filter) : null;
        Show();
    }

    private void Show()
    {
        var query = Search.Text;
        _shown = (_tracks ?? [])
            .Where(t => TrackFilter.Matches(t, query) || (t.Path ?? "").Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        TrackList.ItemsSource = _shown;
        Summary.Text = _tracks is null
            ? L.T("orphans.failed")
            : L.F("orphans.summary", _tracks.Count, Format.Size(_tracks.Sum(FileSize)));
        EmptyText.Text = _tracks switch
        {
            null => L.T("orphans.failed"),
            [] when Filter == OrphanFilter.Ignored => L.T("orphans.none_kept"),
            [] => L.T("orphans.none"),
            _ when _shown.Count == 0 => L.T("common.nothing_found"),
            _ => "",
        };
        EmptyText.Visibility = Ui.VisibleText(EmptyText.Text);
        DeleteAllButton.IsEnabled = _shown.Count > 0;
        SyncButtons();
    }

    private static long FileSize(Track t)
    {
        try
        {
            return t.Path is { } p && File.Exists(p) ? new FileInfo(p).Length : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private List<Track> Selected() => TrackList.SelectedItems.OfType<Track>().ToList();

    /// the keep button follows the selection: "keep" unless every selected
    /// song is kept already.
    private void SyncButtons()
    {
        var sel = Selected();
        var unkeep = sel.Count > 0 && sel.All(t => t.IgnoreOrphan);
        KeepText.Text = L.T(unkeep ? "orphans.unkeep" : "orphans.keep");
        KeepGlyph.Glyph = unkeep ? "" : "";
        ToolTipService.SetToolTip(KeepButton, L.T(unkeep ? "orphans.unkeep_hint" : "orphans.keep_hint"));
        KeepButton.IsEnabled = sel.Count > 0;
        DeleteButton.IsEnabled = sel.Count > 0;
    }

    private void SetKeep(IReadOnlyList<Track> tracks, bool keep)
    {
        var ids = tracks.Select(t => t.Id).OfType<long>().ToList();
        if (ids.Count == 0)
            return;
        var failed = ids.Count(id => CoreApi.Library.SetIgnoreOrphan(id, keep) != IdxStatus.Ok);
        ShellView.Current?.ReloadLibrary(); // -> Reload()
        if (failed > 0)
            ShellView.Current?.Notify(L.F("msg.keep_failed", failed), LogLevel.Error);
        else if (ids.Count == 1)
            ShellView.Current?.Notify(L.F(keep ? "msg.kept_one" : "msg.unkept_one", tracks[0].Title), LogLevel.Ok);
        else
            ShellView.Current?.Notify(L.F(keep ? "msg.kept_many" : "msg.unkept_many", ids.Count), LogLevel.Ok);
    }

    private void ToggleKeep(IReadOnlyList<Track> tracks)
    {
        if (tracks.Count > 0)
            SetKeep(tracks, !tracks.All(t => t.IgnoreOrphan));
    }

    private async void Delete(IReadOnlyList<Track> tracks)
    {
        if (ShellView.Current is { } shell)
            await shell.DeleteFromDeviceAsync(tracks);
    }

    // ───────────────────────── events ─────────────────────────

    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => Reload();
    private void Search_TextChanged(object sender, TextChangedEventArgs e) => Show();
    private void Reload_Click(object sender, RoutedEventArgs e) => Reload();
    private void TrackList_SelectionChanged(object sender, SelectionChangedEventArgs e) => SyncButtons();
    private void Keep_Click(object sender, RoutedEventArgs e) => ToggleKeep(Selected());
    private void Delete_Click(object sender, RoutedEventArgs e) => Delete(Selected());
    private void DeleteAll_Click(object sender, RoutedEventArgs e) => Delete(_shown.ToList());

    private void TrackList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (Ui.TappedItem<Track>(e) is { } t)
            ShellView.Current?.OpenSong(t);
    }

    private void TrackList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Delete && Selected() is { Count: > 0 } sel)
        {
            Delete(sel);
            e.Handled = true;
        }
    }

    private void TrackList_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (Ui.ContextItem<Track>(e) is not { } t)
            return;
        if (!TrackList.SelectedItems.Contains(t))
            TrackList.SelectedItem = t;
        var selected = Selected();
        var many = selected.Count > 1;
        var unkeep = selected.All(x => x.IgnoreOrphan);
        Ui.ShowMenu(Ui.Menu(
            new(L.T("track.open_song"), "", () => ShellView.Current?.OpenSong(t)),
            new(L.T("track.edit_meta"), "", () => ShellView.Current?.OpenTrackEditor(t), !many && t.Id is not null),
            new(L.T("track.show_in_folder"), "", () => Ui.ShowInFolder(t.Path), !many && t.Path is not null),
            ShellView.Current?.AddToPlaylistItem(selected),
            null,
            new(L.T(unkeep ? "orphans.unkeep" : "orphans.keep"),unkeep ? "" : "", () => SetKeep(selected, !unkeep)),
            null,
            new(many ? L.F("track.delete_from_device_n", selected.Count) : L.T("track.delete_from_device"),
                "", () => Delete(selected), Destructive: true)), TrackList, e);
    }
}
