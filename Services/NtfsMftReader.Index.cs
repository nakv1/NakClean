using System.Runtime.InteropServices;

namespace NakClean.Services;

public static partial class NtfsMftReader
{
    /// <summary>Приёмник записей MFT для индекса быстрого поиска по именам.</summary>
    public interface IIndexSink
    {
        void Begin(long totalRecords);
        /// <param name="size">-1, если у записи нет своих данных (папка или данные в доп. записи)</param>
        void Add(long index, ReadOnlySpan<char> name, long parent, long size, long modified, bool isDir);
        /// <summary>Размер из доп. записи (так лежат большие/сильно фрагментированные файлы).</summary>
        void SetSize(long index, long size);
    }

    /// <summary>
    /// Читает всю MFT тома и отдаёт живые записи: имя, папку-родителя, размер, дату изменения.
    /// false - том не открылся (не NTFS / нет прав), тогда индекс строится обычным обходом.
    /// </summary>
    public static bool ReadIndex(char drive, IIndexSink sink, CancellationToken ct)
    {
        using var h = CreateFile($@"\\.\{drive}:", GENERIC_READ, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return false;

        var vd = new byte[128];
        if (!DeviceIoControl(h, FSCTL_GET_NTFS_VOLUME_DATA, IntPtr.Zero, 0, vd, (uint)vd.Length, out _, IntPtr.Zero))
            return false;

        uint bytesPerSector = BitConverter.ToUInt32(vd, 40);
        uint bytesPerCluster = BitConverter.ToUInt32(vd, 44);
        uint recSize = BitConverter.ToUInt32(vd, 48);
        long mftValidLen = BitConverter.ToInt64(vd, 56);
        long mftStartLcn = BitConverter.ToInt64(vd, 64);
        if (bytesPerCluster == 0 || recSize == 0 || bytesPerSector == 0) return false;

        var rec0 = ReadAt(h, mftStartLcn * bytesPerCluster, AlignUp((int)bytesPerCluster, (int)bytesPerSector));
        if (rec0.Length < recSize) return false;
        ApplyFixup(rec0, 0, (int)recSize, (int)bytesPerSector);
        var runs = GetMftDataRuns(rec0, 0, (int)recSize);
        if (runs.Count == 0) return false;

        long totalRecords = mftValidLen / recSize;
        sink.Begin(totalRecords);
        long index = 0;
        const int chunk = 8 * 1024 * 1024;

        foreach (var (lcn, count) in runs)
        {
            if (index >= totalRecords) break;
            long pos = lcn * bytesPerCluster;
            long remaining = count * bytesPerCluster;
            while (remaining > 0 && index < totalRecords)
            {
                ct.ThrowIfCancellationRequested();
                int toRead = (int)Math.Min(chunk, remaining);
                toRead -= toRead % (int)recSize;
                if (toRead <= 0) toRead = (int)recSize;
                var buf = ReadAt(h, pos, toRead);
                if (buf.Length < recSize) break;
                for (int o = 0; o + recSize <= buf.Length && index < totalRecords; o += (int)recSize, index++)
                    ParseIndexRecord(buf, o, (int)recSize, (int)bytesPerSector, index, sink);
                pos += toRead;
                remaining -= toRead;
            }
        }
        return true;
    }

    private static void ParseIndexRecord(byte[] b, int o, int recSize, int sector, long index, IIndexSink sink)
    {
        if (b[o] != (byte)'F' || b[o + 1] != (byte)'I' || b[o + 2] != (byte)'L' || b[o + 3] != (byte)'E') return;
        ApplyFixup(b, o, recSize, sector);

        ushort flags = U16(b, o + 22);
        if ((flags & 1) == 0) return;                       // запись свободна (файл удалён)
        bool isDir = (flags & 2) != 0;
        long baseRef = I64(b, o + 0x20) & 0x0000FFFFFFFFFFFF; // != 0 - это доп. запись другого файла
        int p = o + U16(b, o + 20);

        int nameAt = -1, nameLen = 0; byte bestNs = 255;
        long parent = 0, size = -1, modified = 0;

        while (p + 8 <= o + recSize)
        {
            uint type = U32(b, p);
            if (type == 0xFFFFFFFF) break;
            int len = (int)U32(b, p + 4);
            if (len <= 0 || p + len > o + recSize) break;
            byte nonRes = b[p + 8];
            byte attrNameLen = b[p + 9];

            if (type == 0x10 && nonRes == 0) // $STANDARD_INFORMATION: дата изменения
            {
                int v = p + U16(b, p + 20);
                if (v + 16 <= o + recSize) modified = I64(b, v + 8);
            }
            else if (type == 0x30 && nonRes == 0) // $FILE_NAME
            {
                int v = p + U16(b, p + 20);
                if (v + 66 <= o + recSize)
                {
                    byte fnLen = b[v + 64];
                    byte ns = b[v + 65];
                    if (ns != 2 && v + 66 + fnLen * 2 <= o + recSize   // 2 = DOS-only, пропускаем
                        && (nameAt < 0 || (bestNs == 0 && (ns == 1 || ns == 3))))
                    {
                        nameAt = v + 66; nameLen = fnLen; bestNs = ns;
                        parent = I64(b, v) & 0x0000FFFFFFFFFFFF;
                    }
                }
            }
            else if (type == 0x80 && attrNameLen == 0) // безымянный $DATA = размер файла
            {
                if (nonRes == 0) size = U32(b, p + 16);
                else if (I64(b, p + 16) == 0) size = I64(b, p + 48);  // реальный размер - только в первом куске
            }
            p += len;
        }

        if (baseRef != 0 && baseRef != index)
        {
            if (size >= 0) sink.SetSize(baseRef, size);
            return;
        }
        if (nameAt < 0) return;
        var name = MemoryMarshal.Cast<byte, char>(b.AsSpan(nameAt, nameLen * 2));
        sink.Add(index, name, parent, size, modified, isDir);
    }
}
