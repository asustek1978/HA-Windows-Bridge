using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace HAWindowsBridge;

internal static class NetworkMetrics
{
    private static readonly Dictionary<string, Metric> Known = new();
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
        int start = result.Count;
        NetworkInterface[] adapters;
        try { adapters = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { return; }

        var wlan = adapters.Where(a => a.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            .ToArray();

        var gateway = adapters.Where(a => a.OperationalStatus == OperationalStatus.Up)
            .SelectMany(a => a.GetIPProperties().GatewayAddresses)
            .Select(g => g.Address)
            .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork
                && !IPAddress.IsLoopback(ip));
        if (gateway is not null)
        {
            result.Add(new("gateway_ip", "Шлюз сети", "sensor", gateway.ToString(),
                "mdi:router-network", Category: "diagnostic"));
            AddProbe(result, gateway, "gateway", "Роутер", "mdi:router-network");
        }
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

        var vpn = adapters.Where(a => IsVpn(a)
            && a.OperationalStatus == OperationalStatus.Up
            && a.GetIPProperties().UnicastAddresses.Any(ip =>
                ip.Address.AddressFamily == AddressFamily.InterNetwork))
            .OrderByDescending(a => a.Name.Contains("home-gateway-ru",
                StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
        result.Add(new("vpn_interface", "VPN-интерфейс активен", "binary_sensor",
            vpn is not null, "mdi:vpn"));
        if (vpn is not null)
            result.Add(new("vpn_name", "VPN-интерфейс", "sensor", vpn.Name, "mdi:vpn"));
        if (IPAddress.TryParse(vpnTestHost, out var testAddress)
            && testAddress.AddressFamily == AddressFamily.InterNetwork)
        {
            if (vpn is not null)
                AddProbe(result, testAddress, "vpn_peer", "VPN-узел", "mdi:lan-check");
            else
                result.Add(new("vpn_peer_reachable", "VPN-узел отвечает", "binary_sensor",
                    false, "mdi:lan-check"));
        }
        if (vpn is not null) AddWireGuardHandshake(result, vpn.Name);
        var sampled = result.Skip(start).Select(m => m.Id).ToHashSet();
        foreach (var metric in result.Skip(start)) Known[metric.Id] = metric;
        foreach (var metric in Known.Values)
            if (!sampled.Contains(metric.Id)) result.Add(metric with { State = "unknown" });
    }

    private static void AddProbe(List<Metric> result, IPAddress address, string id,
        string name, string icon)
    {
        long? milliseconds = null;
        try
        {
            using var ping = new Ping();
            var response = ping.Send(address, 900);
            if (response.Status == IPStatus.Success) milliseconds = response.RoundtripTime;
        }
        catch (Exception) { /* Нет маршрута или ICMP запрещён. */ }
        result.Add(new(id + "_reachable", name + " отвечает", "binary_sensor",
            milliseconds.HasValue, icon, Category: "diagnostic"));
        if (milliseconds.HasValue)
            result.Add(new(id + "_latency", name + " — задержка", "sensor",
                milliseconds.Value, "mdi:timer-outline", "ms", null, "measurement",
                "diagnostic"));
    }

    private static void AddWireGuardHandshake(List<Metric> result, string interfaceName)
    {
        // wg.exe устанавливается вместе с WireGuard. Без него показание просто отсутствует.
        string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "WireGuard", "wg.exe");
        if (!File.Exists(exe)) return;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe)
            {
                Arguments = "show all latest-handshakes",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process is null) return;
            if (!process.WaitForExit(1500))
            {
                process.Kill(true);
                return;
            }
            if (process.ExitCode != 0) return;
            string output = process.StandardOutput.ReadToEnd();
            if (output.Length > 65536) return;
            long? timestamp = ParseHandshake(output, interfaceName);
            if (!timestamp.HasValue) return;
            var last = DateTimeOffset.FromUnixTimeSeconds(timestamp.Value);
            result.Add(new("vpn_last_handshake", "Последний handshake WireGuard", "sensor",
                last.ToString("O"), "mdi:vpn", null, "timestamp", null, "diagnostic"));
            result.Add(new("vpn_handshake_age", "После handshake WireGuard", "sensor",
                Math.Round(Math.Max(0, (DateTimeOffset.UtcNow - last).TotalMinutes), 1),
                "mdi:timer-outline", "min", "duration", "measurement", "diagnostic"));
        }
        catch (Exception) { /* Нет доступа к службе туннеля или wg.exe. */ }
    }

    internal static long? ParseHandshake(string output, string interfaceName)
    {
        long latest = 0;
        foreach (string line in output.Split('\n'))
        {
            string[] fields = line.Trim().Split('\t');
            if (fields.Length != 3 || !fields[0].Equals(interfaceName,
                    StringComparison.OrdinalIgnoreCase)
                || !long.TryParse(fields[2], out long unixSeconds)) continue;
            if (unixSeconds > latest && unixSeconds <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 120)
                latest = unixSeconds;
        }
        return latest > 0 ? latest : null;
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
