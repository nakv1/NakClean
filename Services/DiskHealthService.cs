using System.Management;
using NakClean.Models;

namespace NakClean.Services;

/// <summary>
/// Здоровье дисков через современный Storage-провайдер WMI
/// (root\Microsoft\Windows\Storage) + прямое чтение SMART. Аналог того, что показывает
/// CrystalDiskInfo: модель, прошивка, интерфейс, функции, температура, часы работы,
/// износ, объёмы записи, полная таблица атрибутов и оценка состояния.
///
/// Часть данных (температура, часы, износ, SMART) требует прав администратора -
/// без них вернём то, что доступно, и пометим остальное.
/// </summary>
public static class DiskHealthService
{
    private const string Scope = @"\\.\root\Microsoft\Windows\Storage";

    public static List<DiskHealth> GetDisks()
    {
        var result = new List<DiskHealth>();
        try
        {
            var opts = new ConnectionOptions { Impersonation = ImpersonationLevel.Impersonate };
            var mscope = new ManagementScope(Scope, opts);
            mscope.Connect();

            var letters = DriveLetters(mscope);
            var pnp = PnpIds();

            var query = new ObjectQuery("SELECT * FROM MSFT_PhysicalDisk");
            using var searcher = new ManagementObjectSearcher(mscope, query);

            foreach (ManagementObject disk in searcher.Get())
            {
                using (disk)
                    result.Add(BuildDisk(disk, letters, pnp));
            }
        }
        catch
        {
            // Storage-провайдер недоступен - отдаём пустой список,
            // UI покажет понятное сообщение.
        }
        return result;
    }

    private static DiskHealth BuildDisk(ManagementObject disk,
        Dictionary<int, string> letters, Dictionary<int, string> pnp)
    {
        string name = GetString(disk, "FriendlyName") ?? Loc.I["tile_disk"];
        long size = GetLong(disk, "Size") ?? 0;
        ushort? busCode = GetUShort(disk, "BusType");
        string media = MediaTypeName(GetUShort(disk, "MediaType"));
        if (media == "-" && busCode == 17) media = "SSD"; // NVMe - всегда SSD
        string bus = BusTypeName(busCode);
        var (state, stateRaw) = HealthFrom(GetUShort(disk, "HealthStatus"));
        int deviceId = int.TryParse(GetString(disk, "DeviceId"), out var did) ? did : -1;
        string? firmware = Clean(GetString(disk, "FirmwareVersion"));
        string? serial = Clean(GetString(disk, "SerialNumber"));
        long? spindle = GetLong(disk, "SpindleSpeed");
        int? rpm = spindle is > 1 and < 100_000 ? (int)spindle : null;

        int? temp = null;
        long? poh = null;
        int? wear = null;
        long? readErr = null, writeErr = null;
        bool relUnavailable = true;

        try
        {
            foreach (ManagementObject rc in disk.GetRelated("MSFT_StorageReliabilityCounter"))
            {
                using (rc)
                {
                    temp = ToInt(GetUShort(rc, "Temperature"));
                    poh = GetLong(rc, "PowerOnHours");
                    wear = ToInt(GetUShort(rc, "Wear"));
                    readErr = GetLong(rc, "ReadErrorsTotal");
                    writeErr = GetLong(rc, "WriteErrorsTotal");
                    relUnavailable = false;
                }
                break;
            }
        }
        catch { relUnavailable = true; }

        // нулевые значения трактуем как «нет данных», чтобы не врать
        if (temp is 0) temp = null;

        string? iface = bus == "-" ? null : bus;
        long? cycles = null, unsafeOff = null, written = null, read = null;
        var features = new List<(string, bool)>();
        var attrs = new List<SmartAttr>();
        var issues = new List<DiskIssue>();
        var smartState = HealthState.Unknown;

        // Дочитываем недостающее напрямую из SMART (нужен админ):
        //  NVMe (17) - через журнал SMART/Health; SATA (11) / ATA (3) - через ATA-команды.
        if (deviceId >= 0)
        {
            var (dSerial, dFirmware) = SmartReader.ReadDescriptor(deviceId);
            serial = Clean(dSerial) ?? serial;
            firmware = Clean(dFirmware) ?? firmware;

            SmartReader.AtaIdentify? id = null;
            if (busCode is 11 or 3)
            {
                id = SmartReader.ReadIdentify(deviceId);
                if (id is not null)
                {
                    serial = id.Serial ?? serial;
                    firmware = id.Firmware ?? firmware;
                    if (id.SataCur > 0 || id.SataMax > 0)
                        iface = id.SataCur > 0 && id.SataMax > 0 && id.SataCur != id.SataMax
                            ? $"{SataName(id.SataCur)} | {SataName(id.SataMax)}"
                            : SataName(Math.Max(id.SataCur, id.SataMax));
                    if (id.Rpm == 1) { rpm = null; if (media == "-") media = "SSD"; }
                    else if (id.Rpm.HasValue) { rpm = id.Rpm; if (media == "-") media = "HDD"; }
                }
            }
            else if (busCode == 17 && pnp.TryGetValue(deviceId, out var pnpId))
            {
                iface = SmartReader.ReadPcieLink(pnpId) ?? iface;
            }

            SmartReader.SmartData? s = busCode switch
            {
                17 => SmartReader.ReadNvme(deviceId),
                11 or 3 => SmartReader.ReadAta(deviceId),
                _ => null,
            };
            if (s is not null)
            {
                // прямой SMART - авторитетнее WMI: перекрываем значения,
                // если они есть (WMI часто отдаёт 0 вместо реального показателя)
                if (s.TempC.HasValue) temp = s.TempC;
                if (s.PowerOnHours.HasValue) poh = s.PowerOnHours;
                if (s.WearPercent.HasValue) wear = s.WearPercent;
                if (temp.HasValue || poh.HasValue || wear.HasValue)
                    relUnavailable = false;

                cycles = s.PowerCycles;
                unsafeOff = s.UnsafeShutdowns;
                attrs = s.Attrs;
                if (busCode == 17)
                {
                    // единица данных NVMe = 1000 секторов по 512 байт
                    written = s.NvmeUnitsWritten * 512_000;
                    read = s.NvmeUnitsRead * 512_000;
                }
                else
                {
                    (written, read) = HostBytes(name, media, s.Attrs);
                }
                smartState = Evaluate(s, busCode == 17, media, issues);
            }

            // функции диска (как строка «Поддерживаемые функции» в CrystalDiskInfo)
            if (busCode == 17) { if (s is not null) features.Add(("S.M.A.R.T.", true)); }
            else if (id is not null) features.Add(("S.M.A.R.T.", id.SmartEnabled));
            if (media == "SSD" && SmartReader.ReadTrim(deviceId) is bool trim) features.Add(("TRIM", trim));
            if (id is not null && (id.SataMax > 0 || id.SataCur > 0)) features.Add(("NCQ", id.Ncq));
            if (SmartReader.ReadWriteCache(deviceId) is bool wc) features.Add(("cache", wc));
        }

        // итог - худшее из оценки Windows и нашей оценки по SMART
        if (smartState != HealthState.Unknown)
            state = state == HealthState.Unknown ? smartState : Worst(state, smartState);

        return new DiskHealth
        {
            Name = name,
            DeviceId = deviceId,
            MediaType = media,
            BusType = bus,
            SizeBytes = size,
            State = state,
            StateRaw = stateRaw,
            TemperatureC = temp,
            PowerOnHours = poh,
            WearPercent = wear,
            ReadErrors = readErr,
            WriteErrors = writeErr,
            Firmware = firmware,
            Serial = serial,
            Letters = letters.TryGetValue(deviceId, out var l) ? l : "",
            Interface = iface,
            Rpm = media == "SSD" ? null : rpm,
            PowerCycles = cycles,
            UnsafeShutdowns = unsafeOff,
            BytesWritten = written,
            BytesRead = read,
            IsNvme = busCode == 17,
            Features = features,
            Attributes = attrs,
            Issues = issues,
            ReliabilityUnavailable = relUnavailable,
        };
    }

