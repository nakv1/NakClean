using System.ComponentModel;
using NakClean.Services;

namespace NakClean.ViewModels;

/// <summary>Что показать в заголовке колонки: подпись, значок (▴/▾) и выделена ли колонка (по ней сейчас сортировка).</summary>
public sealed record SortHeaderInfo(string Label, string Arrow, bool IsActive);

/// <summary>
/// Единая сортировка кликом по заголовку для ВСЕХ списков программы (стиль заголовка - SortHeader в Theme.xaml):
/// неактивная колонка - серая с бледным ▾ («можно сортировать»), активная - золотая с ▴ (по возрастанию) или ▾.
/// Повторный клик - обратный порядок. В разметке:
/// Content="{Binding XxxSort[name]}" Command="{Binding XxxSort.SortCommand}" CommandParameter="name" Style="{StaticResource SortHeader}".
/// Два режима: сортирует представление списка сам (CollectionView) или зовёт свою сортировку (поиск/восстановление
/// сортируют ВСЕ найденные, а не только показанные строки).
/// </summary>
public sealed class ColumnSort : ViewModelBase
{
    private readonly ICollectionView? _view;
    private readonly Action<string, bool>? _onSort;
    private readonly Dictionary<string, (string Label, string Prop, bool DescFirst)> _cols;
    private string? _key;
    private bool _desc;

    /// <param name="columns">ключ → (подпись в Loc, свойство строки для сортировки, первый клик - по убыванию)</param>
    public ColumnSort(ICollectionView view, params (string Key, string Label, string Prop, bool DescFirst)[] columns)
        : this(columns) => _view = view;

    /// <param name="onSort">своя сортировка: (ключ колонки, по убыванию)</param>
    public ColumnSort(Action<string, bool> onSort, params (string Key, string Label, string Prop, bool DescFirst)[] columns)
        : this(columns) => _onSort = onSort;

    private ColumnSort((string Key, string Label, string Prop, bool DescFirst)[] columns)
    {
        _cols = columns.ToDictionary(c => c.Key, c => (c.Label, c.Prop, c.DescFirst));
        SortCommand = new RelayCommand(p => Sort(p as string));
    }

    public RelayCommand SortCommand { get; }
    public string? Key => _key;
    public bool Desc => _desc;

    /// <summary>Сортировка по умолчанию (подсвечивает колонку сразу). Для списка-представления - применяет её.</summary>
    public ColumnSort Initial(string key, bool desc)
    {
        _key = key; _desc = desc;
        ApplyView();
        OnPropertyChanged("Item[]");
        return this;
    }

    public SortHeaderInfo this[string key] => _cols.TryGetValue(key, out var c)
        ? new SortHeaderInfo(Loc.I[c.Label], key == _key && !_desc ? "▴" : "▾", key == _key)
        : new SortHeaderInfo(key, "", false);

    private void Sort(string? key)
    {
        if (key is null || !_cols.TryGetValue(key, out var c)) return;
        if (key == _key) _desc = !_desc;
        else { _key = key; _desc = c.DescFirst; }
        if (_view != null) ApplyView();
        else _onSort?.Invoke(key, _desc);
        OnPropertyChanged("Item[]");
    }

    private void ApplyView()
    {
        if (_view is null || _key is null || !_cols.TryGetValue(_key, out var c)) return;
        _view.SortDescriptions.Clear();
        _view.SortDescriptions.Add(new SortDescription(c.Prop, _desc ? ListSortDirection.Descending : ListSortDirection.Ascending));
    }

    /// <summary>Смена языка: подписи заголовков и порядок по переведённым словам («Работает», «Папки»).</summary>
    public void RaiseLocalized()
    {
        OnPropertyChanged("Item[]");
        if (_view != null && _key != null) _view.Refresh();
    }
}
