using System.IO;
using System.Runtime.InteropServices;

namespace NakClean.Services;

/// <summary>
/// Удаление в корзину так же, как в Проводнике. Если файл не влезает в корзину,
/// Windows предупредит, что удалит навсегда (а не сотрёт молча).
/// </summary>
public static class RecycleBin
{
    public enum Result { Moved, Cancelled, Failed }

    public static Result Move(string path, IntPtr owner)
    {
        var op = new SHFILEOPSTRUCT
        {
            hwnd = owner,
            wFunc = FO_DELETE,
            pFrom = path + "\0",            // список путей заканчивается двойным нулём
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING,
        };
        int rc = SHFileOperation(ref op);
        if (op.fAnyOperationsAborted) return Result.Cancelled;
        bool gone = !File.Exists(path) && !Directory.Exists(path);
        return rc == 0 && gone ? Result.Moved : Result.Failed;
    }

    /// <summary>
    /// То, что удалять нельзя: корень диска, Windows, Program Files, служебные файлы NTFS и т.п.
    /// Удаление такого сломает систему или просто не получится.
    /// </summary>
    public static bool IsProtected(string path)
    {
        string p;
        try { p = Path.GetFullPath(path).TrimEnd('\\'); }
        catch { return true; }

        string? root = Path.GetPathRoot(p)?.TrimEnd('\\');
        if (root is null || p.Length <= root.Length) return true;          // корень диска

        static string Norm(string? s) => (s ?? "").TrimEnd('\\');
        bool Is(string? dir) => Norm(dir).Length > 0 && p.Equals(Norm(dir), StringComparison.OrdinalIgnoreCase);
        bool Inside(string? dir) => Norm(dir).Length > 0 &&
            (Is(dir) || p.StartsWith(Norm(dir) + "\\", StringComparison.OrdinalIgnoreCase));

        if (Inside(Environment.GetFolderPath(Environment.SpecialFolder.Windows))) return true;
        if (Is(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))) return true;
        if (Is(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86))) return true;
        if (Is(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData))) return true;
        if (Is(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))) return true;
        if (Is(Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)))) return true; // C:\Users

        // верхний уровень диска: служебные файлы и папки
        if (string.Equals(Path.GetDirectoryName(p)?.TrimEnd('\\'), root, StringComparison.OrdinalIgnoreCase))
        {
            string name = Path.GetFileName(p);
            if (name.StartsWith('$')) return true;                         // $MFT, $Recycle.Bin, $Extend...
            if (name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var sys in new[] { "pagefile.sys", "hiberfil.sys", "swapfile.sys" })
                if (name.Equals(sys, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private const uint FO_DELETE = 3;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
}
