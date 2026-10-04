using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using Microsoft.Win32;

namespace NakClean.Services;

// ---------- сырые сведения о железе (без текста интерфейса - его собирает ViewModel на нужном языке) ----------

public sealed class BoardInfo
{
    public string Vendor { get; set; } = "";      // производитель ПК/ноутбука
    public string Model { get; set; } = "";       // модель ПК/ноутбука
    public string Board { get; set; } = "";       // системная плата
    public string Bios { get; set; } = "";
    public DateTime? BiosDate { get; set; }
    public bool? Uefi { get; set; }
    public bool? SecureBoot { get; set; }
    public bool? TpmPresent { get; set; }
    public string TpmVersion { get; set; } = "";
}

public sealed class CpuDetail
{
    public string Name { get; set; } = "";
    public int Cores { get; set; }
    public int Threads { get; set; }
    public int MaxMhz { get; set; }
    public int L2Kb { get; set; }
    public int L3Kb { get; set; }
    public string Socket { get; set; } = "";
    public bool? Virtualization { get; set; }
    public List<string> Instructions { get; } = new();
}

public sealed record RamSlot(string Slot, long Bytes, string Vendor, string Part, int RatedMts, int ConfiguredMts, string Type);

public sealed class MemoryDetail
{
    public List<RamSlot> Slots { get; } = new();
    public int TotalSlots { get; set; }
    public long MaxBytes { get; set; }
}

public sealed record GpuDetail(string Name, long Vram, string Driver, DateTime? DriverDate, int Width, int Height, int Hz);
public sealed record MonitorDetail(string Vendor, string Model, int Year, double Inches);
public sealed record NetDetail(string Name, bool Connected, long SpeedBps, string Mac, string Ip);
public sealed record ProblemDevice(string Name, int Code);

public sealed class OsDetail
{
    public string Caption { get; set; } = "";
    public string Version { get; set; } = "";     // 24H2
    public string Build { get; set; } = "";       // 26100.4061
    public string Arch { get; set; } = "";
    public DateTime? Installed { get; set; }
}

public sealed class HardwareInfo
{
    public OsDetail Os { get; } = new();
    public BoardInfo Board { get; } = new();
    public CpuDetail Cpu { get; } = new();
    public MemoryDetail Memory { get; } = new();
    public List<GpuDetail> Gpus { get; } = new();
    public List<MonitorDetail> Monitors { get; } = new();
    public List<NetDetail> Network { get; } = new();
    public List<string> Audio { get; } = new();
    public List<ProblemDevice> Problems { get; } = new();
}

/// <summary>
/// Подробные сведения о компьютере (как раздел «Компьютер» в AIDA64) - только штатными средствами
/// Windows: WMI, реестр, сведения процессора. Без драйверов и сторонних библиотек.
/// Каждый раздел читается отдельно: если один не прочитался, остальные всё равно будут.
/// </summary>
public static class HardwareInfoService
{
    public static HardwareInfo Collect()
    {
        var hw = new HardwareInfo();
        Try(() => ReadOs(hw.Os));
        Try(() => ReadBoard(hw.Board));
        Try(() => ReadCpu(hw.Cpu));
        Try(() => ReadMemory(hw.Memory));
        Try(() => ReadGpus(hw.Gpus));
        Try(() => ReadMonitors(hw.Monitors));
        Try(() => ReadNetwork(hw.Network));
        Try(() => ReadAudio(hw.Audio));
        Try(() => hw.Problems.AddRange(ProblemDevices()));
        return hw;
    }

    private static void Try(Action a) { try { a(); } catch { } }

