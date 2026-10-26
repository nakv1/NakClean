using System.IO;
using System.Runtime.InteropServices;

namespace NakClean.Services;

/// <summary>Сводка по одному расширению файлов (как панель «типы файлов» в WizTree).</summary>
public sealed class ExtStat
{
    public required string Ext { get; init; }     // ".dll" или "(без расширения)"
    public long Size { get; set; }
    public long Alloc { get; set; }
    public int Files { get; set; }
}

/// <summary>Один файл для плоского списка «Файлы».</summary>
public readonly struct ScanFile
{
    public ScanFile(string path, long size, long alloc) { Path = path; Size = size; Alloc = alloc; }
    public string Path { get; }
    public long Size { get; }
    public long Alloc { get; }
}

/// <summary>Полный результат анализа диска/папки (дерево + типы + файлы + итоги).</summary>
public sealed class ScanResult
{
    public required TreeNode Root { get; init; }
    public required List<ExtStat> Extensions { get; init; }
    public required List<ScanFile> Files { get; init; }
    public long TotalSize { get; init; }
    public long TotalAlloc { get; init; }
    public int FileCount { get; init; }
    public long DriveTotal { get; init; }
    public long DriveFree { get; init; }
    public long DriveUsed => DriveTotal - DriveFree;
    public TimeSpan Elapsed { get; init; }
    /// <summary>Анализировали весь диск (а не папку) - тогда «занято на томе» минус «на диске» = резерв Windows и служебное.</summary>
    public bool WholeDrive { get; init; }
}

/// <summary>
/// Анализ диска/папки в стиле WizTree: один быстрый проход по MFT строит дерево размеров,
/// статистику по расширениям, плоский список файлов и итоги по тому.
/// </summary>
public static class FileScanService
{
    // плоский список ограничиваем, чтобы не раздувать память/UI на дисках с миллионами файлов
    // (показываем крупнейшие - этого хватает; 50k уже не пролистать вручную)
    private const int MaxFlatFiles = 50_000;

