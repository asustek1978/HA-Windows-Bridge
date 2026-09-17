using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace HAWindowsBridge;

internal sealed class HomeAssistantClient : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly BridgeConfig _config;

    public HomeAssistantClient(BridgeConfig config) => _config = config;

    public async Task<(string Url, string Network)> FindServerAsync(CancellationToken ct)
    {
        Exception? last = null;
        foreach (var (url, network) in new[]
            { (_config.LocalUrl, "Локальный адрес"), (_config.ExternalUrl, "Внешний адрес") })
        {
            if (string.IsNullOrEmpty(url)) continue;
            try
            {
                using var request = Authenticated(HttpMethod.Get, url + "/api/");
                using var response = await _http.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
                return (url, network);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                if (ct.IsCancellationRequested) throw;
                last = ex;
            }
        }
        throw new IOException("Home Assistant недоступен по заданным адресам.", last);
    }

    private HttpRequestMessage Authenticated(HttpMethod method, string url, object? payload = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.Token);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return request;
    }

    public async Task EnsureRegistrationAsync(string url, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_config.WebhookId))
        {
            if (_config.UiLocale != "ru" || _config.AppVersion != "1.1.0")
            {
                bool relocalize = _config.UiLocale != "ru";
                using var update = await PostWebhookAsync(url, new
                {
                    type = "update_registration",
                    data = new
                    {
                        app_version = "1.1.0",
                        device_name = "Компьютер Windows " + Environment.MachineName,
                        manufacturer = "Компьютер Windows",
                        model = Environment.MachineName,
                        os_version = Environment.OSVersion.VersionString,
                        app_data = new { push_websocket_channel = true }
                    }
                }, ct);
                if (relocalize) _config.RegisteredSensors.Clear();
                _config.UiLocale = "ru";
                _config.AppVersion = "1.1.0";
                _config.Save();
            }
            return;
        }

        using var request = Authenticated(HttpMethod.Post, url + "/api/mobile_app/registrations",
            new
            {
                device_id = _config.DeviceId,
                app_id = "ha.windows.bridge",
                app_name = "Мост Windows для Home Assistant",
                app_version = "1.1.0",
                device_name = "Компьютер Windows " + Environment.MachineName,
                manufacturer = "Компьютер Windows",
                model = Environment.MachineName,
                os_name = "Windows",
                os_version = Environment.OSVersion.VersionString,
                supports_encryption = false,
                app_data = new { push_websocket_channel = true }
            });
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        string? webhook = json.RootElement.GetProperty("webhook_id").GetString();
        if (string.IsNullOrEmpty(webhook))
            throw new InvalidDataException("Home Assistant не вернул идентификатор webhook.");
        _config.SetWebhook(webhook);
    }

    public async Task SendSensorsAsync(string url, IReadOnlyList<Metric> metrics, CancellationToken ct)
    {
        if (metrics.Count == 0) return;
        if (_config.SensorSchemaVersion < 2)
        {
            // Повторная регистрация меняет единицу с часов на минуты для существующего датчика.
            _config.RegisteredSensors.Remove("uptime");
            _config.SensorSchemaVersion = 2;
            _config.Save();
        }
        foreach (var metric in metrics)
        {
            if (_config.RegisteredSensors.Contains(metric.Id)) continue;
            var data = new Dictionary<string, object?>
            {
                ["unique_id"] = metric.Id, ["name"] = metric.Name,
                ["type"] = metric.Type, ["state"] = metric.State, ["icon"] = metric.Icon
            };
            if (metric.Unit is not null) data["unit_of_measurement"] = metric.Unit;
            if (metric.DeviceClass is not null) data["device_class"] = metric.DeviceClass;
            if (metric.StateClass is not null) data["state_class"] = metric.StateClass;
            await PostWebhookAsync(url, new { type = "register_sensor", data }, ct);
            _config.RegisteredSensors.Add(metric.Id);
            _config.Save();
        }

        var updates = metrics.Select(m => new
        {
            unique_id = m.Id, type = m.Type, state = m.State, icon = m.Icon
        }).ToArray();

        using var response = await PostWebhookAsync(url,
            new { type = "update_sensor_states", data = updates }, ct);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (json.RootElement.ValueKind != JsonValueKind.Object) return;
        bool changed = false;
        foreach (var metric in metrics)
        {
            if (!json.RootElement.TryGetProperty(metric.Id, out var item)) continue;
            if (!item.TryGetProperty("success", out var success)
                || success.ValueKind != JsonValueKind.False) continue;
            string code = item.TryGetProperty("error", out var error)
                && error.TryGetProperty("code", out var value)
                ? value.GetString() ?? "" : "";
            if (code == "not_registered")
            {
                changed |= _config.RegisteredSensors.Remove(metric.Id);
                continue;
            }
            throw new InvalidDataException("Home Assistant отклонил датчик " + metric.Id + ": " + code);
        }
        if (changed) _config.Save();
    }

    private async Task<HttpResponseMessage> PostWebhookAsync(string url, object payload, CancellationToken ct)
    {
        string webhook = _config.WebhookId;
        using var response = await _http.PostAsJsonAsync(
            url + "/api/webhook/" + Uri.EscapeDataString(webhook), payload, ct);
        if (response.StatusCode == HttpStatusCode.Gone)
        {
            _config.ResetRegistration();
            throw new IOException("Устройство mobile_app удалено из HA. Повторная регистрация.");
        }
        response.EnsureSuccessStatusCode();
        // The caller receives an independent copy and must dispose it.
        return new HttpResponseMessage(response.StatusCode)
        {
            Content = new StringContent(await response.Content.ReadAsStringAsync(ct))
        };
    }

    public async Task ListenAsync(string url, Action connected, Action<string, string> notify,
        Action<string> command, CancellationToken ct)
    {
        var uri = new Uri(url);
        var builder = new UriBuilder(uri)
        {
            Scheme = uri.Scheme == "https" ? "wss" : "ws",
            Path = "/api/websocket"
        };
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(builder.Uri, ct);
        using (var hello = JsonDocument.Parse(await ReceiveAsync(socket, ct)))
        {
            if (hello.RootElement.GetProperty("type").GetString() != "auth_required")
                throw new IOException("Неожиданный ответ WebSocket от Home Assistant.");
        }
        await SendAsync(socket, new { type = "auth", access_token = _config.Token }, ct);
        using (var auth = JsonDocument.Parse(await ReceiveAsync(socket, ct)))
        {
            if (auth.RootElement.GetProperty("type").GetString() != "auth_ok")
                throw new UnauthorizedAccessException("Home Assistant отклонил токен доступа.");
        }

        await SendAsync(socket, new
        {
            id = 1, type = "mobile_app/push_notification_channel",
            webhook_id = _config.WebhookId
        }, ct);
        using (var subscription = JsonDocument.Parse(await ReceiveAsync(socket, ct)))
        {
            if (subscription.RootElement.GetProperty("type").GetString() != "result"
                || !subscription.RootElement.GetProperty("success").GetBoolean())
                throw new IOException("Home Assistant отклонил подписку на уведомления.");
        }

        connected();
        while (!ct.IsCancellationRequested)
        {
            using var document = JsonDocument.Parse(await ReceiveAsync(socket, ct));
            var root = document.RootElement;
            if (root.GetProperty("type").GetString() != "event"
                || !root.TryGetProperty("event", out var payload)) continue;
            string title = payload.TryGetProperty("title", out var t)
                ? t.GetString() ?? "Home Assistant" : "Home Assistant";
            string message = payload.TryGetProperty("message", out var m)
                ? m.GetString() ?? "" : "";
            if (message == "HA_WINDOWS_BRIDGE_COMMAND")
            {
                if (WindowsCommands.TryParse(payload, _config, out string action)) command(action);
                continue;
            }
            if (message.Length > 0) notify(title, message);
        }
    }

    private static async Task SendAsync(ClientWebSocket socket, object value, CancellationToken ct)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(value);
        await socket.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, ct);
    }

    private static async Task<string> ReceiveAsync(ClientWebSocket socket, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        WebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(new ArraySegment<byte>(chunk), ct);
            if (received.MessageType == WebSocketMessageType.Close)
                throw new IOException("Home Assistant закрыл соединение WebSocket.");
            if (received.MessageType != WebSocketMessageType.Text
                || buffer.Length + received.Count > 256 * 1024)
                throw new InvalidDataException("Неожиданное или слишком большое сообщение WebSocket.");
            buffer.Write(chunk, 0, received.Count);
        } while (!received.EndOfMessage);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public void Dispose() => _http.Dispose();
}
