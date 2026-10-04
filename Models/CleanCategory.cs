using NakClean.Services;
using NakClean.ViewModels;

namespace NakClean.Models;

public enum TargetKind { Directory, RecycleBin }

/// <summary>Одна категория мусора: что чистим, сколько весит, выбрана ли.</summary>
public sealed class CleanCategory : ViewModelBase
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Glyph { get; init; }
    public required string Group { get; init; }

    public TargetKind Kind { get; init; } = TargetKind.Directory;
    public IReadOnlyList<string> Roots { get; init; } = Array.Empty<string>();
    public string Pattern { get; init; } = "*";
    public bool Recursive { get; init; } = true;

    /// <summary>Отдельные файлы (например C:\Windows\MEMORY.DMP) - вдобавок к папкам.</summary>
    public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();

    /// <summary>Своё удаление вместо обычного (например штатная команда Windows). Возвращает освобождённые байты.</summary>
    public Func<CancellationToken, long>? CustomClean { get; init; }

    /// <summary>Чистим только когда эти программы закрыты (имена процессов без .exe) - иначе можно испортить их данные.</summary>
    public IReadOnlyList<string> MustBeClosed { get; init; } = Array.Empty<string>();

    /// <summary>Последняя очистка пропущена: эта программа из MustBeClosed была открыта (null - не пропускали). Фоновый поток.</summary>
    public string? BlockedBy { get; private set; }

    /// <summary>Помечать ли категорию как требующую прав администратора.</summary>
    public bool NeedsAdmin { get; init; }

    private bool _selected = true;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }

    private long _sizeBytes;
    public long SizeBytes
    {
        get => _sizeBytes;
        set { if (Set(ref _sizeBytes, value)) OnPropertyChanged(nameof(SizeText)); }
    }

    private int _fileCount;
    public int FileCount
    {
        get => _fileCount;
        set { if (Set(ref _fileCount, value)) OnPropertyChanged(nameof(DetailText)); }
    }

    private bool _isScanned;
    public bool IsScanned
    {
        get => _isScanned;
        set { if (Set(ref _isScanned, value)) OnPropertyChanged(nameof(DetailText)); }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set { if (Set(ref _isBusy, value)) OnPropertyChanged(nameof(DetailText)); }
    }

    public string SizeText => Format.Bytes(SizeBytes);

    // локализация: RU - из литералов выше, EN - из Loc по Id
    public string NameText => Loc.I.IsEn ? Loc.I[$"cat_{Id}"] : Name;
    public string DescriptionText => Loc.I.IsEn ? Loc.I[$"catd_{Id}"] : Description;
    public string GroupText => Loc.I.IsEn ? Loc.I[GroupKey] : Group;
    private string GroupKey => Group switch
    {
        "Система" => "grp_system",
        "Браузеры" => "grp_browsers",
        "Следы" => "grp_traces",
        "Программы" => "grp_apps",
        _ => "grp_system",
    };

    public void RaiseLocalized()
    {
        OnPropertyChanged(nameof(NameText));
        OnPropertyChanged(nameof(DescriptionText));
        OnPropertyChanged(nameof(GroupText));
        OnPropertyChanged(nameof(DetailText));
    }

    public string DetailText
    {
        get
        {
            if (IsBusy) return Loc.I["det_scanning"];
            if (!IsScanned) return Loc.I["det_unscanned"];
            if (Kind == TargetKind.RecycleBin)
                return $"{FileCount} {Loc.I["det_items"]}";
            return $"{FileCount:N0} {Loc.I["det_files"]}";
        }
    }

    // ---------- Операции ----------
    // ВАЖНО: *Core-методы вызываются из фонового потока и НЕ трогают
    // bindable-свойства. Запись результатов делает ViewModel в UI-потоке -
    // иначе WPF падает на cross-thread доступе к командам/привязкам.

    /// <summary>Подсчёт размера без удаления. Безопасно для фонового потока.</summary>
    public (long bytes, int files) ScanCore(CancellationToken ct)
    {
        if (Kind == TargetKind.RecycleBin)
        {
            var (size, count) = Native.QueryRecycleBin();
            return (size, (int)count);
        }
        var (bytes, files) = FileOps.Measure(Roots, Pattern, Recursive, ct);
        foreach (var f in Files)
        {
            try { var fi = new System.IO.FileInfo(f); if (fi.Exists) { bytes += fi.Length; files++; } }
            catch { }
        }
        return (bytes, files);
    }

    /// <summary>Какая из программ, которые надо закрыть, сейчас открыта (null - все закрыты).</summary>
    public string? RunningBlocker()
    {
        foreach (var name in MustBeClosed)
        {
            try
            {
                var procs = System.Diagnostics.Process.GetProcessesByName(name);
                bool running = procs.Length > 0;
                foreach (var p in procs) p.Dispose();
                if (running) return name;
            }
            catch { }
        }
        return null;
    }

    /// <summary>Удаление. Возвращает реально освобождённые байты. Фоновый поток.</summary>
    public long CleanCore(CancellationToken ct)
    {
        if (Kind == TargetKind.RecycleBin)
        {
            // освобождено = сколько было минус сколько осталось (если очистить не вышло - честный 0)
            var (before, _) = Native.QueryRecycleBin();
            Native.EmptyRecycleBin();
            var (after, _) = Native.QueryRecycleBin();
            return Math.Max(0, before - after);
        }
        BlockedBy = RunningBlocker();
        if (BlockedBy != null) return 0;
        if (CustomClean != null) return CustomClean(ct);

        var (freedBytes, _, _) = FileOps.Delete(Roots, Pattern, Recursive, null, ct);
        foreach (var f in Files)
        {
            try
            {
                var fi = new System.IO.FileInfo(f);
                if (!fi.Exists) continue;
                long len = fi.Length;
                fi.Delete();
                freedBytes += len;
            }
            catch { /* занят - пропускаем */ }
        }
        return freedBytes;
    }
}
