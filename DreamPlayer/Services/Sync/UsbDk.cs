// USB access through UsbDk (Daynix, https://github.com/daynix/UsbDk) — a
// signed filter driver that lets a program take a USB device away from its
// Windows driver for a while ("redirect") and talk to it directly, then give
// it back. A phone bound to the MTP driver can't get a vendor control request
// any other way from user mode (WinUSB / WinRT need the WinUSB driver), so
// UsbDk is what lets the app switch the phone to an accessory and talk to the
// accessory without installing drivers per device (like libusb does on
// Windows). Installed once by the user (MSI); its helper library is loaded
// dynamically — without it this backend simply reports "not available".
//
// API: UsbDkHelper.h / UsbDkData.h (UsbDk master). Structs use ULONG64 /
// PVOID64 (8 bytes), MAX_DEVICE_ID_LEN = 200 wide chars.

using System.Runtime.InteropServices;

namespace DreamPlayer.Services.Sync;

internal static unsafe class UsbDk
{
    private const int MaxDeviceIdLen = 200;

    [StructLayout(LayoutKind.Sequential)]
    public struct DeviceId
    {
        public fixed char Device[MaxDeviceIdLen];
        public fixed char Instance[MaxDeviceIdLen];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfo
    {
        public DeviceId Id;
        public ulong FilterId;
        public ulong Port;
        public ulong Speed;
        public fixed byte Descriptor[18]; // USB_DEVICE_DESCRIPTOR (packed)
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ConfigRequest
    {
        public DeviceId Id;
        public ulong Index;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TransferRequest
    {
        public ulong EndpointAddress;
        public nint Buffer;
        public ulong BufferLength;
        public ulong TransferType;
        public ulong IsoPacketsSize;
        public nint IsoPackets;
        // USB_DK_TRANSFER_RESULT
        public ulong BytesTransferred;
        public ulong UsbdStatus;
        public nint IsoResults;
    }

    internal const ulong ControlTransfer = 0;
    internal const ulong BulkTransfer = 1;

    // TransferResult
    internal const int TransferFailure = 0;
    internal const int TransferSuccess = 1;
    internal const int TransferSuccessAsync = 2;

    /// a USB device as UsbDk sees it (not redirected).
    public sealed record Device(DeviceId Id, string InstanceId, ushort Vid, ushort Pid, byte Class)
    {
        public string Ids => $"{Vid:x4}:{Pid:x4}";
    }

    private static readonly Lazy<bool> Loaded = new(Load);

    private static delegate* unmanaged<DeviceInfo**, uint*, int> _getDevicesList;
    private static delegate* unmanaged<DeviceInfo*, void> _releaseDevicesList;
    private static delegate* unmanaged<ConfigRequest*, byte**, uint*, int> _getConfigDescriptor;
    private static delegate* unmanaged<byte*, void> _releaseConfigDescriptor;
    private static delegate* unmanaged<DeviceId*, nint> _startRedirect;
    private static delegate* unmanaged<nint, int> _stopRedirect;
    internal static delegate* unmanaged<nint, TransferRequest*, NativeOverlapped*, int> ReadPipe;
    internal static delegate* unmanaged<nint, TransferRequest*, NativeOverlapped*, int> WritePipe;
    internal static delegate* unmanaged<nint, ulong, int> AbortPipe;
    private static delegate* unmanaged<nint, nint> _getSystemHandle;

    /// UsbDk is installed (its helper library loads).
    public static bool Available => Loaded.Value;

    private static bool Load()
    {
        // the installer puts the helper into System32 (libusb loads it from
        // there); the runtime folder is a fallback.
        var candidates = new[]
        {
            "UsbDkHelper.dll",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "UsbDk Runtime Library", "UsbDkHelper.dll"),
        };
        nint lib = 0;
        foreach (var c in candidates)
        {
            if (NativeLibrary.TryLoad(c, typeof(UsbDk).Assembly, DllImportSearchPath.System32 | DllImportSearchPath.SafeDirectories, out lib)
                || (Path.IsPathRooted(c) && NativeLibrary.TryLoad(c, out lib)))
                break;
        }
        if (lib == 0)
        {
            CoreLog.Debug("usb", "UsbDk is not installed (UsbDkHelper.dll not found)");
            return false;
        }
        try
        {
            _getDevicesList = (delegate* unmanaged<DeviceInfo**, uint*, int>)NativeLibrary.GetExport(lib, "UsbDk_GetDevicesList");
            _releaseDevicesList = (delegate* unmanaged<DeviceInfo*, void>)NativeLibrary.GetExport(lib, "UsbDk_ReleaseDevicesList");
            _getConfigDescriptor = (delegate* unmanaged<ConfigRequest*, byte**, uint*, int>)NativeLibrary.GetExport(lib, "UsbDk_GetConfigurationDescriptor");
            _releaseConfigDescriptor = (delegate* unmanaged<byte*, void>)NativeLibrary.GetExport(lib, "UsbDk_ReleaseConfigurationDescriptor");
            _startRedirect = (delegate* unmanaged<DeviceId*, nint>)NativeLibrary.GetExport(lib, "UsbDk_StartRedirect");
            _stopRedirect = (delegate* unmanaged<nint, int>)NativeLibrary.GetExport(lib, "UsbDk_StopRedirect");
            ReadPipe = (delegate* unmanaged<nint, TransferRequest*, NativeOverlapped*, int>)NativeLibrary.GetExport(lib, "UsbDk_ReadPipe");
            WritePipe = (delegate* unmanaged<nint, TransferRequest*, NativeOverlapped*, int>)NativeLibrary.GetExport(lib, "UsbDk_WritePipe");
            AbortPipe = (delegate* unmanaged<nint, ulong, int>)NativeLibrary.GetExport(lib, "UsbDk_AbortPipe");
            _getSystemHandle = (delegate* unmanaged<nint, nint>)NativeLibrary.GetExport(lib, "UsbDk_GetRedirectorSystemHandle");
        }
        catch (EntryPointNotFoundException e)
        {
            CoreLog.Warn("usb", $"UsbDkHelper.dll is incompatible: {e.Message}");
            return false;
        }
        CoreLog.Info("usb", "UsbDk is available");
        return true;
    }

    /// every USB device (hubs included); empty without UsbDk.
    public static List<Device> Devices()
    {
        var result = new List<Device>();
        if (!Available)
            return result;
        DeviceInfo* list = null;
        uint count = 0;
        if (_getDevicesList(&list, &count) == 0)
        {
            CoreLog.Warn("usb", $"UsbDk_GetDevicesList failed ({Marshal.GetLastWin32Error()})");
            return result;
        }
        try
        {
            for (uint i = 0; i < count; i++)
            {
                var info = list[i];
                var d = info.Descriptor;
                var vid = (ushort)(d[8] | (d[9] << 8));
                var pid = (ushort)(d[10] | (d[11] << 8));
                result.Add(new Device(info.Id, new string(info.Id.Instance), vid, pid, d[4]));
            }
        }
        finally
        {
            _releaseDevicesList(list);
        }
        return result;
    }

    /// the raw first configuration descriptor (with its interfaces and
    /// endpoints); `null` — not readable.
    public static byte[]? ConfigDescriptor(Device dev)
    {
        var req = new ConfigRequest { Id = dev.Id, Index = 0 };
        byte* desc = null;
        uint len = 0;
        if (_getConfigDescriptor(&req, &desc, &len) == 0 || desc == null)
            return null;
        try
        {
            return new ReadOnlySpan<byte>(desc, (int)len).ToArray();
        }
        finally
        {
            _releaseConfigDescriptor(desc);
        }
    }

    /// interface classes of a configuration descriptor.
    public static IEnumerable<byte> InterfaceClasses(byte[] config)
    {
        for (var i = 0; i + 1 < config.Length && config[i] > 0; i += config[i])
        {
            if (config[i + 1] == 4 && i + 5 < config.Length) // INTERFACE
                yield return config[i + 5];
        }
    }

    /// bulk endpoints of the first interface: (in, out, max packet).
    public static (byte In, byte Out, int MaxPacket)? BulkEndpoints(byte[] config)
    {
        byte? pin = null, pout = null;
        var max = 512;
        var interfaces = 0;
        for (var i = 0; i + 1 < config.Length && config[i] > 0; i += config[i])
        {
            switch (config[i + 1])
            {
                case 4: // INTERFACE
                    interfaces++;
                    break;
                case 5 when interfaces == 1 && i + 5 < config.Length && (config[i + 3] & 0x03) == 2: // bulk ENDPOINT
                    var addr = config[i + 2];
                    max = Math.Max(1, config[i + 4] | (config[i + 5] << 8));
                    if ((addr & 0x80) != 0)
                        pin ??= addr;
                    else
                        pout ??= addr;
                    break;
            }
        }
        return pin is { } a && pout is { } b ? (a, b, max) : null;
    }

    /// how long taking a device may take: UsbDk waits for Windows, which may
    /// be busy setting a new device (the accessory) up.
    private static readonly TimeSpan RedirectTimeout = TimeSpan.FromSeconds(15);

    /// takes the device away from its Windows driver until disposed. A start
    /// that doesn't return in time is reported (and undone in the
    /// background once it does).
    public static RedirectedDevice Redirect(Device dev)
    {
        var id = dev.Id;
        var start = Task.Run(() => StartRedirect(id));
        if (!start.Wait(RedirectTimeout))
        {
            CoreLog.Warn("usb", $"UsbDk_StartRedirect({dev.Ids}) is still running after {RedirectTimeout.TotalSeconds:0} s");
            start.ContinueWith(t =>
            {
                if (t.Result.Handle is not (0 or -1))
                    _stopRedirect(t.Result.Handle);
            }, TaskScheduler.Default);
            throw new IOException(L.F("sync.usbdk_redirect_slow", dev.Ids));
        }
        var (h, err) = start.Result;
        if (h == 0 || h == -1)
            throw new IOException(L.F("sync.usbdk_redirect_failed", dev.Ids, err));
        CoreLog.Debug("usb", $"{dev.Ids} redirected");
        return new RedirectedDevice(h, _getSystemHandle(h), dev);
    }

    private static (nint Handle, int Error) StartRedirect(DeviceId id)
    {
        var h = _startRedirect(&id);
        return (h, Marshal.GetLastWin32Error());
    }

    internal static void StopRedirect(nint h) => _stopRedirect(h);
}

/// a device redirected by UsbDk: control and bulk transfers.
internal sealed unsafe class RedirectedDevice : IDisposable
{
    private nint _redirector;
    private readonly nint _system;