    // ---------- оценка состояния по SMART (правила как у CrystalDiskInfo) ----------
    private static HealthState Evaluate(SmartReader.SmartData s, bool nvme, string media, List<DiskIssue> issues)
    {
        var st = HealthState.Healthy;
        void Flag(HealthState level, string key, string arg = "")
        {
            st = Worst(st, level);
            issues.Add(new DiskIssue(key, arg, level == HealthState.Unhealthy));
        }

        if (nvme)
        {
            if (s.CriticalWarning is > 0) Flag(HealthState.Unhealthy, "dhi_crit");
            if (s.AvailableSpare.HasValue && s.SpareThreshold is > 0 && s.AvailableSpare < s.SpareThreshold)
                Flag(HealthState.Unhealthy, "dhi_spare");
            if (s.MediaErrors is > 0) Flag(HealthState.Warning, "dhi_media", $"{s.MediaErrors:N0}");
        }
        else
        {
            foreach (var a in s.Attrs)
            {
                if (a.Thresh is > 0 && a.Cur is > 0 && a.Cur <= a.Thresh)
                    Flag(HealthState.Unhealthy, "dhi_thresh", SmartNames.Name(a.Id, false));
                long low = a.Raw & 0xFFFFFFFF;
                switch (a.Id)
                {
                    case 5 when low > 0: Flag(HealthState.Warning, "dhi_realloc", $"{low:N0}"); break;
                    case 197 when low > 0: Flag(HealthState.Warning, "dhi_pending", $"{low:N0}"); break;
                    case 198 when low > 0: Flag(HealthState.Warning, "dhi_uncorr", $"{low:N0}"); break;
                }
            }
        }

        // ресурс SSD: 10% и меньше - пора задуматься о замене
        if (media == "SSD" && s.WearPercent is int w && 100 - w <= 10)
            Flag(HealthState.Warning, "dhi_life", $"{Math.Max(0, 100 - w)}");

        return st;
    }

