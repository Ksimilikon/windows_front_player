// Playback orchestration over the core engine: the queue of the playing
// playlist, next / previous, shuffle / repeat, autoplay on the core end-of-track
// callback, volumes. The core plays one track at a time — the queue lives here.
//
// Reordering the queue (drag-n-drop) is temporary: it is written to the db only
// by SaveOrder. The class is UI-agnostic: the UI calls Tick() from its timer
// on the UI thread, and every method must be called on that thread.

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using DreamPlayer.Core;

namespace DreamPlayer.Services;

/// one queue row.
public sealed partial class TrackItem : ObservableObject
{
    public TrackItem(Track track) => Track = track;

    [ObservableProperty]
    public partial Track Track { get; set; }

    /// 1-based position in the queue (updated on reorder).
    [ObservableProperty]
    public partial int Number { get; set; }

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    public override string ToString() => $"{Number}. {Track}";
}

public sealed partial class Player : ObservableObject
{
    public const float TrackVolumeMax = 2f;
    private static readonly TimeSpan PersistDelay = TimeSpan.FromMilliseconds(800);

    public static Player Instance { get; } = new();

    private readonly Random _rng = new();
    /// shuffle: tracks played in this pass + the order they were played in.
    private readonly HashSet<TrackItem> _played = [];
    private readonly List<TrackItem> _history = [];
    private bool _suppressQueueEvents;
    private float? _pendingMaster;
    private (long Id, float Volume, DateTime At)? _pendingTrackVolume;
    private DateTime _lastPanicCheck;

    private Player()
    {
        PlaylistName = "";
        IsPaused = true;
        TrackVolume = 1f;
        MasterVolume = 1f;
        Queue.CollectionChanged += OnQueueChanged;
    }

    public ObservableCollection<TrackItem> Queue { get; } = [];

    /// the original playlist order (to revert drag-n-drop moves).
    private List<Track> _originalOrder = [];

    [ObservableProperty]
    public partial TrackItem? Current { get; private set; }

    /// a queue is loaded (the "now playing" tab exists).
    [ObservableProperty]
    public partial bool IsActive { get; private set; }

    [ObservableProperty]
    public partial string PlaylistName { get; private set; }

    /// the queue came from the "all songs" pool: its order can't be saved.
    [ObservableProperty]
    public partial bool FromPool { get; private set; }

    /// the queue order differs from the db one.
    [ObservableProperty]
    public partial bool OrderDirty { get; private set; }

    [ObservableProperty]
    public partial bool IsPaused { get; private set; }

    /// the last track finished and nothing follows (repeat off).
    [ObservableProperty]
    public partial bool IsEnded { get; private set; }

    [ObservableProperty]
    public partial ulong Position { get; private set; }

    [ObservableProperty]
    public partial ulong Duration { get; private set; }

    [ObservableProperty]
    public partial float TrackVolume { get; private set; }

    [ObservableProperty]
    public partial float MasterVolume { get; private set; }

    [ObservableProperty]
    public partial bool Shuffle { get; set; }

    [ObservableProperty]
    public partial RepeatMode Repeat { get; set; }

    /// a new track started (also on replay).
    public event Action<TrackItem>? TrackStarted;
    /// playback stopped and the queue was closed.
    public event Action? Stopped;
    /// a user-visible message (text, level) — shown in the bottom bar.
    public event Action<string, LogLevel>? Notify;

    partial void OnShuffleChanged(bool value)
    {
        if (_suppressSettings)
            return;
        AppSettings.Current.Shuffle = value;
        AppSettings.Save();
        ResetShufflePass();
        CoreLog.Info("player", $"shuffle {(value ? "on" : "off")}");
    }

    partial void OnRepeatChanged(RepeatMode value)
    {
        if (_suppressSettings)
            return;
        AppSettings.Current.Repeat = value;
        AppSettings.Save();
        CoreLog.Info("player", $"repeat {value}");
    }

    /// reads the initial state from settings and the core.
    public void Init()
    {
        _suppressSettings = true;
        Shuffle = AppSettings.Current.Shuffle;
        Repeat = AppSettings.Current.Repeat;
        _suppressSettings = false;
        MasterVolume = CoreApi.Engine.MasterVolume();
        if (Math.Abs(AppSettings.Current.Speed - 1f) > 0.001f)
            CoreApi.Engine.SetSpeed(AppSettings.Current.Speed);
    }

    private bool _suppressSettings;

    // ───────────────────────── queue ─────────────────────────