    public UsbDk.Device Device { get; }

    internal RedirectedDevice(nint redirector, nint system, UsbDk.Device device)
    {
        _redirector = redirector;
        _system = system;
        Device = device;
    }

    private nint H => _redirector != 0 ? _redirector : throw new ObjectDisposedException(nameof(RedirectedDevice));

    /// a transfer started; wait for it with `Complete`.
    public sealed class Pending : IDisposable
    {
        internal UsbDk.TransferRequest* Request;
        internal NativeOverlapped* Overlapped;
        internal readonly ManualResetEvent Done = new(false);
        internal bool Finished;

        internal Pending()
        {
            Request = (UsbDk.TransferRequest*)NativeMemory.AllocZeroed((nuint)sizeof(UsbDk.TransferRequest));
            Overlapped = (NativeOverlapped*)NativeMemory.AllocZeroed((nuint)sizeof(NativeOverlapped));
        }

        public void Dispose()
        {
            NativeMemory.Free(Request);
            NativeMemory.Free(Overlapped);
            Done.Dispose();
        }
    }

    /// starts a transfer into / from `buffer` (native memory that outlives
    /// it). `p` is reused between transfers.
    public void Start(Pending p, bool read, byte endpoint, ulong type, byte* buffer, int length)
    {
        *p.Request = new UsbDk.TransferRequest
        {
            EndpointAddress = endpoint,
            Buffer = (nint)buffer,
            BufferLength = (ulong)length,
            TransferType = type,
        };
        *p.Overlapped = default;
        p.Done.Reset();
        p.Overlapped->EventHandle = p.Done.SafeWaitHandle.DangerousGetHandle();
        p.Finished = false;
        var res = read ? UsbDk.ReadPipe(H, p.Request, p.Overlapped) : UsbDk.WritePipe(H, p.Request, p.Overlapped);
        switch (res)
        {
            case UsbDk.TransferSuccess:
                p.Finished = true;
                break;
            case UsbDk.TransferSuccessAsync:
                break;
            default:
                throw Failed((ulong)Marshal.GetLastWin32Error());
        }
    }

