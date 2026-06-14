using System.Windows;
using System.Windows.Media;
using NakClean.Services;

namespace NakClean.ViewModels;

/// <summary>Пункт выпадающего списка выбора диска (как в WizTree).</summary>
public sealed class DriveItemVm
{
    public required string Path { get; init; }     // "C:\"
    public required string Display { get; init; }  // "C: - Локальный диск (146 / 154 ГБ)"
    public override string ToString() => Display;
}

/// <summary>Строка дерева (плоский разворачиваемый список - колонки идеально выровнены).</summary>
public sealed class TreeRowVm : ViewModelBase
{
    private readonly Child _item;
    private readonly long _rootSize;
    private readonly Action<TreeRowVm> _toggle;

    public TreeRowVm(Child item, int depth, long rootSize, Action<TreeRowVm> toggle)
    {
        _item = item;
        Depth = depth;
        _rootSize = rootSize;
        _toggle = toggle;
        ToggleCommand = new RelayCommand(() => _toggle(this));
    }

    public Child Item => _item;
    public TreeNode? Folder => _item.Folder;     // null ⇒ файл
    public string FullPath => _item.FullPath;
    public int Depth { get; }
    public string Name => _item.Name;
    public bool IsFile => !_item.IsFolder;
    public bool HasChildren => _item.HasChildren;

    private bool _expanded;
    public bool IsExpanded { get => _expanded; set { if (Set(ref _expanded, value)) OnPropertyChanged(nameof(Glyph)); } }

    public string Glyph => !HasChildren ? "" : (IsExpanded ? "▾" : "▸");
    public Thickness Indent => new(Depth * 16, 0, 0, 0);

    public string SizeText => Format.Bytes(_item.Size);
    public string AllocText => Format.Bytes(_item.Alloc);
    public string FilesText => _item.IsFolder ? _item.FileCount.ToString("N0") : "";

    public double Percent => _rootSize > 0 ? (double)_item.Size / _rootSize : 0;
    public string PercentText => (Percent * 100).ToString("0.0") + "%";
    public double BarWidth => Math.Max(0, Math.Min(1, Percent)) * 88;

    public Brush RowIcon => IsFile ? FileBrush : FolderBrush;

    public RelayCommand ToggleCommand { get; }

    private static readonly Brush FolderBrush = Freeze(Color.FromRgb(0xDD, 0xB4, 0x4B));
    private static readonly Brush FileBrush = Freeze(Color.FromRgb(0x8A, 0x8A, 0x90));
    private static Brush Freeze(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}

/// <summary>Строка панели «типы файлов».</summary>
public sealed class ExtRowVm
{
    public ExtRowVm(ExtStat s, long totalSize, int colorIndex)
    {
        Ext = s.Ext;
        TypeName = s.TypeName;
        SizeText = Format.Bytes(s.Size);
        AllocText = Format.Bytes(s.Alloc);
        FilesText = s.Files.ToString("N0");
        double pct = totalSize > 0 ? (double)s.Size / totalSize : 0;
        PercentText = (pct * 100).ToString("0.0") + "%";
        BarWidth = Math.Max(0, Math.Min(1, pct)) * 70;
        Swatch = Freeze(FromHsl(colorIndex * 49.0 % 360.0, 0.55, 0.55));
    }

    public string Ext { get; }
    public string TypeName { get; }
    public string SizeText { get; }
    public string AllocText { get; }
    public string FilesText { get; }
    public string PercentText { get; }
    public double BarWidth { get; }
    public Brush Swatch { get; }

    private static Brush Freeze(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private static Color FromHsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = l - c / 2;
        double r = 0, g = 0, b = 0;
        if (h < 60) { r = c; g = x; }
        else if (h < 120) { r = x; g = c; }
        else if (h < 180) { g = c; b = x; }
        else if (h < 240) { g = x; b = c; }
        else if (h < 300) { r = x; b = c; }
        else { r = c; b = x; }
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}

/// <summary>Строка плоского списка «Файлы» (свойства вычисляются лениво - для виртуализации).</summary>
public sealed class FileRowVm
{
    private readonly ScanFile _f;
    public FileRowVm(ScanFile f) => _f = f;

    public string Path => _f.Path;
    public string Name => System.IO.Path.GetFileName(_f.Path);
    public string Folder => System.IO.Path.GetDirectoryName(_f.Path) ?? "";
    public string SizeText => Format.Bytes(_f.Size);
    public string AllocText => Format.Bytes(_f.Alloc);
}