    /// starts `tracks` (in this order) of playlist `pl` from `start`.
    public void Start(PlaylistView pl, IReadOnlyList<Track> tracks, int start)
    {
        if (tracks.Count == 0)
        {
            Notify?.Invoke(L.T("msg.playlist_empty"), LogLevel.Warn);
            return;
        }
        CoreLog.Info("player", $"start playlist \"{pl.DisplayName}\" ({tracks.Count} track(s)) from #{start + 1}");
        _suppressQueueEvents = true;
        Queue.Clear();
        foreach (var t in tracks)
            Queue.Add(new TrackItem(t));
        _suppressQueueEvents = false;
        _originalOrder = [.. tracks];
        Renumber();
        PlaylistName = pl.IsPool ? pl.DisplayName : pl.Name;
        FromPool = pl.IsPool;
        OrderDirty = false;
        IsActive = true;
        ResetShufflePass();
        PlayItem(Queue[Math.Clamp(start, 0, Queue.Count - 1)]);
    }

    /// plays a queue item.
    public void PlayItem(TrackItem item, bool remember = true)
    {
        if (Current is { } prev)
        {
            prev.IsCurrent = false;
            if (remember && prev != item)
                _history.Add(prev);
        }
        Current = item;
        item.IsCurrent = true;
        _played.Add(item);
        IsEnded = false;
        Position = 0;
        var st = CoreApi.Engine.Play(item.Track);
        if (st != EngineStatus.Ok)
        {
            Notify?.Invoke(L.F("msg.play_failed", item.Track.Title, CoreApi.Describe(st)), LogLevel.Error);
            IsPaused = true;
            return;
        }
        IsPaused = false;
        Duration = CoreApi.Engine.Duration();
        if (Duration == 0)
            Duration = item.Track.DurationS;
        TrackVolume = CoreApi.Engine.TrackVolume();
        TrackStarted?.Invoke(item);
    }

    public void Next() => Advance(auto: false);

    public void Prev()
    {
        if (Current is null)
            return;
        // past the first seconds "previous" restarts the track.
        if (Position > 3)
        {
            SeekTo(0);
            return;
        }
        if (Shuffle && _history.Count > 0)
        {
            var back = _history[^1];
            _history.RemoveAt(_history.Count - 1);
            if (Queue.Contains(back))
            {
                PlayItem(back, remember: false);
                return;
            }
        }
        var i = Queue.IndexOf(Current);
        PlayItem(Queue[i <= 0 ? Queue.Count - 1 : i - 1]);
    }

    /// next track. `auto` — the previous one played to the end (repeat rules apply).
    private void Advance(bool auto)
    {
        if (Current is null || Queue.Count == 0)
            return;
        if (auto && Repeat == RepeatMode.One)
        {
            PlayItem(Current, remember: false);
            return;
        }
        var next = Shuffle ? NextShuffled(auto) : NextInOrder(auto);
        if (next is null)
        {
            IsEnded = true;
            IsPaused = true;
            Position = Duration;
            CoreLog.Info("player", "end of the queue (repeat off)");
            return;
        }
        PlayItem(next);
    }

    private TrackItem? NextInOrder(bool auto)
    {
        var i = Queue.IndexOf(Current!);
        if (i + 1 < Queue.Count)
            return Queue[i + 1];
        // the end: wrap with repeat (or on a manual "next").
        return auto && Repeat == RepeatMode.Off ? null : Queue[0];
    }

    private TrackItem? NextShuffled(bool auto)
    {
        var left = Queue.Where(t => !_played.Contains(t)).ToList();
        if (left.Count == 0)
        {
            if (auto && Repeat == RepeatMode.Off)
                return null;
            ResetShufflePass();
            left = Queue.Where(t => t != Current).ToList();
            if (left.Count == 0)
                return Current;
        }
        return left[_rng.Next(left.Count)];
    }

    private void ResetShufflePass()
    {
        _played.Clear();
        _history.Clear();
        if (Current is { } c)
            _played.Add(c);
    }

    public void TogglePause()
    {
        if (Current is null)
            return;
        if (IsEnded)
        {
            PlayItem(Current, remember: false);
            return;
        }
        CoreApi.Engine.PlayPause();
        IsPaused = CoreApi.Engine.IsPaused();
    }

