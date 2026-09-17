using System.Windows.Forms;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace HAWindowsBridge;

internal sealed record Metric(string Id, string Name, string Type, object State,
    string Icon, string? Unit = null, string? DeviceClass = null, string? StateClass = null);

internal sealed class Metrics
{
    private ulong _lastIdle, _lastTotal;
    private long _lastRx, _lastTx;
    private DateTimeOffset _lastNetworkAt;
    private string _lastAdapter = "";

    public bool? SessionLocked { get; set; }
    public bool? DisplayOn { get; set; }

    public List<Metric> Sample()
    {
        var result = new List<Metric>();
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            ulong nowIdle = idle.Value, nowTotal = kernel.Value + user.Value;
            if (_lastTotal != 0 && nowTotal > _lastTotal)
            {
                double cpu = 100.0 * (1.0 - (double)(nowIdle - _lastIdle) / (nowTotal - _lastTotal));
                result.Add(new("cpu", "Загрузка процессора", "sensor", Math.Round(Math.Clamp(cpu, 0, 100), 1),
                    "mdi:chip", "%", null, "measurement"));
            }
            _lastIdle = nowIdle;
            _lastTotal = nowTotal;
        }

        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (GlobalMemoryStatusEx(ref memory) && memory.TotalPhys > 0)
        {
            double percent = 100.0 * (1.0 - (double)memory.AvailPhys / memory.TotalPhys);
            result.Add(new("ram", "Загрузка памяти", "sensor", Math.Round(percent, 1),
                "mdi:memory", "%", null, "measurement"));
        }

        var input = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (GetLastInputInfo(ref input))
        {
            uint elapsedMs = unchecked((uint)Environment.TickCount - input.Tick);
            result.Add(new("idle", "Время бездействия", "sensor", Math.Round(elapsedMs / 60000.0, 1),
                "mdi:timer-outline", "min", null, "measurement"));
        }

        result.Add(new("uptime", "Время работы", "sensor",
            Math.Round(Environment.TickCount64 / 3600000.0, 1), "mdi:clock-outline",
            "h", null, "measurement"));
        result.Add(new("last_seen", "Последний отчёт", "sensor",
            DateTimeOffset.UtcNow.ToString("O"), "mdi:clock-check-outline", null, "timestamp"));

        try
        {
            var adapter = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                    && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                    && n.GetIPProperties().UnicastAddresses.Any(a =>
                        a.Address.AddressFamily == AddressFamily.InterNetwork))
                .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork))
                .FirstOrDefault();

            if (adapter is not null)
            {
                string address = adapter.GetIPProperties().UnicastAddresses.First(a =>
                    a.Address.AddressFamily == AddressFamily.InterNetwork).Address.ToString();
                result.Add(new("ip", "IP-адрес", "sensor", address, "mdi:ip-network"));
                var stats = adapter.GetIPv4Statistics();
                var now = DateTimeOffset.UtcNow;
                double seconds = (now - _lastNetworkAt).TotalSeconds;
                if (_lastAdapter == adapter.Id && seconds > 0 && seconds < 600
                    && stats.BytesReceived >= _lastRx && stats.BytesSent >= _lastTx)
                {
                    result.Add(new("download", "Скорость загрузки", "sensor",
                        Math.Round((stats.BytesReceived - _lastRx) * 8.0 / seconds / 1000000.0, 2),
                        "mdi:download-network", "Mbit/s", null, "measurement"));
                    result.Add(new("upload", "Скорость отдачи", "sensor",
                        Math.Round((stats.BytesSent - _lastTx) * 8.0 / seconds / 1000000.0, 2),
                        "mdi:upload-network", "Mbit/s", null, "measurement"));
                }
                _lastAdapter = adapter.Id;
                _lastRx = stats.BytesReceived;
                _lastTx = stats.BytesSent;
                _lastNetworkAt = now;
            }
        }
        catch (NetworkInformationException) { /* Interfaces can disappear during a reconnect. */ }
        catch (InvalidOperationException) { /* Interface changed during sampling. */ }

        var power = SystemInformation.PowerStatus;
        if (!power.BatteryChargeStatus.HasFlag(BatteryChargeStatus.NoSystemBattery)
            && power.BatteryLifePercent is >= 0 and <= 1)
        {
            result.Add(new("battery", "Заряд аккумулятора", "sensor",
                Math.Round(power.BatteryLifePercent * 100.0, 0),
                "mdi:battery", "%", "battery", "measurement"));
        }

        if (SessionLocked.HasValue)
            result.Add(new("locked", "Сеанс заблокирован", "binary_sensor",
                SessionLocked.Value, "mdi:lock-outline"));
        if (DisplayOn.HasValue)
            result.Add(new("display", "Экран включён", "binary_sensor",
                DisplayOn.Value, "mdi:monitor"));

        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low, High;
        public ulong Value => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile;
        public ulong TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size, Tick;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);
}

internal sealed class DisplayPowerWindow : NativeWindow, IDisposable
{
    private const int WmPowerBroadcast = 0x218;
    private const int PowerSettingChanged = 0x8013;
    private readonly Metrics _metrics;
    private IntPtr _registration;
    private static readonly Guid DisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    public DisplayPowerWindow(Metrics metrics)
    {
        _metrics = metrics;
        CreateHandle(new CreateParams());
        var guid = DisplayState;
        _registration = RegisterPowerSettingNotification(Handle, ref guid, 0);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmPowerBroadcast
            && message.WParam.ToInt64() == PowerSettingChanged
            && message.LParam != IntPtr.Zero)
        {
            var setting = Marshal.PtrToStructure<DisplaySetting>(message.LParam);
            if (setting.Guid == DisplayState && setting.Length >= 4)
                _metrics.DisplayOn = setting.State != 0;
        }
        base.WndProc(ref message);
    }

    public void Dispose()
    {
        if (_registration != IntPtr.Zero) UnregisterPowerSettingNotification(_registration);
        DestroyHandle();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplaySetting
    {
        public Guid Guid;
        public uint Length;
        public uint State;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(
        IntPtr recipient, ref Guid setting, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);
}
