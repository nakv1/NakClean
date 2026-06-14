using System.Management;
using NakClean.Models;

namespace NakClean.Services;

/// <summary>
/// Здоровье дисков через современный Storage-провайдер WMI
/// (root\Microsoft\Windows\Storage). Аналог того, что показывает CrystalDiskInfo:
/// модель, тип, температура, часы работы, износ, ошибки, статус S.M.A.R.T.
///
/// Часть данных (температура, часы, износ) лежит в счётчике надёжности и обычно
/// требует прав администратора - без них вернём то, что доступно, и пометим остальное.
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

            var query = new ObjectQuery("SELECT * FROM MSFT_PhysicalDisk");
            using var searcher = new ManagementObjectSearcher(mscope, query);

            foreach (ManagementObject disk in searcher.Get())
            {
                using (disk)
                    result.Add(BuildDisk(disk));
            }
        }
        catch
        {
            // Storage-провайдер недоступен - отдаём пустой список,
            // UI покажет понятное сообщение.
        }
        return result;
    }

    private static DiskHealth BuildDisk(ManagementObject disk)
    {
        string name = GetString(disk, "FriendlyName") ?? Loc.I["tile_disk"];
        long size = GetLong(disk, "Size") ?? 0;
        ushort? busCode = GetUShort(disk, "BusType");
        string media = MediaTypeName(GetUShort(disk, "MediaType"));
        if (media == "-" && busCode == 17) media = "SSD"; // NVMe - всегда SSD
        string bus = BusTypeName(busCode);
        var (state, stateRaw) = HealthFrom(GetUShort(disk, "HealthStatus"));
        int deviceId = int.TryParse(GetString(disk, "DeviceId"), out var did) ? did : -1;

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

        // Дочитываем недостающее напрямую из SMART (нужен админ):
        //  NVMe (17) - через журнал SMART/Health; SATA (11) / ATA (3) - через ATA pass-through.
        if (deviceId >= 0)
        {
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
            }
        }

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
            ReliabilityUnavailable = relUnavailable,
        };
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