    public static ScanResult Scan(string root, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string prefix = root.TrimEnd('\\');

        // весь NTFS-диск: дерево прямо из оглавления (в разы быстрее, чем пути-строки, и с реальным местом на диске)
        if (prefix.Length == 2 && prefix[1] == ':' && NtfsMftReader.IsNtfs(char.ToUpperInvariant(prefix[0])))
        {
            Dictionary<long, NtfsMftReader.Rec>? table = null;
            try { table = NtfsMftReader.ReadTable(char.ToUpperInvariant(prefix[0]), ct); } catch (OperationCanceledException) { throw; } catch { }
            if (table is { Count: > 0 }) return ScanTable(prefix, table, sw, ct);
        }

        var rootNode = new TreeNode { Name = prefix };

        int cluster = ClusterSize(prefix);
        var extMap = new Dictionary<string, ExtStat>(StringComparer.OrdinalIgnoreCase);
        var flat = new List<ScanFile>();

        long totalSize = 0, totalAlloc = 0;
        int fileCount = 0, counter = 0;

        foreach (var (path, size) in FileEnumerator.Enumerate(root, ct))
        {
            if ((++counter & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            string rest = path.Substring(prefix.Length).TrimStart('\\');
            if (rest.Length == 0) continue;

            long alloc = size <= 0 ? 0 : ((size + cluster - 1) / cluster) * cluster;
            totalSize += size;
            totalAlloc += alloc;
            fileCount++;

            // дерево: проходим/создаём папки, последний сегмент - файл (компактной структурой)
            var segs = rest.Split('\\');
            var node = rootNode;
            for (int i = 0; i < segs.Length - 1; i++)
            {
                node.Sub ??= new(StringComparer.OrdinalIgnoreCase);
                if (!node.Sub.TryGetValue(segs[i], out var child))
                {
                    child = new TreeNode { Name = segs[i], Parent = node };
                    node.Sub[segs[i]] = child;
                }
                node = child;
            }
            node.Files ??= new();
            node.Files.Add(new FileEntry(segs[^1], size, alloc));

            // расширение
            string ext = System.IO.Path.GetExtension(path);
            ext = string.IsNullOrEmpty(ext) ? Loc.I["fs_noext"] : ext.ToLowerInvariant();
            if (!extMap.TryGetValue(ext, out var es))
            {
                es = new ExtStat { Ext = ext };
                extMap[ext] = es;
            }
            es.Size += size;
            es.Alloc += alloc;
            es.Files++;

            flat.Add(new ScanFile(path, size, alloc));
        }

        DiskMapService.Aggregate(rootNode);

        // плоский список: крупнейшие сверху, с ограничением
        flat.Sort((a, b) => b.Size.CompareTo(a.Size));
        if (flat.Count > MaxFlatFiles) flat.RemoveRange(MaxFlatFiles, flat.Count - MaxFlatFiles);

        // списки росли «с запасом» (место под миллион строк при 50 тыс. оставленных, у папок - под файлы
        // и подпапки): замер на C: - 174 МБ данных, без запаса 138 МБ. Работа дерева от этого не меняется.
        flat.TrimExcess();
        TrimTree(rootNode);

        // типы файлов: сортировка по размеру (название типа спрашивается у Windows лениво - в ExtRowVm)
        var exts = extMap.Values.OrderByDescending(e => e.Size).ToList();
        (long total, long free) = DriveSpace(prefix);

        return new ScanResult
        {
            Root = rootNode,
            Extensions = exts,
            Files = flat,
            TotalSize = totalSize,
            TotalAlloc = totalAlloc,
            FileCount = fileCount,
            DriveTotal = total,
            DriveFree = free,
            Elapsed = sw.Elapsed,
        };
    }

    /// <summary>
    /// Весь диск из оглавления: папки - узлы по номеру записи (создаются, только если в них есть файлы, как и раньше),
    /// файлы - сразу в свою папку. Полный путь собирается лишь для 50 тыс. крупнейших файлов плоского списка.
    /// «На диске» - реально занятое место из оглавления (сжатие, «дырявые» файлы), как у WizTree.
    /// Жёсткие ссылки (один файл под двумя именами, напр. WinSxS и System32) считаются ОДИН раз.
    /// </summary>
    private static ScanResult ScanTable(string prefix, Dictionary<long, NtfsMftReader.Rec> table,
                                        System.Diagnostics.Stopwatch sw, CancellationToken ct)
    {
        var rootNode = new TreeNode { Name = prefix };
        var nodes = new Dictionary<long, TreeNode?> { [5] = rootNode };   // запись № 5 - корень тома

        TreeNode? NodeOf(long idx, int depth)
        {
            if (nodes.TryGetValue(idx, out var n)) return n;               // null - «в работе» (защита от циклов) или битая
            if (depth > 1024 || !table.TryGetValue(idx, out var r) || r.Name is null || !r.IsDir || !r.InUse) return null;
            nodes[idx] = null;
            var parent = NodeOf(r.Parent, depth + 1);
            if (parent is null) return null;
            parent.Sub ??= new(StringComparer.OrdinalIgnoreCase);
            if (!parent.Sub.TryGetValue(r.Name, out n))
            {
                n = new TreeNode { Name = r.Name, Parent = parent };
                parent.Sub[r.Name] = n;
            }
            nodes[idx] = n;
            return n;
        }

        var extMap = new Dictionary<string, ExtStat>(StringComparer.OrdinalIgnoreCase);
        var all = new List<(long size, long alloc, TreeNode parent, string name)>(table.Count);
        long totalSize = 0, totalAlloc = 0;
        int fileCount = 0, counter = 0;
        string noExt = Loc.I["fs_noext"];

        foreach (var r in table.Values)
        {
            if ((++counter & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
            if (r.IsDir || !r.InUse || r.Name is null) continue;
            var parent = NodeOf(r.Parent, 0);
            if (parent is null) continue;

            totalSize += r.Size;
            totalAlloc += r.Alloc;
            fileCount++;
            parent.Files ??= new();
            parent.Files.Add(new FileEntry(r.Name, r.Size, r.Alloc));
            all.Add((r.Size, r.Alloc, parent, r.Name));

            string ext = System.IO.Path.GetExtension(r.Name);
            ext = string.IsNullOrEmpty(ext) ? noExt : ext.ToLowerInvariant();
            if (!extMap.TryGetValue(ext, out var es)) extMap[ext] = es = new ExtStat { Ext = ext };
            es.Size += r.Size;
            es.Alloc += r.Alloc;
            es.Files++;
        }

        DiskMapService.Aggregate(rootNode);

        // плоский список: крупнейшие, полный путь - только для них
        all.Sort((a, b) => b.size.CompareTo(a.size));
        var flat = new List<ScanFile>(Math.Min(all.Count, MaxFlatFiles));
        for (int i = 0; i < all.Count && i < MaxFlatFiles; i++)
            flat.Add(new ScanFile(all[i].parent.FullPath + "\\" + all[i].name, all[i].size, all[i].alloc));
        all.Clear();
        TrimTree(rootNode);

        var exts = extMap.Values.OrderByDescending(e => e.Size).ToList();
        (long total, long free) = DriveSpace(prefix);

        return new ScanResult
        {
            Root = rootNode, Extensions = exts, Files = flat,
            TotalSize = totalSize, TotalAlloc = totalAlloc, FileCount = fileCount,
            DriveTotal = total, DriveFree = free, Elapsed = sw.Elapsed, WholeDrive = true,
        };
    }

    // списки росли «с запасом» - после сборки убираем (замер на C: 174 → 138 МБ), работа дерева не меняется
    private static void TrimTree(TreeNode root)
    {
        var stack = new Stack<TreeNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            n.Files?.TrimExcess();
            if (n.Sub is { } sub) { sub.TrimExcess(); foreach (var c in sub.Values) stack.Push(c); }
        }
    }

    // ---------- системные сведения ----------

    private static int ClusterSize(string path)
    {
        try
        {
            string rootDir = System.IO.Path.GetPathRoot(path) ?? path;
            if (GetDiskFreeSpace(rootDir, out uint spc, out uint bps, out _, out _) && spc > 0 && bps > 0)
                return (int)(spc * bps);
        }
        catch { }
        return 4096; // типичный кластер NTFS
    }

    private static (long total, long free) DriveSpace(string path)
    {
        try
        {
            string? rootDir = System.IO.Path.GetPathRoot(path);
            if (rootDir is null) return (0, 0);
            var di = new DriveInfo(rootDir);
            if (di.IsReady) return (di.TotalSize, di.TotalFreeSpace);
        }
        catch { }
        return (0, 0);
    }

    private static readonly Dictionary<string, string> _typeCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Название типа файла у Windows («Приложение», «Файл "MP4"»). Зовётся лениво из строки списка типов.</summary>
    public static string FriendlyType(string ext)
    {
        if (ext == Loc.I["fs_noext"]) return Loc.I["fs_typefile"];
        if (_typeCache.TryGetValue(ext, out var cached)) return cached;

        string result = ext.TrimStart('.').ToUpperInvariant() + Loc.I["fs_typesuffix"];
        try
        {
            uint len = 0;
            const uint ASSOCSTR_FRIENDLYDOCNAME = 3;
            const uint ASSOCF_NONE = 0;
            // первый вызов - узнать длину
            AssocQueryString(ASSOCF_NONE, ASSOCSTR_FRIENDLYDOCNAME, ext, null, null, ref len);
            if (len > 1)
            {
                var sb = new System.Text.StringBuilder((int)len);
                if (AssocQueryString(ASSOCF_NONE, ASSOCSTR_FRIENDLYDOCNAME, ext, null, sb, ref len) == 0
                    && sb.Length > 0)
                    result = sb.ToString();
            }
        }
        catch { }
        _typeCache[ext] = result;
        return result;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpace(string rootPath,
        out uint sectorsPerCluster, out uint bytesPerSector,
        out uint freeClusters, out uint totalClusters);

    [DllImport("Shlwapi.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int AssocQueryString(uint flags, uint str, string assoc,
        string? extra, System.Text.StringBuilder? outBuf, ref uint outBufSize);
}
