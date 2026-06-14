using System.IO;
using System.Text.Json;

namespace NakClean.Services;

/// <summary>Запись журнала изменений: id твика, ключ-имя, когда применён.</summary>
public sealed record ChangeEntry(string Id, string NameKey, string When);

/// <summary>
/// Журнал применённых твиков оптимизации (персистентный между запусками).
/// Позволяет откатить всё разом. JSON в %AppData%\NakClean\changelog.json.
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

    /// <summary>Записать применение твика (заменяет прежнюю запись того же id).</summary>
    public static void Add(string id, string nameKey)
    {
        var l = Load();
        l.RemoveAll(e => e.Id == id);
        l.Add(new ChangeEntry(id, nameKey, DateTime.Now.ToString("o")));
        Save(l);
    }

    /// <summary>Убрать твик из журнала (после отката).</summary>
    public static void Remove(string id)
    {
        var l = Load();
        if (l.RemoveAll(e => e.Id == id) > 0) Save(l);
    }

    public static void Clear() => Save(new());
}
