using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace NakClean.Services;

/// <summary>Результат проверки обновлений на GitHub.</summary>
public readonly record struct UpdateInfo(bool Checked, bool Available, string LatestVersion, string Url,
                                         string ExeUrl = "", string ShaUrl = "");

/// <summary>Проверка и установка новой версии через GitHub Releases (последний релиз репозитория).</summary>
public static class UpdateService
{
    private const string ApiUrl = "https://api.github.com/repos/nakv1/NakClean/releases/latest";
    private const string ExeAsset = "NakClean.exe";
    private const string ShaAsset = "NakClean.exe.sha256";
    public const string UpdatedArg = "--updated";

    private static readonly HttpClient Http = Create(TimeSpan.FromSeconds(10), "application/vnd.github+json");
    private static readonly HttpClient Download = Create(Timeout.InfiniteTimeSpan, "application/octet-stream");

    private static HttpClient Create(TimeSpan timeout, string accept)
    {
        var h = new HttpClient { Timeout = timeout };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("NakClean-Updater");   // GitHub требует User-Agent
        h.DefaultRequestHeaders.Accept.ParseAdd(accept);
        return h;
    }

    private static string ExePath => Environment.ProcessPath ?? "";

    /// <summary>Самообновление только для портабл-версии (один exe). Обычная сборка лежит рядом с NakClean.dll.</summary>
    public static bool CanSelfUpdate =>
        ExePath.Length > 0 && !File.Exists(Path.Combine(AppContext.BaseDirectory, "NakClean.dll"));

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

            string exeUrl = "", shaUrl = "";
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    string name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    string link = a.TryGetProperty("browser_download_url", out var l) ? l.GetString() ?? "" : "";
                    if (name.Equals(ExeAsset, StringComparison.OrdinalIgnoreCase)) exeUrl = link;
                    else if (name.Equals(ShaAsset, StringComparison.OrdinalIgnoreCase)) shaUrl = link;
                }
            }

            var latest = ParseVer(tag);
            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            bool available = latest != null && Norm(latest) > Norm(current);

            return new UpdateInfo(true, available, latest?.ToString(3) ?? tag, url, exeUrl, shaUrl);
        }
        catch
        {
            return new UpdateInfo(false, false, "", "");
        }
    }

    /// <summary>
    /// Скачивает новый exe рядом с текущим, сверяет SHA256 с опубликованным и подменяет файл.
    /// Запущенный exe нельзя удалить, но можно переименовать - старый уходит в .old и удаляется при следующем запуске.
    /// </summary>
    public static async Task InstallAsync(UpdateInfo info, IProgress<int> progress, CancellationToken ct = default)
    {
        string exe = ExePath;
        string newPath = exe + ".new";
        string oldPath = exe + ".old";

        string expected = (await Download.GetStringAsync(info.ShaUrl, ct)).Trim().Split(' ', '\t', '\r', '\n')[0];
        if (expected.Length != 64) throw new InvalidDataException("bad sha256 file");

        try
        {
            using (var resp = await Download.GetAsync(info.ExeUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? -1;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(newPath);
                var buf = new byte[81920];
                long done = 0;
                int n, last = -1;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    int pct = total > 0 ? (int)(done * 100 / total) : -1;
                    if (pct != last) { last = pct; progress.Report(pct); }
                }
            }

            string actual;
            using (var fs = File.OpenRead(newPath)) actual = Convert.ToHexString(SHA256.HashData(fs));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("sha256 mismatch");
        }
        catch
        {
            TryDelete(newPath);
            throw;
        }

        TryDelete(oldPath);
        File.Move(exe, oldPath);
        try { File.Move(newPath, exe); }
        catch { File.Move(oldPath, exe); throw; }
    }

    /// <summary>Перезапуск уже обновлённого exe (права админа наследуются, окна UAC не будет).</summary>
    public static void Restart()
        => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ExePath, UpdatedArg) { UseShellExecute = false });

    /// <summary>Удалить старый exe после обновления. Прошлый процесс может ещё закрываться - пробуем несколько раз.</summary>
    public static void CleanupOld()
    {
        string oldPath = ExePath + ".old";
        if (ExePath.Length == 0 || !File.Exists(oldPath)) return;
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 20 && File.Exists(oldPath); i++)
            {
                TryDelete(oldPath);
                await Task.Delay(500);
            }
        });
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // сравниваем только major.minor.build, чтобы 4-я компонента не путала
    private static Version Norm(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    private static Version? ParseVer(string tag)
    {
        var s = new string(tag.Where(c => char.IsDigit(c) || c == '.').ToArray());
        return Version.TryParse(s, out var v) ? v : null;
    }
}
