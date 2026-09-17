using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace HAWindowsBridge;

internal static class NetworkMetrics
{
    public static bool IsVpn(NetworkInterface adapter)
    {
        if (adapter.NetworkInterfaceType is NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel)
            return true;
        string text = adapter.Name + " " + adapter.Description;
        return new[] { "WireGuard", "Wintun", "OpenVPN", "TAP-Windows", "Tailscale",
            "ZeroTier", "Amnezia", "home-gateway-ru" }
            .Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    public static void Sample(List<Metric> result, string vpnTestHost)
    {
        NetworkInterface[] adapters;
        try { adapters = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { return; }

        var wlan = adapters.Where(a => a.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            .ToArray();
        if (wlan.Length > 0)
        {
            var connected = wlan.FirstOrDefault(a => a.OperationalStatus == OperationalStatus.Up
                && a.GetIPProperties().UnicastAddresses.Any(ip =>
                    ip.Address.AddressFamily == AddressFamily.InterNetwork));
            result.Add(new("wifi_connected", "Wi-Fi подключён", "binary_sensor",
                connected is not null, "mdi:wifi"));
            if (connected is not null && TryGetWifi(connected, out string ssid, out uint quality))
            {
                if (ssid.Length > 0)
                    result.Add(new("wifi_ssid", "Сеть Wi-Fi", "sensor", ssid, "mdi:wifi"));
                result.Add(new("wifi_signal", "Сигнал Wi-Fi", "sensor", quality,
                    "mdi:wifi-strength-3", "%", null, "measurement"));
            }
        }

        var vpn = adapters.FirstOrDefault(a => IsVpn(a)
            && a.OperationalStatus == OperationalStatus.Up
            && a.GetIPProperties().UnicastAddresses.Any(ip =>
                ip.Address.AddressFamily == AddressFamily.InterNetwork));
        result.Add(new("vpn_interface", "VPN-интерфейс активен", "binary_sensor",
            vpn is not null, "mdi:vpn"));
        if (vpn is not null)
            result.Add(new("vpn_name", "VPN-интерфейс", "sensor", vpn.Name, "mdi:vpn"));
        if (IPAddress.TryParse(vpnTestHost, out var testAddress)
            && testAddress.AddressFamily == AddressFamily.InterNetwork)
        {
            bool reachable = false;
            if (vpn is not null)
                try
                {
                    using var ping = new Ping();
                    reachable = ping.Send(testAddress, 1000).Status == IPStatus.Success;
                }
                catch (Exception) { /* ICMP может быть запрещён или сеть недоступна. */ }
            result.Add(new("vpn_peer_reachable", "VPN-узел отвечает", "binary_sensor",
                reachable, "mdi:lan-check"));
        }

        // Состояние интерфейса не подтверждает успешный WireGuard handshake.
    }

    private static bool TryGetWifi(NetworkInterface adapter, out string ssid, out uint quality)
    {
        ssid = "";
        quality = 0;
        if (!Guid.TryParse(adapter.Id, out Guid guid)) return false;
        IntPtr handle = IntPtr.Zero, data = IntPtr.Zero;
        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out handle) != 0) return false;
            if (WlanQueryInterface(handle, ref guid, 7, IntPtr.Zero, out uint size,
                    out data, out _) == 0 && data != IntPtr.Zero
                && size >= 520 + Marshal.SizeOf<AssociationAttributes>()
                && Marshal.ReadInt32(data) == 1)
            {
                // WLAN_CONNECTION_ATTRIBUTES: state + mode + 256 WCHAR, затем association.
                var association = Marshal.PtrToStructure<AssociationAttributes>(IntPtr.Add(data, 520));
                quality = Math.Min(association.SignalQuality, 100U);
                int count = (int)Math.Min((uint)association.Ssid.Length, association.SsidLength);
                ssid = Encoding.UTF8.GetString(association.Ssid, 0, count);
                return true;
            }
            if (data != IntPtr.Zero) { WlanFreeMemory(data); data = IntPtr.Zero; }
            // В Windows 11 качество связи доступно и без разрешения на местоположение.
            if (WlanQueryInterface(handle, ref guid, 19, IntPtr.Zero, out size,
                    out data, out _) == 0 && data != IntPtr.Zero && size >= 8)
            {
                quality = Math.Min((uint)Marshal.ReadInt32(data, 4), 100U);
                return true;
            }
            return false;
        }
        catch (Exception) { return false; } // Нет доступа к местоположению или службы WLAN.
        finally
        {
            if (data != IntPtr.Zero) WlanFreeMemory(data);
            if (handle != IntPtr.Zero) WlanCloseHandle(handle, IntPtr.Zero);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AssociationAttributes
    {
        public uint SsidLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] Ssid;
        public int BssType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
        public byte[] Bssid;
        public int PhyType;
        public uint PhyIndex;
        public uint SignalQuality;
        public uint ReceiveRate;
        public uint TransmitRate;
    }

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved,
        out uint negotiatedVersion, out IntPtr handle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(IntPtr handle, ref Guid interfaceGuid,
        int opcode, IntPtr reserved, out uint dataSize, out IntPtr data,
        out int opcodeValueType);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);
}
