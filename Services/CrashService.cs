using System.Diagnostics.Eventing.Reader;
using System.Globalization;

namespace NakClean.Services;

public enum CrashKind { BlueScreen, SuddenOff }

/// <summary>Причина синего экрана в общих чертах - от неё зависит совет.</summary>
public enum CrashCause { Driver, Memory, Disk, Hardware, Video, Power, System, Unknown }

/// <summary>Один сбой: синий экран (код ошибки + параметры + дамп) или внезапное выключение без синего экрана.</summary>
public sealed record CrashEvent(DateTime When, CrashKind Kind, uint Code, ulong[] Params, string? DumpPath);

/// <summary>
/// Сбои из журнала «Система»:
///  • 1001 (WER-SystemErrorReporting) - «перезагрузка после синего экрана»: код, параметры, путь к дампу;
///  • 41 (Kernel-Power) - система выключилась неправильно. Код ≠ 0 - это тоже синий экран
///    (берём, если для него нет записи 1001 - дамп мог быть выключен); код 0 - пропало питание,
///    зависание с выключением кнопкой, Reset.
/// Только чтение журнала, права администратора не нужны.
/// </summary>
public static class CrashService
{
    public static List<CrashEvent> Read(int days = 365)
    {
        const string xpath =
            "*[System[((Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001) or " +
            "(Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41)) and " +
            "TimeCreated[timediff(@SystemTime) <= {0}]]]";
        var bsods = new List<CrashEvent>();
        var power = new List<CrashEvent>();
        try
        {
            var q = new EventLogQuery("System", PathType.LogName,
                string.Format(CultureInfo.InvariantCulture, xpath, (long)days * 24 * 3600 * 1000)) { ReverseDirection = true };
            using var reader = new EventLogReader(q);
            for (int n = 0; n < 500; n++)
            {
                using var e = reader.ReadEvent();
                if (e is null) break;
                if (e.TimeCreated is not DateTime when) continue;
                var p = e.Properties;
                if (e.Id == 1001 && p.Count > 0 && p[0].Value is string s && TryParseBugcheck(s, out uint code, out var args))
                    bsods.Add(new CrashEvent(when, CrashKind.BlueScreen, code, args, p.Count > 1 ? p[1].Value as string : null));
                else if (e.Id == 41 && p.Count > 4)
                {
                    uint kpCode = Normalize(ToULong(p[0].Value));
                    var kpArgs = new[] { ToULong(p[1].Value), ToULong(p[2].Value), ToULong(p[3].Value), ToULong(p[4].Value) };
                    power.Add(new CrashEvent(when, kpCode == 0 ? CrashKind.SuddenOff : CrashKind.BlueScreen, kpCode, kpArgs, null));
                }
            }
        }
        catch { }

        // 41 с кодом ошибки - тот же синий экран, что и 1001 рядом по времени: не дублируем
        var result = new List<CrashEvent>(bsods);
        foreach (var p in power)
        {
            if (p.Kind == CrashKind.BlueScreen &&
                bsods.Any(b => b.Code == p.Code && Math.Abs((b.When - p.When).TotalMinutes) <= 15))
                continue;
            result.Add(p);
        }
        return result.OrderByDescending(c => c.When).ToList();
    }

    // «0x00000133 (0x0000000000000000, 0x0000000000000500, ...)»
    private static bool TryParseBugcheck(string s, out uint code, out ulong[] args)
    {
        code = 0; args = Array.Empty<ulong>();
        var head = s.Split(' ', '(')[0].Trim();
        if (!TryHex(head, out ulong c)) return false;
        code = Normalize(c);
        int open = s.IndexOf('('), close = s.LastIndexOf(')');
        if (open >= 0 && close > open)
            args = s[(open + 1)..close].Split(',').Select(x => TryHex(x.Trim(), out ulong v) ? v : 0UL).ToArray();
        return true;
    }

