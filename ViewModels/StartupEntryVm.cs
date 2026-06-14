using System.Windows.Media;
using NakClean.Models;

namespace NakClean.ViewModels;

public sealed class StartupEntryVm : ViewModelBase
{
    public StartupEntry Entry { get; }
    public StartupEntryVm(StartupEntry e) { Entry = e; _enabled = e.Enabled; }

    public string Name => Entry.Name;
    public string Command => Entry.Command;
    public string Publisher => string.IsNullOrEmpty(Entry.Publisher) ? "-" : Entry.Publisher;
    public string LocationLabel => Entry.LocationLabel;
    public bool NeedsAdmin => Entry.NeedsAdmin;

    private bool _enabled;
    public bool Enabled
    {
        get => _enabled;
        set { if (Set(ref _enabled, value)) { OnPropertyChanged(nameof(EnabledText)); OnPropertyChanged(nameof(EnabledBrush)); } }
    }

    public string EnabledText => NakClean.Services.Loc.I[Enabled ? "st_yes" : "st_no"];
    public Brush EnabledBrush => Enabled
        ? new SolidColorBrush(Color.FromRgb(0x3D, 0xD6, 0x8C))
        : new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x95));

    public void RaiseLocalized() => OnPropertyChanged(nameof(EnabledText));
}
