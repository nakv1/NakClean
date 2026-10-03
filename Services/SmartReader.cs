using System.Runtime.InteropServices;
using System.Text;

namespace NakClean.Services;

/// <summary>Одна строка таблицы SMART (ATA-атрибут или поле журнала NVMe).</summary>
public sealed record SmartAttr(int Id, int? Cur, int? Worst, int? Thresh, long Raw, byte[] RawBytes, bool IsNvme);

/// <summary>
/// Чтение SMART напрямую с физического диска через DeviceIoControl - как это делает CrystalDiskInfo:
/// NVMe (журнал SMART/Health) и ATA/SATA (SMART READ DATA + пороги + IDENTIFY DEVICE).
/// Требует прав администратора (доступ к \\.\PhysicalDriveN), без них вернёт null.
/// </summary>
public static class SmartReader
{
    public sealed class SmartData
    {
        public int? TempC { get; set; }
        public long? PowerOnHours { get; set; }
        public int? WearPercent { get; set; }
        public long? PowerCycles { get; set; }
        public long? UnsafeShutdowns { get; set; }
        public List<SmartAttr> Attrs { get; } = new();

        // NVMe
        public int? CriticalWarning { get; set; }
        public int? AvailableSpare { get; set; }
        public int? SpareThreshold { get; set; }
        public long? MediaErrors { get; set; }
        public long? NvmeUnitsRead { get; set; }      // в единицах по 512 000 байт
        public long? NvmeUnitsWritten { get; set; }
    }

    /// <summary>Что сообщает ATA IDENTIFY DEVICE.</summary>
    public sealed class AtaIdentify
    {
        public string? Serial { get; init; }
        public string? Firmware { get; init; }
        public bool SmartEnabled { get; init; }
        public bool Ncq { get; init; }
        public int SataMax { get; init; }   // 1 = SATA/150, 2 = SATA/300, 3 = SATA/600, 0 = неизвестно
        public int SataCur { get; init; }
        public int? Rpm { get; init; }      // null = нет данных, 1 = SSD
    }

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint OPEN_EXISTING = 3;
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002d1400;

