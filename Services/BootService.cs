using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;

namespace NakClean.Services;

/// <summary>Что замедлило загрузку: имя + секунды + тип (приложение/драйвер/служба).</summary>
public readonly record struct BootSlow(string Name, int Seconds, string Kind);

/// <summary>Анализ времени загрузки из журнала Microsoft-Windows-Diagnostics-Performance/Operational.</summary>
public readonly record struct BootInfo(bool Available, int BootSeconds, List<BootSlow> Slow);

public static class BootService
{
    public static Task<BootInfo> Analyze() => Task.Run(() =>
    {
        var slow = new List<BootSlow>();
        try
        {
            var q = new EventLogQuery(
                "Microsoft-Windows-Diagnostics-Performance/Operational", PathType.LogName,
                "*[System[(EventID=100 or EventID=101 or EventID=102 or EventID=103)]]")
            { ReverseDirection = true };   // новейшие первыми

            using var reader = new EventLogReader(q);
            int bootMs = 0;
            int seen100 = 0;        // событие 100 = одна загрузка; второе 100 => уже ПРЕДЫДУЩАЯ загрузка
            var seen = new HashSet<string>();
            int scanned = 0;
            for (EventRecord? e = reader.ReadEvent(); e != null && scanned < 200; e = reader.ReadEvent())
            {
                scanned++;
                using (e)
                {
                    try
                    {
                        int id = e.Id;
                        string xml = e.ToXml();
                        if (id == 100)
                        {
                            seen100++;
                            if (seen100 >= 2) break;   // дальше идут события прошлых загрузок - не мешаем их с этой
                            // MainPathBootTime = время до готовности рабочего стола (честный показатель).
                            // BootTime его завышает, т.к. = MainPathBootTime + BootPostBootTime (фоновая догрузка ~до 2 мин).
                            bootMs = DataInt(xml, "MainPathBootTime");
                            if (bootMs == 0) bootMs = DataInt(xml, "BootTime");
                        }
                        else
                        {
                            // DegradationTime = на сколько элемент ЗАМЕДЛИЛ загрузку сверх нормы (честно).
                            // TotalTime - это сколько процесс суммарно работал (с учётом параллельности и пост-буст-окна),
                            // оно может превышать всю загрузку - под заголовок "что замедлило" не годится.
                            int ms = DataInt(xml, "DegradationTime");
                            if (ms <= 0) continue;       // не замедляло - не показываем
                            string name = DataStr(xml, "Name");
                            if (string.IsNullOrWhiteSpace(name)) continue;
                            if (!seen.Add(name.ToLowerInvariant())) continue;
                            string kind = id == 101 ? "app" : id == 102 ? "driver" : "service";
                            slow.Add(new BootSlow(name, Math.Max(1, ms / 1000), kind));
                        }
                    }
                    catch { }
                }
            }

            if (bootMs == 0 && slow.Count == 0) return new BootInfo(false, 0, slow);
            slow.Sort((a, b) => b.Seconds.CompareTo(a.Seconds));
            if (slow.Count > 6) slow = slow.GetRange(0, 6);   // топ-6 по вкладу в задержку
            return new BootInfo(true, bootMs / 1000, slow);
        }
        catch { return new BootInfo(false, 0, slow); }
    });

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
