using System.IO;
using System.Security.Cryptography;
using Microsoft.VisualBasic.FileIO;

namespace NakClean.Services;

public sealed record LargeFile(string Path, long Size);

public sealed class DupGroup
{
    public long Size { get; init; }
    public required List<string> Paths { get; init; }
    public long Wasted => Size * (Paths.Count - 1); // можно освободить, оставив 1 копию
}

/// <summary>
/// Поиск крупных файлов и дубликатов (по размеру + содержимому).
/// Удаление - в Корзину, чтобы ничего не потерять безвозвратно.
/// </summary>
public static class DuplicateService
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

    public static List<LargeFile> FindLargeFiles(string root, int topN, CancellationToken ct)
    {
        var list = new List<LargeFile>();
        foreach (var (path, size) in Enumerate(root, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (size > 0) list.Add(new LargeFile(path, size));
        }
        return list.OrderByDescending(x => x.Size).Take(topN).ToList();
    }

    public static List<DupGroup> FindDuplicates(string root, CancellationToken ct)
    {
        // 1) группируем по размеру (кандидаты - только совпадающие размеры)
        var bySize = new Dictionary<long, List<string>>();
        foreach (var (path, size) in Enumerate(root, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (size < 1024) continue; // мелочь не считаем
            if (!bySize.TryGetValue(size, out var l)) { l = new(); bySize[size] = l; }
            l.Add(path);
        }

        // 2) среди одинаковых по размеру сверяем хэш содержимого
        var groups = new List<DupGroup>();
        foreach (var kv in bySize)
        {
            if (kv.Value.Count < 2) continue;
            ct.ThrowIfCancellationRequested();

            var byHash = new Dictionary<string, List<string>>();
            foreach (var p in kv.Value)
            {
                string? h = Hash(p, ct);
                if (h is null) continue;
                if (!byHash.TryGetValue(h, out var l)) { l = new(); byHash[h] = l; }
                l.Add(p);
            }
            foreach (var hg in byHash.Values)
                if (hg.Count > 1)
                    groups.Add(new DupGroup { Size = kv.Key, Paths = hg });
        }
        return groups.OrderByDescending(g => g.Wasted).ToList();
    }

    private static string? Hash(string path, CancellationToken ct)
    {
        try
        {
            using var md5 = MD5.Create();
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(md5.ComputeHash(fs));
        }
        catch { return null; }
    }

    /// <summary>Удаляет файл в Корзину (обратимо). true = успех.</summary>
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
