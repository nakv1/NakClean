using System.Management;
using System.Text;
using System.Text.RegularExpressions;

namespace NakClean.Services;

/// <summary>
/// Анализ дисков для оптимизации: тип носителя (SSD/HDD) + дата последней оптимизации
/// (из журнала событий Windows, как «Оптимизация дисков») + нужна ли оптимизация.
/// HDD → дефрагментация, SSD → TRIM. Рекомендуется, если прошло ≥ 7 дней или дата неизвестна.
/// </summary>
public static class DefragService
{
    private static readonly Regex DriveRx = new(@"\(([A-Za-z]):\)", RegexOptions.Compiled);

    public static Task<CheckResult> AnalyzeAll() => Task.Run(() =>
    {
        var media = MediaByLetter();
        var lastOpt = LastOptimizedByLetter();

        var sb = new StringBuilder();
        sb.Append(Loc.I["mnt_defrag_head"]);
        bool any = false, actionNeeded = false;

        foreach (var kv in media.OrderBy(k => k.Key))
        {
            char letter = kv.Key;
            bool ssd = kv.Value == 4;
            string op = Loc.I[ssd ? "mnt_op_ssd" : "mnt_op_hdd"];

            string days;
            bool needs;
            if (lastOpt.TryGetValue(letter, out var dt))
            {
                int d = (int)(DateTime.Now - dt).TotalDays;
                days = d <= 0 ? Loc.I["mnt_op_lasttoday"] : string.Format(Loc.I["mnt_op_lastdays"], d);
                needs = d >= 7;
            }
            else { days = Loc.I["mnt_op_lastunknown"]; needs = true; }

            if (needs) actionNeeded = true;
            sb.Append("\n• ").Append(string.Format(Loc.I["mnt_op_line"],
                letter, op, days, Loc.I[needs ? "mnt_op_need" : "mnt_op_ok"]));
            any = true;
        }

        if (!any) sb.Append('\n').Append(Loc.I["mnt_defrag_noinfo"]);
        return new CheckResult(sb.ToString(), actionNeeded);
    });

    /// <summary>Буква тома → тип носителя (4 = SSD, 3 = HDD, 0 = неизвестно).</summary>
    private static Dictionary<char, ushort> MediaByLetter()
    {
        var result = new Dictionary<char, ushort>();
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();

            var diskMedia = new Dictionary<uint, ushort>();
            using (var s = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT DeviceId, MediaType FROM MSFT_PhysicalDisk")))
                foreach (ManagementObject d in s.Get())
                    using (d)
                        if (uint.TryParse(d["DeviceId"]?.ToString(), out var num))
                            diskMedia[num] = Convert.ToUInt16(d["MediaType"] ?? (ushort)0);

            using var ps = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT DiskNumber, DriveLetter FROM MSFT_Partition"));
            foreach (ManagementObject p in ps.Get())
                using (p)
                {
                    var dl = p["DriveLetter"];
                    if (dl is null) continue;
                    char letter = Convert.ToChar(dl);
                    if (letter is '\0' or ' ') continue;
                    uint dn = Convert.ToUInt32(p["DiskNumber"]);
                    result[char.ToUpperInvariant(letter)] = diskMedia.TryGetValue(dn, out var v) ? v : (ushort)0;
                }
        }
        catch { }
        return result;
    }

    /// <summary>Дата последней оптимизации по букве тома (из событий Microsoft-Windows-Defrag, ID 258).</summary>
    private static Dictionary<char, DateTime> LastOptimizedByLetter()
    {
        var result = new Dictionary<char, DateTime>();
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT TimeGenerated, Message FROM Win32_NTLogEvent " +
                "WHERE Logfile='Application' AND SourceName='Microsoft-Windows-Defrag' AND EventCode=258");
            foreach (ManagementObject e in s.Get())
                using (e)
                    try
                    {
                        string msg = e["Message"] as string ?? "";
                        var m = DriveRx.Match(msg);
                        if (!m.Success) continue;
                        char letter = char.ToUpperInvariant(m.Groups[1].Value[0]);
                        var dt = ManagementDateTimeConverter.ToDateTime(e["TimeGenerated"]?.ToString());
                        if (!result.TryGetValue(letter, out var prev) || dt > prev) result[letter] = dt;
                    }
                    catch { }
        }
        catch { }
        return result;
    }
}
