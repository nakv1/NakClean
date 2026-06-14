using System.Runtime.InteropServices;

namespace NakClean.Services;

/// <summary>
/// Тонкая обёртка над Win32 API. Никаких внешних зависимостей -
/// только то, что есть в самой Windows.
/// </summary>
internal static class Native
{
    // ---------- Память ----------
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    /// <summary>Возвращает (всего байт, использовано байт, процент загрузки).</summary>
    public static (ulong total, ulong used, double percent) GetMemory()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m))
            return (0, 0, 0);
        ulong used = m.ullTotalPhys - m.ullAvailPhys;
        return (m.ullTotalPhys, used, m.dwMemoryLoad);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out long totalMemoryInKilobytes);

    /// <summary>Физически установленная ОЗУ в байтах (напр. 16 ГБ), а не доступная Windows (13.9 ГБ). 0 при ошибке.</summary>
    public static ulong GetInstalledMemoryBytes()
        => GetPhysicallyInstalledSystemMemory(out long kb) && kb > 0 ? (ulong)kb * 1024UL : 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;        // 0 = от батареи, 1 = от сети, 255 = неизвестно
        public byte BatteryFlag;         // бит 3 (8) = заряжается, 128 = нет батареи
        public byte BatteryLifePercent;  // 0-100, 255 = неизвестно
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    /// <summary>Текущий заряд батареи: (ok, процент 0-100 или -1, от сети, заряжается).</summary>
    public static (bool ok, int percent, bool onAc, bool charging) GetPowerStatus()
    {
        if (!GetSystemPowerStatus(out var s)) return (false, -1, false, false);
        int pct = s.BatteryLifePercent == 255 ? -1 : s.BatteryLifePercent;
        bool onAc = s.ACLineStatus == 1;
        bool charging = (s.BatteryFlag & 8) != 0;
        return (true, pct, onAc, charging);
    }

    // ---------- Загрузка CPU (через системные времена) ----------
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long lpIdleTime, out long lpKernelTime, out long lpUserTime);

    private static long _prevIdle, _prevKernel, _prevUser;

    /// <summary>
    /// Загрузка CPU в процентах. Считается по разнице между вызовами,
    /// поэтому вызывать с интервалом (например раз в секунду).
    /// </summary>
    public static double GetCpuUsage()
    {
        if (!GetSystemTimes(out long idle, out long kernel, out long user))
            return 0;

        long idleDiff = idle - _prevIdle;
        long kernelDiff = kernel - _prevKernel;
        long userDiff = user - _prevUser;

        _prevIdle = idle;
        _prevKernel = kernel;
        _prevUser = user;

        long total = kernelDiff + userDiff; // kernel уже включает idle
        if (total <= 0) return 0;
        double usage = (total - idleDiff) * 100.0 / total;
        return Math.Clamp(usage, 0, 100);
    }

    // ---------- Корзина ----------
    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    private const uint SHERB_NOCONFIRMATION = 0x00000001;
    private const uint SHERB_NOPROGRESSUI = 0x00000002;
    private const uint SHERB_NOSOUND = 0x00000004;

    /// <summary>Возвращает (размер в байтах, количество элементов) в корзине.</summary>
    public static (long size, long count) QueryRecycleBin()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        int hr = SHQueryRecycleBin(null, ref info);
        if (hr != 0) return (0, 0);
        return (info.i64Size, info.i64NumItems);
    }

    /// <summary>Очищает корзину без подтверждения/звука/прогресс-окна.</summary>
    public static void EmptyRecycleBin()
    {
        SHEmptyRecycleBin(IntPtr.Zero, null,
            SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
    }
}
