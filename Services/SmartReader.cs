using System.Runtime.InteropServices;

namespace NakClean.Services;

/// <summary>
/// Чтение SMART напрямую с физического диска через DeviceIoControl -
/// как это делает CrystalDiskInfo. Сейчас реализован NVMe (журнал
/// SMART/Health). Требует прав администратора (доступ к \\.\PhysicalDriveN).
///
/// ATA/SATA pass-through пока не реализован - для SATA вернёт null
/// (данные возьмутся из WMI-счётчика надёжности, если он их отдаёт).
/// </summary>
public static class SmartReader
{
    public sealed record SmartData(int? TempC, long? PowerOnHours, int? WearPercent);

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint OPEN_EXISTING = 3;
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002d1400;

    // StorageDeviceProtocolSpecificProperty = 50, PropertyStandardQuery = 0
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

    /// <summary>Читает NVMe SMART/Health журнал. null, если не удалось (нет прав/не NVMe).</summary>
    public static SmartData? ReadNvme(int physicalDriveNumber)
    {
        try
        {
            using var h = CreateFile($@"\\.\PhysicalDrive{physicalDriveNumber}",
                GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
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

            int logStart = queryHeader + protoData; // 48

            // NVMe SMART/Health Information Log (по спецификации NVMe):
            //  01-02: Composite Temperature (Kelvin)
            //  05:    Percentage Used (%)
            //  112-127: Power Cycles    ← НЕ часы! (тут раньше была ошибка)
            //  128-143: Power On Hours (16 байт, берём младшие 8)
            ushort kelvin = BitConverter.ToUInt16(buf, logStart + 1);
            byte wear = buf[logStart + 5];
            ulong poh = BitConverter.ToUInt64(buf, logStart + 128);

            int? temp = kelvin > 0 ? kelvin - 273 : null;
            long? hours = poh > 0 ? (long)poh : null;
            int? wearPct = wear; // 0 - допустимо (новый диск)

            if (temp is null && hours is null) return null;
            return new SmartData(temp, hours, wearPct);
        }
        catch { return null; }
    }

    private const uint IOCTL_ATA_PASS_THROUGH = 0x0004D02C;
    private const uint SMART_RCV_DRIVE_DATA = 0x0007C088;

    /// <summary>
    /// Читает SMART с ATA/SATA диска. Пробуем два метода (более совместимый
    /// SMART_RCV_DRIVE_DATA, затем ATA_PASS_THROUGH) и разбираем атрибуты:
    /// 194/190 - температура, 9 - часы работы, 177/231/233 - износ SSD.
    /// Требует прав администратора.
    /// </summary>
    public static SmartData? ReadAta(int physicalDriveNumber)
    {
        byte[]? smart = ReadSmartRcv(physicalDriveNumber) ?? ReadSmartPassThrough(physicalDriveNumber);
        return smart is null ? null : ParseAtaSmart(smart);
    }

    /// <summary>Метод 1: SMART_RCV_DRIVE_DATA (старый, широко поддерживаемый). Вернёт 512 байт атрибутов.</summary>
    private static byte[]? ReadSmartRcv(int n)
    {
        try
        {
            using var h = CreateFile($@"\\.\PhysicalDrive{n}",
                GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) return null;

            // SENDCMDINPARAMS (заголовок 32 байта)
            var inBuf = new byte[32];
            BitConverter.GetBytes((uint)512).CopyTo(inBuf, 0); // cBufferSize
            inBuf[4] = 0xD0; // bFeaturesReg = SMART READ DATA
            inBuf[5] = 0x01; // bSectorCountReg
            inBuf[6] = 0x01; // bSectorNumberReg
            inBuf[7] = 0x4F; // bCylLowReg
            inBuf[8] = 0xC2; // bCylHighReg
            inBuf[9] = 0xA0; // bDriveHeadReg
            inBuf[10] = 0xB0; // bCommandReg = SMART
            inBuf[12] = (byte)n; // bDriveNumber

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

    /// <summary>Метод 2: ATA_PASS_THROUGH_EX. Вернёт 512 байт атрибутов.</summary>
    private static byte[]? ReadSmartPassThrough(int n)
    {
        try
        {
            using var h = CreateFile($@"\\.\PhysicalDrive{n}",
                GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) return null;

            const int sizeofEx = 56;   // ATA_PASS_THROUGH_EX (x64)
            const int dataLen = 512;
            int total = sizeofEx + dataLen;
            var buf = new byte[total];

            BitConverter.GetBytes((ushort)sizeofEx).CopyTo(buf, 0);   // Length
            BitConverter.GetBytes((ushort)0x03).CopyTo(buf, 2);       // AtaFlags = DATA_IN | DRDY_REQUIRED
            BitConverter.GetBytes((uint)dataLen).CopyTo(buf, 8);      // DataTransferLength
            BitConverter.GetBytes((ulong)10).CopyTo(buf, 16);         // TimeOutValue (сек)
            BitConverter.GetBytes((ulong)sizeofEx).CopyTo(buf, 32);   // DataBufferOffset

            buf[48] = 0xD0; buf[49] = 0x01; buf[50] = 0x00;
            buf[51] = 0x4F; buf[52] = 0xC2; buf[53] = 0x00; buf[54] = 0xB0;

            if (!DeviceIoControl(h, IOCTL_ATA_PASS_THROUGH, buf, total, buf, total, out _, IntPtr.Zero))
                return null;

            var block = new byte[512];
            Array.Copy(buf, sizeofEx, block, 0, 512);
            return block;
        }
        catch { return null; }
    }

    /// <summary>Разбирает 512-байтный блок SMART-атрибутов (по 12 байт, начиная с offset 2).</summary>
    private static SmartData? ParseAtaSmart(byte[] block)
    {
        int? temp = null;
        long? hours = null;
        int? wear = null;

        for (int i = 0; i < 30; i++)
        {
            int e = 2 + i * 12;
            if (e + 11 >= block.Length) break;
            byte id = block[e];
            if (id == 0) continue;

            byte cur = block[e + 3];        // нормализованное значение
            byte raw0 = block[e + 5];       // первый байт сырого значения

            switch (id)
            {
                case 194: // температура
                    temp = (raw0 is > 0 and < 110) ? raw0 : (cur is > 0 and < 110 ? cur : temp);
                    break;
                case 190: // airflow temperature (запасной источник)
                    temp ??= (raw0 is > 0 and < 110) ? raw0 : null;
                    break;
                case 9: // часы работы
                    uint h9 = BitConverter.ToUInt32(block, e + 5);
                    if (h9 > 0) hours = h9;
                    break;
                case 177: // Wear Leveling Count (нормализованное: 100 = новый)
                case 231: // SSD Life Left
                case 233: // Media Wearout Indicator
                    if (cur is > 0 and <= 100) wear ??= 100 - cur;
                    break;
            }
        }

        if (temp is null && hours is null && wear is null) return null;
        return new SmartData(temp, hours, wear);
    }
}
