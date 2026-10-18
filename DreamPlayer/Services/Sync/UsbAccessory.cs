// USB transport without adb / debug mode: Android Open Accessory (AOA), ported
// from the TUI `tui/src/sync/usb.rs` (libusb there, WinUSB directly here).
// This side is the USB host: it finds the phone, asks it to restart as an
// accessory of ours (the phone then offers to open Dream Player) and talks to
// the accessory interface over two bulk endpoints. The phone is the listening
// side (the host of the sync session).
//
// AOA v1: vendor control requests 51 (protocol version), 52 (identity
// strings), 53 (start); the phone re-enumerates as Google 18d1:2d00 (2d01 —
// with adb). Windows lets user mode send USB requests only to a device on the
// WinUSB driver; a phone normally sits on the MTP driver. So (as libusb):
// - a device / interface on WinUSB (e.g. adb with the Google driver, an
//   accessory set up with Zadig) is used directly;
// - anything else goes through UsbDk (see UsbDk.cs): the phone is taken from
//   the MTP driver just for the switch, the accessory — for the session.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace DreamPlayer.Services.Sync;

internal static partial class UsbAccessory
{
    // the identity the phone app's accessory filter matches.
    private static readonly string[] Identity =
    [
        "DreamPlayer",                                    // manufacturer
        "DreamPlayerSync",                                // model
        "Dream Player library sync",                      // description
        "1",                                              // version
        "https://github.com/ksimilikon/rs_dream_player", // URI
        "",                                               // serial
    ];

    private const ushort GoogleVid = 0x18d1;
    /// accessory; accessory + adb.
    private static readonly ushort[] AccessoryPids = [0x2d00, 0x2d01];

    private const byte ReqGetProtocol = 51;
    private const byte ReqSendString = 52;
    private const byte ReqStart = 53;

    /// how long the phone takes to come back as an accessory and Windows to
    /// set it up (a new device: the first time Windows may look for a driver).
    private static readonly TimeSpan Reenumerate = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(200);

    /// a USB device node (a whole device or one interface `MI_xx`).
    private sealed record UsbNode(string InstanceId, ushort Vid, ushort Pid, string? Service, string? Path,
        string? Name = null, string? Class = null, uint Problem = 0)
    {
        /// one interface (`MI_xx`) of a composite device.
        public bool IsInterface => InstanceId.Contains("&MI_", StringComparison.OrdinalIgnoreCase);
        public bool WinUsb => string.Equals(Service, "WinUSB", StringComparison.OrdinalIgnoreCase) && Path is not null;
        public bool IsAccessory => Vid == GoogleVid && AccessoryPids.Contains(Pid);
        public string Ids => $"{Vid:x4}:{Pid:x4}";
        /// Windows has no driver for it (CM_PROB_FAILED_INSTALL / NOT_CONFIGURED).
        /// UsbDk can't take such a device: its start waits forever.
        public bool NoDriver => Problem is 28 or 1;
    }

    private const uint DnHasProblem = 0x400;

