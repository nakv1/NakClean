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

    /// <summary>Категория для группировки (по расширению).</summary>
    public string Category
    {
        get
        {
            var ext = System.IO.Path.GetExtension(File.Name).TrimStart('.').ToLowerInvariant();
            return ext switch
            {
                "jpg" or "jpeg" or "png" or "gif" or "bmp" or "webp" or "heic" or "tiff" or "tif" or "svg" or "ico" or "raw" or "cr2" or "nef" => Loc.I["rcat_img"],
                "mp4" or "avi" or "mkv" or "mov" or "wmv" or "flv" or "webm" or "m4v" or "mpg" or "mpeg" => Loc.I["rcat_video"],
                "mp3" or "wav" or "flac" or "ogg" or "m4a" or "aac" or "wma" or "opus" => Loc.I["rcat_audio"],
                "doc" or "docx" or "xls" or "xlsx" or "ppt" or "pptx" or "pdf" or "txt" or "rtf" or "odt" or "ods" or "csv" or "md" => Loc.I["rcat_doc"],
                "zip" or "rar" or "7z" or "tar" or "gz" or "bz2" or "xz" or "cab" or "iso" => Loc.I["rcat_archive"],
                "exe" or "dll" or "msi" or "bin" or "sys" or "bat" or "cmd" or "ps1" or "com" => Loc.I["rcat_prog"],
                "" => Loc.I["rcat_other"],
                _ => Loc.I["rcat_other"],
            };
        }
    }

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
