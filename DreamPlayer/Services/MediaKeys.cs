// System media transport controls (SMTC): the Windows media overlay and the
// media keys, like `integration/windows.rs` of the TUI. A desktop app gets
// its SMTC through a MediaPlayer with the command manager disabled — it plays
// nothing, the core does; we only mirror the state and take button presses.

using System.ComponentModel;
using DreamPlayer.Views;
using Microsoft.UI.Dispatching;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace DreamPlayer.Services;

public sealed class MediaKeys : IDisposable
{
    private readonly MediaPlayer _mp;
    private readonly SystemMediaTransportControls _smtc;
    private readonly DispatcherQueue _ui;
    private readonly Player _player = Player.Instance;

    private MediaKeys(DispatcherQueue ui)
    {
        _ui = ui;
        _mp = new MediaPlayer();
        _mp.CommandManager.IsEnabled = false;
        _smtc = _mp.SystemMediaTransportControls;
        _smtc.IsEnabled = true;
        _smtc.IsPlayEnabled = true;
        _smtc.IsPauseEnabled = true;
        _smtc.IsStopEnabled = true;
        _smtc.IsNextEnabled = true;
        _smtc.IsPreviousEnabled = true;
        _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
        _smtc.ButtonPressed += OnButton;

        _player.TrackStarted += OnTrackStarted;
        _player.Stopped += OnStopped;
        _player.PropertyChanged += OnPlayerChanged;
    }

    /// `null` if the system refused (the app still works without media keys).
    public static MediaKeys? TryCreate(DispatcherQueue ui)
    {
        try
        {
            var keys = new MediaKeys(ui);
            CoreLog.Info("smtc", "system media controls attached");
            return keys;
        }
        catch (Exception e)
        {
            CoreLog.Warn("smtc", $"system media controls unavailable: {e.Message}");
            return null;
        }
    }

    private void OnButton(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs e)
    {
        var button = e.Button;
        // raised on a system thread.
        _ui.TryEnqueue(() =>
        {
            CoreLog.Info("smtc", $"media key: {button}");
            switch (button)
            {
                case SystemMediaTransportControlsButton.Play when _player.IsPaused:
                case SystemMediaTransportControlsButton.Pause when !_player.IsPaused:
                    _player.TogglePause();
                    break;
                case SystemMediaTransportControlsButton.Next:
                    _player.Next();
                    break;
                case SystemMediaTransportControlsButton.Previous:
                    _player.Prev();
                    break;
                case SystemMediaTransportControlsButton.Stop:
                    _player.Stop();
                    break;
            }
        });
    }

    private async void OnTrackStarted(TrackItem item)
    {
        var t = item.Track;
        var updater = _smtc.DisplayUpdater;
        updater.ClearAll();
        updater.Type = MediaPlaybackType.Music;
        updater.MusicProperties.Title = t.Title;
        updater.MusicProperties.Artist = t.ArtistsLine;
        updater.MusicProperties.AlbumTitle = t.Album ?? "";
        try
        {
            if (await Covers.BytesAsync(t) is { Length: > 0 } bytes)
                updater.Thumbnail = RandomAccessStreamReference.CreateFromStream(await Covers.ToStreamAsync(bytes));
        }
        catch (Exception e)
        {
            CoreLog.Debug("smtc", $"thumbnail not set: {e.Message}");
        }
        updater.Update();
        _smtc.PlaybackStatus = MediaPlaybackStatus.Playing;
    }

    private void OnStopped()
    {
        _smtc.DisplayUpdater.ClearAll();
        _smtc.DisplayUpdater.Update();
        _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Player.IsPaused) || !_player.IsActive)
            return;
        _smtc.PlaybackStatus = _player.IsPaused ? MediaPlaybackStatus.Paused : MediaPlaybackStatus.Playing;
    }

    public void Dispose()
    {
        _player.TrackStarted -= OnTrackStarted;
        _player.Stopped -= OnStopped;
        _player.PropertyChanged -= OnPlayerChanged;
        _smtc.ButtonPressed -= OnButton;
        _mp.Dispose();
    }
}
