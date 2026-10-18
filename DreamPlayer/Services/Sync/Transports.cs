// The sync transports (the core has none: it only turns bytes into a session,
// see CoreApi.SyncSession). Ported from the TUI `tui/src/sync/{net,bt}.rs`:
// - TCP: wait on a port (host) or connect to `host[:port]` (default 47800);
// - ADB: `adb forward` / `adb reverse` + TCP over the cable;
// - Bluetooth RFCOMM: a service with the sync UUID (the phone finds it via
//   SDP); devices are paired in Windows beforehand, the sync keys go on top;
// - USB without adb (Android Open Accessory) — see UsbAccessory.
// Each returns a connected duplex stream and whether this side is the host.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Networking.Sockets;

namespace DreamPlayer.Services.Sync;

/// a connected byte stream to the peer.
public sealed class SyncLink : IAsyncDisposable
{
    public required Stream Stream { get; init; }
    /// this side listened for the connection.
    public required bool Host { get; init; }
    /// keeps transport resources alive while the connection lives.
    public IDisposable? Guard { get; init; }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        try
        {
            await Stream.DisposeAsync();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException)
        {
        }
        Guard?.Dispose();
    }
}

/// a paired Bluetooth device of Windows.
public sealed record BtDevice(string Id, string Name)
{
    public override string ToString() => Name;
}

public static class Transports
{
    /// default sync port (TCP and ADB).
    public const int DefaultPort = 47800;

    /// UUID of the Dream Player sync service (`tui/src/sync/bt.rs`).
    public static readonly Guid BtService = new("6f3c1a52-8d0e-4b8e-9a4b-3d2f7c51a9e1");

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BtConnectTimeout = TimeSpan.FromSeconds(20);

    /// brings the transport up. `info` gets progress lines; cancellation —
    /// `OperationCanceledException`, a failure — an exception with a message
    /// for the user.
    public static Task<SyncLink> ConnectAsync(SyncTarget t, Action<string> info, CancellationToken ct) => t.Kind switch
    {
        TransportKind.TcpListen => AcceptTcpAsync(new IPEndPoint(IPAddress.Any, t.Port), info, ct),
        TransportKind.TcpConnect => ConnectTcpAsync(t.Address ?? "", info, ct),
        TransportKind.AdbConnect => AdbConnectAsync(t.Port, info, ct),
        TransportKind.AdbListen => AdbListenAsync(t.Port, info, ct),
        TransportKind.UsbAndroid => UsbAccessory.ConnectAsync(t.UsbFilter, info, ct),
        TransportKind.BtListen => AcceptBtAsync(info, ct),
        TransportKind.BtConnect => ConnectBtAsync(t.BtDevice ?? "", info, ct),
        _ => throw new ArgumentOutOfRangeException(nameof(t)),
    };

    // ───────────────────────── TCP ─────────────────────────

