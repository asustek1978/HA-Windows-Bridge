using System.Security.Cryptography;
using System.Text;
using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.Hardware.Storage;

namespace HAWindowsBridge;

internal sealed class HardwareMetrics : IDisposable
{
    private Computer? _computer;
    private DateTimeOffset _retryAfter;
    private readonly Dictionary<string, Metric> _known = new();

    public List<Metric> Sample()
    {
        var result = new List<Metric>();
        if (_computer is null && DateTimeOffset.UtcNow < _retryAfter) return WithMissing(result);
        try
        {
            if (_computer is null)
            {
                var computer = new Computer
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = true,
                    IsStorageEnabled = true,
                    IsMotherboardEnabled = true,
                    IsControllerEnabled = true
                };
                try { computer.Open(); }
                catch (Exception)
                {
                    try { computer.Close(); } catch (Exception) { }
                    throw;
                }
                _computer = computer;
            }

            foreach (var hardware in _computer.Hardware)
                SampleHardware(hardware, result);
        }
        catch (Exception)
        {
            // Некоторые платы и диски не открывают низкоуровневые датчики без драйвера.
            try { _computer?.Close(); } catch (Exception) { }
            _computer = null;
            _retryAfter = DateTimeOffset.UtcNow.AddMinutes(5);
        }
        return WithMissing(result);
    }

    private List<Metric> WithMissing(List<Metric> current)
    {
        var received = current.Select(m => m.Id).ToHashSet();
        foreach (var metric in current) _known[metric.Id] = metric;
        foreach (var metric in _known.Values)
            if (!received.Contains(metric.Id)) current.Add(metric with { State = "unknown" });
        return current;
    }

    private static void SampleHardware(IHardware hardware, List<Metric> result)
    {
        try
        {
            hardware.Update();
            string id = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(hardware.Identifier.ToString())))[..10].ToLowerInvariant();
            switch (hardware.HardwareType)
            {
                case HardwareType.Cpu:
                    AddBest(result, hardware, SensorType.Temperature,
                        "cpu_" + id + "_temperature", "Температура процессора",
                        "mdi:thermometer", "°C", "temperature", ScoreCpuTemperature, -20, 130);
                    AddBest(result, hardware, SensorType.Power,
                        "cpu_" + id + "_power", "Мощность процессора",
                        "mdi:flash", "W", "power", n => n.Contains("package") ? 10 : 0, 0, 1000);
                    break;
                case HardwareType.GpuNvidia:
                case HardwareType.GpuAmd:
                case HardwareType.GpuIntel:
                    string name = "GPU " + hardware.Name;
                    AddBest(result, hardware, SensorType.Load,
                        "gpu_" + id + "_load", name + " — загрузка",
                        "mdi:expansion-card", "%", null, ScoreGpuLoad, 0, 100);
                    AddBest(result, hardware, SensorType.Temperature,
                        "gpu_" + id + "_temperature", name + " — температура",
                        "mdi:thermometer", "°C", "temperature", n =>
                            n.Contains("core") || n == "gpu" ? 20 :
                            n.Contains("hot spot") || n.Contains("hotspot") ? -10 : 1, -20, 130);
                    AddBest(result, hardware, SensorType.Temperature,
                        "gpu_" + id + "_hotspot", name + " — горячая точка",
                        "mdi:thermometer-high", "°C", "temperature", n =>
                            n.Contains("hot spot") || n.Contains("hotspot") ? 10 : -100, -20, 150);
                    AddBest(result, hardware, SensorType.Fan,
                        "gpu_" + id + "_fan", name + " — вентилятор",
                        "mdi:fan", "rpm", null, _ => 1, 0, 15000);
                    AddBest(result, hardware, SensorType.Power,
                        "gpu_" + id + "_power", name + " — мощность",
                        "mdi:flash", "W", "power", n =>
                            n.Contains("package") || n.Contains("total") ? 10 : 0, 0, 1000);
                    AddMemory(result, hardware, "gpu_" + id + "_vram", name + " — занято видеопамяти");
                    break;
                case HardwareType.Storage:
                    AddBest(result, hardware, SensorType.Temperature,
                        "storage_" + id + "_temperature", hardware.Name + " — температура диска",
                        "mdi:harddisk", "°C", "temperature", n =>
                            n.Contains("temperature") || n.Contains("drive") ? 10 : 0, -20, 130);
                    AddStorageHealth(result, hardware, "storage_" + id, hardware.Name);
                    break;
            }
            foreach (var child in hardware.SubHardware)
                SampleHardware(child, result);
        }
        catch (Exception)
        {
            // Сбой одного устройства не должен останавливать отчёты остальных.
        }
    }

    private static int ScoreCpuTemperature(string name) =>
        name.Contains("package") || name.Contains("tctl") || name.Contains("tdie") ? 20 :
        name.Contains("core max") || name.Contains("cpu") ? 10 : 1;

    private static int ScoreGpuLoad(string name) =>
        name.Contains("core") || name.Contains("total") ? 20 :
        name == "gpu" || name.Contains("3d") ? 10 : -100;

    private static void AddBest(List<Metric> result, IHardware hardware, SensorType type,
        string id, string name, string icon, string unit, string? deviceClass,
        Func<string, int> score, double min, double max)
    {
        var selected = hardware.Sensors
            .Where(s => s.SensorType == type && s.Value is float value
                && float.IsFinite(value) && value >= min && value <= max)
            .OrderByDescending(s => score(s.Name.ToLowerInvariant()))
            .FirstOrDefault();
        if (selected is null || score(selected.Name.ToLowerInvariant()) < 0) return;
        result.Add(new Metric(id, name, "sensor", Math.Round(selected.Value!.Value, 1),
            icon, unit, deviceClass, "measurement"));
    }

    private static void AddMemory(List<Metric> result, IHardware hardware, string id, string name)
    {
        var sensor = hardware.Sensors.FirstOrDefault(s =>
            (s.SensorType == SensorType.Data || s.SensorType == SensorType.SmallData)
            && s.Name.Contains("Memory Used", StringComparison.OrdinalIgnoreCase)
            && s.Value is float value && float.IsFinite(value) && value >= 0);
        if (sensor is null) return;
        double gib = sensor.Value!.Value / (sensor.SensorType == SensorType.SmallData ? 1024 : 1);
        result.Add(new Metric(id, name, "sensor", Math.Round(gib, 2),
            "mdi:memory", "GiB", "data_size", "measurement"));
    }

    private static void AddStorageHealth(List<Metric> result, IHardware hardware,
        string id, string name)
    {
        var life = hardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Level
            && s.Name.Equals("Life", StringComparison.OrdinalIgnoreCase)
            && s.Value is float value && float.IsFinite(value) && value is >= 0 and <= 100);
        if (life is not null)
            result.Add(new(id + "_life", name + " — ресурс SSD", "sensor",
                Math.Round(life.Value!.Value, 1), "mdi:harddisk", "%", null,
                "measurement", "diagnostic"));

        var hours = hardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Factor
            && s.Name.Equals("Power On Hours", StringComparison.OrdinalIgnoreCase)
            && s.Value is float value && float.IsFinite(value) && value is >= 0 and < 1000000);
        if (hours is not null)
            result.Add(new(id + "_power_on_hours", name + " — наработка", "sensor",
                Math.Round(hours.Value!.Value), "mdi:clock-outline", "h", "duration",
                "measurement", "diagnostic"));

        if (hardware is not ISmart smart) return;
        var errors = new[]
        {
            ("reallocated", "переназначенные сектора", "Reallocated"),
            ("pending", "ожидающие сектора", "Current Pending Sector"),
            ("uncorrectable", "неисправимые сектора", "Uncorrectable")
        };
        bool warning = life is not null && life.Value!.Value < 15;
        bool hasHealth = life is not null;
        foreach (var (suffix, label, attributeName) in errors)
        {
            var attribute = smart.Attributes.FirstOrDefault(a => a.Name.Contains(attributeName,
                StringComparison.OrdinalIgnoreCase) && float.IsFinite(a.Value) && a.Value >= 0);
            if (attribute is null) continue;
            hasHealth = true;
            warning |= attribute.Value > 0;
            result.Add(new(id + "_" + suffix, name + " — " + label, "sensor",
                Math.Round(attribute.Value), "mdi:harddisk-alert", null, null,
                "measurement", "diagnostic"));
        }
        if (hasHealth)
            result.Add(new(id + "_warning", name + " — проблемы диска", "binary_sensor",
                warning, "mdi:harddisk-alert", Category: "diagnostic"));
    }

    public void Dispose()
    {
        try { _computer?.Close(); } catch (Exception) { }
        _computer = null;
    }
}