    // ---------- Windows ----------
    private static void ReadOs(OsDetail os)
    {
        foreach (var o in Query("SELECT Caption, InstallDate FROM Win32_OperatingSystem"))
        {
            os.Caption = Str(o, "Caption").Replace("Майкрософт ", "").Replace("Microsoft ", "").Trim();
            try { os.Installed = ManagementDateTimeConverter.ToDateTime(Str(o, "InstallDate")); } catch { }
        }
        // не из WMI: там разрядность на языке Windows («64-разрядная») - в английском интерфейсе было бы по-русски
        os.Arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64", Architecture.Arm64 => "ARM64", Architecture.X86 => "x86",
            var a => a.ToString(),
        };
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        os.Version = k?.GetValue("DisplayVersion") as string ?? "";
        string build = k?.GetValue("CurrentBuild") as string ?? "";
        os.Build = k?.GetValue("UBR") is int ubr && build.Length > 0 ? $"{build}.{ubr}" : build;
    }

    // ---------- плата, BIOS, безопасность ----------
    [DllImport("kernel32.dll")]
    private static extern bool GetFirmwareType(out int type);   // 1 = старый BIOS, 2 = UEFI

    private static void ReadBoard(BoardInfo b)
    {
        foreach (var o in Query("SELECT Manufacturer, Model FROM Win32_ComputerSystem"))
        {
            b.Vendor = Clean(Str(o, "Manufacturer"));
            b.Model = Clean(Str(o, "Model"));
            // часть производителей дублирует модель: «TUF Gaming FX505DT_FX505DT» → «TUF Gaming FX505DT»
            int us = b.Model.LastIndexOf('_');
            if (us > 0 && b.Model[..us].EndsWith(b.Model[(us + 1)..], StringComparison.OrdinalIgnoreCase))
                b.Model = b.Model[..us];
        }
        foreach (var o in Query("SELECT Manufacturer, Product FROM Win32_BaseBoard"))
            b.Board = string.Join(" ", new[] { Clean(Str(o, "Manufacturer")), Clean(Str(o, "Product")) }.Where(s => s.Length > 0));
        foreach (var o in Query("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS"))
        {
            b.Bios = string.Join(" ", new[] { Clean(Str(o, "Manufacturer")), Clean(Str(o, "SMBIOSBIOSVersion")) }.Where(s => s.Length > 0));
            try { b.BiosDate = ManagementDateTimeConverter.ToDateTime(Str(o, "ReleaseDate")); } catch { }
        }

        try { if (GetFirmwareType(out int t)) b.Uefi = t == 2 ? true : t == 1 ? false : null; } catch { }

        // безопасная загрузка: значение есть только у UEFI; на старом BIOS её не бывает
        using (var sb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State"))
            b.SecureBoot = sb?.GetValue("UEFISecureBootEnabled") is int v ? v == 1 : b.Uefi == false ? false : null;

        try
        {
            var scope = new ManagementScope(@"\\.\root\cimv2\Security\MicrosoftTpm");
            scope.Connect();
            using var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT IsEnabled_InitialValue, SpecVersion FROM Win32_Tpm"));
            b.TpmPresent = false;
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    bool enabled = o["IsEnabled_InitialValue"] is bool e && e;
                    b.TpmPresent = enabled;
                    b.TpmVersion = Str(o, "SpecVersion").Split(',')[0].Trim();   // «2.0, 0, 1.59» → «2.0»
                }
        }
        catch { b.TpmPresent = null; }
    }

    // ---------- процессор ----------
    private static void ReadCpu(CpuDetail c)
    {
        foreach (var o in Query("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L2CacheSize, L3CacheSize, SocketDesignation, VirtualizationFirmwareEnabled FROM Win32_Processor"))
        {
            c.Name = Clean(Str(o, "Name").Replace("(R)", "").Replace("(TM)", "").Replace("(tm)", ""));
            c.Cores += Int(o, "NumberOfCores");
            c.Threads += Int(o, "NumberOfLogicalProcessors");
            c.MaxMhz = Int(o, "MaxClockSpeed");
            c.L2Kb += Int(o, "L2CacheSize");
            c.L3Kb += Int(o, "L3CacheSize");
            c.Socket = Clean(Str(o, "SocketDesignation"));
            if (o["VirtualizationFirmwareEnabled"] is bool v) c.Virtualization = v;
        }
        // если уже работает гипервизор (Hyper-V, WSL2, песочница) - Windows отвечает «выключено», хотя включено
        foreach (var o in Query("SELECT HypervisorPresent FROM Win32_ComputerSystem"))
            if (o["HypervisorPresent"] is bool h && h) c.Virtualization = true;

        if (Sse42.IsSupported) c.Instructions.Add("SSE4.2");
        if (Avx.IsSupported) c.Instructions.Add("AVX");
        if (Avx2.IsSupported) c.Instructions.Add("AVX2");
        if (Fma.IsSupported) c.Instructions.Add("FMA3");
        if (Avx512F.IsSupported) c.Instructions.Add("AVX-512");
        if (System.Runtime.Intrinsics.X86.Aes.IsSupported) c.Instructions.Add("AES-NI");
    }

    // ---------- память по планкам ----------
    private static void ReadMemory(MemoryDetail m)
    {
        foreach (var o in Query("SELECT MemoryDevices, MaxCapacityEx FROM Win32_PhysicalMemoryArray"))
        {
            m.TotalSlots += Int(o, "MemoryDevices");
            m.MaxBytes += Long(o, "MaxCapacityEx") * 1024;   // в килобайтах
        }
        var raw = new List<(string loc, string bank, RamSlot slot)>();
        foreach (var o in Query("SELECT DeviceLocator, BankLabel, Capacity, Manufacturer, PartNumber, Speed, ConfiguredClockSpeed, SMBIOSMemoryType, FormFactor FROM Win32_PhysicalMemory"))
        {
            string type = MemType(Int(o, "SMBIOSMemoryType"));
            if (Int(o, "FormFactor") == 12) type = (type + " SO-DIMM").Trim();
            raw.Add((Clean(Str(o, "DeviceLocator")), Clean(Str(o, "BankLabel")),
                new RamSlot("", Long(o, "Capacity"), RamVendor(Clean(Str(o, "Manufacturer"))),
                    Clean(Str(o, "PartNumber")), Int(o, "Speed"), Int(o, "ConfiguredClockSpeed"), type)));
        }

        // имя слота: у части ноутбуков у всех планок одинаковое («DIMM 0») - тогда добавляем банк или номер
        bool dupLoc = raw.GroupBy(r => r.loc).Any(g => g.Count() > 1);
        bool banksOk = raw.All(r => r.bank.Length > 0) && raw.Select(r => r.bank).Distinct().Count() == raw.Count;
        for (int i = 0; i < raw.Count; i++)
        {
            var (loc, bank, slot) = raw[i];
            string name = !dupLoc && loc.Length > 0 ? loc
                : banksOk ? (loc.Length > 0 ? $"{loc} · {bank}" : bank)
                : $"#{i + 1}";
            m.Slots.Add(slot with { Slot = name });
        }
        if (m.TotalSlots < m.Slots.Count) m.TotalSlots = m.Slots.Count;
    }

    private static string MemType(int smbios) => smbios switch
    {
        20 => "DDR", 21 => "DDR2", 24 => "DDR3", 26 => "DDR4", 29 => "LPDDR3",
        30 => "LPDDR4", 34 => "DDR5", 35 => "LPDDR5", _ => "",
    };

    // часть плат отдаёт производителя кодом JEDEC вместо названия
    private static string RamVendor(string raw)
    {
        string code = raw.Length >= 4 ? raw[..4].ToUpperInvariant() : raw.ToUpperInvariant();
        return code switch
        {
            "80CE" or "00CE" => "Samsung",
            "80AD" or "00AD" => "SK hynix",
            "802C" or "002C" => "Micron",
            "859B" => "Crucial",
            "0198" or "9801" => "Kingston",
            "04CD" or "CD04" => "G.Skill",
            "029E" or "9E02" => "Corsair",
            "04CB" => "A-DATA",
            "0B01" => "Apacer",
            _ => raw,
        };
    }

    // ---------- видеокарты ----------
    private static void ReadGpus(List<GpuDetail> list)
    {
        foreach (var o in Query("SELECT Name, DriverVersion, DriverDate, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController"))
        {
            string name = Clean(Str(o, "Name"));
            if (name.Length == 0 || name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)) continue;
            DateTime? date = null;
            try { date = ManagementDateTimeConverter.ToDateTime(Str(o, "DriverDate")); } catch { }
            list.Add(new GpuDetail(name, VideoMemory(name), Str(o, "DriverVersion"), date,
                Int(o, "CurrentHorizontalResolution"), Int(o, "CurrentVerticalResolution"), Int(o, "CurrentRefreshRate")));
        }
    }

    /// <summary>
    /// Настоящий объём видеопамяти из реестра драйвера. WMI (AdapterRAM) хранит его 32-битным числом
    /// и для карт больше 4 ГБ показывает 4 ГБ. 0 - не нашли.
    /// </summary>
    public static long VideoMemory(string adapterName)
    {
        const string cls = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        long best = 0;
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(cls);
            if (root is null) return 0;
            foreach (var sub in root.GetSubKeyNames())
            {
                if (sub.Length != 4 || !sub.All(char.IsDigit)) continue;
                try
                {
                    using var k = root.OpenSubKey(sub);
                    if (k is null || !string.Equals(k.GetValue("DriverDesc") as string, adapterName, StringComparison.OrdinalIgnoreCase)) continue;
                    long v = RegNumber(k.GetValue("HardwareInformation.qwMemorySize"));
                    if (v <= 0) v = RegNumber(k.GetValue("HardwareInformation.MemorySize"));
                    if (v > best) best = v;
                }
                catch { }
            }
        }
        catch { }
        return best;
    }

    private static long RegNumber(object? v) => v switch
    {
        long l => l,
        int i => (uint)i,
        byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
        byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
        _ => 0,
    };

    // ---------- мониторы ----------
    private static void ReadMonitors(List<MonitorDetail> list)
    {
        var scope = new ManagementScope(@"\\.\root\wmi");
        scope.Connect();
        var sizes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        using (var s = new ManagementObjectSearcher(scope, new ObjectQuery(
                   "SELECT InstanceName, MaxHorizontalImageSize, MaxVerticalImageSize FROM WmiMonitorBasicDisplayParams")))
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    double w = Int(o, "MaxHorizontalImageSize"), h = Int(o, "MaxVerticalImageSize");   // в сантиметрах
                    if (w > 0 && h > 0) sizes[Str(o, "InstanceName")] = Math.Sqrt(w * w + h * h) / 2.54;
                }

        using var ids = new ManagementObjectSearcher(scope, new ObjectQuery(
            "SELECT InstanceName, ManufacturerName, UserFriendlyName, YearOfManufacture FROM WmiMonitorID"));
        foreach (ManagementObject o in ids.Get())
            using (o)
            {
                string code = Chars(o["ManufacturerName"]);
                sizes.TryGetValue(Str(o, "InstanceName"), out double inches);
                list.Add(new MonitorDetail(MonitorVendor(code), Chars(o["UserFriendlyName"]), Int(o, "YearOfManufacture"), inches));
            }
    }

    private static string Chars(object? v) => v is ushort[] a
        ? new string(a.Where(c => c != 0).Select(c => (char)c).ToArray()).Trim()
        : "";

    private static string MonitorVendor(string code) => code.ToUpperInvariant() switch
    {
        "SAM" or "SEC" => "Samsung", "GSM" or "LGD" => "LG", "AUO" => "AU Optronics", "BOE" => "BOE",
        "CMN" or "INL" => "Innolux", "DEL" => "Dell", "AUS" => "ASUS", "ACR" => "Acer", "HWP" or "HPN" => "HP",
        "LEN" => "Lenovo", "BNQ" => "BenQ", "AOC" => "AOC", "MSI" => "MSI", "SHP" => "Sharp", "PHL" => "Philips",
        "VSC" => "ViewSonic", "GBT" => "Gigabyte", "IVM" => "iiyama", "HKC" => "HKC", "XMI" => "Xiaomi",
        "APP" => "Apple", "SNY" => "Sony", "NEC" => "NEC", "EIZ" or "ENC" => "EIZO", "CSO" => "CSOT", "TMA" => "Tianma",
        _ => code,
    };

    // ---------- сеть ----------
    private static void ReadNetwork(List<NetDetail> list)
    {
        var ips = new Dictionary<int, string>();
        foreach (var o in Query("SELECT Index, IPAddress FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE"))
            if (o["IPAddress"] is string[] a && a.FirstOrDefault(x => x.Contains('.')) is { } v4)
                ips[Int(o, "Index")] = v4;

        foreach (var o in Query("SELECT Index, Name, MACAddress, Speed, NetConnectionStatus, PNPDeviceID FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE"))
        {
            // только настоящие платы (PCI/USB); виртуальные адаптеры VPN, Hyper-V и т.п. не показываем
            string pnp = Str(o, "PNPDeviceID");
            if (!pnp.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase) && !pnp.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)) continue;
            bool connected = Int(o, "NetConnectionStatus") == 2;
            long speed = Long(o, "Speed");
            if (!connected || speed >= long.MaxValue / 2) speed = 0;
            list.Add(new NetDetail(Clean(Str(o, "Name")), connected, speed, Str(o, "MACAddress"),
                ips.TryGetValue(Int(o, "Index"), out var ip) ? ip : ""));
        }
    }

    // ---------- звук ----------
    private static void ReadAudio(List<string> list)
    {
        foreach (var o in Query("SELECT Name FROM Win32_SoundDevice"))
        {
            string n = Clean(Str(o, "Name"));
            if (n.Length > 0 && !list.Contains(n)) list.Add(n);
        }
    }

    // ---------- устройства с ошибками ----------
    /// <summary>Устройства, у которых Windows видит ошибку (код из Диспетчера устройств). 22 = отключено вручную.</summary>
    public static List<ProblemDevice> ProblemDevices()
    {
        var list = new List<ProblemDevice>();
        foreach (var o in Query("SELECT Name, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0"))
        {
            string n = Clean(Str(o, "Name"));
            list.Add(new ProblemDevice(n.Length > 0 ? n : "?", Int(o, "ConfigManagerErrorCode")));
        }
        return list;
    }

    // ---------- помощники ----------
    private static List<ManagementBaseObject> Query(string wql)
    {
        var list = new List<ManagementBaseObject>();
        using var s = new ManagementObjectSearcher(wql);
        foreach (var o in s.Get()) list.Add(o);
        return list;
    }

    private static string Str(ManagementBaseObject o, string p)
    {
        try { return o[p]?.ToString() ?? ""; } catch { return ""; }
    }
    private static int Int(ManagementBaseObject o, string p)
    {
        try { return o[p] is null ? 0 : Convert.ToInt32(o[p], CultureInfo.InvariantCulture); } catch { return 0; }
    }
    private static long Long(ManagementBaseObject o, string p)
    {
        try { return o[p] is null ? 0 : (long)Math.Min(Convert.ToDecimal(o[p], CultureInfo.InvariantCulture), long.MaxValue); } catch { return 0; }
    }

    // заглушки производителей вместо реальных данных («To be filled by O.E.M.», «Default string»...)
    private static string Clean(string s)
    {
        s = s.Trim();
        string l = s.ToLowerInvariant();
        if (l.Length == 0 || l.Contains("to be filled") || l.Contains("default string") || l == "system product name"
            || l == "system manufacturer" || l == "not applicable" || l == "unknown" || l == "none" || l == "0")
            return "";
        return s;
    }
}
