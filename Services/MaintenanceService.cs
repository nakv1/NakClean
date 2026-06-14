using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NakClean.Services;

/// <summary>Прогресс операции обслуживания: процент (null = неопределённый/анимированный).</summary>
public readonly record struct MaintProgress(double? Percent);

/// <summary>Результат проверки: текст-вердикт + нужно ли вообще запускать действие.</summary>
public readonly record struct CheckResult(string Text, bool ActionNeeded);

/// <summary>
/// Обёртки над штатными инструментами обслуживания Windows (SFC, DISM, defrag, mdsched).
/// Запуск в фоне с парсингом процента, захватом вывода и поддержкой отмены (kill).
/// Результат проверки интерпретируется (RU+EN) → понятный вердикт «что нашлось».
/// </summary>
public static class MaintenanceService
{
    static MaintenanceService()
    {
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }
    }

    public static (string file, string args) SfcAnalyze => ("sfc.exe", "/verifyonly");
    public static (string file, string args) SfcRun => ("sfc.exe", "/scannow");
    // /CheckHealth - мгновенно (читает флаг повреждения), в отличие от долгого /ScanHealth
    public static (string file, string args) DismAnalyze => ("dism.exe", "/Online /Cleanup-Image /CheckHealth");
    public static (string file, string args) DismRun => ("dism.exe", "/Online /Cleanup-Image /RestoreHealth");
    public static (string file, string args) WinsxsAnalyze => ("dism.exe", "/Online /Cleanup-Image /AnalyzeComponentStore");
    public static (string file, string args) WinsxsRun => ("dism.exe", "/Online /Cleanup-Image /StartComponentCleanup");
    public static (string file, string args) DefragAnalyze => ("defrag.exe", "/C /A");
    public static (string file, string args) DefragRun => ("defrag.exe", "/C /O");

    private static readonly Regex PercentRx = new(@"(\d{1,3}(?:[.,]\d+)?)\s*%", RegexOptions.Compiled);

    public static bool LaunchMemoryTest()
    {
        try { Process.Start(new ProcessStartInfo("mdsched.exe") { UseShellExecute = true }); return true; }
        catch { return false; }
    }

    /// <summary>Запускает инструмент: парсит %, копит читаемый вывод, поддерживает отмену. Возвращает (успех, вывод).</summary>
    public static async Task<(bool ok, string text)> RunAsync((string file, string args) step,
        IProgress<MaintProgress> prog, CancellationToken ct, bool unicodeOutput = false)
    {
        Encoding enc;
        if (unicodeOutput) enc = Encoding.Unicode;          // sfc.exe пишет UTF-16
        else { try { enc = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); } catch { enc = Encoding.UTF8; } }

        var psi = new ProcessStartInfo
        {
            FileName = step.file, Arguments = step.args,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = enc, StandardErrorEncoding = enc,
        };

        Process? p;
        try { p = Process.Start(psi); }
        catch { return (false, ""); }
        if (p is null) return (false, "");

        var token = new StringBuilder();
        var captured = new StringBuilder();
        void Flush()
        {
            var s = new string(token.ToString().Where(c => !char.IsControl(c) || c == ' ').ToArray()).Trim();
            token.Clear();
            if (s.Length == 0) return;

            var m = PercentRx.Match(s);
            if (m.Success && double.TryParse(m.Groups[1].Value.Replace(',', '.'),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                prog.Report(new MaintProgress(Math.Clamp(v / 100.0, 0, 1)));

            // копим содержательные строки (с буквами) для интерпретации результата
            if (s.Length >= 4 && s.Any(char.IsLetter)) captured.Append(s).Append('\n');
        }

        try
        {
            var reader = p.StandardOutput;
            var buf = new char[1];
            while (true)
            {
                int n = await reader.ReadAsync(buf.AsMemory(0, 1), ct).ConfigureAwait(false);
                if (n == 0) break;
                char c = buf[0];
                if (c == '\r' || c == '\n') Flush();
                else token.Append(c);
            }
            Flush();
        }
        catch (OperationCanceledException) { }
        catch { }

        if (ct.IsCancellationRequested)
        {
            try { p.Kill(true); } catch { }
            try { p.Dispose(); } catch { }
            return (false, "");
        }

        try { await p.WaitForExitAsync().ConfigureAwait(false); } catch { }
        bool ok = false;
        try { ok = p.HasExited && p.ExitCode == 0; } catch { }
        try { p.Dispose(); } catch { }
        return (ok, captured.ToString());
    }

    // ---------- интерпретация результата проверки (по ключевым словам RU+EN) ----------
    private static bool Has(string s, params string[] needles) => needles.Any(n => s.Contains(n));

    private static readonly Regex SizeRx =
        new(@"(\d+(?:[.,]\d+)?)\s*(TB|GB|MB|KB|ТБ|ГБ|МБ|КБ)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Достаёт объём, который освободит очистка WinSxS (строка «архивные копии…/backups…»).</summary>
    private static string? ExtractReclaimable(string output)
    {
        // освобождается = «Резервные копии и отключённые компоненты» + «Кэш и временные данные»
        double mb = 0; bool found = false;
        foreach (var line in output.Split('\n'))
        {
            var l = line.ToLowerInvariant();
            bool isBackups = l.Contains("backups") || l.Contains("резервн");
            bool isCache = l.Contains("cache and temporary") || l.Contains("кэш и времен");
            if (!isBackups && !isCache) continue;

            var m = SizeRx.Match(line);
            if (!m.Success) continue;
            if (!double.TryParse(m.Groups[1].Value.Replace(',', '.'),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out var num)) continue;

            mb += m.Groups[2].Value.ToUpperInvariant() switch
            {
                "TB" or "ТБ" => num * 1024 * 1024,
                "GB" or "ГБ" => num * 1024,
                "KB" or "КБ" => num / 1024,
                _ => num,                       // MB / МБ
            };
            found = true;
        }
        return found ? Format.Bytes((long)(mb * 1024 * 1024)) : null;
    }

    public static CheckResult InterpretSfc(string output)
    {
        var o = output.ToLowerInvariant();
        if (Has(o, "did not find any integrity", "не обнаружила нарушений", "не обнаружены нарушения", "нарушений целостности не"))
            return new(Loc.I["mnt_r_sfc_clean"], false);
        if (Has(o, "unable to fix", "не удалось восстановить", "не удается восстановить", "не смогла восстановить"))
            return new(Loc.I["mnt_r_sfc_unfixable"], true);
        if (Has(o, "found integrity violations", "обнаружила повреж", "обнаружены повреж", "обнаружены наруш", "восстановила"))
            return new(Loc.I["mnt_r_sfc_found"], true);
        return new(Loc.I["mnt_analyzed"], true);
    }

    public static CheckResult InterpretDism(string output)
    {
        var o = output.ToLowerInvariant();
        if (Has(o, "no component store corruption", "повреждение хранилища компонентов не обнаруж", "повреждения хранилища компонентов не обнаруж"))
            return new(Loc.I["mnt_r_dism_clean"], false);
        if (Has(o, "repairable", "the component store is repairable", "можно восстановить", "подлежит восстановлению", "обнаружено повреждение"))
            return new(Loc.I["mnt_r_dism_repairable"], true);
        return new(Loc.I["mnt_analyzed"], true);
    }

    public static CheckResult InterpretWinsxs(string output)
    {
        var o = output.ToLowerInvariant();
        if (Has(o, "recommended : no", "recommended: no", "не рекомендуется"))
            return new(Loc.I["mnt_r_winsxs_no"], false);
        if (Has(o, "recommended : yes", "recommended: yes", "рекомендуется"))
        {
            var size = ExtractReclaimable(output);
            return new(size != null
                ? string.Format(Loc.I["mnt_r_winsxs_yes_sz"], size)
                : Loc.I["mnt_r_winsxs_yes"], true);
        }
        return new(Loc.I["mnt_analyzed"], true);
    }
}
