using NakClean.Models;

namespace NakClean.ViewModels;

/// <summary>Категория сканирования реестра (галочка области поиска).</summary>
public sealed class RegScanOption : ViewModelBase
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    public string NameText => NakClean.Services.Loc.I.IsEn ? NakClean.Services.Loc.I[$"rcat_{Id}"] : Name;
    public void RaiseLocalized() => OnPropertyChanged(nameof(NameText));

    private bool _selected = true;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
}

/// <summary>Найденная проблема реестра с галочкой выбора.</summary>
public sealed class RegistryIssueVm : ViewModelBase
{
    public RegistryIssue Issue { get; }
    public RegistryIssueVm(RegistryIssue i) => Issue = i;

    public string Category => Issue.Category;
    public string Problem => Issue.Problem;
    public string Target => Issue.Target;
    public string FullPath => Issue.FullPath;

    private bool _selected = true;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
}
