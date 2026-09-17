using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HAWindowsBridge;

internal static class WindowsCommands
{
    private const string Marker = "HA_WINDOWS_BRIDGE_COMMAND";
    private static readonly HashSet<string> Allowed = ["lock", "sleep", "restart",
        "shutdown", "cancel_shutdown"];

    public static bool TryParse(JsonElement payload, BridgeConfig config, out string action)
    {
        action = "";
        if (!config.AllowRemoteControl || payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.String
            || message.GetString() != Marker
            || !payload.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("ha_windows_bridge", out var command)
            || command.ValueKind != JsonValueKind.Object
            || !command.TryGetProperty("action", out var name)
            || name.ValueKind != JsonValueKind.String
            || !command.TryGetProperty("key", out var key)
            || key.ValueKind != JsonValueKind.String
            || !command.TryGetProperty("issued_at", out var issuedAt))
            return false;

        string candidate = name.GetString() ?? "";
        if (!Allowed.Contains(candidate)) return false;
        string expected = config.CommandKey;
        string supplied = key.GetString() ?? "";
        if (expected.Length == 0 || supplied.Length != expected.Length
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(supplied)))
            return false;

        long seconds;
        if (issuedAt.ValueKind == JsonValueKind.Number)
        {
            if (!issuedAt.TryGetInt64(out seconds)) return false;
        }
        else if (issuedAt.ValueKind == JsonValueKind.String)
        {
            if (!long.TryParse(issuedAt.GetString(), out seconds)) return false;
        }
        else return false;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (seconds < now - 120 || seconds > now + 120)
            return false;

        action = candidate;
        return true;
    }

    public static string Execute(string action)
    {
        try
        {
            return action switch
            {
                "lock" => LockWorkStation() ? "Сеанс заблокирован" : "Не удалось заблокировать сеанс",
                "sleep" => SetSuspendState(false, false, false)
                    ? "Сон выполнен" : "Не удалось перевести ПК в сон",
                "restart" => RunShutdown("/r", "Перезагрузка запланирована через 60 секунд"),
                "shutdown" => RunShutdown("/s", "Выключение запланировано через 60 секунд"),
                "cancel_shutdown" => RunShutdown("/a", "Запланированное выключение отменено"),
                _ => "Команда отклонена"
            };
        }
        catch (Exception)
        {
            return "Команда Windows не выполнена";
        }
    }

    private static string RunShutdown(string option, string success)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(option);
        if (option != "/a")
        {
            start.ArgumentList.Add("/t");
            start.ArgumentList.Add("60");
        }
        using var process = Process.Start(start);
        if (process is null || !process.WaitForExit(5000) || process.ExitCode != 0)
            return "Не удалось выполнить " + option;
        return success;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SetSuspendState(
        [MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool forceCritical,
        [MarshalAs(UnmanagedType.U1)] bool disableWakeEvent);
}
