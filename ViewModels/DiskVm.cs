using System.Windows.Media;
using NakClean.Services;

namespace NakClean.ViewModels;

public sealed class DiskVm : ViewModelBase
{
    public string Name { get; }
    private double _percent;
    private string _detail = "";

    public DiskVm(DiskStat s)
    {
        Name = s.Name;
        Update(s);
    }

    public double Percent { get => _percent; private set { if (Set(ref _percent, value)) OnPropertyChanged(nameof(Accent)); } }
    public string Detail { get => _detail; private set => Set(ref _detail, value); }

    public Brush Accent => Percent >= 90
        ? new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D))   // красный - почти полный
        : Percent >= 75
            ? new SolidColorBrush(Color.FromRgb(0xDD, 0xB4, 0x4B)) // золотой
            : new SolidColorBrush(Color.FromRgb(0x3D, 0xD6, 0x8C)); // зелёный - много свободного

    public void Update(DiskStat s)
    {
        Percent = s.UsedPercent;
        Detail = $"{Format.Bytes(s.UsedBytes)} / {Format.Bytes(s.TotalBytes)}";
    }
}
