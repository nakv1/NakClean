using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;

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

        var usage = UsageByLuid();   // нагрузка по LUID видеокарты
        var luids = LuidsByName();   // имя видеокарты → её LUID (через DXGI)
        // nvidia-smi дёргаем ТОЛЬКО если есть NVIDIA-адаптер - иначе на AMD/Intel ПК
        // это был бы бесполезный запуск процесса каждые несколько секунд.
        bool hasNvidia = adapters.Any(a => a.Vendor == "NVIDIA");
        var nv = hasNvidia ? NvidiaSmi() : new List<NvGpu>();

        var result = new List<GpuStat>();
        int nvIdx = 0;

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
                // нагрузку берём строго своей карты; не сопоставили - честно «нет данных», а не чужая цифра
                double? u = luids.TryGetValue(a.Name, out var luid) && usage.TryGetValue(luid, out var v) ? v : null;
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
    private static readonly Dictionary<string, long> _vramCache = new();

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

                    // AdapterRAM 32-битный - больше 4 ГБ не покажет; настоящий объём берём из реестра драйвера
                    long vram = 0;
                    try { vram = Convert.ToInt64(mo["AdapterRAM"]); } catch { }
                    if (!_vramCache.TryGetValue(name, out long regVram))
                        _vramCache[name] = regVram = HardwareInfoService.VideoMemory(name);
                    if (regVram > vram) vram = regVram;

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
    private static Dictionary<string, double> UsageByLuid()
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
        return byLuid.ToDictionary(kv => kv.Key,
            kv => Math.Clamp(kv.Value.Count == 0 ? 0 : kv.Value.Values.Max(), 0, 100),
            StringComparer.OrdinalIgnoreCase);
    }

    // ---------- LUID видеокарт через DXGI (тот же номер, что в счётчиках «luid_0x..._0x...») ----------
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        void EnumAdapters(); void MakeWindowAssociation(); void GetWindowAssociation(); void CreateSwapChain(); void CreateSoftwareAdapter();
        [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        void EnumOutputs(); void GetDesc(); void CheckInterfaceSupport();
        [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object factory);

    private static Dictionary<string, string>? _luidCache;

    /// <summary>Имя видеокарты → LUID в виде «0x00000000_0x0000D1B4» (как в именах счётчиков GPU Engine).</summary>
    private static Dictionary<string, string> LuidsByName()
    {
        if (_luidCache != null) return _luidCache;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var iid = typeof(IDXGIFactory1).GUID;
            if (CreateDXGIFactory1(ref iid, out var obj) == 0 && obj is IDXGIFactory1 f)
            {
                for (uint i = 0; f.EnumAdapters1(i, out var a) == 0; i++)
                {
                    if (a.GetDesc1(out var d) == 0 && !string.IsNullOrWhiteSpace(d.Description))
                        map.TryAdd(d.Description.Trim(), $"0x{d.LuidHigh:X8}_0x{d.LuidLow:X8}");
                    Marshal.ReleaseComObject(a);
                }
                Marshal.ReleaseComObject(f);
            }
        }
        catch { }
        return _luidCache = map;
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