    // StorageDeviceProtocolSpecificProperty = 50, PropertyStandardQuery = 0
    private const int StorageDeviceProperty = 0;
    private const int StorageDeviceWriteCacheProperty = 4;
    private const int StorageDeviceTrimProperty = 8;
    private const int StorageDeviceProtocolSpecificProperty = 50;
    private const int ProtocolTypeNvme = 3;
    private const int NVMeDataTypeLogPage = 2;
    private const int NvmeSmartLogPageId = 0x02;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandleNative CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurity,
        uint dwCreationDisposition, uint dwFlags, IntPtr hTemplate);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandleNative hDevice, uint ioControlCode,
        byte[] inBuffer, int inBufferSize, byte[] outBuffer, int outBufferSize,
        out uint bytesReturned, IntPtr overlapped);

    private sealed class SafeFileHandleNative : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeFileHandleNative() : base(true) { }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    private static SafeFileHandleNative Open(int n, bool write = true) =>
        CreateFile($@"\\.\PhysicalDrive{n}",
            write ? GENERIC_READ | GENERIC_WRITE : 0, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

    // ================= NVMe =================

    /// <summary>Читает NVMe SMART/Health журнал. null, если не удалось (нет прав/не NVMe).</summary>
    public static SmartData? ReadNvme(int physicalDriveNumber)
    {
        try
        {
            using var h = Open(physicalDriveNumber);
            if (h.IsInvalid) return null;

            const int queryHeader = 8;       // STORAGE_PROPERTY_QUERY (PropertyId + QueryType)
            const int protoData = 40;        // STORAGE_PROTOCOL_SPECIFIC_DATA
            const int logSize = 512;         // журнал SMART/Health
            int total = queryHeader + protoData + logSize;
            var buf = new byte[total];

            // STORAGE_PROPERTY_QUERY
            BitConverter.GetBytes(StorageDeviceProtocolSpecificProperty).CopyTo(buf, 0);
            BitConverter.GetBytes(0).CopyTo(buf, 4); // PropertyStandardQuery

            // STORAGE_PROTOCOL_SPECIFIC_DATA (начинается с offset 8)
            BitConverter.GetBytes(ProtocolTypeNvme).CopyTo(buf, 8);    // ProtocolType
            BitConverter.GetBytes(NVMeDataTypeLogPage).CopyTo(buf, 12); // DataType
            BitConverter.GetBytes(NvmeSmartLogPageId).CopyTo(buf, 16);  // RequestValue
            BitConverter.GetBytes(0).CopyTo(buf, 20);                   // RequestSubValue
            BitConverter.GetBytes(protoData).CopyTo(buf, 24);           // ProtocolDataOffset (от начала proto-структуры)
            BitConverter.GetBytes(logSize).CopyTo(buf, 28);             // ProtocolDataLength

            if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, buf, total, buf, total, out _, IntPtr.Zero))
                return null;

            int s = queryHeader + protoData; // начало журнала (48)

            // NVMe SMART/Health Information Log (по спецификации NVMe):
            //  0: Critical Warning, 1-2: Composite Temperature (K), 3: Available Spare,
            //  4: Spare Threshold, 5: Percentage Used, 32: Data Units Read, 48: Data Units Written,
            //  64/80: Host Read/Write Commands, 96: Controller Busy Time, 112: Power Cycles,
            //  128: Power On Hours, 144: Unsafe Shutdowns, 160: Media Errors, 176: Error Log Entries.
            // 16-байтные счётчики читаем по младшим 8 байтам (старшие на практике нули).
            long U(int off) => (long)Math.Min(BitConverter.ToUInt64(buf, s + off), long.MaxValue);

            ushort kelvin = BitConverter.ToUInt16(buf, s + 1);
            var d = new SmartData
            {
                CriticalWarning = buf[s],
                TempC = kelvin > 0 ? kelvin - 273 : null,
                AvailableSpare = buf[s + 3],
                SpareThreshold = buf[s + 4],
                WearPercent = buf[s + 5], // 0 - допустимо (новый диск)
                NvmeUnitsRead = U(32),
                NvmeUnitsWritten = U(48),
                PowerCycles = U(112),
                PowerOnHours = U(128) > 0 ? U(128) : null,
                UnsafeShutdowns = U(144),
                MediaErrors = U(160),
            };
            if (d.TempC is null && d.PowerOnHours is null) return null;

            // строки таблицы - как в CrystalDiskInfo (ID 01..0F)
            void Row(int id, long raw) => d.Attrs.Add(new SmartAttr(id, null, null, null, raw, Array.Empty<byte>(), true));
            Row(0x01, buf[s]);
            Row(0x02, kelvin);
            Row(0x03, buf[s + 3]);
            Row(0x04, buf[s + 4]);
            Row(0x05, buf[s + 5]);
            Row(0x06, U(32));
            Row(0x07, U(48));
            Row(0x08, U(64));
            Row(0x09, U(80));
            Row(0x0A, U(96));
            Row(0x0B, U(112));
            Row(0x0C, U(128));
            Row(0x0D, U(144));
            Row(0x0E, U(160));
            Row(0x0F, U(176));
            return d;
        }
        catch { return null; }
    }

    // ================= ATA / SATA =================

    private const uint IOCTL_ATA_PASS_THROUGH = 0x0004D02C;
    private const uint SMART_RCV_DRIVE_DATA = 0x0007C088;

    private const byte CMD_SMART = 0xB0, CMD_IDENTIFY = 0xEC;
    private const byte SMART_READ_DATA = 0xD0, SMART_READ_THRESHOLDS = 0xD1;

    /// <summary>
    /// Читает SMART с ATA/SATA диска: атрибуты + пороги производителя.
    /// Пробуем два метода (SMART_RCV_DRIVE_DATA, затем ATA_PASS_THROUGH). Требует прав администратора.
    /// </summary>
    public static SmartData? ReadAta(int physicalDriveNumber)
    {
        byte[]? data = AtaRead(physicalDriveNumber, SMART_READ_DATA, CMD_SMART);
        if (data is null) return null;
        byte[]? thr = AtaRead(physicalDriveNumber, SMART_READ_THRESHOLDS, CMD_SMART);
        return ParseAtaSmart(data, thr);
    }

    /// <summary>ATA IDENTIFY DEVICE: серийный номер, прошивка, NCQ, режим SATA, обороты.</summary>
    public static AtaIdentify? ReadIdentify(int physicalDriveNumber)
    {
        byte[]? b = AtaRead(physicalDriveNumber, 0, CMD_IDENTIFY);
        if (b is null) return null;
        ushort W(int i) => BitConverter.ToUInt16(b, i * 2);
        if (W(0) == 0 && W(10) == 0) return null; // пустой ответ

        ushort w76 = W(76), w77 = W(77);
        bool sataValid = w76 != 0 && w76 != 0xFFFF;
        int max = 0;
        if (sataValid)
            for (int g = 3; g >= 1; g--)
                if ((w76 & (1 << g)) != 0) { max = g; break; }
        int cur = sataValid ? (w77 >> 1) & 0x7 : 0;
        if (cur > 3) cur = 0;

        ushort w217 = W(217);
        int? rpm = w217 == 1 ? 1 : (w217 >= 0x0401 && w217 < 0xFFFF ? w217 : null);

        return new AtaIdentify
        {
            Serial = AtaString(b, 10, 10),
            Firmware = AtaString(b, 23, 4),
            SmartEnabled = (W(85) & 1) != 0,
            Ncq = sataValid && (w76 & (1 << 8)) != 0,
            SataMax = max,
            SataCur = cur,
            Rpm = rpm,
        };
    }

    // строки в IDENTIFY хранятся словами с переставленными байтами
    private static string? AtaString(byte[] b, int word, int words)
    {
        var sb = new StringBuilder(words * 2);
        for (int i = 0; i < words; i++)
        {
            sb.Append((char)b[(word + i) * 2 + 1]);
            sb.Append((char)b[(word + i) * 2]);
        }
        string s = sb.ToString().Trim('\0', ' ');
        return s.Length == 0 ? null : s;
    }

    private static byte[]? AtaRead(int n, byte feature, byte command) =>
        ReadSmartRcv(n, feature, command) ?? ReadPassThrough(n, feature, command);

    /// <summary>Метод 1: SMART_RCV_DRIVE_DATA (старый, широко поддерживаемый). Вернёт 512 байт.</summary>
    private static byte[]? ReadSmartRcv(int n, byte feature, byte command)
    {
        try
        {
            using var h = Open(n);
            if (h.IsInvalid) return null;

            bool smart = command == CMD_SMART;
            // SENDCMDINPARAMS (заголовок 32 байта)
            var inBuf = new byte[32];
            BitConverter.GetBytes((uint)512).CopyTo(inBuf, 0); // cBufferSize
            inBuf[4] = feature;              // bFeaturesReg
            inBuf[5] = 0x01;                 // bSectorCountReg
            inBuf[6] = 0x01;                 // bSectorNumberReg
            inBuf[7] = smart ? (byte)0x4F : (byte)0; // bCylLowReg
            inBuf[8] = smart ? (byte)0xC2 : (byte)0; // bCylHighReg
            inBuf[9] = 0xA0;                 // bDriveHeadReg
            inBuf[10] = command;             // bCommandReg
            inBuf[12] = (byte)n;             // bDriveNumber

            // SENDCMDOUTPARAMS: cBufferSize(4) + DRIVERSTATUS(12) + bBuffer(512)
            var outBuf = new byte[16 + 512];
            if (!DeviceIoControl(h, SMART_RCV_DRIVE_DATA, inBuf, inBuf.Length, outBuf, outBuf.Length, out _, IntPtr.Zero))
                return null;

            var block = new byte[512];
            Array.Copy(outBuf, 16, block, 0, 512);
            return block;
        }
        catch { return null; }
    }

    /// <summary>Метод 2: ATA_PASS_THROUGH_EX (x64: 48 байт). Вернёт 512 байт.</summary>
    private static byte[]? ReadPassThrough(int n, byte feature, byte command)
    {
        try
        {
            using var h = Open(n);
            if (h.IsInvalid) return null;

            const int sizeofEx = 48;
            const int dataLen = 512;
            int total = sizeofEx + dataLen;
            var buf = new byte[total];

            BitConverter.GetBytes((ushort)sizeofEx).CopyTo(buf, 0);   // Length
            BitConverter.GetBytes((ushort)0x03).CopyTo(buf, 2);       // AtaFlags = DRDY_REQUIRED | DATA_IN
            BitConverter.GetBytes((uint)dataLen).CopyTo(buf, 8);      // DataTransferLength
            BitConverter.GetBytes((uint)10).CopyTo(buf, 12);          // TimeOutValue (сек)
            BitConverter.GetBytes((ulong)sizeofEx).CopyTo(buf, 24);   // DataBufferOffset

            bool smart = command == CMD_SMART;
            // CurrentTaskFile (offset 40): Features, SectorCount, LBA low, LBA mid, LBA high, Device, Command
            buf[40] = feature; buf[41] = 0x01; buf[42] = 0x00;
            buf[43] = smart ? (byte)0x4F : (byte)0;
            buf[44] = smart ? (byte)0xC2 : (byte)0;
            buf[45] = 0xA0; buf[46] = command;

            if (!DeviceIoControl(h, IOCTL_ATA_PASS_THROUGH, buf, total, buf, total, out _, IntPtr.Zero))
                return null;

            var block = new byte[512];
            Array.Copy(buf, sizeofEx, block, 0, 512);
            return block;
        }
        catch { return null; }
    }

    /// <summary>Разбирает 512-байтный блок SMART-атрибутов (по 12 байт, начиная с offset 2) + пороги.</summary>
    private static SmartData? ParseAtaSmart(byte[] block, byte[]? thr)
    {
        var thresholds = new Dictionary<int, int>();
        if (thr is not null)
            for (int i = 0; i < 30; i++)
            {
                int e = 2 + i * 12;
                if (thr[e] != 0) thresholds[thr[e]] = thr[e + 1];
            }

        var d = new SmartData();
        for (int i = 0; i < 30; i++)
        {
            int e = 2 + i * 12;
            byte id = block[e];
            if (id == 0) continue;

            byte cur = block[e + 3];        // нормализованное значение
            byte worst = block[e + 4];
            var rawBytes = new byte[6];
            Array.Copy(block, e + 5, rawBytes, 0, 6);
            long raw = 0;
            for (int k = 5; k >= 0; k--) raw = (raw << 8) | rawBytes[k];
            uint raw32 = BitConverter.ToUInt32(block, e + 5);
            byte raw0 = rawBytes[0];

            d.Attrs.Add(new SmartAttr(id, cur, worst,
                thresholds.TryGetValue(id, out var t) ? t : null, raw, rawBytes, false));

            switch (id)
            {
                case 194: // температура
                    d.TempC = (raw0 is > 0 and < 110) ? raw0 : (cur is > 0 and < 110 ? cur : d.TempC);
                    break;
                case 190: // airflow temperature (запасной источник)
                    d.TempC ??= (raw0 is > 0 and < 110) ? raw0 : null;
                    break;
                case 9: // часы работы
                    if (raw32 > 0) d.PowerOnHours = raw32;
                    break;
                case 12: // число включений
                    d.PowerCycles = raw32;
                    break;
                case 174: // внезапные отключения питания (SSD)
                    d.UnsafeShutdowns = raw32;
                    break;
                case 192: // аварийные выключения (если нет 174)
                    d.UnsafeShutdowns ??= raw32;
                    break;
                case 177: // Wear Leveling Count (нормализованное: 100 = новый)
                case 202: // Percent Lifetime Remaining (Crucial/Micron)
                case 231: // SSD Life Left
                case 233: // Media Wearout Indicator
                    if (cur is > 0 and <= 100) d.WearPercent ??= 100 - cur;
                    break;
            }
        }

        return d.Attrs.Count == 0 ? null : d;
    }

    // ================= Общие свойства устройства =================

    /// <summary>Серийный номер и прошивка из STORAGE_DEVICE_DESCRIPTOR (работает для любой шины).</summary>
    public static (string? serial, string? firmware) ReadDescriptor(int n)
    {
        var b = QueryProperty(n, StorageDeviceProperty, 1024);
        if (b is null || b.Length < 36) return (null, null);
        string? Str(int offField)
        {
            int off = BitConverter.ToInt32(b, offField);
            if (off <= 0 || off >= b.Length) return null;
            int end = off;
            while (end < b.Length && b[end] != 0) end++;
            string s = Encoding.ASCII.GetString(b, off, end - off).Trim();
            return s.Length == 0 ? null : s;
        }
        return (Str(24), Str(20));
    }

    /// <summary>TRIM включён? null - неизвестно.</summary>
    public static bool? ReadTrim(int n)
    {
        var b = QueryProperty(n, StorageDeviceTrimProperty, 12);
        return b is null ? null : b[8] != 0;
    }

    /// <summary>Кэш записи включён? null - неизвестно.</summary>
    public static bool? ReadWriteCache(int n)
    {
        var b = QueryProperty(n, StorageDeviceWriteCacheProperty, 32);
        if (b is null) return null;
        int enabled = BitConverter.ToInt32(b, 12); // 0 неизвестно, 1 выключен, 2 включён
        return enabled switch { 1 => false, 2 => true, _ => null };
    }

    private static byte[]? QueryProperty(int n, int propertyId, int outSize)
    {
        try
        {
            using var h = Open(n, write: false);
            if (h.IsInvalid) return null;
            var q = new byte[12];
            BitConverter.GetBytes(propertyId).CopyTo(q, 0);
            var outBuf = new byte[outSize];
            return DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, q, q.Length, outBuf, outBuf.Length, out uint got, IntPtr.Zero)
                   && got >= 8 ? outBuf : null;
        }
        catch { return null; }
    }

    // ================= PCIe-линк контроллера NVMe =================

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);
    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key, out uint type,
        byte[] buffer, ref uint size, uint flags);

    private static readonly Guid PciDeviceProps = new("3ab22e31-8264-4b4e-9af5-a8d2d8e33e62");

    /// <summary>
    /// Режим PCIe контроллера, к которому подключён диск: «PCIe 4.0 x4» или
    /// «PCIe 3.0 x4 | PCIe 4.0 x4» (текущий | максимальный), как в CrystalDiskInfo.
    /// </summary>
    public static string? ReadPcieLink(string pnpDeviceId)
    {
        try
        {
            if (CM_Locate_DevNodeW(out uint dev, pnpDeviceId, 0) != 0) return null;
            if (CM_Get_Parent(out uint ctrl, dev, 0) != 0) return null;
            uint? P(uint pid)
            {
                var key = new DEVPROPKEY { fmtid = PciDeviceProps, pid = pid };
                var buf = new byte[4];
                uint size = 4;
                return CM_Get_DevNode_PropertyW(ctrl, ref key, out _, buf, ref size, 0) == 0 && size == 4
                    ? BitConverter.ToUInt32(buf, 0) : null;
            }
            uint? curSpeed = P(9), curWidth = P(10), maxSpeed = P(11), maxWidth = P(12);
            if (curSpeed is null or 0 || curWidth is null or 0) return null;
            string cur = $"PCIe {curSpeed}.0 x{curWidth}";
            if (maxSpeed is null or 0 || maxWidth is null or 0 || (maxSpeed == curSpeed && maxWidth == curWidth))
                return cur;
            return $"{cur} | PCIe {maxSpeed}.0 x{maxWidth}";
        }
        catch { return null; }
    }
}
