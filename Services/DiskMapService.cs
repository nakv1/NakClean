namespace NakClean.Services;

/// <summary>Файл внутри папки - компактная структура (без накладных расходов объекта-узла).</summary>
public readonly struct FileEntry
{
    public FileEntry(string name, long size, long alloc) { Name = name; Size = size; Alloc = alloc; }
    public readonly string Name;
    public readonly long Size;
    public readonly long Alloc;
}

/// <summary>
/// Узел дерева - ТОЛЬКО папка. Файлы хранятся компактным списком <see cref="Files"/> (структуры),
/// а не отдельными узлами - это резко снижает память на дисках с миллионами файлов.
/// </summary>
public sealed class TreeNode
{
    public required string Name { get; init; }

    /// <summary>Найти подпапку по имени (без учёта регистра).</summary>
    public TreeNode? FindSub(string name)
        => Sub != null && Sub.TryGetValue(name, out var n) ? n : null;   // словарь уже без учёта регистра
    public TreeNode? Parent { get; init; }

    public Dictionary<string, TreeNode>? Sub;  // подпапки
    public List<FileEntry>? Files;             // прямые файлы

    public long Size;       // суммарный размер (с подпапками и файлами)
    public long Alloc;      // суммарно «на диске» (с учётом кластеров)
    public int FileCount;   // сколько файлов внутри (рекурсивно)

    /// <summary>Полный путь - по цепочке родителей (не храним на каждом узле).</summary>
    public string FullPath => Parent is null ? Name : Parent.FullPath + "\\" + Name;

    public bool HasChildren => Sub is { Count: > 0 } || Files is { Count: > 0 };

    private List<Child>? _sorted;

    /// <summary>Дети (подпапки + файлы) единым списком, отсортированные по размеру.</summary>
    public List<Child> SortedChildren()
    {
        if (_sorted != null) return _sorted;
        var list = new List<Child>((Sub?.Count ?? 0) + (Files?.Count ?? 0));
        if (Sub != null) foreach (var f in Sub.Values) list.Add(new Child(f));
        if (Files != null) foreach (var f in Files) list.Add(new Child(this, f));
        list.Sort(static (a, b) => b.Size.CompareTo(a.Size));
        _sorted = list;
        return list;
    }

    /// <summary>
    /// Убрать из дерева удалённую подпапку или файл и вычесть их размер из всех родителей.
    /// Возвращает (размер, на диске, файлов) удалённого - или null, если такого нет.
    /// </summary>
    public (long Size, long Alloc, int Files)? RemoveChild(string name)
    {
        long s, a;
        int f;
        if (Sub != null && Sub.Remove(name, out var folder))
        {
            s = folder.Size; a = folder.Alloc; f = folder.FileCount;
        }
        else
        {
            int i = Files?.FindIndex(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)) ?? -1;
            if (i < 0) return null;
            var fe = Files![i];
            Files.RemoveAt(i);
            s = fe.Size; a = fe.Alloc; f = 1;
        }

        for (var n = this; n != null; n = n.Parent)
        {
            n.Size -= s; n.Alloc -= a; n.FileCount -= f;
            n._sorted = null;                // порядок детей мог измениться - пересоберётся при раскрытии
        }
        return (s, a, f);
    }
}

/// <summary>Унифицированный «ребёнок» дерева: либо подпапка (<see cref="Folder"/>), либо файл.</summary>
public readonly struct Child
{
    public readonly TreeNode? Folder;   // null ⇒ это файл
    public readonly TreeNode Owner;     // папка-владелец (для построения пути файла)
    public readonly FileEntry File;

    public Child(TreeNode folder) { Folder = folder; Owner = folder.Parent!; File = default; }
    public Child(TreeNode owner, FileEntry file) { Folder = null; Owner = owner; File = file; }

    public bool IsFolder => Folder != null;
    public string Name => Folder?.Name ?? File.Name;
    public long Size => Folder?.Size ?? File.Size;
    public long Alloc => Folder?.Alloc ?? File.Alloc;
    public int FileCount => Folder?.FileCount ?? 0;
    public bool HasChildren => Folder?.HasChildren ?? false;
    public string FullPath => Folder?.FullPath ?? Owner.FullPath + "\\" + File.Name;
}

public static class DiskMapService
{
    /// <summary>Рекурсивно складывает размеры/«на диске»/число файлов снизу вверх.</summary>
    public static void Aggregate(TreeNode n)
    {
        long s = 0, a = 0;
        int files = 0;

        if (n.Files != null)
            foreach (var f in n.Files) { s += f.Size; a += f.Alloc; files++; }

        if (n.Sub != null)
            foreach (var c in n.Sub.Values)
            {
                Aggregate(c);
                s += c.Size;
                a += c.Alloc;
                files += c.FileCount;
            }

        n.Size = s;
        n.Alloc = a;
        n.FileCount = files;
    }
}
