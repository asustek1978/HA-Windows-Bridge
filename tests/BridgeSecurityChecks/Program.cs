using System.Text.Json;
using HAWindowsBridge;

var config = new BridgeConfig { AllowRemoteControl = true };
string key = config.EnsureCommandKey();
long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

Check("Разрешённая команда", "lock", key, now, true);
Check("Неверный ключ", "lock", new string('0', key.Length), now, false);
Check("Просроченная команда", "lock", key, now - 300, false);
Check("Время в будущем", "lock", key, now + 300, false);
Check("Произвольная команда", "run_shell", key, now, false);
config.AllowRemoteControl = false;
Check("Управление выключено", "lock", key, now, false);
Console.WriteLine("Проверки команд Windows пройдены.");

void Check(string name, string action, string suppliedKey, long issuedAt, bool expected)
{
    string json = JsonSerializer.Serialize(new
    {
        message = "HA_WINDOWS_BRIDGE_COMMAND",
        data = new { ha_windows_bridge = new { action, key = suppliedKey, issued_at = issuedAt } }
    });
    using var doc = JsonDocument.Parse(json);
    bool accepted = WindowsCommands.TryParse(doc.RootElement, config, out string parsed);
    if (accepted != expected || (accepted && parsed != action))
        throw new InvalidOperationException(name + ": неверный результат проверки");
}
