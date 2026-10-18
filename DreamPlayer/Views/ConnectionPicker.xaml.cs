// The connection chooser: transport + address / port / phone / Bluetooth
// device, remembered in the settings. `Target()` validates it into a
// SyncTarget for the sync worker.

using System.Globalization;
using DreamPlayer.Services;
using DreamPlayer.Services.Sync;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DreamPlayer.Views;

public sealed partial class ConnectionPicker : UserControl
{
    private static readonly TransportKind[] Kinds = Enum.GetValues<TransportKind>();
    private bool _btLoaded;
    private bool _usbLoaded;

    public ConnectionPicker()
    {
        InitializeComponent();
        foreach (var k in Kinds)
            KindBox.Items.Add(L.T($"conn.{k}"));
        var s = AppSettings.Current;
        PortBox.Value = s.SyncPort is > 0 and <= 65535 ? s.SyncPort : Transports.DefaultPort;
        AddressBox.Text = s.SyncAddress;
        BtBox.Text = s.SyncBtDevice ?? "";
        KindBox.SelectedIndex = Math.Max(0, Array.IndexOf(Kinds, s.SyncTransport));
    }

    private TransportKind Kind => Kinds[Math.Max(0, KindBox.SelectedIndex)];

    private int Port => double.IsNaN(PortBox.Value) ? Transports.DefaultPort : (int)PortBox.Value;

    private void Sync()
    {
        var k = Kind;
        AddressBox.Visibility = Ui.Visible(k == TransportKind.TcpConnect);
        PortBox.Visibility = Ui.Visible(k is TransportKind.TcpListen or TransportKind.AdbConnect or TransportKind.AdbListen);
        UsbPanel.Visibility = Ui.Visible(k == TransportKind.UsbAndroid);
        if (k == TransportKind.UsbAndroid && !_usbLoaded)
            LoadUsb();
        BtPanel.Visibility = Ui.Visible(k == TransportKind.BtConnect);
        KindHint.Text = k switch
        {
            TransportKind.TcpListen => Transports.LocalAddresses() is { Count: > 0 } ips
                ? L.F("conn.hint_TcpListen", string.Join(", ", ips.Select(ip => $"{ip}:{Port}")))
                : L.T("conn.hint_TcpListen_noip"),
            _ => L.T($"conn.hint_{k}"),
        };
        if (k == TransportKind.BtConnect && !_btLoaded)
            _ = LoadBtAsync();
    }

    private async Task LoadBtAsync()
    {
        _btLoaded = true;
        var keep = BtBox.SelectedItem is BtDevice d ? d.Id : AppSettings.Current.SyncBtDevice;
        try
        {
            var devices = await Transports.PairedBtDevicesAsync();
            BtBox.ItemsSource = devices;
            if (devices.FirstOrDefault(x => x.Id == keep) is { } sel)
                BtBox.SelectedItem = sel;
            else if (keep is not null && Transports.ParseMac(keep) is not null)
                BtBox.Text = keep;
        }
        catch (Exception e)
        {
            CoreLog.Warn("sync", $"bluetooth devices: {e.Message}");
            ShellView.Current?.Notify(L.F("sync.bt_unavailable", e.Message), LogLevel.Warn);
        }
    }

    /// the chosen connection (saved to the settings); `null` — invalid, the
    /// reason in `error`.
    public SyncTarget? Target(out string? error)
    {
        error = null;
        var s = AppSettings.Current;
        var k = Kind;
        s.SyncTransport = k;
        s.SyncPort = Port;
        s.SyncAddress = AddressBox.Text.Trim();
        SyncTarget? target = null;
        switch (k)
        {
            case TransportKind.TcpConnect when s.SyncAddress.Length == 0:
                error = L.T("sync.no_address");
                break;
            case TransportKind.UsbAndroid:
                s.SyncUsbDevice = (UsbBox.SelectedItem as UsbChoice)?.Ids ?? "";
                target = new SyncTarget(k, UsbFilter: ParseVidPid(s.SyncUsbDevice));
                break;
            case TransportKind.BtConnect:
                var dev = BtBox.SelectedItem is BtDevice d ? d.Id : BtBox.Text?.Trim() ?? "";
                if (dev.Length == 0)
                {
                    error = L.T("sync.bt_no_device");
                    break;
                }
                s.SyncBtDevice = dev;
                target = new SyncTarget(k, BtDevice: dev);
                break;
            default:
                target = new SyncTarget(k, Port, s.SyncAddress);
                break;
        }
        AppSettings.Save();
        return target;
    }

    /// `18d1:4ee7` -> (0x18d1, 0x4ee7).
    private static (ushort, ushort)? ParseVidPid(string s)
    {
        var parts = s.Split(':');
        return parts.Length == 2
            && ushort.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)
            && ushort.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var p)
                ? (v, p)
                : null;
    }

    private void KindBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => Sync();

    private void PortBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (Kind == TransportKind.TcpListen)
            Sync();
    }

    private async void BtRefresh_Click(object sender, RoutedEventArgs e) => await LoadBtAsync();

    private void UsbRefresh_Click(object sender, RoutedEventArgs e) => LoadUsb();

    /// an entry of the phone list: a connected device, "automatically" (empty
    /// ids) or the remembered phone while it is unplugged.
    private sealed record UsbChoice(string Ids, string Text)
    {
        public override string ToString() => Text;
    }

    /// the connected USB devices (the phone is chosen explicitly; the
    /// remembered one stays selected).
    private void LoadUsb()
    {
        _usbLoaded = true;
        var keep = (UsbBox.SelectedItem as UsbChoice)?.Ids ?? AppSettings.Current.SyncUsbDevice;
        var items = new List<UsbChoice> { new("", L.T("conn.usb_auto")) };
        items.AddRange(UsbAccessory.Candidates().Select(c => new UsbChoice(c.Ids, c.ToString())));
        if (keep.Length > 0 && items.All(i => !string.Equals(i.Ids, keep, StringComparison.OrdinalIgnoreCase)))
            items.Add(new UsbChoice(keep, L.F("conn.usb_unplugged", keep)));
        UsbBox.ItemsSource = items;
        UsbBox.SelectedItem = items.FirstOrDefault(i => string.Equals(i.Ids, keep, StringComparison.OrdinalIgnoreCase)) ?? items[0];
    }
}
