// Where to connect for a sync / send / receive, and what the session does.
// Mirrors the TUI `:sync` targets (`tui/src/sync/mod.rs`): the side that
// listens is the host of the session (it gives its library first).

using DreamPlayer.Core;

namespace DreamPlayer.Services.Sync;

public enum TransportKind
{
    /// wait for a device on the local network (host).
    TcpListen,
    /// connect to `host[:port]` on the local network.
    TcpConnect,
    /// USB cable to an Android phone without adb: the phone becomes an
    /// accessory of ours (Android Open Accessory); the phone is the host.
    /// Other platforms (iOS, macOS) get kinds of their own — the protocols
    /// over the cable differ.
    UsbAndroid,
    /// `adb forward`, then connect to the phone that listens.
    AdbConnect,
    /// `adb reverse`, then wait for the phone (host).
    AdbListen,
    /// wait for a Bluetooth connection (host).
    BtListen,
    /// connect to a paired Bluetooth device.
    BtConnect,
}

/// a connection: the transport and its parameters.
public sealed record SyncTarget(
    TransportKind Kind,
    int Port = Transports.DefaultPort,
    // `host[:port]` for TcpConnect.
    string? Address = null,
    // a Windows device id or a `AA:BB:CC:DD:EE:FF` address for BtConnect.
    string? BtDevice = null,
    // the phone as `VID:PID` for UsbAndroid (`null` — found automatically).
    (ushort Vid, ushort Pid)? UsbFilter = null);

/// what the session does once connected.
public abstract record SyncMode
{
    /// two-way library sync (the other side syncs too).
    public sealed record Sync : SyncMode;

    /// send a selection with the export flags (the other side receives).
    public sealed record Send(ExportMode What, IReadOnlyList<string> Playlists, IReadOnlyList<long> Ids, PackFlags Flags)
        : SyncMode;

    /// accept a send (the other side sends); `Force` — as in import.
    public sealed record Receive(bool Force) : SyncMode;
}
