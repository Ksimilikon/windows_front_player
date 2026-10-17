// SONG tab: one track in full. Opened from the bottom bar it follows the
// playing track; opened from a context menu it shows that track. The vertical
// slider is the per-track volume (applied at once when the track plays, saved
// to the db).

using System.ComponentModel;
using DreamPlayer.Core;
using DreamPlayer.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Shapes;

namespace DreamPlayer.Views;

public sealed partial class SongPage : UserControl, ITabPage
{
    private readonly Player _p = Player.Instance;
    private Track? _track;
    private bool _syncing;

    /// `track` null — follow the playing track.
    public SongPage(Track? track)
    {
        InitializeComponent();
        FollowsPlayer = track is null;
        _p.PropertyChanged += OnPlayerChanged;
        Library.Instance.Changed += OnLibraryChanged;
        Show(track ?? _p.Current?.Track);
    }

    public bool FollowsPlayer { get; }

    public void OnClosed()
    {
        _p.PropertyChanged -= OnPlayerChanged;
        Library.Instance.Changed -= OnLibraryChanged;
    }

    private bool IsPlaying => _track is not null && _p.Current?.Track.Key == _track.Key;

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Player.Current) when FollowsPlayer && _p.Current is { } cur:
                Show(cur.Track);
                break;
            case nameof(Player.Current):
                PlayButton.IsEnabled = !IsPlaying;
                break;
            case nameof(Player.TrackVolume) when IsPlaying:
                SyncVolume(_p.TrackVolume);
                break;
        }
    }

    private void OnLibraryChanged()
    {
        // metadata edited elsewhere: show the fresh copy.
        if (_track?.Id is { } id && Library.Instance.TrackById(id) is { } fresh)
            Show(fresh);
    }

    private void Show(Track? t)
    {
        _track = t;
        if (t is null)
            return;
        TitleText.Text = t.Title;
        ArtistsText.Text = t.ArtistsLine;
        InvalidBar.Visibility = Ui.Visible(t.Invalid);
        PlayButton.IsEnabled = !IsPlaying;
        EditButton.IsEnabled = t.Id is not null;
        FolderButton.IsEnabled = t.Path is not null;
        SyncVolume(IsPlaying ? _p.TrackVolume : t.Volume);
        VolumeSlider.IsEnabled = t.Id is not null || IsPlaying;
        FillMeta(t);
        _ = LoadCoverAsync(t);
    }

    private async Task LoadCoverAsync(Track t)
    {
        Cover.Source = null;
        NoCover.Visibility = Visibility.Visible;
        var image = await Covers.ImageAsync(t, 680);
        if (_track?.Key != t.Key)
            return;
        Cover.Source = image;
        NoCover.Visibility = Ui.Hidden(image is not null);
    }

    private void FillMeta(Track t)
    {
        MetaGrid.Children.Clear();
        MetaGrid.RowDefinitions.Clear();
        var rows = new (string Key, UIElement Value)[]
        {
            ("meta.album", Text(Ui.Dash(t.Album))),
            ("meta.genres", Text(Ui.Dash(t.GenresLine))),
            ("meta.duration", Text(t.DurationText)),
            ("meta.bitrate", Text(t.Bitrate > 0 ? $"{t.Bitrate} kbps" : "—")),
            ("meta.samplerate", Text(t.SampleRate > 0 ? $"{t.SampleRate} Hz" : "—")),
            ("meta.listens", Text(t.ListenCount.ToString())),
            ("meta.label", Text(Ui.Dash(t.Label))),
            ("meta.color", ColorValue(t.Color)),
            ("meta.volume", Text($"{Math.Round(t.Volume * 100)}%")),
            ("meta.path", Text(Ui.Dash(t.Path))),
            ("meta.cover", Text(t.CoverArt ?? L.T("meta.cover_embedded"))),
            ("meta.id", Text(t.Id?.ToString() ?? L.T("meta.not_in_library"))),
        };
        for (var i = 0; i < rows.Length; i++)
        {
            MetaGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var key = new TextBlock { Text = L.T(rows[i].Key), Foreground = Ui.Muted };
            Grid.SetRow(key, i);
            Grid.SetRow((FrameworkElement)rows[i].Value, i);
            Grid.SetColumn((FrameworkElement)rows[i].Value, 1);
            MetaGrid.Children.Add(key);
            MetaGrid.Children.Add(rows[i].Value);
        }
    }

    private static TextBlock Text(string s) => new()
    {
        Text = s,
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
        FontWeight = FontWeights.Normal,
    };

    private static UIElement ColorValue(string? hex)
    {
        if (Ui.ParseColor(hex) is null)
            return Text("—");
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(new Rectangle { Width = 16, Height = 16, RadiusX = 3, RadiusY = 3, Fill = Ui.ColorBrush(hex) });
        panel.Children.Add(Text(hex!));
        return panel;
    }

    // ───────────────────────── volume ─────────────────────────

    private void SyncVolume(float v)
    {
        _syncing = true;
        VolumeSlider.Value = Math.Round(v * 100);
        _syncing = false;
        VolumeText.Text = $"{Math.Round(v * 100)}%";
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        VolumeText.Text = $"{Math.Round(e.NewValue)}%";
        if (_syncing || _track is null)
            return;
        _p.RequestTrackVolume(_track, (float)(e.NewValue / 100));
    }

    private void VolumeSlider_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(VolumeSlider).Properties.MouseWheelDelta;
        VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + Math.Sign(delta) * 5, 0, 200);
        e.Handled = true;
    }

    private void ResetVolume_Click(object sender, RoutedEventArgs e) => VolumeSlider.Value = 100;

    // ───────────────────────── buttons ─────────────────────────

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_track is null)
            return;
        // in the queue — jump to it; otherwise play it from the pool.
        if (_p.Queue.FirstOrDefault(i => i.Track.Key == _track.Key) is { } item)
        {
            _p.PlayItem(item);
            return;
        }
        var pool = Library.Instance.Find("", pool: true);
        if (pool is not null && pool.Tracks.ToList().FindIndex(x => x.Key == _track.Key) is var i and >= 0)
            _p.Start(pool, pool.Tracks, i);
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (_track is not null)
            ShellView.Current?.OpenTrackEditor(_track);
    }

    private void Folder_Click(object sender, RoutedEventArgs e) => Ui.ShowInFolder(_track?.Path);
}
