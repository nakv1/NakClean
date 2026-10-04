using System.IO;
using System.Text;

namespace NakClean.Services;

public enum SearchSort { Name, Size, Date }

/// <summary>Строка результата поиска - готовые данные для показа.</summary>
public sealed record SearchRow(string Name, string Folder, string Path, long Size, long Modified, bool IsDir);

public sealed record SearchResult(int Total, List<SearchRow> Rows);

public readonly record struct SearchHit(VolumeIndex Vol, int Id);

/// <summary>
/// Индекс имён одного диска (как в Everything): все файлы и папки в компактных массивах
/// по номеру записи MFT. Для NTFS свежесть держится журналом изменений (UsnJournal).
/// </summary>
public sealed class VolumeIndex : NtfsMftReader.IIndexSink
{
    private const byte Used = 1, Dir = 2;
    private const int NtfsRoot = 5;          // корневая папка тома в MFT
    private const int NtfsFirstUser = 16;    // записи 0-15 - служебные файлы NTFS ($MFT, $Bitmap...)

    public char Drive { get; }
    public bool IsNtfs { get; private set; }
    /// <summary>Индекс сам догоняет изменения (журнал NTFS включён). Иначе - только по «Перечитать».</summary>
    public bool IsLive => _usn != null;
    public int ItemCount { get; private set; }

    internal readonly object Gate = new();
    private readonly object _catchGate = new();

    private int[] _nameOff = [];
    private byte[] _nameLen = [];
    private int[] _parent = [];
    private long[] _size = [];
    private long[] _mtime = [];
    private byte[] _flags = [];
    private char[] _names = [];
    private int _namesUsed;
    private int _root;
    private UsnJournal.State? _usn;

    private VolumeIndex(char drive) => Drive = drive;

    private int Capacity => _flags.Length;

