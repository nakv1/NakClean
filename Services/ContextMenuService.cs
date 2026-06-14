using Microsoft.Win32;
using NakClean.Models;

namespace NakClean.Services;

public sealed class ContextMenuEntry
{
    public required string Name { get; init; }        // имя verb-ключа
    public required string DisplayName { get; init; }  // подпись в меню
    public required string Location { get; init; }     // где показывается
    public string Command { get; init; } = "";
    public required string SubKey { get; init; }       // путь под HKCR
    public bool Enabled { get; set; }
}

/// <summary>
/// Пункты контекстного меню проводника (shell-verbs) из основных мест реестра.
/// Выключение - через значение LegacyDisable (штатный механизм Windows),
/// удаление - с резервной копией .reg. Только HKCR (классы файлов/папок/дисков).
/// </summary>
public static class ContextMenuService
{
    private static readonly (string sub, string label)[] Roots =
    {
        (@"*\shell", "cmw_files"),
        (@"Directory\shell", "cmw_folders"),
        (@"Directory\Background\shell", "cmw_folderbg"),
        (@"Drive\shell", "cmw_drives"),
        (@"AllFilesystemObjects\shell", "cmw_allobjects"),
    };

    private static RegistryKey Base() => RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry64);

    public static List<ContextMenuEntry> GetEntries()
    {
        var list = new List<ContextMenuEntry>();
        using var baseKey = Base();
        foreach (var (sub, label) in Roots)
        {
            try
            {
                using var shell = baseKey.OpenSubKey(sub);
                if (shell is null) continue;
                foreach (var verb in shell.GetSubKeyNames())
                {
                    try
                    {
                        using var vk = shell.OpenSubKey(verb);
                        if (vk is null) continue;

                        string? def = vk.GetValue(null) as string;
                        string display = (string.IsNullOrWhiteSpace(def) || def!.StartsWith('@')) ? verb : def!;

                        string cmd = "";
                        using (var ck = vk.OpenSubKey("command"))
                            cmd = ck?.GetValue(null) as string ?? "";

                        bool enabled = vk.GetValue("LegacyDisable") is null;

                        list.Add(new ContextMenuEntry
                        {
                            Name = verb,
                            DisplayName = display,
                            Location = label,
                            Command = cmd,
                            SubKey = $"{sub}\\{verb}",
                            Enabled = enabled,
                        });
                    }
                    catch { }
                }
            }
            catch { }
        }
        return list;
    }

    public static bool SetEnabled(ContextMenuEntry e, bool enable)
    {
        try
        {
            using var baseKey = Base();
            using var vk = baseKey.OpenSubKey(e.SubKey, writable: true);
            if (vk is null) return false;
            if (enable) vk.DeleteValue("LegacyDisable", throwOnMissingValue: false);
            else vk.SetValue("LegacyDisable", "");
            e.Enabled = enable;
            return true;
        }
        catch { return false; }
    }

    /// <summary>Удаляет пункт меню (с резервной копией .reg).</summary>
    public static bool Delete(ContextMenuEntry e)
    {
        var issue = new RegistryIssue
        {
            Category = "Контекстное меню",
            Problem = "Удаление пункта",
            Target = e.DisplayName,
            Hive = RegistryHive.ClassesRoot,
            SubKey = e.SubKey,
            ValueName = null,
        };
        try
        {
            RegistryFixService.Backup(new[] { issue });
            var (_, failed) = RegistryFixService.Delete(new[] { issue });
            return failed == 0;
        }
        catch { return false; }
    }
}
