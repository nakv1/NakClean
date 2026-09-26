using System.Windows.Media;

namespace NakClean.ViewModels;

/// <summary>
/// Единая плитка «Обзора системы» (процессор / память / видеокарта / диск).
/// Все метрики в одной адаптивной сетке - цвет кольца по нагрузке.
/// </summary>
public sealed class TileVm : ViewModelBase
{
    public TileVm(string title, string caption)
    {
        _title = title;
        _caption = caption;
    }

    public string Glyph { get; init; } = "";

    private string _title;
    public string Title { get => _title; set => Set(ref _title, value); }

    private string _caption;
    public string Caption { get => _caption; set => Set(ref _caption, value); }

    private double _percent;
    public double Percent { get => _percent; set { if (Set(ref _percent, value)) OnPropertyChanged(nameof(Accent)); } }

    private string _line1 = "";
    public string Line1 { get => _line1; set => Set(ref _line1, value); }

    private string _line2 = "";
    public string Line2 { get => _line2; set => Set(ref _line2, value); }

    /// <summary>Делегат обновления значений из источника (CPU/RAM/GPU/диск).</summary>
    public Action<TileVm>? Refresh;

    // явный цвет кольца (для батареи логика обратная: мало заряда = плохо). null => по нагрузке.
    private Brush? _accentOverride;
    public Brush? AccentOverride { get => _accentOverride; set { if (Set(ref _accentOverride, value)) OnPropertyChanged(nameof(Accent)); } }

    // цвет кольца по нагрузке (та же схема, что у дисков): красный ≥90, золото ≥75, иначе зелёный
    public Brush Accent => _accentOverride ?? (_percent >= 90 ? Red : _percent >= 75 ? Gold : Green);

    private static readonly Brush Red = Freeze(Color.FromRgb(0xE5, 0x48, 0x4D));
    private static readonly Brush Gold = Freeze(Color.FromRgb(0xDD, 0xB4, 0x4B));
    private static readonly Brush Green = Freeze(Color.FromRgb(0x3D, 0xD6, 0x8C));
    private static Brush Freeze(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}
