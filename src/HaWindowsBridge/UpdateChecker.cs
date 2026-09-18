using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace HAWindowsBridge;

internal static class UpdateChecker
{
    private const string Releases = "https://github.com/asustek1978/HA-Windows-Bridge/releases/";
    private const string LatestApi =
        "https://api.github.com/repos/asustek1978/HA-Windows-Bridge/releases/latest";

    public static async Task<(string Message, string? Url)> CheckAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestApi);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("HAWindowsBridge", AppInfo.Version));
        using var response = await http.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return ("Пока нет опубликованного релиза. Новую сборку можно скачать из GitHub Actions.", null);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string tag = json.RootElement.GetProperty("tag_name").GetString() ?? "";
        string url = json.RootElement.GetProperty("html_url").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)
            || !Version.TryParse(AppInfo.Version, out var current)
            || !url.StartsWith(Releases, StringComparison.Ordinal))
            throw new InvalidDataException("GitHub вернул некорректную версию релиза.");
        return latest > current
            ? ($"Доступна версия {tag}. Открыть страницу загрузки?", url)
            : ($"Установлена актуальная версия {AppInfo.Version}.", null);
    }
}
