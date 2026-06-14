using System.Management;

namespace NakClean.Services;

public sealed record CpuInfo(string Name, int Cores, int Threads, double BaseGhz);
public sealed record RamInfo(string Type, int Count, int EachGib, bool Same, int TotalGib, int Speed);
public sealed record OsInfo(string Caption, string Build, DateTime BootTime);

/// <summary>
/// Статичные сведения о железе (модель CPU, ядра/потоки/частота; тип и планки RAM).
/// Читается один раз при старте - для «Обзора системы». Через WMI, без сторонних библиотек.
/// </summary>
public static class SystemInfoService
{
    public static CpuInfo GetCpu()
    {
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor");
            foreach (ManagementObject mo in s.Get())
                using (mo)
                {
                    string name = CleanName(mo["Name"]?.ToString() ?? Loc.I["tile_cpu"]);
                    int cores = ToInt(mo["NumberOfCores"]);
                    int threads = ToInt(mo["NumberOfLogicalProcessors"]);
                    double ghz = ToInt(mo["MaxClockSpeed"]) / 1000.0;
                    return new CpuInfo(name, cores, threads, ghz);
                }
        }
        catch { }
        return new CpuInfo(Loc.I["tile_cpu"], Environment.ProcessorCount, Environment.ProcessorCount, 0);
    }

    public static RamInfo GetRam()
    {
        try
        {
            var caps = new List<long>();
            int speed = 0;
            string type = "";

            using var s = new ManagementObjectSearcher(
                "SELECT Capacity, Speed, SMBIOSMemoryType FROM Win32_PhysicalMemory");
            foreach (ManagementObject mo in s.Get())
                using (mo)
                {
                    long cap = 0;
                    try { cap = Convert.ToInt64(mo["Capacity"]); } catch { }
                    if (cap > 0) caps.Add(cap);

                    int sp = ToInt(mo["Speed"]);
                    if (sp > speed) speed = sp;

                    if (type.Length == 0) type = MemTypeName(ToInt(mo["SMBIOSMemoryType"]));
                }

            if (caps.Count == 0) return new RamInfo("", 0, 0, true, 0, 0);

            const long gib = 1024L * 1024 * 1024;
            bool same = caps.TrueForAll(c => c == caps[0]);
            return new RamInfo(type, caps.Count, (int)(caps[0] / gib), same, (int)(caps.Sum() / gib), speed);
        }
        catch { }
        return new RamInfo("", 0, 0, true, 0, 0);
    }

    /// <summary>ОС: название (без «Майкрософт»), номер сборки, время последней загрузки.</summary>
    public static OsInfo GetOs()
    {
        string cap = "Windows", build = "";
        DateTime boot = DateTime.Now;
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT Caption, BuildNumber, LastBootUpTime FROM Win32_OperatingSystem");
            foreach (ManagementObject mo in s.Get())
                using (mo)
                {
                    cap = (mo["Caption"]?.ToString() ?? "Windows")
                        .Replace("Майкрософт ", "").Replace("Microsoft ", "").Trim();
                    build = mo["BuildNumber"]?.ToString() ?? "";
                    try { boot = ManagementDateTimeConverter.ToDateTime(mo["LastBootUpTime"].ToString()); } catch { }
                    break;
                }
        }
        catch { }
        return new OsInfo(cap, build, boot);
    }

    // ---------- помощники ----------
    private static string CleanName(string name) => name
        .Replace("(R)", "").Replace("(TM)", "").Replace("(tm)", "")
        .Replace("  ", " ").Trim();

    private static string MemTypeName(int smbios) => smbios switch
    {
        20 => "DDR",
        21 => "DDR2",
        24 => "DDR3",
        26 => "DDR4",
        30 => "LPDDR4",
        34 => "DDR5",
        35 => "LPDDR5",
        _ => "",
    };

    private static int ToInt(object? v)
    {
        try { return v is null ? 0 : Convert.ToInt32(v); } catch { return 0; }
    }
}
