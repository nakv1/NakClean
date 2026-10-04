using System.IO;
using System.Text.Json;

namespace NakClean.Services;

/// <summary>
/// Запись журнала изменений: id твика, ключ-имя, когда применён и что было ДО него
/// (Original - снимок исходного состояния; null у записей старых версий).
/// </summary>
public sealed record ChangeEntry(string Id, string NameKey, string When, string? Original = null);

/// <summary>
/// Журнал применённых твиков оптимизации (персистентный между запусками).
/// Позволяет откатить всё разом - ровно к тому, что было у человека. JSON в %AppData%\NakClean\changelog.json.
/// </summary>
public static class ChangeLogService
{
    private static string PathFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NakClean", "changelog.json");

    public static List<ChangeEntry> Load()
    {
        try
        {
            if (File.Exists(PathFile))
                return JsonSerializer.Deserialize<List<ChangeEntry>>(File.ReadAllText(PathFile)) ?? new();
        }
        catch { }
        return new();
    }

    private static void Save(List<ChangeEntry> list)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathFile)!);
            File.WriteAllText(PathFile, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public static ChangeEntry? Get(string id) => Load().Find(e => e.Id == id);

    /// <summary>
    /// Записать применение твика. Если он уже в журнале - запись не трогаем: там снимок
    /// состояния ДО первого применения, а повторный снимок был бы уже «после».
    /// </summary>
    public static void Add(string id, string nameKey, string? original)
    {
        var l = Load();
        if (l.Exists(e => e.Id == id)) return;
        l.Add(new ChangeEntry(id, nameKey, DateTime.Now.ToString("o"), original));
        Save(l);
    }

    /// <summary>Убрать твик из журнала (после отката).</summary>
    public static void Remove(string id)
    {
        var l = Load();
        if (l.RemoveAll(e => e.Id == id) > 0) Save(l);
    }
}