    /// <summary>Строит индекс диска. null - диск не прочитался.</summary>
    public static VolumeIndex? TryBuild(char drive, CancellationToken ct)
    {
        try
        {
            var v = new VolumeIndex(drive);
            if (NtfsMftReader.IsNtfs(drive))
            {
                // позицию журнала берём ДО чтения MFT: всё, что поменяется во время чтения, догонится потом
                var st = UsnJournal.Query(drive);
                if (NtfsMftReader.ReadIndex(drive, v, ct))
                {
                    v.IsNtfs = true;
                    v._root = NtfsRoot;
                    v._usn = st;
                    v.Finish();
                    return v;
                }
                v = new VolumeIndex(drive);
            }
            v.Walk(ct);
            v.Finish();
            return v;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    // ---------- приём записей MFT ----------
    void NtfsMftReader.IIndexSink.Begin(long totalRecords)
    {
        int cap = (int)Math.Clamp(totalRecords, 16, int.MaxValue - 1);
        Resize(cap);
        _names = new char[Math.Min((long)cap * 14 + 4096, int.MaxValue - 64)];
    }

    void NtfsMftReader.IIndexSink.Add(long index, ReadOnlySpan<char> name, long parent, long size, long modified, bool isDir)
    {
        if (index >= int.MaxValue - 1 || parent >= int.MaxValue) return;
        int id = (int)index;
        EnsureCapacity(id + 1);
        StoreName(id, name);
        _parent[id] = (int)parent;
        if (size >= 0) _size[id] = size;
        _mtime[id] = modified;
        _flags[id] = (byte)(Used | (isDir ? Dir : 0));
    }

    void NtfsMftReader.IIndexSink.SetSize(long index, long size)
    {
        if (index >= int.MaxValue - 1) return;
        int id = (int)index;
        EnsureCapacity(id + 1);
        _size[id] = size;
    }

    // ---------- не NTFS (флешки FAT32/exFAT): обычный обход папок ----------
    private void Walk(CancellationToken ct)
    {
        Resize(4096);
        _names = new char[64 * 1024];
        _root = 0;
        _parent[0] = -1;
        _flags[0] = Used | Dir;
        int next = 1;

        var opts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,   // ссылки на другие папки - не ходим (иначе петли)
            RecurseSubdirectories = false,
        };
        var stack = new Stack<(string path, int id)>();
        stack.Push((Drive + @":\", 0));
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (path, pid) = stack.Pop();
            try
            {
                foreach (var e in new DirectoryInfo(path).EnumerateFileSystemInfos("*", opts))
                {
                    int id = next++;
                    EnsureCapacity(id + 1);
                    bool dir = (e.Attributes & FileAttributes.Directory) != 0;
                    StoreName(id, e.Name);
                    _parent[id] = pid;
                    _flags[id] = (byte)(Used | (dir ? Dir : 0));
                    try { _mtime[id] = e.LastWriteTimeUtc.ToFileTimeUtc(); } catch { }
                    if (dir) stack.Push((e.FullName, id));
                    else if (e is FileInfo f) _size[id] = f.Length;
                }
            }
            catch { }
        }
    }

    private void Finish()
    {
        int n = 0;
        for (int i = 0; i < Capacity; i++)
            if (Visible(i)) n++;
        ItemCount = n;
        // запас пула имён после чтения больше не нужен - отдаём память
        if (_names.Length - _namesUsed > 256 * 1024) Array.Resize(ref _names, _namesUsed + 64 * 1024);
    }

    // ---------- хранение ----------
    private void Resize(int cap)
    {
        Array.Resize(ref _nameOff, cap);
        Array.Resize(ref _nameLen, cap);
        Array.Resize(ref _parent, cap);
        Array.Resize(ref _size, cap);
        Array.Resize(ref _mtime, cap);
        Array.Resize(ref _flags, cap);
    }

    private void EnsureCapacity(int n)
    {
        if (n > Capacity) Resize((int)Math.Min(Math.Max(n, (long)Capacity * 5 / 4 + 1024), int.MaxValue - 1));
    }

    private void StoreName(int id, ReadOnlySpan<char> name)
    {
        if (name.Length > 255) name = name[..255];
        if (_namesUsed + name.Length > _names.Length)
            Array.Resize(ref _names, (int)Math.Min(Math.Max((long)_names.Length * 3 / 2, _namesUsed + name.Length + 4096), int.MaxValue - 64));
        name.CopyTo(_names.AsSpan(_namesUsed));
        _nameOff[id] = _namesUsed;
        _nameLen[id] = (byte)name.Length;
        _namesUsed += name.Length;
    }

    private ReadOnlySpan<char> NameOf(int id) => _names.AsSpan(_nameOff[id], _nameLen[id]);

    // служебное NTFS ($MFT, всё внутри $Extend) и сам корень в поиск не попадают
    private bool Visible(int i)
    {
        if ((_flags[i] & Used) == 0) return false;
        if (!IsNtfs) return i != _root;
        if (i < NtfsFirstUser) return false;
        int p = _parent[i];
        return p >= NtfsFirstUser || p == NtfsRoot;
    }

    /// <summary>Полный путь по цепочке родителей (вызывать под Gate). null - цепочка оборвана.</summary>
    private string? PathOfLocked(int id)
    {
        Span<int> chain = stackalloc int[256];
        int n = 0;
        int cur = id;
        while (cur != _root)
        {
            if ((uint)cur >= (uint)Capacity || (_flags[cur] & Used) == 0 || n == chain.Length) return null;
            chain[n++] = cur;
            cur = _parent[cur];
        }
        var sb = new StringBuilder(64);
        sb.Append(Drive).Append(':');
        if (n == 0) return sb.Append('\\').ToString();
        for (int i = n - 1; i >= 0; i--) sb.Append('\\').Append(NameOf(chain[i]));
        return sb.ToString();
    }

    // ---------- поиск (вызывать под Gate) ----------
    internal void Match(SearchQuery q, List<SearchHit> into, CancellationToken ct)
    {
        const int chunk = 64 * 1024;
        int cap = Capacity;
        int parts = (cap + chunk - 1) / chunk;
        var local = new List<int>[parts];
        Parallel.For(0, parts, new ParallelOptions { CancellationToken = ct }, pi =>
        {
            var l = new List<int>();
            int end = Math.Min(cap, (pi + 1) * chunk);
            for (int i = pi * chunk; i < end; i++)
                if (Visible(i) && q.IsMatch(NameOf(i), (_flags[i] & Dir) != 0)) l.Add(i);
            local[pi] = l;
        });
        foreach (var l in local)
            foreach (int i in l) into.Add(new SearchHit(this, i));
    }

    internal int CompareName(int a, VolumeIndex other, int b)
        => NameOf(a).CompareTo(other.NameOf(b), StringComparison.OrdinalIgnoreCase);
    internal long SizeOf(int id) => (_flags[id] & Dir) != 0 ? -1 : _size[id];
    internal long DateOf(int id) => _mtime[id];

    internal SearchRow? RowLocked(int id)
    {
        string? folder = PathOfLocked(_parent[id]);
        if (folder is null) return null;
        if (folder.Contains(@"\$Recycle.Bin", StringComparison.OrdinalIgnoreCase)) return null;   // содержимое корзины
        string name = NameOf(id).ToString();
        string path = folder.EndsWith('\\') ? folder + name : folder + "\\" + name;
        bool dir = (_flags[id] & Dir) != 0;
        return new SearchRow(name, folder, path, dir ? 0 : _size[id], _mtime[id], dir);
    }

    // ---------- свежесть: догоняем журнал изменений NTFS ----------
    /// <summary>false - журнал больше не годится, диск надо перечитать целиком.</summary>
    internal bool CatchUp()
    {
        lock (_catchGate)
        {
            if (_usn is not { } st) return true;    // журнала нет - индекс обновляется кнопкой «Обновить»
            var changes = new List<UsnJournal.Change>();
            var next = UsnJournal.Read(Drive, st, changes, 300_000);
            if (next is null) { _usn = null; return false; }
            if (changes.Count == 0) { _usn = next; return true; }

            var touched = new HashSet<int>();
            lock (Gate)
            {
                foreach (var c in changes) Apply(c, touched);
                _usn = next;
            }
            RefreshDetails(touched);
            return true;
        }
    }

    private void Apply(in UsnJournal.Change c, HashSet<int> touched)
    {
        if (c.Id >= int.MaxValue - 1 || c.Parent >= int.MaxValue) return;
        int id = (int)c.Id;
        uint r = c.Reason;

        if ((r & UsnJournal.ReasonFileDelete) != 0)
        {
            if (id < Capacity && (_flags[id] & Used) != 0) { _flags[id] = 0; ItemCount--; }
            touched.Remove(id);
            return;
        }
        if ((r & UsnJournal.ReasonRenameOld) != 0 && (r & UsnJournal.ReasonRenameNew) == 0) return;   // старое имя

        EnsureCapacity(id + 1);
        bool wasUsed = (_flags[id] & Used) != 0;
        if (!wasUsed || !NameOf(id).SequenceEqual(c.Name)) StoreName(id, c.Name);
        _parent[id] = (int)c.Parent;
        _flags[id] = (byte)(Used | (c.IsDir ? Dir : 0));
        if (!wasUsed)
        {
            _size[id] = 0;
            _mtime[id] = 0;
            ItemCount++;
        }

        const uint dataChange = UsnJournal.ReasonDataOverwrite | UsnJournal.ReasonDataExtend |
            UsnJournal.ReasonDataTruncation | UsnJournal.ReasonFileCreate | UsnJournal.ReasonBasicInfo;
        if (!wasUsed || (r & dataChange) != 0) touched.Add(id);
    }

    // журнал не знает размер - для изменённых файлов спрашиваем у Windows (обычно их единицы)
    private void RefreshDetails(HashSet<int> touched)
    {
        var work = new List<(int id, string path, bool dir)>();
        lock (Gate)
        {
            foreach (int id in touched)
            {
                if (work.Count >= 5000) break;
                if (!Visible(id)) continue;
                var p = PathOfLocked(id);
                if (p != null) work.Add((id, p, (_flags[id] & Dir) != 0));
            }
        }

        var found = new List<(int id, long size, long time)>(work.Count);
        foreach (var (id, path, dir) in work)
        {
            try
            {
                if (dir)
                {
                    var d = new DirectoryInfo(path);
                    if (d.Exists) found.Add((id, 0, d.LastWriteTimeUtc.ToFileTimeUtc()));
                }
                else
                {
                    var f = new FileInfo(path);
                    if (f.Exists) found.Add((id, f.Length, f.LastWriteTimeUtc.ToFileTimeUtc()));
                }
            }
            catch { }
        }

        lock (Gate)
        {
            foreach (var (id, size, time) in found)
                if ((_flags[id] & Used) != 0) { _size[id] = size; _mtime[id] = time; }
        }
    }
}

/// <summary>
/// Запрос: слова через пробел (все должны быть в имени), маски с * и ? (*.mp4),
/// плюс фильтр по типу (папки, документы, видео...).
/// </summary>
public sealed class SearchQuery
{
    private static readonly Dictionary<string, string[]> TypeExts = new()
    {
        ["docs"] = ["txt", "doc", "docx", "odt", "rtf", "pdf", "xls", "xlsx", "ods", "csv", "ppt", "pptx", "odp", "md", "epub", "djvu", "fb2"],
        ["images"] = ["jpg", "jpeg", "png", "gif", "bmp", "webp", "tif", "tiff", "heic", "heif", "svg", "ico", "raw", "cr2", "nef", "arw", "dng", "psd"],
        ["video"] = ["mp4", "mkv", "avi", "mov", "wmv", "webm", "flv", "m4v", "mpg", "mpeg", "ts", "3gp"],
        ["audio"] = ["mp3", "wav", "flac", "aac", "ogg", "m4a", "wma", "opus", "aiff", "mid", "midi"],
        ["archives"] = ["zip", "rar", "7z", "tar", "gz", "bz2", "xz", "iso", "cab", "tgz", "zst"],
        ["apps"] = ["exe", "msi", "bat", "cmd", "ps1", "lnk", "appx", "msix"],
    };

    private readonly string[] _words;
    private readonly string[] _masks;
    private readonly HashSet<string>? _exts;
    private readonly bool _foldersOnly;

    private SearchQuery(string[] words, string[] masks, HashSet<string>? exts, bool foldersOnly)
    {
        _words = words; _masks = masks; _exts = exts; _foldersOnly = foldersOnly;
    }

    public static IReadOnlyList<string> Types { get; } = ["all", "folders", "docs", "images", "video", "audio", "archives", "apps"];

    public bool IsEmpty => _words.Length == 0 && _masks.Length == 0 && _exts is null && !_foldersOnly;

    public static SearchQuery Parse(string? text, string type)
    {
        var terms = (text ?? "").Replace("\"", " ")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var words = terms.Where(t => t.IndexOfAny(['*', '?']) < 0).ToArray();
        var masks = terms.Where(t => t.IndexOfAny(['*', '?']) >= 0 && t.Trim('*').Length > 0).ToArray();
        var exts = TypeExts.TryGetValue(type, out var list) ? new HashSet<string>(list, StringComparer.OrdinalIgnoreCase) : null;
        return new SearchQuery(words, masks, exts, type == "folders");
    }

    public bool IsMatch(ReadOnlySpan<char> name, bool isDir)
    {
        if (_foldersOnly && !isDir) return false;
        if (_exts != null)
        {
            if (isDir) return false;
            int dot = name.LastIndexOf('.');
            if (dot < 0 || !_exts.GetAlternateLookup<ReadOnlySpan<char>>().Contains(name[(dot + 1)..])) return false;
        }
        foreach (var w in _words)
            if (!name.Contains(w, StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var m in _masks)
            if (!Wildcard(name, m)) return false;
        return true;
    }

    // маска на всё имя: * - любые символы, ? - один символ, без учёта регистра
    private static bool Wildcard(ReadOnlySpan<char> s, string p)
    {
        int si = 0, pi = 0, star = -1, mark = 0;
        while (si < s.Length)
        {
            if (pi < p.Length && (p[pi] == '?' || char.ToUpperInvariant(p[pi]) == char.ToUpperInvariant(s[si]))) { si++; pi++; }
            else if (pi < p.Length && p[pi] == '*') { star = pi++; mark = si; }
            else if (star >= 0) { pi = star + 1; si = ++mark; }
            else return false;
        }
        while (pi < p.Length && p[pi] == '*') pi++;
        return pi == p.Length;
    }
}

/// <summary>Индекс всех дисков + поиск по нему.</summary>
public sealed class FileIndex
{
    private VolumeIndex[] _vols = [];
    private readonly object _swapGate = new();
    private readonly Dictionary<char, DateTime> _lastRebuild = new();

    public IReadOnlyList<VolumeIndex> Volumes => Volatile.Read(ref _vols);
    public bool IsReady { get; private set; }
    public int ItemCount => Volumes.Sum(v => v.ItemCount);

    /// <summary>Диски, которые индексируем: встроенные и съёмные (как в анализе места).</summary>
    public static List<char> IndexableDrives()
    {
        var list = new List<char>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable && d.Name.Length >= 2 && d.Name[1] == ':')
                    list.Add(char.ToUpperInvariant(d.Name[0]));
            }
            catch { }
        }
        return list;
    }

