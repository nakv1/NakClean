using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace NakClean.Services;

/// <summary>Что замедлило загрузку: имя + секунды + тип (приложение/драйвер/служба).</summary>
public readonly record struct BootSlow(string Name, double Seconds, string Kind);

/// <summary>Этап загрузки Windows: ключ названия + длительность в мс.</summary>
public readonly record struct BootPhase(string Key, int Ms);

/// <summary>Итог анализа последней загрузки.</summary>
public sealed record BootInfo(
    bool Available,
    int WindowsMs,          // MainPathBootTime: от старта Windows до готового рабочего стола (без ввода пароля)
    int PostBootMs,         // сколько Windows ещё догружалась в фоне после появления рабочего стола
    int BiosMs,             // время BIOS/UEFI до начала загрузки Windows (как в Диспетчере задач); 0 - неизвестно
    DateTime? When,
    List<BootPhase> Phases,
    List<BootSlow> Slow,
    int HiddenSmall);       // сколько мелких задержек (меньше 2 с) не показано

/// <summary>Анализ времени загрузки из журнала Microsoft-Windows-Diagnostics-Performance/Operational.</summary>
public static class BootService
{
    private const int MinSlowMs = 2000;   // меньше 2 с человек не заметит - в список не берём, только считаем

    public static Task<BootInfo> Analyze() => Task.Run(() =>
    {
        int bios = BiosMs();
        try
        {
            var q = new EventLogQuery(
                "Microsoft-Windows-Diagnostics-Performance/Operational", PathType.LogName,
                "*[System[(EventID=100 or EventID=101 or EventID=102 or EventID=103)]]")
            { ReverseDirection = true };   // новейшие первыми

            // Порядок в журнале (новые сверху): «что замедлило» последней загрузки, затем её итог (100),
            // затем события ПРОШЛОЙ загрузки. Поэтому берём только события рядом по времени с первым итогом.
            using var reader = new EventLogReader(q);
            var details = new List<(int id, string xml, DateTime? time)>();
            string? summary = null;
            DateTime? t100 = null;
            int scanned = 0;
            for (EventRecord? e = reader.ReadEvent(); e != null && scanned < 300; e = reader.ReadEvent())
            {
                scanned++;
                using (e)
                {
                    try
                    {
                        if (e.Id == 100)
                        {
                            if (summary != null) break;          // итог прошлой загрузки - дальше чужое
                            summary = e.ToXml();
                            t100 = e.TimeCreated;
                        }
                        else details.Add((e.Id, e.ToXml(), e.TimeCreated));
                    }
                    catch { }
                }
            }
            if (summary is null)
                return new BootInfo(false, 0, 0, bios, null, new(), new(), 0);

            int Field(string n) => DataInt(summary, n);
            var phases = new List<BootPhase>
            {
                new("boot_ph_drivers", Field("BootKernelInitTime") + Field("BootDriverInitTime") + Field("BootDevicesInitTime")),
                new("boot_ph_system", Field("BootPrefetchInitTime") + Field("BootAutoChkTime") + Field("BootSmssInitTime") + Field("BootCriticalServicesInitTime")),
                new("boot_ph_logon", Field("BootUserProfileProcessingTime") + Field("BootMachineProfileProcessingTime")),
                new("boot_ph_desktop", Field("BootExplorerInitTime")),
            };
            phases.RemoveAll(p => p.Ms <= 0);

            int windows = Field("MainPathBootTime");
            if (windows == 0) windows = Field("BootTime");

            // остаток, который Windows не относит ни к одному этапу - чтобы этапы честно сходились с итогом
            int rest = windows - phases.Sum(p => p.Ms);
            if (rest >= 300) phases.Add(new BootPhase("boot_ph_other", rest));

            var slow = new List<BootSlow>();
            var seen = new HashSet<string>();
            int hidden = 0;
            foreach (var (id, xml, time) in details)
            {
                // событие прошлой загрузки (записано не вместе с итогом) - пропускаем
                if (t100 is { } t && time is { } ti && Math.Abs((ti - t).TotalSeconds) > 120) continue;
                // DegradationTime = на сколько элемент ЗАМЕДЛИЛ загрузку сверх нормы
                int ms = DataInt(xml, "DegradationTime");
                if (ms <= 0) continue;
                string name = DataStr(xml, "Name");
                if (string.IsNullOrWhiteSpace(name) || !seen.Add(name.ToLowerInvariant())) continue;
                if (ms < MinSlowMs) { hidden++; continue; }
                string kind = id == 101 ? "app" : id == 102 ? "driver" : "service";
                slow.Add(new BootSlow(name, Math.Round(ms / 1000.0, 1), kind));
            }
            slow.Sort((a, b) => b.Seconds.CompareTo(a.Seconds));
            if (slow.Count > 8) { hidden += slow.Count - 8; slow = slow.GetRange(0, 8); }

            return new BootInfo(windows > 0, windows, Field("BootPostBootTime"), bios, t100, phases, slow, hidden);
        }
        catch { return new BootInfo(false, 0, 0, bios, null, new(), new(), 0); }
    });

    /// <summary>Время BIOS/UEFI последнего запуска (Windows хранит его для Диспетчера задач), мс.</summary>
    private static int BiosMs()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power");
            return k?.GetValue("FwPOSTTime") is int v && v > 0 && v < 600_000 ? v : 0;
        }
        catch { return 0; }
    }

    private static string DataStr(string xml, string name)
    {
        var m = Regex.Match(xml, $"<Data Name=['\"]{name}['\"][^>]*>(.*?)</Data>", RegexOptions.Singleline);
        return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : "";
    }

    private static int DataInt(string xml, string name)
    {
        var d = new string(DataStr(xml, name).Where(char.IsDigit).ToArray());
        return int.TryParse(d, out var v) ? v : 0;
    }
}