    private static bool TryHex(string s, out ulong v)
    {
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return ulong.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);
    }

    private static ulong ToULong(object? o)
    {
        try { return o switch { null => 0, ulong u => u, long l => unchecked((ulong)l), _ => Convert.ToUInt64(o) }; }
        catch { return 0; }
    }

    // 0x1000007F и т.п. - «расширенный» вариант того же кода 0x7F
    private static uint Normalize(ulong code) =>
        (code & 0xF0000000) == 0x10000000 ? (uint)(code & 0x0FFFFFFF) : (uint)code;

    // ---------- справочник кодов: официальное имя + общая причина ----------
    private static readonly Dictionary<uint, (string Name, CrashCause Cause)> Codes = new()
    {
        [0x0A] = ("IRQL_NOT_LESS_OR_EQUAL", CrashCause.Driver),
        [0x19] = ("BAD_POOL_HEADER", CrashCause.Driver),
        [0x1A] = ("MEMORY_MANAGEMENT", CrashCause.Memory),
        [0x1E] = ("KMODE_EXCEPTION_NOT_HANDLED", CrashCause.Driver),
        [0x24] = ("NTFS_FILE_SYSTEM", CrashCause.Disk),
        [0x3B] = ("SYSTEM_SERVICE_EXCEPTION", CrashCause.Driver),
        [0x4E] = ("PFN_LIST_CORRUPT", CrashCause.Memory),
        [0x50] = ("PAGE_FAULT_IN_NONPAGED_AREA", CrashCause.Memory),
        [0x77] = ("KERNEL_STACK_INPAGE_ERROR", CrashCause.Disk),
        [0x7A] = ("KERNEL_DATA_INPAGE_ERROR", CrashCause.Disk),
        [0x7B] = ("INACCESSIBLE_BOOT_DEVICE", CrashCause.Disk),
        [0x7E] = ("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", CrashCause.Driver),
        [0x7F] = ("UNEXPECTED_KERNEL_MODE_TRAP", CrashCause.Hardware),
        [0x9C] = ("MACHINE_CHECK_EXCEPTION", CrashCause.Hardware),
        [0x9F] = ("DRIVER_POWER_STATE_FAILURE", CrashCause.Power),
        [0xA0] = ("INTERNAL_POWER_ERROR", CrashCause.Power),
        [0xBE] = ("ATTEMPTED_WRITE_TO_READONLY_MEMORY", CrashCause.Driver),
        [0xC2] = ("BAD_POOL_CALLER", CrashCause.Driver),
        [0xC5] = ("DRIVER_CORRUPTED_EXPOOL", CrashCause.Driver),
        [0xD1] = ("DRIVER_IRQL_NOT_LESS_OR_EQUAL", CrashCause.Driver),
        [0xEA] = ("THREAD_STUCK_IN_DEVICE_DRIVER", CrashCause.Video),
        [0xEF] = ("CRITICAL_PROCESS_DIED", CrashCause.System),
        [0xF4] = ("CRITICAL_OBJECT_TERMINATION", CrashCause.Disk),
        [0xFC] = ("ATTEMPTED_EXECUTE_OF_NOEXECUTE_MEMORY", CrashCause.Driver),
        [0x101] = ("CLOCK_WATCHDOG_TIMEOUT", CrashCause.Hardware),
        [0x109] = ("CRITICAL_STRUCTURE_CORRUPTION", CrashCause.Memory),
        [0x10D] = ("WDF_VIOLATION", CrashCause.Driver),
        [0x113] = ("VIDEO_DXGKRNL_FATAL_ERROR", CrashCause.Video),
        [0x116] = ("VIDEO_TDR_FAILURE", CrashCause.Video),
        [0x117] = ("VIDEO_TDR_TIMEOUT_DETECTED", CrashCause.Video),
        [0x119] = ("VIDEO_SCHEDULER_INTERNAL_ERROR", CrashCause.Video),
        [0x124] = ("WHEA_UNCORRECTABLE_ERROR", CrashCause.Hardware),
        [0x12B] = ("FAULTY_HARDWARE_CORRUPTED_PAGE", CrashCause.Memory),
        [0x133] = ("DPC_WATCHDOG_VIOLATION", CrashCause.Driver),
        [0x139] = ("KERNEL_SECURITY_CHECK_FAILURE", CrashCause.Driver),
        [0x13A] = ("KERNEL_MODE_HEAP_CORRUPTION", CrashCause.Driver),
        [0x154] = ("UNEXPECTED_STORE_EXCEPTION", CrashCause.Disk),
        [0xC000021A] = ("STATUS_SYSTEM_PROCESS_TERMINATED", CrashCause.System),
    };

    public static (string? Name, CrashCause Cause) Describe(uint code) =>
        Codes.TryGetValue(code, out var d) ? d : (null, CrashCause.Unknown);

    /// <summary>Уточнение к частым кодам по параметрам (ключ Loc или null).</summary>
    public static string? NoteKey(CrashEvent c) => c.Code switch
    {
        0x7F when c.Params.Length > 0 && c.Params[0] == 8 => "cr_note_7f8",   // двойная ошибка
        0x133 => "cr_note_133",
        0x124 => "cr_note_124",
        _ => null,
    };
}
