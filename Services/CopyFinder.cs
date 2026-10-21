using System.IO;
using System.Management;

namespace NakClean.Services;

/// <summary>Найденная копия удалённого файла.</summary>
public sealed record FoundCopy(string Path, string SourceKey, DateTime? When, long Size);

/// <summary>
/// Поиск копий удалённого файла там, куда Windows и Office кладут их сами:
///  • снимки Windows (точки восстановления / «Предыдущие версии») - на SSD это часто единственный шанс:
///    TRIM не стирает то, что лежит в снимке;
///  • автосохранения Word / Excel / PowerPoint.
/// Файлы с тем же именем на дисках ищет вызывающий (через общий индекс поиска).
/// Только чтение. Снимки видны только администратору - программа и так запущена от него.
/// </summary>
public static class CopyFinder
{
    /// <summary>Копии в снимках Windows: тот же путь внутри каждого снимка этого диска.</summary>
    public static (List<FoundCopy> found, int shadowsOnDrive) FromShadowCopies(string originalPath)
    {
        var found = new List<FoundCopy>();
        int count = 0;
        if (originalPath.Length < 4 || originalPath[1] != ':' || originalPath[2] != '\\') return (found, 0);
        string letter = originalPath[..2].ToUpperInvariant();
        string rel = originalPath[3..];
        try
        {
            // том снимка задан GUID-путём (\\?\Volume{...}\) - сопоставляем его с буквой диска
            var volLetters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var s = new ManagementObjectSearcher("SELECT DeviceID, DriveLetter FROM Win32_Volume"))
                foreach (ManagementObject v in s.Get())
                    using (v)
                        if (v["DeviceID"] is string id && v["DriveLetter"] is string dl) volLetters[id] = dl.ToUpperInvariant();

            using var sc = new ManagementObjectSearcher("SELECT DeviceObject, VolumeName, InstallDate FROM Win32_ShadowCopy");
            foreach (ManagementObject o in sc.Get())
                using (o)
                {
                    if (o["VolumeName"] is not string vol || !volLetters.TryGetValue(vol, out var l) || l != letter) continue;
                    if (o["DeviceObject"] is not string dev) continue;
                    count++;
                    string cand = dev.TrimEnd('\\') + "\\" + rel;
                    try
                    {
                        var fi = new FileInfo(cand);
                        if (!fi.Exists) continue;
                        DateTime? when = null;
                        try { if (o["InstallDate"] is string d) when = ManagementDateTimeConverter.ToDateTime(d); } catch { }
                        found.Add(new FoundCopy(cand, "cp_src_shadow", when, fi.Length));
                    }
                    catch { }
                }
        }
        catch { }
        return (found, count);
    }

    /// <summary>Автосохранения Office: файлы, в имени которых есть имя документа.</summary>
    public static List<FoundCopy> FromOfficeAutosave(string fileName)
    {
        var found = new List<FoundCopy>();
        string stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.Length < 3) return found;   // короткое имя («1.docx») дало бы кучу случайных совпадений

        string app = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dirs = new[]
        {
            Path.Combine(app, "Microsoft", "Word"),
            Path.Combine(app, "Microsoft", "Excel"),
            Path.Combine(app, "Microsoft", "PowerPoint"),
            Path.Combine(local, "Microsoft", "Office", "UnsavedFiles"),
        };
        var opts = new System.IO.EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var d in dirs.Where(Directory.Exists))
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(d, "*", opts))
                {
                    if (!Path.GetFileName(f).Contains(stem, StringComparison.OrdinalIgnoreCase)) continue;
                    try { var fi = new FileInfo(f); found.Add(new FoundCopy(f, "cp_src_office", fi.LastWriteTime, fi.Length)); } catch { }
                }
            }
            catch { }
        }
        return found;
    }
}
