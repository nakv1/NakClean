using System.Diagnostics;
using System.Management;

namespace NakClean.Services;

/// <summary>Снимок одной видеокарты для «Обзора системы».</summary>
public sealed class GpuStat
{
    public required string Name { get; init; }
    public double UsagePercent { get; init; }
    public bool HasUsage { get; init; }
    public int? TempC { get; init; }
    public string Detail { get; init; } = "";
}

/// <summary>
/// Мониторинг видеокарт (поддержка нескольких GPU). Без сторонних библиотек:
///  • список адаптеров и VRAM - WMI Win32_VideoController;
///  • загрузка - счётчики производительности GPU Engine (WMI, уже форматированные %);
///  • температура (и точная загрузка/память для NVIDIA) - nvidia-smi, если он есть.
/// AMD/Intel температуру штатно не отдают - там покажем «-».
/// </summary>
public static class GpuService
{
    private sealed record Adapter(string Name, long Vram, string Vendor);
    private sealed record NvGpu(string Name, double Util, int Temp, int MemUsedMb, int MemTotalMb);

    public static List<GpuStat> Read()
    {
        var adapters = GetAdapters();
        if (adapters.Count == 0) return new List<GpuStat>();

        var usage = UsageByLuid();   // по убыванию загрузки
        // nvidia-smi дёргаем ТОЛЬКО если есть NVIDIA-адаптер - иначе на AMD/Intel ПК
        // это был бы бесполезный запуск процесса каждые несколько секунд.
        bool hasNvidia = adapters.Any(a => a.Vendor == "NVIDIA");
        var nv = hasNvidia ? NvidiaSmi() : new List<NvGpu>();

        var result = new List<GpuStat>();
        int usageIdx = 0, nvIdx = 0;

        foreach (var a in adapters)
        {
            if (a.Vendor == "NVIDIA" && nvIdx < nv.Count)
            {
                var g = nv[nvIdx++];
                result.Add(new GpuStat
                {
                    Name = a.Name,
                    UsagePercent = g.Util,
                    HasUsage = true,
                    TempC = g.Temp,
                    Detail = g.MemTotalMb > 0 ? $"{g.MemUsedMb} / {g.MemTotalMb} {Loc.I["u_mb"]}" : VramText(a.Vram),
                });
            }
            else
            {
                double? u = usageIdx < usage.Count ? usage[usageIdx++] : null;
                result.Add(new GpuStat
                {
                    Name = a.Name,
                    UsagePercent = u ?? 0,
                    HasUsage = u.HasValue,
                    TempC = null,
                    Detail = VramText(a.Vram),
                });
            }
        }
        return result;
    }

    // ---------- список адаптеров ----------
    private static List<Adapter> GetAdapters()
    {
        var list = new List<Adapter>();
        try
        {
            using var s = new ManagementObjectSearcher("root\\CIMV2",
                "SELECT Name, AdapterRAM, PNPDeviceID, AdapterCompatibility FROM Win32_VideoController");
            foreach (ManagementObject mo in s.Get())
            {
                using (mo)
                {
                    string name = mo["Name"]?.ToString() ?? Loc.I["tile_gpu"];
                    if (IsVirtual(name)) continue;

                    long vram = 0;
                    try { vram = Convert.ToInt64(mo["AdapterRAM"]); } catch { }

                    string pnp = mo["PNPDeviceID"]?.ToString() ?? "";
                    string compat = mo["AdapterCompatibility"]?.ToString() ?? "";
                    string vendor =
                        pnp.Contains("VEN_10DE") || compat.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "NVIDIA" :
                        pnp.Contains("VEN_1002") || compat.Contains("AMD", StringComparison.OrdinalIgnoreCase) || compat.Contains("ATI", StringComparison.OrdinalIgnoreCase) ? "AMD" :
                        pnp.Contains("VEN_8086") || compat.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "Intel" : "";

                    list.Add(new Adapter(name, vram, vendor));
                }
            }
        }
        catch { }
        return list;
    }

    private static bool IsVirtual(string name) =>
        name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Remote", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Mirror", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Parsec", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Meta ", StringComparison.OrdinalIgnoreCase);

    private static string VramText(long vram) =>
        vram > 0 ? $"VRAM {Format.Bytes(vram)}" : "";

    // ---------- загрузка по LUID (счётчики GPU Engine) ----------
    private static List<double> UsageByLuid()
    {
        var byLuid = new Dictionary<string, Dictionary<string, double>>();
        try
        {
            using var s = new ManagementObjectSearcher("root\\CIMV2",
                "SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine");
            foreach (ManagementObject mo in s.Get())
            {
                using (mo)
                {
                    string inst = mo["Name"]?.ToString() ?? "";
                    double util;
                    try { util = Convert.ToDouble(mo["UtilizationPercentage"]); } catch { continue; }

                    string luid = Between(inst, "luid_", "_phys");
                    string eng = After(inst, "engtype_");
                    if (luid.Length == 0) continue;

                    if (!byLuid.TryGetValue(luid, out var engs)) { engs = new(); byLuid[luid] = engs; }
                    engs.TryGetValue(eng, out double cur);
                    engs[eng] = cur + util;   // суммируем по процессам внутри движка
                }
            }
        }
        catch { }

        // загрузка одной карты = максимум по типам движков (как в Диспетчере задач)
        return byLuid.Values
            .Select(e => e.Count == 0 ? 0 : e.Values.Max())
            .Select(v => Math.Clamp(v, 0, 100))
            .OrderByDescending(v => v)
            .ToList();
    }

    // ---------- NVIDIA через nvidia-smi ----------
    private static List<NvGpu> NvidiaSmi()
    {
        var list = new List<NvGpu>();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name,utilization.gpu,temperature.gpu,memory.used,memory.total --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return list;
            string outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2500);

            foreach (var line in outp.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(',');
                if (parts.Length < 5) continue;
                string name = parts[0].Trim();
                double.TryParse(parts[1].Trim(), out double util);
                int.TryParse(parts[2].Trim(), out int temp);
                int.TryParse(parts[3].Trim(), out int memUsed);
                int.TryParse(parts[4].Trim(), out int memTotal);
                list.Add(new NvGpu(name, util, temp, memUsed, memTotal));
            }
        }
        catch { /* nvidia-smi нет - не NVIDIA или нет драйвера */ }
        return list;
    }

    private static string Between(string s, string a, string b)
    {
        int i = s.IndexOf(a, StringComparison.Ordinal);
        if (i < 0) return "";
        i += a.Length;
        int j = s.IndexOf(b, i, StringComparison.Ordinal);
        return j < 0 ? s[i..] : s[i..j];
    }

    private static string After(string s, string a)
    {
        int i = s.IndexOf(a, StringComparison.Ordinal);
        return i < 0 ? "" : s[(i + a.Length)..];
    }
}
