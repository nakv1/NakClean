using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace NakClean.Services;

/// <summary>Результат проверки обновлений на GitHub.</summary>
public readonly record struct UpdateInfo(bool Checked, bool Available, string LatestVersion, string Url);

/// <summary>Проверка новой версии через GitHub Releases (последний релиз репозитория).</summary>
public static class UpdateService
{
    private const string ApiUrl = "https://api.github.com/repos/nakv1/NakClean/releases/latest";
    private static readonly HttpClient Http = Create();

    private static HttpClient Create()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("NakClean-Updater");   // GitHub требует User-Agent
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return h;
    }

    public static async Task<UpdateInfo> CheckAsync()
    {
        try
        {
            using var resp = await Http.GetAsync(ApiUrl);
            if (!resp.IsSuccessStatusCode) return new UpdateInfo(false, false, "", "");

            await using var stream = await resp.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var root = doc.RootElement;

            string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            string url = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";

            var latest = ParseVer(tag);
            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            bool available = latest != null && Norm(latest) > Norm(current);

            return new UpdateInfo(true, available, latest?.ToString(3) ?? tag, url);
        }
        catch
        {
            return new UpdateInfo(false, false, "", "");
        }
    }

    // сравниваем только major.minor.build, чтобы 4-я компонента не путала
    private static Version Norm(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    private static Version? ParseVer(string tag)
    {
        var s = new string(tag.Where(c => char.IsDigit(c) || c == '.').ToArray());
        return Version.TryParse(s, out var v) ? v : null;
    }
}