    /// <summary>Диски, которые ещё дочитываются в фоне (флешки FAT/exFAT - обычный обход медленный).</summary>
    public IReadOnlyList<char> Pending { get { lock (_swapGate) return _pending.ToList(); } }
    private readonly List<char> _pending = new();
    private int _generation;

    /// <summary>Набор дисков в индексе изменился (дочиталась флешка) - вызывается из фонового потока.</summary>
    public event Action? VolumesChanged;

    /// <summary>
    /// NTFS-диски читаются через MFT за секунды - ждём только их, и поиск сразу работает.
    /// Остальные (флешки) дочитываются в фоне и добавляются по готовности, чтобы не задерживать поиск.
    /// </summary>
    public async Task BuildAsync(CancellationToken ct)
    {
        int gen = Interlocked.Increment(ref _generation);
        var drives = IndexableDrives();
        var fast = drives.Where(NtfsMftReader.IsNtfs).ToList();
        var slow = drives.Except(fast).ToList();

        var built = await Task.WhenAll(fast.Select(d => Task.Run(() => VolumeIndex.TryBuild(d, ct), ct)));
        lock (_swapGate)
        {
            // флешки из прошлого индекса остаются, пока не дочитаются заново
            var keep = Volumes.Where(v => slow.Contains(v.Drive));
            Volatile.Write(ref _vols, built.OfType<VolumeIndex>().Concat(keep).OrderBy(v => v.Drive).ToArray());
            _pending.Clear();
            _pending.AddRange(slow);
        }
        IsReady = true;

        foreach (char d in slow)
        {
            _ = Task.Run(() =>
            {
                var v = VolumeIndex.TryBuild(d, ct);
                lock (_swapGate)
                {
                    if (gen != _generation) return;   // уже идёт новое чтение - этот результат устарел
                    _pending.Remove(d);
                    var list = Volumes.Where(x => x.Drive != d).ToList();
                    if (v != null) list.Add(v);
                    Volatile.Write(ref _vols, list.OrderBy(x => x.Drive).ToArray());
                }
                VolumesChanged?.Invoke();
            });
        }
    }

