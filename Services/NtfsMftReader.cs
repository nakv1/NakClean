using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NakClean.Services;

/// <summary>
/// Быстрый перебор файлов тома через прямое чтение MFT (главной таблицы файлов NTFS) -
/// та же техника, что делает сканеры вроде WizTree быстрыми (секунды вместо минут).
/// Реализовано с нуля. Требует прав администратора и тома NTFS.
/// Никакого стороннего кода - только Win32 API и разбор формата NTFS.
/// </summary>
public static class NtfsMftReader
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_RW = 0x3;
    private const uint OPEN_EXISTING = 3;
    private const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x00090064;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share,
        IntPtr sec, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code,
        IntPtr inBuf, uint inSize, byte[] outBuf, uint outSize, out uint returned, IntPtr ov);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFilePointerEx(SafeFileHandle h, long distance, out long newPtr, uint method);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle h, IntPtr buffer, uint toRead, out uint read, IntPtr ov);

    private struct Rec { public string? Name; public long Parent; public long Size; public bool IsDir; public bool InUse; public byte Ns; }

    public static bool IsNtfs(char drive)
    {
        try { return new DriveInfo(drive + ":").DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>Все файлы тома: полный путь + размер. Пустой список - если не удалось (откат на обычный обход).</summary>
    public static List<(string path, long size)> EnumerateFiles(char drive, CancellationToken ct)
    {
        var files = new List<(string, long)>();
        using var h = CreateFile($@"\\.\{drive}:", GENERIC_READ, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return files;

        var vd = new byte[128];
        if (!DeviceIoControl(h, FSCTL_GET_NTFS_VOLUME_DATA, IntPtr.Zero, 0, vd, (uint)vd.Length, out _, IntPtr.Zero))
            return files;

        uint bytesPerSector = BitConverter.ToUInt32(vd, 40);
        uint bytesPerCluster = BitConverter.ToUInt32(vd, 44);
        uint recSize = BitConverter.ToUInt32(vd, 48);
        long mftValidLen = BitConverter.ToInt64(vd, 56);
        long mftStartLcn = BitConverter.ToInt64(vd, 64);
        if (bytesPerCluster == 0 || recSize == 0 || bytesPerSector == 0) return files;

        // запись 0 = $MFT: берём её data-runs, чтобы прочитать всю таблицу
        var rec0 = ReadAt(h, mftStartLcn * bytesPerCluster, AlignUp((int)bytesPerCluster, (int)bytesPerSector));
        if (rec0.Length < recSize) return files;
        ApplyFixup(rec0, 0, (int)recSize, (int)bytesPerSector);
        var runs = GetMftDataRuns(rec0, 0, (int)recSize);
        if (runs.Count == 0) return files;

        long totalRecords = mftValidLen / recSize;
        var dict = new Dictionary<long, Rec>(capacity: (int)Math.Min(Math.Max(totalRecords, 16), 4_000_000));
        long index = 0;
        const int chunk = 8 * 1024 * 1024;

        foreach (var (lcn, count) in runs)
        {
            if (index >= totalRecords) break;
            ct.ThrowIfCancellationRequested();
            long pos = lcn * bytesPerCluster;
            long remaining = count * bytesPerCluster;
            while (remaining > 0 && index < totalRecords)
            {
                int toRead = (int)Math.Min(chunk, remaining);
                toRead -= toRead % (int)recSize;
                if (toRead <= 0) toRead = (int)recSize;
                var buf = ReadAt(h, pos, toRead);
                if (buf.Length < recSize) break;
                for (int o = 0; o + recSize <= buf.Length && index < totalRecords; o += (int)recSize, index++)
                    ParseRecord(buf, o, (int)recSize, (int)bytesPerSector, index, dict);
                pos += toRead;
                remaining -= toRead;
            }
        }

        // строим полные пути по цепочке родителей
        var cache = new Dictionary<long, string?>();
        string root = drive + ":";

        string? PathOf(long idx, int depth)
        {
            if (idx == 5) return root;                  // корневой каталог
            if (depth > 1024) return null;
            if (cache.TryGetValue(idx, out var c)) return c;
            cache[idx] = null;                          // защита от циклов
            if (!dict.TryGetValue(idx, out var r) || r.Name is null) return null;
            var parent = PathOf(r.Parent, depth + 1);
            if (parent is null) return null;
            var p = parent + "\\" + r.Name;
            cache[idx] = p;
            return p;
        }

        foreach (var kv in dict)
        {
            var r = kv.Value;
            if (r.IsDir || !r.InUse || r.Name is null) continue;
            var folder = PathOf(r.Parent, 0);
            if (folder is null) continue;
            files.Add((folder + "\\" + r.Name, r.Size));
        }
        return files;
    }

    private static void ParseRecord(byte[] b, int o, int recSize, int sector, long index, Dictionary<long, Rec> dict)
    {
        if (b[o] != (byte)'F' || b[o + 1] != (byte)'I' || b[o + 2] != (byte)'L' || b[o + 3] != (byte)'E') return;
        ApplyFixup(b, o, recSize, sector);

        ushort flags = U16(b, o + 22);
        bool inUse = (flags & 1) != 0;
        bool isDir = (flags & 2) != 0;
        int p = o + U16(b, o + 20);

        string? name = null; byte bestNs = 255; long parent = 0; long size = 0;

        while (p + 8 <= o + recSize)
        {
            uint type = U32(b, p);
            if (type == 0xFFFFFFFF) break;
            int len = (int)U32(b, p + 4);
            if (len <= 0 || p + len > o + recSize) break;
            byte nonRes = b[p + 8];
            byte nameLen = b[p + 9];

            if (type == 0x30) // $FILE_NAME (resident)
            {
                int v = p + U16(b, p + 20);
                if (v + 66 <= o + recSize)
                {
                    long par = I64(b, v) & 0x0000FFFFFFFFFFFF;
                    byte fnLen = b[v + 64];
                    byte ns = b[v + 65];
                    if (ns != 2 && v + 66 + fnLen * 2 <= o + recSize) // 2 = DOS-only, пропускаем
                    {
                        if (name is null || (bestNs == 0 && (ns == 1 || ns == 3)))
                        {
                            name = Encoding.Unicode.GetString(b, v + 66, fnLen * 2);
                            parent = par;
                            bestNs = ns;
                        }
                    }
                }
            }
            else if (type == 0x80 && nameLen == 0) // безымянный $DATA = размер файла
            {
                if (nonRes == 0) size = U32(b, p + 16);          // резидентный: длина значения
                else size = I64(b, p + 48);                       // нерезидентный: реальный размер
            }
            p += len;
        }

        if (name is not null)
            dict[index] = new Rec { Name = name, Parent = parent, Size = size, IsDir = isDir, InUse = inUse, Ns = bestNs };
    }

    private static List<(long lcn, long count)> GetMftDataRuns(byte[] b, int o, int recSize)
    {
        var runs = new List<(long, long)>();
        int p = o + U16(b, o + 20);
        while (p + 8 <= o + recSize)
        {
            uint type = U32(b, p);
            if (type == 0xFFFFFFFF) break;
            int len = (int)U32(b, p + 4);
            if (len <= 0) break;
            byte nonRes = b[p + 8];
            byte nameLen = b[p + 9];
            if (type == 0x80 && nameLen == 0 && nonRes == 1)
            {
                int q = p + U16(b, p + 32); // DataRunsOffset
                long lcn = 0;
                while (q < o + recSize && b[q] != 0)
                {
                    byte hdr = b[q++];
                    int lenSize = hdr & 0xF;
                    int offSize = (hdr >> 4) & 0xF;
                    if (q + lenSize + offSize > o + recSize) break;
                    long runLen = ReadLE(b, q, lenSize); q += lenSize;
                    long runOff = ReadLESigned(b, q, offSize); q += offSize;
                    lcn += runOff;
                    if (runLen > 0) runs.Add((lcn, runLen));
                }
                break;
            }
            p += len;
        }
        return runs;
    }

    private static void ApplyFixup(byte[] b, int o, int recSize, int sector)
    {
        ushort usaOff = U16(b, o + 4);
        ushort usaCount = U16(b, o + 6);
        for (int i = 1; i < usaCount; i++)
        {
            int sectorEnd = o + i * sector - 2;
            int usaEntry = o + usaOff + i * 2;
            if (sectorEnd + 1 < o + recSize && usaEntry + 1 < o + recSize)
            {
                b[sectorEnd] = b[usaEntry];
                b[sectorEnd + 1] = b[usaEntry + 1];
            }
        }
    }

    private static byte[] ReadAt(SafeFileHandle h, long offset, int length)
    {
        if (!SetFilePointerEx(h, offset, out _, 0)) return Array.Empty<byte>();
        var buf = new byte[length];
        var gch = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            int total = 0;
            while (total < length)
            {
                IntPtr ptr = IntPtr.Add(gch.AddrOfPinnedObject(), total);
                if (!ReadFile(h, ptr, (uint)(length - total), out uint read, IntPtr.Zero) || read == 0) break;
                total += (int)read;
            }
            if (total == length) return buf;
            var t = new byte[total];
            Array.Copy(buf, t, total);
            return t;
        }
        finally { gch.Free(); }
    }

    private static int AlignUp(int v, int a) => ((v + a - 1) / a) * a;
    private static ushort U16(byte[] b, int i) => BitConverter.ToUInt16(b, i);
    private static uint U32(byte[] b, int i) => BitConverter.ToUInt32(b, i);
    private static long I64(byte[] b, int i) => BitConverter.ToInt64(b, i);

    private static long ReadLE(byte[] b, int i, int n)
    {
        long v = 0;
        for (int k = 0; k < n; k++) v |= (long)b[i + k] << (8 * k);
        return v;
    }

    private static long ReadLESigned(byte[] b, int i, int n)
    {
        if (n == 0) return 0;
        long v = ReadLE(b, i, n);
        long signBit = 1L << (8 * n - 1);
        if ((v & signBit) != 0) v -= 1L << (8 * n); // знаковое расширение
        return v;
    }
}
