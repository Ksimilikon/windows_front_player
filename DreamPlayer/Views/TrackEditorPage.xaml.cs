// Track metadata editor (the TUI MOD_SONG_ tab). Only changed fields are sent
// to the core, each with its own `idx_set_track_*` call (visible in the log).

using DreamPlayer.Core;
using DreamPlayer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DreamPlayer.Views;

public sealed partial class TrackEditorPage : UserControl, ITabPage
{
    private readonly Track _t;
    private bool _syncingColor;

    public TrackEditorPage(Track t)
    {
        InitializeComponent();
        _t = t;
        Heading.Text = Ui.TrackLine(t);
        TitleBox.Text = t.Title;
        ArtistsBox.Text = t.ArtistsLine;
        AlbumBox.Text = t.Album ?? "";
        GenresBox.Text = t.GenresLine;
        LabelBox.Text = t.Label ?? "";
        ColorBox.Text = t.Color ?? "";
        CoverBox.Text = t.CoverArt ?? "";
        PathBox.Text = t.Path ?? "";
        InvalidBox.IsChecked = t.Invalid;
        SyncSwatch();
        FillReadOnly();
    }

    private void FillReadOnly()
    {
        var rows = new (string, string)[]
        {
            ("meta.id", _t.Id?.ToString() ?? "—"),
            ("meta.duration", _t.DurationText),
            ("meta.bitrate", _t.Bitrate > 0 ? $"{_t.Bitrate} kbps" : "—"),
            ("meta.samplerate", _t.SampleRate > 0 ? $"{_t.SampleRate} Hz" : "—"),
            ("meta.listens", _t.ListenCount.ToString()),
            ("meta.volume", $"{Math.Round(_t.Volume * 100)}%"),
        };
        for (var i = 0; i < rows.Length; i++)
        {
            ReadOnlyGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var key = new TextBlock { Text = L.T(rows[i].Item1), Foreground = Ui.Muted };
            var value = new TextBlock { Text = rows[i].Item2, IsTextSelectionEnabled = true };
            Grid.SetRow(key, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            ReadOnlyGrid.Children.Add(key);
            ReadOnlyGrid.Children.Add(value);
        }
    }

    // ───────────────────────── color ─────────────────────────

    private void SyncSwatch()
    {
        var c = Ui.ParseColor(ColorBox.Text);
        ColorSwatch.Background = c is { } col ? new SolidColorBrush(col) : null;
        if (c is { } color)
        {
            _syncingColor = true;
            Picker.Color = color;
            _syncingColor = false;
        }
    }

    private void Picker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_syncingColor)
            return;
        _syncingColor = true;
        ColorBox.Text = Ui.ToHex(args.NewColor);
        _syncingColor = false;
        ColorSwatch.Background = new SolidColorBrush(args.NewColor);
    }

    private void ColorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_syncingColor)
            SyncSwatch();
    }

    private void ClearColor_Click(object sender, RoutedEventArgs e) => ColorBox.Text = "";

    // ───────────────────────── save ─────────────────────────

    private void Save()
    {
        if (_t.Id is not { } id)
            return;
        var title = TitleBox.Text.Trim();
        if (title.Length == 0)
        {
            ShellView.Current?.Notify(L.T("msg.title_required"), LogLevel.Warn);
            return;
        }
        var color = ColorBox.Text.Trim();
        if (color.Length > 0 && Ui.ParseColor(color) is null)
        {
            ShellView.Current?.Notify(L.F("msg.bad_color", color), LogLevel.Warn);
            return;
        }

        var results = new List<IdxStatus>();
        var artists = Ui.SplitList(ArtistsBox.Text);
        var titleChanged = title != _t.Title;
        var artistsChanged = !artists.SequenceEqual(_t.Artists);
        if (titleChanged || artistsChanged)
            results.Add(CoreApi.Library.SetMeta(id, titleChanged ? title : null, artistsChanged ? artists : null));

        var album = AlbumBox.Text.Trim();
        if (album != (_t.Album ?? ""))
            results.Add(CoreApi.Library.SetAlbum(id, Null(album)));

        var genres = Ui.SplitList(GenresBox.Text);
        if (!genres.SequenceEqual(_t.Genres))
            results.Add(CoreApi.Library.SetGenres(id, genres));

        var label = LabelBox.Text.Trim();
        if (label != (_t.Label ?? ""))
            results.Add(CoreApi.Library.SetLabel(id, Null(label)));

        var hex = color.Length > 0 ? Ui.ToHex(Ui.ParseColor(color)!.Value) : "";
        if (!string.Equals(hex, _t.Color ?? "", StringComparison.OrdinalIgnoreCase))
            results.Add(CoreApi.Library.SetColor(id, Null(hex)));

        var cover = CoverBox.Text.Trim();
        if (cover != (_t.CoverArt ?? ""))
        {
            if (cover.Length > 0 && !File.Exists(cover))
                ShellView.Current?.Notify(L.F("msg.cover_missing", cover), LogLevel.Warn);
            results.Add(CoreApi.Library.SetCover(id, Null(cover)));
            Covers.Forget(_t);
        }

        var path = PathBox.Text.Trim();
        if (path.Length > 0 && path != (_t.Path ?? ""))
        {
            if (!File.Exists(path))
                ShellView.Current?.Notify(L.F("msg.path_missing", path), LogLevel.Warn);
            results.Add(CoreApi.Library.SetPath(id, path));
            Covers.Forget(_t);
        }

        var invalid = InvalidBox.IsChecked == true;
        if (invalid != _t.Invalid)
            results.Add(CoreApi.Library.SetInvalid(id, invalid));

        var failed = results.Count(r => r != IdxStatus.Ok);
        if (results.Count == 0)
            ShellView.Current?.Notify(L.T("msg.nothing_changed"), LogLevel.Info);
        else if (failed == 0)
            ShellView.Current?.Notify(L.F("msg.meta_saved", results.Count), LogLevel.Ok);
        else
            ShellView.Current?.Notify(L.F("msg.meta_failed", failed, results.Count), LogLevel.Error);
        ShellView.Current?.ReloadLibrary();
        if (failed == 0)
            ShellView.Current?.CloseTab(this);
    }

    private static string? Null(string s) => s.Length == 0 ? null : s;

    // ───────────────────────── events ─────────────────────────

    private void Save_Click(object sender, RoutedEventArgs e) => Save();
    private void Cancel_Click(object sender, RoutedEventArgs e) => ShellView.Current?.CloseTab(this);

    private async void BrowseCover_Click(object sender, RoutedEventArgs e)
    {
        if (await Ui.PickFileAsync(Ui.ImageTypes) is { } path)
            CoverBox.Text = path;
    }

    private void ClearCover_Click(object sender, RoutedEventArgs e) => CoverBox.Text = "";

    private async void BrowsePath_Click(object sender, RoutedEventArgs e)
    {
        if (await Ui.PickFileAsync(Ui.AudioTypes) is { } path)
            PathBox.Text = path;
    }
}
