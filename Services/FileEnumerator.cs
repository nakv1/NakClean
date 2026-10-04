using System.IO;
using Microsoft.VisualBasic.FileIO;

namespace NakClean.Services;

/// <summary>
/// Быстрый перебор всех файлов (для «Поиска файлов») и удаление мелких служебных файлов в Корзину.
/// </summary>
public static class FileEnumerator
{
    /// <summary>
    /// Перебор файлов: сначала пробуем быстрый MFT (NTFS + админ), иначе обычный обход.
    /// Возвращает (путь, размер).
    /// </summary>
    public static IEnumerable<(string path, long size)> Enumerate(string root, CancellationToken ct)
    {
        char drive = root.Length >= 2 && root[1] == ':' ? char.ToUpperInvariant(root[0]) : '\0';
        if (drive != '\0' && NtfsMftReader.IsNtfs(drive))
        {
            List<(string, long)>? all = null;
            try { all = NtfsMftReader.EnumerateFiles(drive, ct); } catch { all = null; }
            if (all is { Count: > 0 })
            {
                string prefix = root.TrimEnd('\\') + "\\";
                foreach (var f in all)
                    if (f.Item1.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        yield return f;
                yield break;
            }
        }
        // откат: обычный обход каталогов
        foreach (var f in FileOps.SafeFiles(root, "*", true))
        {
            long len = 0; bool ok = false;
            try { len = new FileInfo(f).Length; ok = true; } catch { }
            if (ok) yield return (f, len);
        }
    }

    /// <summary>
    /// Удаляет небольшой файл (копию реестра, ярлык автозапуска) в Корзину (обратимо). true = успех.
    /// Для больших файлов - <see cref="RecycleBin.Move"/> (предупредит, если не влезет в корзину).
    /// </summary>
    public static bool DeleteToRecycle(string path)
    {
        try
        {
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            return true;
        }
        catch { return false; }
    }
}
