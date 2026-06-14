using NakClean.Models;
using NakClean.Services;

namespace NakClean.ViewModels;

public sealed class InstalledAppVm : ViewModelBase
{
    public InstalledApp App { get; }
    public InstalledAppVm(InstalledApp a) => App = a;

    public string Name => App.Name;
    public string Publisher => string.IsNullOrEmpty(App.Publisher) ? "-" : App.Publisher;
    public string Version => string.IsNullOrEmpty(App.Version) ? "-" : App.Version;
    public string DateText => App.InstallDate?.ToString("dd.MM.yyyy") ?? "-";
    public string SizeText => App.SizeBytes > 0 ? Format.Bytes(App.SizeBytes) : "-";

    public bool CanUninstall => App.CanUninstall;
    public bool CanRepair => App.CanRepair;

    // поля для сортировки (по реальным значениям, а не по тексту)
    public DateTime SortDate => App.InstallDate ?? DateTime.MinValue;
    public long SortSize => App.SizeBytes;

    /// <summary>Уведомить UI после переименования.</summary>
    public void RefreshName() => OnPropertyChanged(nameof(Name));
}
