using System.Collections.ObjectModel;
using System.Diagnostics;
using NakClean.Services;

namespace NakClean.ViewModels;

/// <summary>Вариант в выпадающем списке поиска (тип файлов / диск).</summary>
public sealed class SearchOptionVm
{
    public required string Id { get; init; }
    public required string Display { get; init; }
    public override string ToString() => Display;
}

/// <summary>Строка результата поиска по имени.</summary>
public sealed class SearchRowVm
{
    public SearchRowVm(SearchRow r) => Row = r;
    public SearchRow Row { get; }
    public string Name => Row.Name;
    public string Folder => Row.Folder;
    public string Path => Row.Path;
    public bool IsDir => Row.IsDir;
    public string Icon => Row.IsDir ? "📁" : "📄";
    public string SizeText => Row.IsDir ? "" : Format.Bytes(Row.Size);
    public string DateText
    {
        get
        {
            if (Row.Modified <= 0) return "";
            try { return DateTime.FromFileTimeUtc(Row.Modified).ToLocalTime().ToString(Loc.I["fx_datefmt"]); }
            catch { return ""; }
        }
    }
}

// ---------- Поиск файлов по имени (как Everything) ----------
public sealed partial class MainViewModel
{
    private const int SearchTake = 10_000;   // больше строк на экране не нужно - лучше уточнить запрос

    private readonly FileIndex _index = new();
    private bool _indexStarted, _suppressSearch;
    private CancellationTokenSource? _searchCts;
    private SearchSort _searchSort = SearchSort.Name;
    private bool _searchDesc;
    private double _indexSecs;
    private int _lastTotal = -1;
    private double _lastSecs;

    public ObservableCollection<SearchOptionVm> SearchTypes { get; } = new();
    public ObservableCollection<SearchOptionVm> SearchDrives { get; } = new();
    public RelayCommand RebuildIndexCommand { get; private set; } = null!;
    public RelayCommand SortSearchCommand { get; private set; } = null!;

