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
        return (bytes, files);
    }

    /// <summary>Удаление. Возвращает реально освобождённые байты. Фоновый поток.</summary>
    public long CleanCore(CancellationToken ct)
    {
        if (Kind == TargetKind.RecycleBin)
        {
            long freed = SizeBytes;
            Native.EmptyRecycleBin();
            return freed;
        }
        var (freedBytes, _, _) = FileOps.Delete(Roots, Pattern, Recursive, null, ct);
        return freedBytes;
    }
}