    /// stops playback and closes the queue.
    public void Stop()
    {
        if (!IsActive)
            return;
        CoreLog.Info("player", "stop");
        FlushPending(force: true);
        CoreApi.Engine.Stop();
        _suppressQueueEvents = true;
        Queue.Clear();
        _suppressQueueEvents = false;
        _originalOrder = [];
        _played.Clear();
        _history.Clear();
        Current = null;
        IsActive = false;
        IsPaused = true;
        IsEnded = false;
        OrderDirty = false;
        Position = 0;
        Duration = 0;
        Stopped?.Invoke();
    }

    public void SeekBy(int secs)
    {
        if (Current is null)
            return;
        var target = Math.Clamp((long)Position + secs, 0, Duration > 0 ? (long)Duration : long.MaxValue);
        SeekTo((ulong)target);
    }

    public void SeekTo(ulong secs)
    {
        if (Current is null)
            return;
        if (IsEnded)
            PlayItem(Current, remember: false);
        var st = CoreApi.Engine.Seek(secs);
        if (st == EngineStatus.Ok)
            Position = secs;
        else
            Notify?.Invoke(L.F("msg.seek_failed", CoreApi.Describe(st)), LogLevel.Warn);
    }

    // ───────────────────────── volumes ─────────────────────────

    /// master volume 0..1; applied on the next Tick (the core writes its config
    /// to disk on every change, so a slider drag is throttled).
    public void RequestMasterVolume(float v)
    {
        v = Math.Clamp(v, 0f, 1f);
        MasterVolume = v;
        _pendingMaster = v;
    }

    /// per-track volume 0..2 of `track`: applied to the engine at once if it
    /// is playing, saved to the db after the slider settles.
    public void RequestTrackVolume(Track track, float v)
    {
        v = Math.Clamp(v, 0f, TrackVolumeMax);
        if (Current?.Track.Key == track.Key)
        {
            TrackVolume = v;
            CoreApi.Engine.SetTrackVolume(v);
        }
        if (track.Id is { } id)
            _pendingTrackVolume = (id, v, DateTime.UtcNow);
        // keep queue copies in sync, so a replay shows the new value.
        foreach (var item in Queue.Where(i => i.Track.Key == track.Key))
            item.Track = item.Track with { Volume = v };
    }

    private void FlushPending(bool force)
    {
        if (_pendingMaster is { } m)
        {
            _pendingMaster = null;
            var st = CoreApi.Engine.SetMasterVolume(m);
            if (st == EngineStatus.ConfigWriteFailed)
                Notify?.Invoke(L.T("msg.master_not_saved"), LogLevel.Warn);
        }
        if (_pendingTrackVolume is { } tv && (force || DateTime.UtcNow - tv.At >= PersistDelay))
        {
            _pendingTrackVolume = null;
            if (CoreApi.Library.SetVolume(tv.Id, tv.Volume) != IdxStatus.Ok)
                Notify?.Invoke(L.T("msg.track_volume_not_saved"), LogLevel.Error);
        }
    }

    public void SetSpeed(float speed)
    {
        CoreApi.Engine.SetSpeed(speed);
        AppSettings.Current.Speed = speed;
        AppSettings.Save();
    }

    // ───────────────────────── reorder / remove ─────────────────────────