    private static HealthState Worst(HealthState a, HealthState b)
    {
        static int Rank(HealthState x) => x switch
        {
            HealthState.Unhealthy => 3, HealthState.Warning => 2, HealthState.Healthy => 1, _ => 0,
        };
        return Rank(a) >= Rank(b) ? a : b;
    }

    /// <summary>
    /// Объём записи/чтения для SATA: единицы атрибутов 241/242/246 у каждого производителя свои.
    /// Пересчитываем только для известных; для остальных честно не показываем.
    /// </summary>
    private static (long? written, long? read) HostBytes(string model, string media, List<SmartAttr> attrs)
    {
        long? Raw(int id) => attrs.FirstOrDefault(a => a.Id == id)?.Raw;
        string m = model.ToUpperInvariant();

        long unit = 0;
        if (m.Contains("SAMSUNG")) unit = 512;
        else if (m.Contains("INTEL")) unit = 32L * 1024 * 1024;
        else if (m.Contains("KINGSTON")) unit = 1024L * 1024 * 1024;
        else if (m.Contains("CRUCIAL") || m.Contains("MICRON") || m.StartsWith("CT"))
        {
            long? w = Raw(246);
            return (w * 512, null);
        }
        else if (media == "HDD" && (m.StartsWith("ST") || m.Contains("SEAGATE"))) unit = 512;

        if (unit == 0) return (null, null);
        return (Raw(241) * unit, Raw(242) * unit);
    }

    private static string SataName(int gen) => gen switch
    {
        1 => "SATA/150", 2 => "SATA/300", 3 => "SATA/600", _ => "SATA",
    };

    /// <summary>Буквы разделов для каждого физического диска: «C: D:».</summary>
    private static Dictionary<int, string> DriveLetters(ManagementScope scope)
    {
        var map = new Dictionary<int, List<char>>();
        try
        {
            using var s = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT DiskNumber, DriveLetter FROM MSFT_Partition"));
            foreach (ManagementObject p in s.Get())
                using (p)
                {
                    int n = (int)(GetLong(p, "DiskNumber") ?? -1);
                    char c = (char)(GetUShort(p, "DriveLetter") ?? 0);
                    if (n < 0 || !char.IsLetter(c)) continue;
                    if (!map.TryGetValue(n, out var list)) map[n] = list = new();
                    list.Add(char.ToUpperInvariant(c));
                }
        }
        catch { }
        return map.ToDictionary(kv => kv.Key, kv => string.Join(" ", kv.Value.OrderBy(c => c).Select(c => $"{c}:")));
    }

    /// <summary>PnP-идентификаторы дисков (нужны, чтобы найти PCIe-контроллер NVMe).</summary>
    private static Dictionary<int, string> PnpIds()
    {
        var map = new Dictionary<int, string>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Index, PNPDeviceID FROM Win32_DiskDrive");
            foreach (ManagementObject d in s.Get())
                using (d)
                {
                    int i = (int)(GetLong(d, "Index") ?? -1);
                    string? id = GetString(d, "PNPDeviceID");
                    if (i >= 0 && id is not null) map[i] = id;
                }
        }
        catch { }
        return map;
    }

    private static string? Clean(string? s)
    {
        s = s?.Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    // ---------- разбор значений ----------
    private static string MediaTypeName(ushort? v) => v switch
    {
        3 => "HDD",
        4 => "SSD",
        5 => "SCM",
        _ => "-",
    };

    private static string BusTypeName(ushort? v) => v switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "1394", 5 => "SSA",
        6 => "Fibre", 7 => "USB", 8 => "RAID", 9 => "iSCSI", 10 => "SAS",
        11 => "SATA", 12 => "SD", 13 => "MMC", 17 => "NVMe", _ => "-",
    };

    private static (HealthState, string) HealthFrom(ushort? v) => v switch
    {
        0 => (HealthState.Healthy, "Здоров"),
        1 => (HealthState.Warning, "Предупреждение"),
        2 => (HealthState.Unhealthy, "Проблемы"),
        _ => (HealthState.Unknown, "Неизвестно"),
    };

    private static string? GetString(ManagementBaseObject o, string p)
    {
        try { return o[p]?.ToString(); } catch { return null; }
    }
    private static long? GetLong(ManagementBaseObject o, string p)
    {
        try { return o[p] is null ? null : Convert.ToInt64(o[p]); } catch { return null; }
    }
    private static ushort? GetUShort(ManagementBaseObject o, string p)
    {
        try { return o[p] is null ? null : Convert.ToUInt16(o[p]); } catch { return null; }
    }
    private static int? ToInt(ushort? v) => v.HasValue ? v.Value : null;
}
