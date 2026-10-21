namespace NakClean.Services;

/// <summary>
/// «Подписи» форматов: с каких байтов начинается файл данного типа. Если у удалённого .jpg
/// в начале не FF D8 FF, значит его место уже занято чужими данными - восстановится мусор.
/// Проверяем только форматы с надёжной подписью; у текстовых (.txt, .csv) её нет - их не трогаем.
/// </summary>
public static class FileSignatures
{
    private static readonly Dictionary<string, Func<byte[], bool>> Checks = Build();

    private static Dictionary<string, Func<byte[], bool>> Build()
    {
        static bool At(byte[] b, int off, params byte[] sig)
        {
            if (b.Length < off + sig.Length) return false;
            for (int i = 0; i < sig.Length; i++) if (b[off + i] != sig[i]) return false;
            return true;
        }
        static byte[] A(string s) => System.Text.Encoding.ASCII.GetBytes(s);

        Func<byte[], bool> jpeg = b => At(b, 0, 0xFF, 0xD8, 0xFF);
        Func<byte[], bool> png = b => At(b, 0, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A);
        Func<byte[], bool> gif = b => At(b, 0, A("GIF8"));
        Func<byte[], bool> bmp = b => At(b, 0, A("BM"));
        Func<byte[], bool> webp = b => At(b, 0, A("RIFF")) && At(b, 8, A("WEBP"));
        Func<byte[], bool> tiff = b => At(b, 0, 0x49, 0x49, 0x2A, 0x00) || At(b, 0, 0x4D, 0x4D, 0x00, 0x2A);
        Func<byte[], bool> psd = b => At(b, 0, A("8BPS"));
        // PDF: заголовок может стоять не в самом начале (до 1 КБ мусора допускается)
        Func<byte[], bool> pdf = b => System.Text.Encoding.ASCII.GetString(b, 0, Math.Min(b.Length, 1024)).Contains("%PDF");
        Func<byte[], bool> zip = b => At(b, 0, A("PK"));                                   // docx/xlsx/pptx/odt/epub - тоже ZIP
        Func<byte[], bool> ole = b => At(b, 0, 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1); // старые doc/xls/ppt, msi
        Func<byte[], bool> rtf = b => At(b, 0, A("{\\rtf"));
        Func<byte[], bool> rar = b => At(b, 0, A("Rar!"));
        Func<byte[], bool> sevenZip = b => At(b, 0, 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C);
        Func<byte[], bool> gz = b => At(b, 0, 0x1F, 0x8B);
        Func<byte[], bool> isoMp4 = b => At(b, 4, A("ftyp"));                               // mp4/mov/m4a/heic/3gp
        Func<byte[], bool> mkv = b => At(b, 0, 0x1A, 0x45, 0xDF, 0xA3);                    // mkv/webm
        Func<byte[], bool> riff = b => At(b, 0, A("RIFF"));                                 // avi/wav
        Func<byte[], bool> mp3 = b => At(b, 0, A("ID3")) || (b.Length > 1 && b[0] == 0xFF && (b[1] & 0xE0) == 0xE0);
        Func<byte[], bool> flac = b => At(b, 0, A("fLaC"));
        Func<byte[], bool> ogg = b => At(b, 0, A("OggS"));
        Func<byte[], bool> mz = b => At(b, 0, A("MZ"));                                     // exe/dll

        var d = new Dictionary<string, Func<byte[], bool>>(StringComparer.OrdinalIgnoreCase);
        void Add(Func<byte[], bool> f, params string[] exts) { foreach (var e in exts) d[e] = f; }
        Add(jpeg, "jpg", "jpeg", "jfif");
        Add(png, "png"); Add(gif, "gif"); Add(bmp, "bmp"); Add(webp, "webp"); Add(tiff, "tif", "tiff"); Add(psd, "psd");
        Add(pdf, "pdf");
        Add(zip, "zip", "docx", "xlsx", "pptx", "odt", "ods", "odp", "epub", "jar", "apk");
        Add(ole, "doc", "xls", "ppt", "msi");
        Add(rtf, "rtf");
        Add(rar, "rar"); Add(sevenZip, "7z"); Add(gz, "gz", "tgz");
        Add(isoMp4, "mp4", "m4v", "mov", "m4a", "3gp", "heic", "heif");
        Add(mkv, "mkv", "webm"); Add(riff, "avi", "wav");
        Add(mp3, "mp3"); Add(flac, "flac"); Add(ogg, "ogg", "opus");
        Add(mz, "exe", "dll");
        return d;
    }

    private static readonly Func<byte[], bool>[] AllChecks = Checks.Values.Distinct().ToArray();

    /// <summary>
    /// true - формат известен, а начало файла не похоже ни на него, ни на какой-либо другой известный формат
    /// (данные чужие). Файл с «не тем» расширением (.png, внутри которого JPEG; .doc, внутри которого DOCX)
    /// целый - его не помечаем: проверено на реальных файлах, такое встречается.
    /// </summary>
    public static bool Mismatch(string fileName, byte[] head)
    {
        int dot = fileName.LastIndexOf('.');
        if (dot < 0 || dot == fileName.Length - 1) return false;
        if (!Checks.TryGetValue(fileName[(dot + 1)..], out var ok) || ok(head)) return false;
        foreach (var other in AllChecks) if (other(head)) return false;
        return true;
    }

    /// <summary>Служебный файл Word/Excel «~$имя.docx» (~160 байт: только имя того, кто открыл документ).</summary>
    public static bool IsOfficeLock(string fileName, long size) =>
        size < 1024 && fileName.StartsWith("~$", StringComparison.Ordinal);
}
