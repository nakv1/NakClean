using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NakClean.Services;

/// <summary>
/// Журнал изменений NTFS (USN): Windows сама записывает туда каждое создание, удаление и
/// переименование файла. Читаем только новое с прошлого раза - индекс поиска остаётся свежим
/// без повторного чтения диска и без фоновой работы. Журнал только читаем, не создаём и не меняем.
/// </summary>
public static class UsnJournal
{
    public readonly record struct State(ulong JournalId, long NextUsn);
    public readonly record struct Change(long Id, long Parent, uint Reason, bool IsDir, string Name);

    public const uint ReasonDataOverwrite = 0x1, ReasonDataExtend = 0x2, ReasonDataTruncation = 0x4,
        ReasonFileCreate = 0x100, ReasonFileDelete = 0x200, ReasonRenameOld = 0x1000,
        ReasonRenameNew = 0x2000, ReasonBasicInfo = 0x8000;

    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_RW = 0x3;
    private const uint OPEN_EXISTING = 3;
    private const uint FSCTL_QUERY_USN_JOURNAL = 0x000900F4;
    private const uint FSCTL_READ_USN_JOURNAL = 0x000900BB;
    private const long FrnMask = 0x0000FFFFFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share,
        IntPtr sec, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code,
        byte[]? inBuf, int inSize, byte[] outBuf, int outSize, out int returned, IntPtr ov);

    private static SafeFileHandle Open(char drive)
        => CreateFile($@"\\.\{drive}:", GENERIC_READ, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

    /// <summary>Текущее положение журнала. null - журнал на томе выключен.</summary>
    public static State? Query(char drive)
    {
        using var h = Open(drive);
        return h.IsInvalid ? null : Query(h);
    }

    private static State? Query(SafeFileHandle h)
    {
        var buf = new byte[80];
        if (!DeviceIoControl(h, FSCTL_QUERY_USN_JOURNAL, null, 0, buf, buf.Length, out _, IntPtr.Zero)) return null;
        return new State(BitConverter.ToUInt64(buf, 0), BitConverter.ToInt64(buf, 16));
    }

    /// <summary>
    /// Изменения с позиции <paramref name="from"/> до текущего конца журнала.
    /// null - журнал пересоздан, старые записи уже стёрты или изменений слишком много:
    /// том проще перечитать целиком.
    /// </summary>
    public static State? Read(char drive, State from, List<Change> changes, int limit)
    {
        using var h = Open(drive);
        if (h.IsInvalid) return null;
        if (Query(h) is not { } cur || cur.JournalId != from.JournalId) return null;

        long usn = from.NextUsn;
        if (usn >= cur.NextUsn) return from;

        var input = new byte[40];
        var output = new byte[64 * 1024];
        while (usn < cur.NextUsn)
        {
            // READ_USN_JOURNAL_DATA_V0: StartUsn, ReasonMask, ReturnOnlyOnClose, Timeout, BytesToWaitFor, UsnJournalID
            BitConverter.TryWriteBytes(input.AsSpan(0), usn);
            BitConverter.TryWriteBytes(input.AsSpan(8), 0xFFFFFFFFu);
            BitConverter.TryWriteBytes(input.AsSpan(12), 0u);
            BitConverter.TryWriteBytes(input.AsSpan(16), 0L);
            BitConverter.TryWriteBytes(input.AsSpan(24), 0L);   // не ждать новых записей
            BitConverter.TryWriteBytes(input.AsSpan(32), from.JournalId);
            if (!DeviceIoControl(h, FSCTL_READ_USN_JOURNAL, input, input.Length, output, output.Length, out int got, IntPtr.Zero))
                return null;
            if (got <= 8) break;

            long next = BitConverter.ToInt64(output, 0);
            int p = 8;
            while (p + 60 <= got)
            {
                int recLen = BitConverter.ToInt32(output, p);
                if (recLen < 60 || p + recLen > got) break;
                if (BitConverter.ToUInt16(output, p + 4) == 2)   // USN_RECORD_V2 (NTFS)
                {
                    long id = BitConverter.ToInt64(output, p + 8) & FrnMask;
                    long parent = BitConverter.ToInt64(output, p + 16) & FrnMask;
                    uint reason = BitConverter.ToUInt32(output, p + 40);
                    uint attrs = BitConverter.ToUInt32(output, p + 52);
                    int nameLen = BitConverter.ToUInt16(output, p + 56);
                    int nameOff = BitConverter.ToUInt16(output, p + 58);
                    if (nameOff + nameLen <= recLen)
                    {
                        string name = Encoding.Unicode.GetString(output, p + nameOff, nameLen);
                        changes.Add(new Change(id, parent, reason, (attrs & 0x10) != 0, name));
                        if (changes.Count > limit) return null;
                    }
                }
                p += recLen;
            }
            if (next <= usn) break;
            usn = next;
        }
        return new State(from.JournalId, usn);
    }
}