    public SearchResult Search(SearchQuery q, char? drive, SearchSort sort, bool desc, int take, CancellationToken ct)
    {
        CatchUp();
        ct.ThrowIfCancellationRequested();

        var vols = Volumes.Where(v => drive is null || v.Drive == drive).ToArray();
        var locked = new List<VolumeIndex>();
        try
        {
            foreach (var v in vols) { Monitor.Enter(v.Gate); locked.Add(v); }

            var hits = new List<SearchHit>();
            foreach (var v in vols) v.Match(q, hits, ct);
            ct.ThrowIfCancellationRequested();

            var arr = hits.ToArray();
            int sign = desc ? -1 : 1;
            Comparison<SearchHit> byName = (a, b) => a.Vol.CompareName(a.Id, b.Vol, b.Id);
            Comparison<SearchHit> cmp = sort switch
            {
                SearchSort.Size => (a, b) => { int c = a.Vol.SizeOf(a.Id).CompareTo(b.Vol.SizeOf(b.Id)); return c != 0 ? sign * c : byName(a, b); },
                SearchSort.Date => (a, b) => { int c = a.Vol.DateOf(a.Id).CompareTo(b.Vol.DateOf(b.Id)); return c != 0 ? sign * c : byName(a, b); },
                _ => (a, b) => sign * byName(a, b),
            };
            Array.Sort(arr, cmp);
            ct.ThrowIfCancellationRequested();

            var rows = new List<SearchRow>(Math.Min(arr.Length, take));
            int skipped = 0;
            foreach (var h in arr)
            {
                if (rows.Count >= take) break;
                var r = h.Vol.RowLocked(h.Id);
                if (r is null) skipped++;
                else rows.Add(r);
            }
            return new SearchResult(arr.Length - skipped, rows);
        }
        finally
        {
            foreach (var v in locked) Monitor.Exit(v.Gate);
        }
    }

    // перед каждым поиском подтягиваем изменения (обычно доли миллисекунды)
    private void CatchUp()
    {
        foreach (var v in Volumes)
        {
            bool ok;
            try { ok = v.CatchUp(); } catch { ok = false; }
            if (!ok) RebuildLater(v.Drive);
        }
    }

    // журнал устарел (Windows его пересоздала / диск отключили) - тихо перечитываем этот диск,
    // а пока ищем по старому индексу. Не чаще раза в 2 минуты на диск.
    private void RebuildLater(char drive)
    {
        lock (_swapGate)
        {
            if (_lastRebuild.TryGetValue(drive, out var t) && DateTime.UtcNow - t < TimeSpan.FromMinutes(2)) return;
            _lastRebuild[drive] = DateTime.UtcNow;
        }
        _ = Task.Run(() =>
        {
            var nv = VolumeIndex.TryBuild(drive, CancellationToken.None);
            lock (_swapGate)
            {
                var list = Volumes.Where(v => v.Drive != drive).ToList();
                if (nv != null) list.Add(nv);
                Volatile.Write(ref _vols, list.OrderBy(v => v.Drive).ToArray());
            }
        });
    }
}
