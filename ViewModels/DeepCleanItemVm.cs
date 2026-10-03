using NakClean.Services;

namespace NakClean.ViewModels;

/// <summary>Строка в списке «Глубокая очистка»: след + сколько записей найдено.</summary>
public sealed class DeepCleanItemVm : ViewModelBase
{
    private readonly DeepTrace _trace;
    public DeepCleanItemVm(DeepTrace trace) => _trace = trace;

    public string Id => _trace.Id;
    public string Glyph => _trace.Glyph;
    public string Name => Loc.I[$"deep_{_trace.Id}"];
    public string Desc => Loc.I[$"deep_{_trace.Id}_d"];

    private bool _selected = true;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }

    // -1 = ещё не проверяли
    private int _count = -1;
    public int Count
    {
        get => _count;
        set { if (Set(ref _count, value)) { OnPropertyChanged(nameof(CountText)); OnPropertyChanged(nameof(HasTraces)); } }
    }

    public bool HasTraces => _count > 0;
    public string CountText => _count < 0 ? "" : _count == 0
        ? Loc.I["deep_none"]
        : string.Format(Loc.I["deep_count"], _count);

    /// <summary>Посчитать записи (можно звать из фонового потока - UI не трогает).</summary>
    public int CountOnly() => SafeRun(_trace.Count);

    /// <summary>Очистить след в реестре (можно звать из фонового потока - UI не трогает).</summary>
    public void ClearRegistry() => SafeAction(_trace.Clear);

    public void RaiseLocalized()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Desc));
        OnPropertyChanged(nameof(CountText));
    }

    private static int SafeRun(Func<int> f) { try { return f(); } catch { return 0; } }
    private static void SafeAction(Action a) { try { a(); } catch { } }
}