    /// IPv4 addresses of this computer on the local networks (for the user to
    /// type on the other device).
    public static IReadOnlyList<string> LocalAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                    && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString())
                .Distinct()
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    /// `host[:port]` -> host and port (the port defaults to DefaultPort).
    public static (string Host, int Port) ParseAddress(string s)
    {
        s = s.Trim();
        var colon = s.LastIndexOf(':');
        // one colon — `host:port`; more — a bare IPv6 address.
        if (colon > 0 && s.IndexOf(':') == colon
            && int.TryParse(s[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            && port is > 0 and <= 65535)
            return (s[..colon], port);
        return (s.Trim('[', ']'), DefaultPort);
    }

    /// network interfaces that look like a VPN (they often block the local
    /// network: a WireGuard "block untunneled traffic" kill switch, ...).
    private static List<string> VpnInterfaces()
    {
        // kill switches are typical of these; other VPNs are listed only
        // when none of them is up.
        string[] killers = ["wireguard", "wintun"];
        string[] marks = ["vpn", "tap-windows", "openvpn"];
        bool Has(NetworkInterface n, string[] words) => words.Any(m =>
            n.Name.Contains(m, StringComparison.OrdinalIgnoreCase) || n.Description.Contains(m, StringComparison.OrdinalIgnoreCase));
        try
        {
            var up = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                    && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel
                    && n.GetIPProperties().UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                .ToList();
            var strong = up.Where(n => Has(n, killers)).Select(n => n.Name).ToList();
            return strong.Count > 0
                ? strong
                : up.Where(n => n.NetworkInterfaceType == NetworkInterfaceType.Ppp || Has(n, marks)).Select(n => n.Name).ToList();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    /// WSAEACCES on connect: the system (a VPN kill switch / firewall filter)
    /// forbids the connection — for every program, not just this one.
    private static IOException Blocked(string where)
    {
        var vpn = VpnInterfaces();
        return new IOException(vpn.Count > 0
            ? L.F("sync.net_blocked_vpn", where, string.Join(", ", vpn))
            : L.F("sync.net_blocked", where));
    }

    private static async Task<SyncLink> AcceptTcpAsync(IPEndPoint at, Action<string> info, CancellationToken ct)
    {
        var listener = new TcpListener(at);
        try
        {
            listener.Start();
        }
        catch (SocketException e) when (e.SocketErrorCode is System.Net.Sockets.SocketError.AccessDenied or System.Net.Sockets.SocketError.AddressAlreadyInUse)
        {
            // a port in a range Windows reserved (Hyper-V / WSL) or taken.
            throw new IOException(L.F("sync.port_unavailable", at.Port, e.Message));
        }
        try
        {
            info(L.F("sync.tcp_waiting", at.Port));
            var client = await listener.AcceptTcpClientAsync(ct);
            client.NoDelay = true;
            info(L.F("sync.connected", client.Client.RemoteEndPoint));
            return new SyncLink { Stream = client.GetStream(), Host = true, Guard = client };
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<SyncLink> ConnectTcpAsync(string address, Action<string> info, CancellationToken ct)
    {
        var (host, port) = ParseAddress(address);
        if (host.Length == 0)
            throw new IOException(L.T("sync.no_address"));
        return await ConnectTcpAsync(host, port, info, ct);
    }

    private static async Task<SyncLink> ConnectTcpAsync(string host, int port, Action<string> info, CancellationToken ct)
    {
        info(L.F("sync.tcp_connecting", $"{host}:{port}"));
        var client = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            await client.ConnectAsync(host, port, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            client.Dispose();
            throw new IOException(L.F("sync.tcp_timeout", $"{host}:{port}"));
        }
        catch (SocketException e) when (e.SocketErrorCode == System.Net.Sockets.SocketError.AccessDenied)
        {
            client.Dispose();
            throw Blocked($"{host}:{port}");
        }
        catch
        {
            client.Dispose();
            throw;
        }
        info(L.F("sync.connected", client.Client.RemoteEndPoint));
        return new SyncLink { Stream = client.GetStream(), Host = false, Guard = client };
    }

    // ───────────────────────── ADB ─────────────────────────

    private static async Task<SyncLink> AdbConnectAsync(int port, Action<string> info, CancellationToken ct)
    {
        var p = $"tcp:{port}";
        await AdbAsync(ct, "forward", p, p);
        info(L.F("sync.adb_forward", p));
        return await ConnectTcpAsync(IPAddress.Loopback.ToString(), port, info, ct);
    }

    private static async Task<SyncLink> AdbListenAsync(int port, Action<string> info, CancellationToken ct)
    {
        var p = $"tcp:{port}";
        await AdbAsync(ct, "reverse", p, p);
        info(L.F("sync.adb_reverse", p));
        return await AcceptTcpAsync(new IPEndPoint(IPAddress.Loopback, port), info, ct);
    }

    /// runs `adb <args>`; a failure carries adb's own message.
    private static async Task AdbAsync(CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("adb")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        Process proc;
        try
        {
            proc = Process.Start(psi) ?? throw new IOException("adb");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new IOException(L.T("sync.adb_missing"));
        }
        using (proc)
        {
            var err = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0)
                throw new IOException($"adb {string.Join(' ', args)}: {(await err).Trim()}");
        }
    }

    // ───────────────────────── Bluetooth ─────────────────────────

    /// paired Bluetooth devices of Windows (to connect to).
    public static async Task<IReadOnlyList<BtDevice>> PairedBtDevicesAsync()
    {
        var found = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true));
        return found
            .Select(d => new BtDevice(d.Id, string.IsNullOrWhiteSpace(d.Name) ? d.Id : d.Name))
            .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static async Task<SyncLink> AcceptBtAsync(Action<string> info, CancellationToken ct)
    {
        RfcommServiceProvider provider;
        try
        {
            provider = await RfcommServiceProvider.CreateAsync(RfcommServiceId.FromUuid(BtService));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new IOException(L.F("sync.bt_unavailable", e.Message), e);
        }
        var listener = new StreamSocketListener();
        var accepted = new TaskCompletionSource<StreamSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.ConnectionReceived += (_, a) => accepted.TrySetResult(a.Socket);
        var advertising = false;
        try
        {
            await listener.BindServiceNameAsync(provider.ServiceId.AsString(),
                SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication).AsTask(ct);
            provider.StartAdvertising(listener, true);
            advertising = true;
            info(L.T("sync.bt_waiting"));
            using (ct.Register(() => accepted.TrySetCanceled(ct)))
            {
                var socket = await accepted.Task;
                info(L.F("sync.connected", socket.Information.RemoteHostName?.DisplayName));
                return BtLink(socket, host: true);
            }
        }
        finally
        {
            if (advertising)
                provider.StopAdvertising();
            listener.Dispose();
        }
    }

    private static async Task<SyncLink> ConnectBtAsync(string device, Action<string> info, CancellationToken ct)
    {
        if (device.Length == 0)
            throw new IOException(L.T("sync.bt_no_device"));
        BluetoothDevice? dev;
        try
        {
            dev = ParseMac(device) is { } mac
                ? await BluetoothDevice.FromBluetoothAddressAsync(mac).AsTask(ct)
                : await BluetoothDevice.FromIdAsync(device).AsTask(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new IOException(L.F("sync.bt_unavailable", e.Message), e);
        }
        if (dev is null)
            throw new IOException(L.F("sync.bt_not_found", device));
        using (dev)
        {
            info(L.F("sync.bt_connecting", dev.Name));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(BtConnectTimeout);
            try
            {
                var services = await dev.GetRfcommServicesForIdAsync(RfcommServiceId.FromUuid(BtService),
                    BluetoothCacheMode.Uncached).AsTask(timeout.Token);
                if (services.Error != BluetoothError.Success || services.Services.Count == 0)
                    throw new IOException(L.F("sync.bt_no_service", dev.Name, services.Error));
                var svc = services.Services[0];
                var socket = new StreamSocket();
                try
                {
                    await socket.ConnectAsync(svc.ConnectionHostName, svc.ConnectionServiceName,
                        SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication).AsTask(timeout.Token);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
                info(L.F("sync.connected", dev.Name));
                return BtLink(socket, host: false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new IOException(L.F("sync.bt_timeout", dev.Name));
            }
        }
    }

    private static SyncLink BtLink(StreamSocket socket, bool host) => new()
    {
        Stream = new DuplexStream(socket.InputStream.AsStreamForRead(0), socket.OutputStream.AsStreamForWrite(0)),
        Host = host,
        Guard = socket,
    };

    /// `AA:BB:CC:DD:EE:FF` -> the 48-bit address.
    public static ulong? ParseMac(string s)
    {
        var parts = s.Trim().Split(':', '-');
        if (parts.Length != 6)
            return null;
        ulong v = 0;
        foreach (var p in parts)
        {
            if (p.Length != 2 || !byte.TryParse(p, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                return null;
            v = (v << 8) | b;
        }
        return v;
    }
}

/// one stream over a separate input and output (a WinRT socket).
internal sealed class DuplexStream(Stream input, Stream output) : Stream
{
    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        input.ReadAsync(buffer, ct);

    public override void Write(byte[] buffer, int offset, int count) => output.Write(buffer, offset, count);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
        output.WriteAsync(buffer, ct);

    public override void Flush() => output.Flush();
    public override Task FlushAsync(CancellationToken ct) => output.FlushAsync(ct);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            input.Dispose();
            output.Dispose();
        }
        base.Dispose(disposing);
    }
}