    /// waits up to `timeoutMs` (-1 — forever); `null` — still running.
    public int? Wait(Pending p, int timeoutMs)
    {
        if (!p.Finished)
        {
            if (!p.Done.WaitOne(timeoutMs))
                return null;
            if (!GetOverlappedResult(_system, p.Overlapped, out _, false))
            {
                var err = Marshal.GetLastWin32Error();
                p.Finished = true;
                throw Failed((ulong)err);
            }
            p.Finished = true;
        }
        if (p.Request->UsbdStatus != 0)
            throw Failed(p.Request->UsbdStatus);
        return (int)p.Request->BytesTransferred;
    }

    /// cancels transfers on the pipe (a pending one completes with an error).
    public void Abort(byte endpoint)
    {
        if (_redirector != 0)
            UsbDk.AbortPipe(_redirector, endpoint);
    }

    /// one synchronous transfer with a timeout (aborted on timeout).
    public int Transfer(bool read, byte endpoint, ulong type, Span<byte> data, int timeoutMs)
    {
        var buf = (byte*)NativeMemory.Alloc((nuint)Math.Max(1, data.Length));
        using var p = new Pending();
        try
        {
            // also for reads: a control IN transfer carries its setup packet
            // in front of the data.
            data.CopyTo(new Span<byte>(buf, data.Length));
            Start(p, read, endpoint, type, buf, data.Length);
            var n = Wait(p, timeoutMs);
            if (n is null)
            {
                Abort(endpoint);
                try
                {
                    Wait(p, -1);
                }
                catch (IOException)
                {
                }
                throw new TimeoutException();
            }
            // the whole buffer: for a control transfer the count excludes the
            // setup packet in front.
            if (read)
                new Span<byte>(buf, data.Length).CopyTo(data);
            return n.Value;
        }
        finally
        {
            NativeMemory.Free(buf);
        }
    }

