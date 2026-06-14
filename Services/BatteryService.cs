using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace NakClean.Services;

/// <summary>Здоровье батареи: проектная/текущая ёмкость, износ, циклы, паспорт. Источник — powercfg /batteryreport.</summary>
public readonly record struct BatteryInfo(
    bool Present, long DesignMwh, long FullMwh, int CycleCount, int WearPercent,
    string Name, string Manufacturer, string Serial, string Chemistry);

public static class BatteryService
{
    private static readonly Regex CapRx =
        new(@"([\d][\d.,  ]*)\s*(?:mWh|мВт)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static Task<BatteryInfo> Get() => Task.Run(() =>
    {
        string tmp = Path.Combine(Path.GetTempPath(), "nakclean-battery.html");
        try
        {
            var psi = new ProcessStartInfo("powercfg.exe", $"/batteryreport /output \"{tmp}\"")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using (var p = Process.Start(psi)) p?.WaitForExit(15000);

            if (!File.Exists(tmp)) return Empty;
            string html = File.ReadAllText(tmp);
            try { File.Delete(tmp); } catch { }

            // первые две ёмкости в отчёте: DESIGN CAPACITY, FULL CHARGE CAPACITY
            var caps = CapRx.Matches(html);
            if (caps.Count < 2) return Empty;
            long design = Num(caps[0].Groups[1].Value);
            long full = Num(caps[1].Groups[1].Value);
            if (design <= 0) return Empty;

            int cycles = 0;
            var cm = Regex.Match(html,
                @"(?:CYCLE COUNT|КОЛИЧЕСТВО ЦИКЛОВ|ЦИКЛ[^<]*)\s*</td>\s*<td[^>]*>\s*([\d.,  ]+)",
                RegexOptions.IgnoreCase);
            if (cm.Success) cycles = (int)Num(cm.Groups[1].Value);

            // паспорт батареи (метки EN/RU)
            string name = Field(html, "NAME", "ИМЯ");
            string mfr = Field(html, "MANUFACTURER", "ИЗГОТОВИТЕЛЬ", "ПРОИЗВОДИТЕЛЬ");
            string serial = Field(html, "SERIAL NUMBER", "СЕРИЙНЫЙ НОМЕР");
            string chem = Chem(Field(html, "CHEMISTRY", "ХИМИЧЕСКИЙ СОСТАВ", "ХИМИЯ"));

            int wear = (int)Math.Round((1 - (double)full / design) * 100);
            if (wear < 0) wear = 0;
            return new BatteryInfo(true, design, full, cycles, wear, name, mfr, serial, chem);
        }
        catch { return Empty; }
    });

    private static long Num(string s)
    {
        var d = new string(s.Where(char.IsDigit).ToArray());
        return long.TryParse(d, out var v) ? v : 0;
    }

    private static readonly BatteryInfo Empty = new(false, 0, 0, 0, 0, "", "", "", "");

    /// <summary>Значение ячейки по метке: ищем &lt;td&gt;МЕТКА&lt;/td&gt;&lt;td&gt;значение&lt;/td&gt;.</summary>
    private static string Field(string html, params string[] labels)
    {
        foreach (var lbl in labels)
        {
            var m = Regex.Match(html,
                @"<td[^>]*>\s*" + Regex.Escape(lbl) + @"\s*</td>\s*<td[^>]*>\s*([^<]*)",
                RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var v = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
                if (!string.IsNullOrWhiteSpace(v) && v != "-") return v;
            }
        }
        return "";
    }

    /// <summary>Код химии -> читаемое имя.</summary>
    private static string Chem(string raw)
    {
        var u = raw.Trim().ToUpperInvariant();
        return u switch
        {
            "LION" or "LI-ION" or "LIION" => "Li-ion",
            "LIP" or "LIPO" or "LI-PO" => "Li-poly",
            "NIMH" => "Ni-MH",
            "NICD" => "Ni-Cd",
            "PBAC" => "Pb-Acid",
            "" => "",
            _ => raw.Trim(),
        };
    }
}