    /// the device node's problem code (0 — none).
    private static uint ProblemCode(uint devInst) =>
        CM_Get_DevNode_Status(out var status, out var problem, devInst, 0) == 0 && (status & DnHasProblem) != 0
            ? problem
            : 0;

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);

    /// a connected USB device the user can pick as the phone.
    public sealed record Candidate(ushort Vid, ushort Pid, string Name)
    {
        public string Ids => $"{Vid:x4}:{Pid:x4}";
        public override string ToString() => $"{Name} ({Ids})";
    }

    /// connected USB devices that may be a phone: whole devices (not
    /// interfaces), no hubs, no input devices.
    public static IReadOnlyList<Candidate> Candidates()
    {
        List<UsbNode> nodes;
        try
        {
            nodes = Enumerate();
        }
        catch (IOException e)
        {
            CoreLog.Warn("usb", e.Message);
            return [];
        }
        bool Input(UsbNode n) => string.Equals(n.Class, "HIDClass", StringComparison.OrdinalIgnoreCase)
            || string.Equals(n.Class, "Keyboard", StringComparison.OrdinalIgnoreCase)
            || string.Equals(n.Class, "Mouse", StringComparison.OrdinalIgnoreCase);
        return nodes
            .Where(n => !n.IsInterface && !n.IsAccessory
                && !(n.Service ?? "").StartsWith("USBHUB", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(n.Service, "usbhub", StringComparison.OrdinalIgnoreCase)
                && !Input(n)
                // a composite device made only of input interfaces (keyboards, mice).
                && !(string.Equals(n.Service, "usbccgp", StringComparison.OrdinalIgnoreCase)
                     && nodes.Where(c => c.IsInterface && c.Vid == n.Vid && c.Pid == n.Pid).All(Input)))
            .Select(n => new Candidate(n.Vid, n.Pid, n.Name ?? n.Ids))
            .DistinctBy(c => (c.Vid, c.Pid))
            .ToList();
    }

    public static Task<SyncLink> ConnectAsync((ushort Vid, ushort Pid)? filter, Action<string> info, CancellationToken ct) =>
        Task.Run(() => Connect(filter, info, ct), ct);

    private static SyncLink Connect((ushort Vid, ushort Pid)? filter, Action<string> info, CancellationToken ct)
    {
        if (TryOpenAccessory(final: true) is { } ready)
        {
            info(L.T("sync.usb_already"));
            return ready;
        }
        SwitchPhone(filter, info);
        info(L.T("sync.usb_confirm"));
        var started = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Thread.Sleep(Poll);
            // right after re-enumeration Windows may still be setting the
            // device up: failures are retried until the deadline.
            var final = started.Elapsed > Reenumerate;
            if (TryOpenAccessory(final) is { } link)
            {
                info(L.T("sync.usb_accessory_open"));
                return link;
            }
            if (final)
                throw new IOException(L.T("sync.usb_no_return"));
        }
    }

    /// opens the accessory if it is attached: over WinUSB if it has that
    /// driver, else through UsbDk. `null` — not attached (or not openable yet
    /// unless `final`).
    private static SyncLink? TryOpenAccessory(bool final)
    {
        try
        {
            var node = Enumerate().FirstOrDefault(n => n.IsAccessory);
            if (node is { WinUsb: true })
            {
                CoreLog.Info("usb", $"accessory {node.Ids} on WinUSB: opening");
                return Open(node);
            }
            if (node is { NoDriver: true })
            {
                // a known dead end: UsbDk would hang on it. Says so at once.
                CoreLog.Warn("usb", $"accessory {node.Ids} has no Windows driver (problem {node.Problem})");
                throw new AccessoryDriverException(node.Ids);
            }
            if (UsbDk.Available && UsbDk.Devices().FirstOrDefault(d => d.Vid == GoogleVid && AccessoryPids.Contains(d.Pid)) is { } dk)
            {
                CoreLog.Info("usb", $"accessory {dk.Ids} (Windows driver: {node?.Service ?? "none"}): opening via UsbDk");
                return OpenDk(dk);
            }
            if (node is not null && final)
                throw new IOException(L.F("sync.usb_accessory_driver", node.Ids, node.Service ?? "—"));
            return null;
        }
        catch (IOException e) when (!final && e is not AccessoryDriverException)
        {
            CoreLog.Debug("usb", $"accessory not ready: {e.Message}");
            return null;
        }
    }

    /// asks the phone to restart as our accessory: through a WinUSB
    /// interface of it if there is one (e.g. adb with the Google driver),
    /// otherwise by taking it from the MTP driver for a moment with UsbDk.
    private static void SwitchPhone((ushort Vid, ushort Pid)? filter, Action<string> info)
    {
        bool Wanted(ushort vid, ushort pid) => filter is not { } f || (vid == f.Vid && pid == f.Pid);

        var nodes = Enumerate();
        foreach (var node in nodes.Where(n => n.WinUsb && !n.IsAccessory && Wanted(n.Vid, n.Pid)))
        {
            try
            {
                using var dev = WinUsbDevice.Open(node.Path!);
                if (dev.Protocol() < 1)
                    continue;
                info(L.F("sync.usb_switching", node.Ids));
                for (var i = 0; i < Identity.Length; i++)
                    dev.ControlOut(ReqSendString, 0, (ushort)i, Encoding.UTF8.GetBytes(Identity[i] + "\0"));
                dev.ControlOut(ReqStart, 0, 0, []);
                return;
            }
            catch (IOException e)
            {
                CoreLog.Debug("usb", $"{node.InstanceId}: {e.Message}");
            }
        }

        // a phone on its MTP driver: only UsbDk can reach it.
        var phones = nodes.Where(n => !n.IsAccessory && !n.WinUsb && Wanted(n.Vid, n.Pid)
            && string.Equals(n.Service, "WUDFWpdMtp", StringComparison.OrdinalIgnoreCase)).ToList();
        if (!UsbDk.Available)
        {
            if (filter is { } want && !nodes.Any(n => Wanted(n.Vid, n.Pid)))
                throw new IOException(L.F("sync.usb_no_device", $"{want.Vid:x4}:{want.Pid:x4}"));
            throw new IOException(phones.Count > 0
                ? L.F("sync.usb_need_usbdk", string.Join(", ", phones.Select(p => p.Ids)))
                : L.T("sync.usb_no_phone"));
        }
        var tried = 0;
        string? lastError = null;
        foreach (var d in UsbDk.Devices().Where(d => Wanted(d.Vid, d.Pid) && !(d.Vid == GoogleVid && AccessoryPids.Contains(d.Pid))))
        {
            if (d.Class == 0x09 || UsbDk.ConfigDescriptor(d) is not { } config)
                continue; // hubs
            var classes = UsbDk.InterfaceClasses(config).ToHashSet();
            // a phone: MTP / vendor interfaces; never take an input device
            // away (a keyboard with a vendor interface) unless asked by id.
            if (filter is null && (!(classes.Contains(0x06) || classes.Contains(0xff)) || classes.Contains(0x03)))
                continue;
            tried++;
            info(L.F("sync.usbdk_trying", d.Ids));
            try
            {
                using var dev = UsbDk.Redirect(d);
                Span<byte> version = stackalloc byte[2];
                if (dev.Control(0xC0, ReqGetProtocol, 0, 0, version) < 2 || BitConverter.ToUInt16(version) < 1)
                {
                    lastError = L.F("sync.usb_no_aoa", d.Ids);
                    continue;
                }
                CoreLog.Info("usb", $"{d.Ids}: AOA protocol v{BitConverter.ToUInt16(version)}");
                info(L.F("sync.usb_switching", d.Ids));
                for (var i = 0; i < Identity.Length; i++)
                    dev.Control(0x40, ReqSendString, 0, (ushort)i, Encoding.UTF8.GetBytes(Identity[i] + "\0"));
                dev.Control(0x40, ReqStart, 0, 0, []);
                return;
            }
            catch (Exception e) when (e is IOException or TimeoutException)
            {
                CoreLog.Warn("usb", $"{d.InstanceId}: {e.Message}");
                lastError = L.F("sync.usb_switch_failed", d.Ids, e is TimeoutException ? L.T("sync.usb_timeout") : e.Message);
            }
        }
        if (filter is { } id && tried == 0)
            throw new IOException(L.F("sync.usb_no_device", $"{id.Vid:x4}:{id.Pid:x4}"));
        // a phone was found but didn't switch: say why, not "no phone".
        throw new IOException(lastError ?? L.T("sync.usb_no_phone"));
    }

    /// the accessory through UsbDk: taken from Windows for the session.
    private static SyncLink OpenDk(UsbDk.Device acc)
    {
        var config = UsbDk.ConfigDescriptor(acc) ?? throw new IOException(L.T("sync.usb_no_endpoints"));
        var (pin, pout, max) = UsbDk.BulkEndpoints(config) ?? throw new IOException(L.T("sync.usb_no_endpoints"));
        var dev = UsbDk.Redirect(acc);
        return new SyncLink { Stream = new UsbDkStream(dev, pin, pout, max), Host = false };
    }

    private static SyncLink Open(UsbNode acc)
    {
        var dev = WinUsbDevice.Open(acc.Path!);
        try
        {
            return new SyncLink { Stream = new UsbStream(dev), Host = false };
        }
        catch
        {
            dev.Dispose();
            throw;
        }
    }

    // ───────────────────────── enumeration (SetupAPI) ─────────────────────────

    private const uint DigcfPresent = 0x02;
    private const uint DigcfAllClasses = 0x04;
    private const uint SpdrpDeviceDesc = 0x00;
    private const uint SpdrpService = 0x04;
    private const uint SpdrpClass = 0x07;
    private const uint SpdrpFriendlyName = 0x0C;
    private const uint DicsFlagGlobal = 0x01;
    private const uint DiregDev = 0x01;
    private const uint KeyRead = 0x20019;
    private static readonly Guid GuidUsbDevice = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");

    [GeneratedRegex(@"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex VidPid();

    /// every present USB device node with its driver service and (for
    /// WinUSB) the device interface path.
    private static unsafe List<UsbNode> Enumerate()
    {
        var result = new List<UsbNode>();
        var set = SetupDiGetClassDevsW(null, "USB", 0, DigcfPresent | DigcfAllClasses);
        if (set == -1)
            throw new IOException($"usb: SetupDiGetClassDevs failed ({Marshal.GetLastPInvokeError()})");
        try
        {
            var data = new SpDevinfoData { CbSize = (uint)sizeof(SpDevinfoData) };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                var id = InstanceId(set, ref data);
                if (id is null || VidPid().Match(id) is not { Success: true } m)
                    continue;
                var vid = ushort.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var pid = ushort.Parse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var service = StringProperty(set, ref data, SpdrpService);
                var path = string.Equals(service, "WinUSB", StringComparison.OrdinalIgnoreCase)
                    ? InterfacePath(set, ref data, id)
                    : null;
                var name = StringProperty(set, ref data, SpdrpFriendlyName) ?? StringProperty(set, ref data, SpdrpDeviceDesc);
                result.Add(new UsbNode(id, vid, pid, service, path, name, StringProperty(set, ref data, SpdrpClass), ProblemCode(data.DevInst)));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return result;
    }

    private static unsafe string? InstanceId(nint set, ref SpDevinfoData data)
    {
        var buf = stackalloc char[512];
        return SetupDiGetDeviceInstanceIdW(set, ref data, buf, 512, out _) ? new string(buf) : null;
    }

    private static unsafe string? StringProperty(nint set, ref SpDevinfoData data, uint prop)
    {
        var buf = stackalloc byte[1024];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, prop, out _, buf, 1024, out var len))
            return null;
        return new string((char*)buf, 0, (int)Math.Max(0, len / 2 - 1)).TrimEnd('\0');
    }

    /// the device interface path of a WinUSB node: its interface GUIDs from
    /// the driver key, else the generic USB device interface.
    private static unsafe string? InterfacePath(nint set, ref SpDevinfoData data, string instanceId)
    {
        var guids = new List<Guid>();
        var key = SetupDiOpenDevRegKey(set, ref data, DicsFlagGlobal, 0, DiregDev, KeyRead);
        if (key != -1)
        {
            using var reg = RegistryKey.FromHandle(new SafeRegistryHandle(key, ownsHandle: true));
            var values = reg.GetValue("DeviceInterfaceGUIDs") as string[]
                ?? (reg.GetValue("DeviceInterfaceGUID") is string one ? [one] : []);
            foreach (var v in values)
            {
                if (Guid.TryParse(v, out var g))
                    guids.Add(g);
            }
        }
        guids.Add(GuidUsbDevice);
        foreach (var g in guids)
        {
            var guid = g;
            if (CM_Get_Device_Interface_List_SizeW(out var len, ref guid, instanceId, 0) != 0 || len <= 1)
                continue;
            var buf = new char[len];
            fixed (char* p = buf)
            {
                if (CM_Get_Device_Interface_ListW(ref guid, instanceId, p, len, 0) != 0)
                    continue;
            }
            var first = new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (first is not null)
                return first;
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevinfoData
    {
        public uint CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public nint Reserved;
    }

    [LibraryImport("setupapi.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial nint SetupDiGetClassDevsW(Guid* classGuid, string? enumerator, nint parent, uint flags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiEnumDeviceInfo(nint set, uint index, ref SpDevinfoData data);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetupDiGetDeviceInstanceIdW(nint set, ref SpDevinfoData data, char* buf, uint size, out uint required);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetupDiGetDeviceRegistryPropertyW(nint set, ref SpDevinfoData data, uint property,
        out uint regType, byte* buf, uint size, out uint required);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    private static partial nint SetupDiOpenDevRegKey(nint set, ref SpDevinfoData data, uint scope, uint profile, uint keyType, uint access);

    [LibraryImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDeviceInfoList(nint set);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_Interface_List_SizeW(out uint len, ref Guid guid, string deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint CM_Get_Device_Interface_ListW(ref Guid guid, string deviceId, char* buf, uint len, uint flags);
}

/// one WinUSB interface opened: control transfers and the bulk pipes.
internal sealed partial class WinUsbDevice : IDisposable
{
    private const uint ErrorSemTimeout = 121;
    private const uint PipeTransferTimeout = 0x03;
    private const uint ControlTimeoutMs = 1000;

    private readonly SafeFileHandle _file;
    private nint _usb;

    private WinUsbDevice(SafeFileHandle file, nint usb)
    {
        _file = file;
        _usb = usb;
    }

    public static WinUsbDevice Open(string path)
    {
        const uint genericRw = 0x80000000 | 0x40000000;
        const uint shareRw = 0x1 | 0x2;
        const uint openExisting = 3;
        const uint flags = 0x80 | 0x40000000; // normal | overlapped (required by WinUSB)
        var file = CreateFileW(path, genericRw, shareRw, 0, openExisting, flags, 0);
        if (file.IsInvalid)
            throw Error("CreateFile");
        if (!WinUsb_Initialize(file, out var usb))
        {
            var e = Error("WinUsb_Initialize");
            file.Dispose();
            throw e;
        }
        var dev = new WinUsbDevice(file, usb);
        dev.SetTimeout(0, ControlTimeoutMs);
        return dev;
    }

    private nint Usb => _usb != 0 ? _usb : throw new ObjectDisposedException(nameof(WinUsbDevice));

    /// the AOA protocol version (0 — not supported).
    public unsafe ushort Protocol()
    {
        var setup = new SetupPacket { RequestType = 0xC0, Request = 51, Length = 2 };
        ushort v = 0;
        if (!WinUsb_ControlTransfer(Usb, setup, (byte*)&v, 2, out var n, 0))
            throw Error("control 51");
        return n == 2 ? v : (ushort)0;
    }

    /// a vendor request host -> device.
    public unsafe void ControlOut(byte request, ushort value, ushort index, byte[] data)
    {
        var setup = new SetupPacket
        {
            RequestType = 0x40,
            Request = request,
            Value = value,
            Index = index,
            Length = (ushort)data.Length,
        };
        fixed (byte* p = data)
        {
            if (!WinUsb_ControlTransfer(Usb, setup, p, (uint)data.Length, out _, 0))
                throw Error($"control {request}");
        }
    }

    /// the bulk in / out pipes of the interface and the packet size.
    public (byte In, byte Out, int MaxPacket) BulkPipes()
    {
        if (!WinUsb_QueryInterfaceSettings(Usb, 0, out var iface))
            throw Error("WinUsb_QueryInterfaceSettings");
        byte? pin = null, pout = null;
        var max = 512;
        for (byte i = 0; i < iface.NumEndpoints; i++)
        {
            if (!WinUsb_QueryPipe(Usb, 0, i, out var pipe) || pipe.PipeType != 2 /* bulk */)
                continue;
            max = Math.Max(1, (int)pipe.MaximumPacketSize);
            if ((pipe.PipeId & 0x80) != 0)
                pin = pipe.PipeId;
            else
                pout = pipe.PipeId;
        }
        if (pin is null || pout is null)
            throw new IOException(L.T("sync.usb_no_endpoints"));
        return (pin.Value, pout.Value, max);
    }

    public void SetTimeout(byte pipe, uint ms)
    {
        var v = ms;
        WinUsb_SetPipePolicy(Usb, pipe, PipeTransferTimeout, 4, ref v);
    }

    /// `null` — the pipe timed out.
    public unsafe int? Read(byte pipe, Span<byte> buf)
    {
        fixed (byte* p = buf)
        {
            if (WinUsb_ReadPipe(Usb, pipe, p, (uint)buf.Length, out var n, 0))
                return (int)n;
        }
        if ((uint)Marshal.GetLastPInvokeError() == ErrorSemTimeout)
            return null;
        throw Error("read");
    }

    /// `null` — the pipe timed out.
    public unsafe int? Write(byte pipe, ReadOnlySpan<byte> buf)
    {
        fixed (byte* p = buf)
        {
            if (WinUsb_WritePipe(Usb, pipe, p, (uint)buf.Length, out var n, 0))
                return (int)n;
        }
        if ((uint)Marshal.GetLastPInvokeError() == ErrorSemTimeout)
            return null;
        throw Error("write");
    }

    /// cancels pending transfers on the pipe (unblocks a read).
    public void Abort(byte pipe)
    {
        if (_usb != 0)
            WinUsb_AbortPipe(_usb, pipe);
    }

    public void Dispose()
    {
        if (_usb != 0)
        {
            WinUsb_Free(_usb);
            _usb = 0;
        }
        _file.Dispose();
    }

    private static IOException Error(string what)
    {
        var code = Marshal.GetLastPInvokeError();
        return code switch
        {
            5 => new IOException(L.T("sync.usb_access")),
            // ERROR_BAD_COMMAND, ERROR_GEN_FAILURE, ERROR_DEVICE_NOT_CONNECTED, ERROR_OPERATION_ABORTED
            22 or 31 or 995 or 1167 => new IOException(L.T("sync.usb_disconnected")),
            _ => new IOException($"usb: {what}: {new System.ComponentModel.Win32Exception(code).Message} ({code})"),
        };
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct SetupPacket
    {
        public byte RequestType;
        public byte Request;
        public ushort Value;
        public ushort Index;
        public ushort Length;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct InterfaceDescriptor
    {
        public byte Length;
        public byte DescriptorType;
        public byte InterfaceNumber;
        public byte AlternateSetting;
        public byte NumEndpoints;
        public byte InterfaceClass;
        public byte InterfaceSubClass;
        public byte InterfaceProtocol;
        public byte Interface;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PipeInformation
    {
        public int PipeType;
        public byte PipeId;
        public ushort MaximumPacketSize;
        public byte Interval;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string path, uint access, uint share, nint security,
        uint disposition, uint flags, nint template);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinUsb_Initialize(SafeFileHandle file, out nint usb);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinUsb_Free(nint usb);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool WinUsb_ControlTransfer(nint usb, SetupPacket setup, byte* buf, uint len,
        out uint transferred, nint overlapped);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinUsb_QueryInterfaceSettings(nint usb, byte alt, out InterfaceDescriptor desc);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinUsb_QueryPipe(nint usb, byte alt, byte index, out PipeInformation pipe);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinUsb_SetPipePolicy(nint usb, byte pipe, uint policy, uint len, ref uint value);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool WinUsb_ReadPipe(nint usb, byte pipe, byte* buf, uint len, out uint transferred, nint overlapped);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool WinUsb_WritePipe(nint usb, byte pipe, byte* buf, uint len, out uint transferred, nint overlapped);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinUsb_AbortPipe(nint usb, byte pipe);
}

/// the accessory interface as a stream: blocking reads (the worker reads on
/// its own task), chunked writes ending a whole-packet transfer with a
/// zero-length packet.
internal sealed class UsbStream : Stream
{
    /// one bulk transfer (the phone reads the accessory in 16 KiB requests).
    private const int Chunk = 16 * 1024;
    /// a read wakes up this often to notice the stream was closed.
    private const uint ReadTimeoutMs = 500;
    /// the phone app may still be waiting for the user to start the session.
    private const uint WriteTimeoutMs = 60_000;

    private readonly WinUsbDevice _dev;
    private readonly byte _in;
    private readonly byte _out;
    private readonly int _maxPacket;
    private readonly object _readLock = new();
    private readonly object _writeLock = new();
    private readonly byte[] _rx = new byte[Chunk];
    private int _rxStart;
    private int _rxEnd;
    private volatile bool _closed;

    public UsbStream(WinUsbDevice dev)
    {
        _dev = dev;
        (_in, _out, _maxPacket) = dev.BulkPipes();
        dev.SetTimeout(_in, ReadTimeoutMs);
        dev.SetTimeout(_out, WriteTimeoutMs);
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
                // whole packets only: a smaller request could overflow.
                int? n;
                try
                {
                    n = _dev.Read(_in, _rx);
                }
                catch (IOException) when (_closed)
                {
                    return 0;
                }
                if (n is { } got)
                {
                    _rxStart = 0;
                    _rxEnd = got;
                }
            }
            var take = Math.Min(buffer.Length, _rxEnd - _rxStart);
            _rx.AsSpan(_rxStart, take).CopyTo(buffer);
            _rxStart += take;
            return take;
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        Task.Run(() => Read(buffer, offset, count), ct);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        new(Task.Run(() => Read(buffer.Span), ct));

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        lock (_writeLock)
        {
            while (!buffer.IsEmpty)
            {
                var chunk = buffer[..Math.Min(buffer.Length, Chunk)];
                var n = _dev.Write(_out, chunk) ?? throw new IOException(L.T("sync.usb_not_taking"));
                // a transfer of whole packets has no end mark: the phone would
                // wait for more. A zero-length packet ends it.
                if (n > 0 && n % _maxPacket == 0)
                    _dev.Write(_out, []);
                buffer = buffer[n..];
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
            // a running read / write ends within its timeout.
            lock (_readLock)
            lock (_writeLock)
                _dev.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// the accessory came up without a Windows driver: neither WinUSB nor UsbDk
/// can open it until it gets one (WinUSB, once).
internal sealed class AccessoryDriverException(string ids) : IOException(L.F("sync.usb_accessory_nodriver", ids))
{
    public string Ids { get; } = ids;
}