    /// a vendor control request; IN when `RequestType` has bit 7.
    public int Control(byte requestType, byte request, ushort value, ushort index, Span<byte> data, int timeoutMs = 1000)
    {
        // UsbDk takes the 8-byte setup packet followed by the data.
        var buf = new byte[8 + data.Length];
        buf[0] = requestType;
        buf[1] = request;
        BitConverter.TryWriteBytes(buf.AsSpan(2), value);
        BitConverter.TryWriteBytes(buf.AsSpan(4), index);
        BitConverter.TryWriteBytes(buf.AsSpan(6), (ushort)data.Length);
        var read = (requestType & 0x80) != 0;
        if (!read)
            data.CopyTo(buf.AsSpan(8));
        var n = Transfer(read, 0, UsbDk.ControlTransfer, buf, timeoutMs);
        if (read)
            buf.AsSpan(8, data.Length).CopyTo(data);
        return Math.Max(0, Math.Min(n, data.Length));
    }

    private static IOException Failed(ulong status) => status switch
    {
        // ERROR_GEN_FAILURE, ERROR_DEVICE_NOT_CONNECTED, ERROR_OPERATION_ABORTED, USBD_STATUS_CANCELED / DEVICE_GONE
        31 or 1167 or 995 or 0xC0010000 or 0xC0007000 => new IOException(L.T("sync.usb_disconnected")),
        _ => new IOException($"usb (UsbDk): 0x{status:x}"),
    };

