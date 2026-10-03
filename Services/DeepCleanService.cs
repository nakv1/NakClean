using Microsoft.Win32;

namespace NakClean.Services;

/// <summary>Один вид следа для глубокой очистки: сколько записей найдено + как очистить.</summary>
public sealed class DeepTrace
{
    public required string Id { get; init; }
    public required string Glyph { get; init; }
    public required Func<int> Count { get; init; }
    public required Action Clear { get; init; }
}

/// <summary>
/// Глубокая очистка приватности - то, чего НЕТ в обычной «Очистке».
/// Пока: история в реестре (набранные пути, поиск, «Выполнить», недавние документы, недавние файлы Office).
/// Всё в ветке текущего пользователя (HKCU), права администратора не нужны.
/// </summary>
public static class DeepCleanService
{
    private const string Explorer = @"Software\Microsoft\Windows\CurrentVersion\Explorer";

    public static List<DeepTrace> RegistryTraces() => new()
    {
        new DeepTrace
        {
            Id = "typed_paths", Glyph = "📁",
            Count = () => CountValues($@"{Explorer}\TypedPaths"),
            Clear = () => DeleteTree($@"{Explorer}\TypedPaths"),
        },
        new DeepTrace
        {
            Id = "explorer_search", Glyph = "🔎",
            Count = () => CountTree($@"{Explorer}\WordWheelQuery"),
            Clear = () => DeleteTree($@"{Explorer}\WordWheelQuery"),
        },
        new DeepTrace
        {
            Id = "run_mru", Glyph = "⌨",
            Count = () => CountValues($@"{Explorer}\RunMRU"),
            Clear = () => DeleteTree($@"{Explorer}\RunMRU"),
        },
        new DeepTrace
        {
            Id = "recent_docs", Glyph = "🕘",
            Count = () => CountTree($@"{Explorer}\RecentDocs"),
            Clear = () => DeleteTree($@"{Explorer}\RecentDocs"),
        },
        new DeepTrace
        {
            Id = "office_mru", Glyph = "📄",
            Count = CountOfficeMru,
            Clear = ClearOfficeMru,
        },
    };

    // ---------- подсчёт ----------

    /// <summary>Значения одного ключа (без служебных MRUList/MRUListEx и значения по умолчанию).</summary>
    private static int CountValues(string subKey)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(subKey);
            if (k is null) return 0;
            return k.GetValueNames().Count(IsRealValue);
        }
        catch { return 0; }
    }

    /// <summary>Значения ключа и всех его подключей (для RecentDocs - записи по типам файлов).</summary>
    private static int CountTree(string subKey)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(subKey);
            return k is null ? 0 : CountTree(k);
        }
        catch { return 0; }
    }

    private static int CountTree(RegistryKey k)
    {
        int n = k.GetValueNames().Count(IsRealValue);
        foreach (var sub in k.GetSubKeyNames())
        {
            try { using var s = k.OpenSubKey(sub); if (s != null) n += CountTree(s); }
            catch { }
        }
        return n;
    }

    private static bool IsRealValue(string name)
        => name.Length > 0
           && !name.Equals("MRUList", StringComparison.OrdinalIgnoreCase)
           && !name.Equals("MRUListEx", StringComparison.OrdinalIgnoreCase);

    // ---------- Office: списки недавних файлов и папок (File MRU / Place MRU) ----------

    private static void ForEachOfficeMru(Action<RegistryKey> action)
    {
        try
        {
            using var office = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Office", writable: true);
            if (office is null) return;
            WalkOfficeMru(office, action);
        }
        catch { }
    }

    private static void WalkOfficeMru(RegistryKey node, Action<RegistryKey> action)
    {
        foreach (var name in node.GetSubKeyNames())
        {
            RegistryKey? sub = null;
            try { sub = node.OpenSubKey(name, writable: true); }
            catch { }
            if (sub is null) continue;
            using (sub)
            {
                if (name.Equals("File MRU", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Place MRU", StringComparison.OrdinalIgnoreCase))
                    action(sub);
                else
                    WalkOfficeMru(sub, action);
            }
        }
    }

    private static int CountOfficeMru()
    {
        int n = 0;
        ForEachOfficeMru(k => n += k.GetValueNames().Count(IsRealValue));
        return n;
    }

    private static void ClearOfficeMru()
    {
        ForEachOfficeMru(k =>
        {
            foreach (var v in k.GetValueNames().Where(IsRealValue))
            {
                try { k.DeleteValue(v, throwOnMissingValue: false); }
                catch { }
            }
        });
    }

    // ---------- очистка ----------

    /// <summary>Удаляет ключ целиком - Windows создаёт его заново пустым при следующем использовании.</summary>
    private static void DeleteTree(string subKey)
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false); }
        catch { }
    }
}
