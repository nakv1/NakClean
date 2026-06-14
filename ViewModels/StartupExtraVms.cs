using System.Windows.Media;
using NakClean.Services;

namespace NakClean.ViewModels;

public sealed class TaskEntryVm : ViewModelBase
{
    public ScheduledTaskEntry Entry { get; }
    public TaskEntryVm(ScheduledTaskEntry e) { Entry = e; _enabled = e.Enabled; }

    public string Name => Entry.Name;
    public string Path => Entry.Path;
    public string Command => string.IsNullOrEmpty(Entry.Command) ? "-" : Entry.Command;
    public string Author => string.IsNullOrEmpty(Entry.Author) ? "-" : Entry.Author;

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

public sealed class ContextMenuEntryVm : ViewModelBase
{
    public ContextMenuEntry Entry { get; }
    public ContextMenuEntryVm(ContextMenuEntry e) { Entry = e; _enabled = e.Enabled; }

    public string DisplayName => Entry.DisplayName;
    public string Name => Entry.Name;
    public string Location => Loc.I[Entry.Location];
    public string Command => string.IsNullOrEmpty(Entry.Command) ? "-" : Entry.Command;

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

    public void RaiseLocalized()
    {
        OnPropertyChanged(nameof(EnabledText));
        OnPropertyChanged(nameof(Location));
    }
}

public sealed class ServiceEntryVm : ViewModelBase
{
    public ServiceEntry Entry { get; }
    public ServiceEntryVm(ServiceEntry e) { Entry = e; }

    public string Name => Entry.Name;
    public string DisplayName => Entry.DisplayName;

    public string StateText => Entry.State;
    public string StartModeText => Entry.StartMode switch
    {
        "Auto" => Loc.I["sm_auto"],
        "Manual" => Loc.I["sm_manual"],
        "Disabled" => Loc.I["sm_disabled"],
        _ => Entry.StartMode,
    };

    public Brush StateBrush => Entry.State == "Running"
        ? new SolidColorBrush(Color.FromRgb(0x3D, 0xD6, 0x8C))
        : new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x95));

    public void Refresh()
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(StartModeText));
        OnPropertyChanged(nameof(StateBrush));
    }
}
