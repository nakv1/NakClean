using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NakClean.Services;

namespace NakClean.ViewModels;

public sealed class RecoveryFileVm : ViewModelBase
{
    public DeletedFile File { get; }
    public RecoveryFileVm(DeletedFile f)
    {
        File = f;
        PreviewCommand = new RelayCommand(async () => await TogglePreviewAsync());
    }

    // ---------- предпросмотр: что внутри, без восстановления ----------
    public RelayCommand PreviewCommand { get; }
    private const long PreviewMax = 30L * 1024 * 1024;
    private bool _previewLoaded;

    private bool _previewOpen;
    public bool PreviewOpen { get => _previewOpen; private set => Set(ref _previewOpen, value); }

    private ImageSource? _previewImage;
    public ImageSource? PreviewImage
    {
        get => _previewImage;
        private set { if (Set(ref _previewImage, value)) OnPropertyChanged(nameof(HasPreviewImage)); }
    }
    public bool HasPreviewImage => _previewImage != null;

    private string _previewText = "";
    public string PreviewText
    {
        get => _previewText;
        private set { if (Set(ref _previewText, value)) OnPropertyChanged(nameof(HasPreviewText)); }
    }
    public bool HasPreviewText => _previewText.Length > 0;

    private async Task TogglePreviewAsync()
    {
        if (PreviewOpen) { PreviewOpen = false; return; }
        PreviewOpen = true;
        if (_previewLoaded) return;
        PreviewText = Loc.I["pv_loading"];
        var (img, text) = await Task.Run(() => BuildPreview(File));
        PreviewImage = img;
        PreviewText = text ?? "";
        _previewLoaded = true;
    }

    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
        { "jpg", "jpeg", "jfif", "png", "gif", "bmp", "tif", "tiff", "ico", "webp", "heic" };
    private static readonly HashSet<string> TextExts = new(StringComparer.OrdinalIgnoreCase)
        { "txt", "csv", "md", "log", "json", "xml", "html", "htm", "ini", "cfg", "conf", "yml", "yaml",
          "cs", "py", "js", "ts", "css", "sql", "bat", "cmd", "ps1", "reg", "c", "cpp", "h", "java" };

    /// <summary>Картинка (уменьшенная) или до ~600 символов текста. Всё в фоне, только чтение с диска.</summary>
    private static (ImageSource? img, string? text) BuildPreview(DeletedFile f)
    {
        if (f.Size > PreviewMax) return (null, string.Format(Loc.I["pv_big"], Format.Bytes(PreviewMax)));
        var bytes = NtfsMftReader.ReadContent(f);
        if (bytes is null) return (null, Loc.I["pv_fail"]);
        if (Array.TrueForAll(bytes, b => b == 0)) return (null, Loc.I["pv_zeros"]);

        // «замок» Word: первый байт - длина имени, дальше само имя того, кто открыл документ
        if (FileSignatures.IsOfficeLock(f.Name, f.Size))
        {
            int len = bytes.Length > 1 ? Math.Min(bytes[0], bytes.Length - 1) : 0;
            return (null, string.Format(Loc.I["pv_lock"], Encoding.ASCII.GetString(bytes, 1, len)));
        }

        string ext = System.IO.Path.GetExtension(f.Name).TrimStart('.');
        string kind = FileSignatures.Detect(bytes[..Math.Min(bytes.Length, 4096)]) ?? Loc.I["pv_unknown"];

        if (ImageExts.Contains(ext) || kind is "JPEG" or "PNG" or "GIF" or "BMP" or "WebP" or "TIFF")
        {
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.StreamSource = new System.IO.MemoryStream(bytes);
                bi.DecodePixelHeight = 220;   // для предпросмотра хватит, память не тратим
                bi.EndInit();
                bi.Freeze();
                return (bi, null);
            }
            catch { return (null, string.Format(Loc.I["pv_bad_image"], kind)); }
        }

        if (ext.Equals("docx", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var zip = new System.IO.Compression.ZipArchive(new System.IO.MemoryStream(bytes));
                var entry = zip.GetEntry("word/document.xml");
                if (entry != null)
                {
                    using var r = new System.IO.StreamReader(entry.Open(), Encoding.UTF8);
                    string xml = r.ReadToEnd().Replace("</w:p>", "\n");
                    string plain = System.Net.WebUtility.HtmlDecode(Regex.Replace(xml, "<[^>]+>", ""));
                    plain = Regex.Replace(plain, @"\n\s*\n+", "\n").Trim();
                    return (null, plain.Length == 0 ? Loc.I["pv_docx_empty"] : Cut(plain));
                }
            }
            catch { }
            return (null, string.Format(Loc.I["pv_none"], kind));
        }

        if (TextExts.Contains(ext)) return (null, Cut(DecodeText(bytes)));
        return (null, string.Format(Loc.I["pv_none"], kind));
    }

    private static string Cut(string s) => s.Length <= 600 ? s : s[..600] + " …";

    // кодировка: метка UTF-16/UTF-8, иначе пробуем строгий UTF-8, не вышло - кириллица Windows (1251)
    private static string DecodeText(byte[] b)
    {
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, Math.Min(b.Length - 2, 4096));
        int n = Math.Min(b.Length, 4096);
        var strict = new UTF8Encoding(false, true);
        // обрезка на 4 КБ может разрезать русскую букву (2 байта) - пробуем отступить на 1-3 байта
        for (int cut = 0; cut <= 3 && n - cut > 0; cut++)
        {
            try { return strict.GetString(b, 0, n - cut).TrimStart('﻿'); }
            catch { if (n == b.Length) break; }   // файл целиком и всё равно не UTF-8 - дальше не пробуем
        }
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1251).GetString(b, 0, n);
        }
        catch { return Encoding.Latin1.GetString(b, 0, n); }
    }

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

    /// <summary>Вторая строка: где лежал; для файлов из корзины - исходная папка и когда отправлен в корзину.</summary>
    public string PathText => File.RecycledAt is { } when
        ? $"{File.Path}  ·  " + string.Format(Loc.I["rec_from_bin"],
            when.ToString("g", System.Globalization.CultureInfo.GetCultureInfo(Loc.I.IsEn ? "en-US" : "ru-RU")))
        : File.Path;

    public string ChanceText => File.Chance switch
    {
        2 => Loc.I["rec_high"],
        1 => Loc.I["rec_mid"],
        0 => Loc.I["rec_low"],
        DeletedFile.ChanceOverwritten => Loc.I["rec_overwritten"],
        DeletedFile.ChanceService => Loc.I["rec_service"],
        _ => Loc.I["rec_wiped"],
    };

    /// <summary>Пояснение к метке (подсказка при наведении) - только для «нельзя восстановить».</summary>
    public string? ChanceTip => File.Chance switch
    {
        DeletedFile.ChanceWiped => Loc.I["rec_tip_wiped"],
        DeletedFile.ChanceOverwritten => Loc.I["rec_tip_overwritten"],
        DeletedFile.ChanceService => Loc.I["rec_tip_service"],
        _ => null,
    };

    public Brush ChanceBrush => File.Chance switch
    {
        2 => Freeze(0x3D, 0xD6, 0x8C),
        1 => Freeze(0xDD, 0xB4, 0x4B),
        0 => Freeze(0xE5, 0x48, 0x4D),
        _ => Freeze(0x8A, 0x8A, 0x94),
    };

    private static Brush Freeze(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }
}