    private void InitSearch()
    {
        RebuildIndexCommand = new RelayCommand(async () => await BuildIndexAsync(), () => !IndexBusy);
        SortSearchCommand = new RelayCommand(p => SortSearch(p as string));
        BuildSearchOptions();
    }

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value ?? "")) return;
            OnPropertyChanged(nameof(SearchTextEmpty));
            QueueSearch(150);   // ждём паузу в наборе, чтобы не искать на каждую букву
        }
    }
    public bool SearchTextEmpty => SearchText.Length == 0;

    private SearchOptionVm? _selectedSearchType;
    public SearchOptionVm? SelectedSearchType
    {
        get => _selectedSearchType;
        set { if (Set(ref _selectedSearchType, value) && value != null) QueueSearch(0); }
    }

    private SearchOptionVm? _selectedSearchDrive;
    public SearchOptionVm? SelectedSearchDrive
    {
        get => _selectedSearchDrive;
        set { if (Set(ref _selectedSearchDrive, value) && value != null) QueueSearch(0); }
    }

    private IReadOnlyList<SearchRowVm> _searchResults = Array.Empty<SearchRowVm>();
    public IReadOnlyList<SearchRowVm> SearchResults { get => _searchResults; private set => Set(ref _searchResults, value); }

    private SearchRowVm? _selectedSearchRow;
    public SearchRowVm? SelectedSearchRow { get => _selectedSearchRow; set => Set(ref _selectedSearchRow, value); }

    private string _searchStatus = "";
    public string SearchStatus { get => _searchStatus; private set => Set(ref _searchStatus, value); }

    private bool _indexBusy;
    public bool IndexBusy
    {
        get => _indexBusy;
        private set { if (Set(ref _indexBusy, value)) RebuildIndexCommand.RaiseCanExecuteChanged(); }
    }

    public string SearchNameHeader => SortHeader("fs_c_name", SearchSort.Name);
    public string SearchSizeHeader => SortHeader("fs_c_size", SearchSort.Size);
    public string SearchDateHeader => SortHeader("fx_c_date", SearchSort.Date);
    private string SortHeader(string key, SearchSort s)
        => Loc.I[key] + (_searchSort == s ? (_searchDesc ? "  ▼" : "  ▲") : "");

    /// <summary>Первое открытие поиска - читаем диски; дальше просто освежаем результаты.</summary>
    public void EnsureSearchIndex()
    {
        if (_indexStarted) { RefreshSearch(); return; }
        _indexStarted = true;
        _ = BuildIndexAsync();
    }

    /// <summary>Пересчитать текущий запрос (после возврата в окно, удаления файла и т.п.).</summary>
    public void RefreshSearch()
    {
        if (_index.IsReady && !IndexBusy) QueueSearch(0);
    }

    private async Task BuildIndexAsync()
    {
        if (IndexBusy) return;
        IndexBusy = true;
        _indexStarted = true;
        SearchStatus = Loc.I["fx_reading"];
        var sw = Stopwatch.StartNew();
        try
        {
            await _index.BuildAsync(CancellationToken.None);
            _indexSecs = sw.Elapsed.TotalSeconds;
            BuildSearchOptions();
            SearchStatus = IndexInfo();
        }
        catch (Exception ex) { SearchStatus = string.Format(Loc.I["err"], ex.Message); }
        finally
        {
            IndexBusy = false;
            ReleaseMemory();   // буферы чтения MFT больше не нужны
        }
        QueueSearch(0);
    }

    private string IndexInfo()
    {
        if (!_index.IsReady) return IndexBusy ? Loc.I["fx_reading"] : "";
        string drives = string.Join(", ", _index.Volumes.Select(v => v.Drive + ":"));
        return string.Format(Loc.I["fx_indexed"], _index.ItemCount.ToString("N0"), drives, _indexSecs.ToString("0.0"));
    }

    private void BuildSearchOptions()
    {
        _suppressSearch = true;
        try
        {
            string type = SelectedSearchType?.Id ?? "all";
            string drive = SelectedSearchDrive?.Id ?? "";

            SearchTypes.Clear();
            foreach (var t in SearchQuery.Types)
                SearchTypes.Add(new SearchOptionVm { Id = t, Display = Loc.I["fx_t_" + t] });

            SearchDrives.Clear();
            SearchDrives.Add(new SearchOptionVm { Id = "", Display = Loc.I["fx_alldrives"] });
            foreach (var v in _index.Volumes)
                SearchDrives.Add(new SearchOptionVm { Id = v.Drive.ToString(), Display = v.Drive + ":" });

            SelectedSearchType = SearchTypes.FirstOrDefault(x => x.Id == type) ?? SearchTypes[0];
            SelectedSearchDrive = SearchDrives.FirstOrDefault(x => x.Id == drive) ?? SearchDrives[0];
        }
        finally { _suppressSearch = false; }
    }

    private void SortSearch(string? key)
    {
        var s = key switch { "size" => SearchSort.Size, "date" => SearchSort.Date, _ => SearchSort.Name };
        if (s == _searchSort) _searchDesc = !_searchDesc;
        else
        {
            _searchSort = s;
            _searchDesc = s != SearchSort.Name;   // размер и дата - сначала большие и новые
        }
        RaiseSortHeaders();
        QueueSearch(0);
    }

    private void RaiseSortHeaders()
    {
        OnPropertyChanged(nameof(SearchNameHeader));
        OnPropertyChanged(nameof(SearchSizeHeader));
        OnPropertyChanged(nameof(SearchDateHeader));
    }

    // новый запрос отменяет предыдущий; перед поиском индекс догоняет изменения на дисках
    private async void QueueSearch(int delayMs)
    {
        if (_suppressSearch || !_index.IsReady) return;
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        try
        {
            if (delayMs > 0) await Task.Delay(delayMs, cts.Token);

            var q = SearchQuery.Parse(SearchText, SelectedSearchType?.Id ?? "all");
            if (q.IsEmpty)
            {
                SearchResults = Array.Empty<SearchRowVm>();
                _lastTotal = -1;
                SearchStatus = IndexInfo();
                return;
            }

            char? drive = SelectedSearchDrive is { Id.Length: 1 } d ? d.Id[0] : null;
            var sort = _searchSort;
            bool desc = _searchDesc;
            var sw = Stopwatch.StartNew();
            var res = await Task.Run(() => _index.Search(q, drive, sort, desc, SearchTake, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;

            // если ничего не поменялось - список не трогаем (не сбрасываем прокрутку и выделение)
            if (!SameRows(res.Rows)) SearchResults = res.Rows.Select(r => new SearchRowVm(r)).ToList();
            _lastTotal = res.Total;
            _lastSecs = sw.Elapsed.TotalSeconds;
            SearchStatus = FoundText();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SearchStatus = string.Format(Loc.I["err"], ex.Message); }
    }

    private bool SameRows(List<SearchRow> rows)
    {
        if (rows.Count != SearchResults.Count) return false;
        for (int i = 0; i < rows.Count; i++)
            if (!rows[i].Equals(SearchResults[i].Row)) return false;
        return true;
    }

    private string FoundText()
    {
        if (_lastTotal < 0) return IndexInfo();
        if (_lastTotal == 0) return Loc.I["fx_nothing"];
        return _lastTotal > SearchTake
            ? string.Format(Loc.I["fx_found_cut"], _lastTotal.ToString("N0"), SearchTake.ToString("N0"))
            : string.Format(Loc.I["fx_found"], _lastTotal.ToString("N0"), _lastSecs.ToString("0.00"));
    }

    private void OnSearchLanguageChanged()
    {
        BuildSearchOptions();
        RaiseSortHeaders();
        SearchResults = SearchResults.Select(r => new SearchRowVm(r.Row)).ToList();   // формат даты
        SearchStatus = IndexBusy ? Loc.I["fx_reading"] : FoundText();
    }
}
