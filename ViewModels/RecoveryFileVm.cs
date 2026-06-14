using System.Windows.Media;
using NakClean.Services;

namespace NakClean.ViewModels;

public sealed class RecoveryFileVm : ViewModelBase
{
    public DeletedFile File { get; }
    public RecoveryFileVm(DeletedFile f) { File = f; }

    private bool _selected;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }

    public string Name => File.Name;
    public string Path => File.Path;
    public string SizeText => Format.Bytes(File.Size);

    public string ChanceText => File.Chance switch
    {
        2 => Loc.I["rec_high"],
        1 => Loc.I["rec_mid"],
        _ => Loc.I["rec_low"],
    };

    public Brush ChanceBrush => File.Chance switch
    {
        2 => Freeze(0x3D, 0xD6, 0x8C),
        1 => Freeze(0xDD, 0xB4, 0x4B),
        _ => Freeze(0xE5, 0x48, 0x4D),
    };

    private static Brush Freeze(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }
}