    public void Dispose()
    {
        if (_redirector == 0)
            return;
        UsbDk.StopRedirect(_redirector);
        _redirector = 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOverlappedResult(nint file, NativeOverlapped* overlapped, out uint transferred,
        [MarshalAs(UnmanagedType.Bool)] bool wait);
}

/// the accessory interface redirected by UsbDk as a stream (see UsbStream for
/// the WinUSB one): one read kept pending, chunked writes with a ZLP after a
/// whole-packet transfer.
internal sealed unsafe class UsbDkStream : Stream
{
    private const int Chunk = 16 * 1024;
    private const int PollMs = 500;
    private const int WriteTimeoutMs = 60_000;

    private readonly RedirectedDevice _dev;
    private readonly byte _in;
    private readonly byte _out;
    private readonly int _maxPacket;
    private readonly object _readLock = new();
    private readonly object _writeLock = new();
    private readonly byte* _rx = (byte*)NativeMemory.Alloc(Chunk);
    private readonly RedirectedDevice.Pending _read = new();
    private bool _readPending;
    private int _rxStart;
    private int _rxEnd;
    private volatile bool _closed;

    public UsbDkStream(RedirectedDevice dev, byte pin, byte pout, int maxPacket)
    {
        _dev = dev;
        _in = pin;
        _out = pout;
        _maxPacket = maxPacket;
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        lock (_readLock)
        {
            while (_rxStart == _rxEnd)
            {
                if (_closed)
                    return 0;
                try
                {
                    if (!_readPending)
                    {
                        _dev.Start(_read, true, _in, UsbDk.BulkTransfer, _rx, Chunk);
                        _readPending = true;
                    }
                    // the read stays pending across polls: nothing is lost.
                    if (_dev.Wait(_read, PollMs) is { } n)
                    {
                        _readPending = false;
                        _rxStart = 0;
                        _rxEnd = n;
                    }
                }
                catch (IOException) when (_closed)
                {
                    return 0;
                }
            }
            var take = Math.Min(buffer.Length, _rxEnd - _rxStart);
            new ReadOnlySpan<byte>(_rx + _rxStart, take).CopyTo(buffer);
            _rxStart += take;
            return take;
        }
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        new(Task.Run(() => Read(buffer.Span), ct));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        Task.Run(() => Read(buffer, offset, count), ct);

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        lock (_writeLock)
        {
            var tmp = new byte[Math.Min(buffer.Length, Chunk)];
            while (!buffer.IsEmpty)
            {
                var chunk = buffer[..Math.Min(buffer.Length, Chunk)];
                chunk.CopyTo(tmp);
                int n;
                try
                {
                    n = _dev.Transfer(false, _out, UsbDk.BulkTransfer, tmp.AsSpan(0, chunk.Length), WriteTimeoutMs);
                }
                catch (TimeoutException)
                {
                    throw new IOException(L.T("sync.usb_not_taking"));
                }
                // a transfer of whole packets has no end mark: a zero-length
                // packet ends it.
                if (n > 0 && n % _maxPacket == 0)
                    _dev.Transfer(false, _out, UsbDk.BulkTransfer, [], WriteTimeoutMs);
                buffer = buffer[Math.Max(n, 1)..];
            }
        }
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
        new(Task.Run(() => Write(buffer.Span), ct));

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        Task.Run(() => Write(buffer, offset, count), ct);

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_closed)
        {
            _closed = true;
            _dev.Abort(_in);
            _dev.Abort(_out);
            lock (_readLock)
            lock (_writeLock)
            {
                if (_readPending)
                {
                    try
                    {
                        if (_dev.Wait(_read, 2000) is not null)
                            _readPending = false;
                    }
                    catch (IOException)
                    {
                        _readPending = false;
                    }
                }
                _dev.Dispose();
                // a read the driver still holds keeps its memory (a few KiB
                // leaked) rather than risk a write into freed memory.
                if (!_readPending)
                {
                    _read.Dispose();
                    NativeMemory.Free(_rx);
                }
            }
        }
        base.Dispose(disposing);
    }
}
