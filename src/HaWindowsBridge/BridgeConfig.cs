using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HAWindowsBridge;

internal sealed class BridgeConfig
{
    private static readonly object SaveLock = new();
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HAWindowsBridge");
    private static readonly string FilePath = Path.Combine(DirectoryPath, "settings.json");

    public string LocalUrl { get; set; } = "";
    public string ExternalUrl { get; set; } = "";
    public int IntervalSeconds { get; set; } = 30;
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");
    public string TokenCipher { get; set; } = "";
    public string WebhookCipher { get; set; } = "";
    public string UiLocale { get; set; } = "";
    public HashSet<string> RegisteredSensors { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public string Token => Unprotect(TokenCipher);
    [System.Text.Json.Serialization.JsonIgnore]
    public string WebhookId => Unprotect(WebhookCipher);
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(TokenCipher)
        && (!string.IsNullOrWhiteSpace(LocalUrl) || !string.IsNullOrWhiteSpace(ExternalUrl));

    public static BridgeConfig Load()
    {
        if (!File.Exists(FilePath)) return new BridgeConfig();
        var config = JsonSerializer.Deserialize<BridgeConfig>(File.ReadAllText(FilePath))
            ?? throw new InvalidDataException("Файл настроек пуст.");
        config.RegisteredSensors ??= [];
        if (string.IsNullOrWhiteSpace(config.DeviceId)) config.DeviceId = Guid.NewGuid().ToString("N");
        return config;
    }

    public void SetToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Токен не может быть пустым.");
        TokenCipher = Protect(token.Trim());
        WebhookCipher = "";
        RegisteredSensors.Clear();
    }

    public void SetWebhook(string webhook)
    {
        WebhookCipher = Protect(webhook);
        UiLocale = "ru";
        RegisteredSensors.Clear();
        Save();
    }

    public void ResetRegistration()
    {
        WebhookCipher = "";
        RegisteredSensors.Clear();
        Save();
    }

    public void Save()
    {
        lock (SaveLock)
        {
            Directory.CreateDirectory(DirectoryPath);
            string temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            File.Move(temporary, FilePath, true);
        }
    }

    private static string Protect(string value) => Convert.ToBase64String(
        ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));

    private static string Unprotect(string value) => string.IsNullOrEmpty(value) ? "" : Encoding.UTF8.GetString(
        ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));

    public static string NormalizeUrl(string input, bool external)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https")
            || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/"
            || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Укажите полный адрес HTTP(S) без пути и данных для входа.");
        if (external && uri.Scheme != "https")
            throw new ArgumentException("Внешний адрес должен использовать HTTPS.");
        if (uri.Scheme == "http" && !IsPrivateAddress(uri.Host))
            throw new ArgumentException("HTTP разрешён только для частных локальных адресов.");
        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    private static bool IsPrivateAddress(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(host, out var address)) return false;
        if (IPAddress.IsLoopback(address)) return true;
        byte[] b = address.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254));
    }
}