    private void OnQueueChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_suppressQueueEvents)
            return;
        // the ListView reorders by Remove + Add; renumber once the item is back.
        if (e.Action != NotifyCollectionChangedAction.Remove)
        {
            Renumber();
            UpdateDirty();
        }
    }

    public void Move(TrackItem item, int to)
    {
        var from = Queue.IndexOf(item);
        if (from < 0)
            return;
        to = Math.Clamp(to, 0, Queue.Count - 1);
        if (from == to)
            return;
        Queue.Move(from, to);
        CoreLog.Debug("player", $"queue: move #{from + 1} → #{to + 1}");
    }

    /// right after the playing track.
    public void MoveAfterCurrent(TrackItem item)
    {
        if (Current is null || item == Current)
            return;
        var from = Queue.IndexOf(item);
        var cur = Queue.IndexOf(Current);
        Move(item, from > cur ? cur + 1 : cur);
    }

    /// removes a track from the queue only (temporary).
    public void RemoveFromQueue(TrackItem item)
    {
        if (!Queue.Contains(item))
            return;
        if (item == Current)
        {
            if (Queue.Count == 1)
            {
                Stop();
                return;
            }
            Next();
        }
        _played.Remove(item);
        _history.RemoveAll(h => h == item);
        Queue.Remove(item);
        Renumber();
        UpdateDirty();
        CoreLog.Info("player", $"removed from queue: \"{item.Track.Title}\"");
    }

    /// removes a track from the queue AND from the playlist in the db.
    public bool RemoveFromPlaylist(TrackItem item)
    {
        if (FromPool)
            return false;
        var saved = _originalOrder.Where(t => t.Key != item.Track.Key).ToList();
        if (!SaveToDb(saved.Select(t => t.Id).OfType<long>().ToList()))
            return false;
        _originalOrder = saved;
        RemoveFromQueue(item);
        return true;
    }

    /// writes the current queue order into the playlist in the db.
    public bool SaveOrder()
    {
        if (FromPool || !IsActive)
            return false;
        if (!SaveToDb(Queue.Select(i => i.Track.Id).OfType<long>().ToList()))
            return false;
        _originalOrder = Queue.Select(i => i.Track).ToList();
        OrderDirty = false;
        Notify?.Invoke(L.T("msg.order_saved"), LogLevel.Ok);
        return true;
    }

    private bool SaveToDb(IReadOnlyList<long> ids)
    {
        // the cover is rewritten together with the tracks: keep the current one.
        var cover = Library.Instance.Find(PlaylistName)?.CoverArt;
        var st = CoreApi.Library.SavePlaylist(PlaylistName, ids, cover);
        if (st != IdxStatus.Ok)
        {
            Notify?.Invoke(L.F("msg.playlist_save_failed", CoreApi.Describe(st)), LogLevel.Error);
            return false;
        }
        Library.Instance.Reload();
        return true;
    }

    /// puts the queue back into the db order.
    public void RevertOrder()
    {
        var byKey = Queue.ToDictionary(i => i.Track.Key);
        _suppressQueueEvents = true;
        Queue.Clear();
        foreach (var t in _originalOrder)
        {
            if (byKey.TryGetValue(t.Key, out var item))
                Queue.Add(item);
        }
        _suppressQueueEvents = false;
        Renumber();
        OrderDirty = false;
        CoreLog.Info("player", "queue order reverted");
    }

    private void Renumber()
    {
        for (var i = 0; i < Queue.Count; i++)
            Queue[i].Number = i + 1;
    }

    private void UpdateDirty() =>
        OrderDirty = !Queue.Select(i => i.Track.Key).SequenceEqual(_originalOrder.Select(t => t.Key));

    /// the library was reloaded (metadata edits, removals): refresh queue
    /// copies, drop tracks that are gone from the db.
    public void SyncWithLibrary()
    {
        if (!IsActive)
            return;
        var lib = Library.Instance;
        foreach (var item in Queue.ToList())
        {
            if (item.Track.Id is not { } id)
                continue;
            if (lib.TrackById(id) is { } fresh)
                item.Track = fresh;
            else
                RemoveFromQueue(item);
        }
        _originalOrder = _originalOrder
            .Select(t => t.Id is { } id ? lib.TrackById(id) : t)
            .OfType<Track>()
            .ToList();
    }

    // ───────────────────────── output device ─────────────────────────

    /// switches the output device with auto-resume: the engine drops the track
    /// on a device change, so the position / pause state is restored.
    public bool SwitchDevice(string name)
    {
        var pos = Position;
        var paused = IsPaused;
        var st = CoreApi.Engine.SetOutputDevice(name);
        if (st != EngineStatus.Ok)
        {
            Notify?.Invoke(L.F("msg.device_failed", name, CoreApi.Describe(st)), LogLevel.Error);
            return false;
        }
        if (Current is { } cur && !IsEnded)
        {
            PlayItem(cur, remember: false);
            if (pos > 0)
                SeekTo(pos);
            if (paused)
            {
                CoreApi.Engine.Pause();
                IsPaused = true;
            }
        }
        Notify?.Invoke(L.F("msg.device_switched", name), LogLevel.Ok);
        return true;
    }

    // ───────────────────────── polling ─────────────────────────

    /// called by the UI timer (~5 Hz): autoplay, position, pending volumes,
    /// core panics.
    public void Tick()
    {
        if (TrackEndSignal.Take())
        {
            CoreLog.Info("core", "callback on_track_end: track played to the end");
            if (Current is not null)
                Advance(auto: true);
        }
        if (Current is not null && !IsEnded)
        {
            Position = CoreApi.Engine.Position();
            IsPaused = CoreApi.Engine.IsPaused();
            if (Duration == 0)
                Duration = CoreApi.Engine.Duration();
        }
        FlushPending(force: false);
        if (DateTime.UtcNow - _lastPanicCheck > TimeSpan.FromSeconds(2))
        {
            _lastPanicCheck = DateTime.UtcNow;
            CoreApi.CheckPanics();
        }
    }
}
