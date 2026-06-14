namespace NakClean.ViewModels;

/// <summary>Кликабельный фильтр-раздел над списком восстановления.</summary>
public sealed class RecoveryCategoryVm : ViewModelBase
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public int Count { get; init; }
    public string Label => $"{Name}  ({Count})";

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}
