using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;
using NakClean.Models;

namespace NakClean.Services;

/// <summary>Файл резервной копии реестра: когда, откуда (kind) и сколько записей (-1 = неизвестно).</summary>
public readonly record struct RegBackup(string Path, DateTime When, string Kind, int Count);

/// <summary>
/// Резервное копирование и удаление записей реестра.
/// ПЕРЕД любым удалением экспортирует затронутые ключи в .reg-файл,
/// чтобы изменения всегда можно было откатить двойным кликом по бэкапу.
/// </summary>
public static class RegistryFixService
{
    public static string BackupDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "NakClean", "RegistryBackups");

    // строка-метка внутри .reg (комментарий «;» reg.exe игнорирует при импорте)
    private const string MetaPrefix = "; NakClean";

    /// <summary>Экспортирует затронутые ключи в один .reg-файл. Возвращает путь.</summary>
    /// <param name="kind">откуда копия: registry / contextmenu / startup / uninstall (для списка копий)</param>
    /// <param name="requireData">не удалось сохранить ни одного ключа - удалить пустой файл и бросить исключение (удалять без копии нельзя)</param>
    public static string Backup(IReadOnlyList<RegistryIssue> issues, string kind = "registry", bool requireData = false)
    {
        int exported = 0;
        Directory.CreateDirectory(BackupDir);
        string file = Path.Combine(BackupDir, $"reg-backup-{DateTime.Now:yyyyMMdd-HHmmss-fff}.reg");

        var sb = new StringBuilder();
        sb.AppendLine("Windows Registry Editor Version 5.00");
        sb.AppendLine($"{MetaPrefix} kind={kind} count={issues.Count}");
        sb.AppendLine();

        // уникальные ключи для экспорта (для значений - содержащий ключ)
        var paths = issues.Select(i => i.FullPath).Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            string tmp = Path.Combine(Path.GetTempPath(), $"pw-reg-{Guid.NewGuid():N}.reg");
            try
            {
                if (RunReg($"export \"{path}\" \"{tmp}\" /y") && File.Exists(tmp))
                {
                    exported++;
                    // отбрасываем строку-заголовок версии у каждого фрагмента
                    foreach (var line in File.ReadAllLines(tmp, Encoding.Unicode))
                    {
                        if (line.StartsWith("Windows Registry Editor")) continue;
                        sb.AppendLine(line);
                    }
                }
            }
            catch { /* не вышло экспортировать этот ключ - пропускаем */ }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }

        if (requireData && exported == 0) throw new InvalidOperationException("backup failed");
        File.WriteAllText(file, sb.ToString(), Encoding.Unicode);
        return file;
    }

    /// <summary>
    /// Копия ОТДЕЛЬНЫХ значений (а не всего ключа) - при восстановлении вернётся ровно удалённое,
    /// соседние значения не перезапишутся. Бросает исключение, если сохранить нечего (удалять без копии нельзя).
    /// </summary>
    public static string BackupValues(IEnumerable<(RegistryHive Hive, RegistryView View, string SubKey, string Name)> values,
                                      string kind, int count)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Windows Registry Editor Version 5.00");
        sb.AppendLine($"{MetaPrefix} kind={kind} count={count}");
        int saved = 0;

        foreach (var (hive, view, subKey, name) in values)
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var k = baseKey.OpenSubKey(subKey);
            object? data = k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (k is null || data is null) continue;

            string? line = RegLine(name, k.GetValueKind(name), data);
            if (line is null) continue;
            sb.AppendLine();
            sb.AppendLine($"[{HiveName(hive)}\\{RealSubKey(hive, view, subKey, name)}]");
            sb.AppendLine(line);
            saved++;
        }

        if (saved == 0) throw new InvalidOperationException("nothing to back up");
        Directory.CreateDirectory(BackupDir);
        string file = Path.Combine(BackupDir, $"reg-backup-{DateTime.Now:yyyyMMdd-HHmmss-fff}.reg");
        File.WriteAllText(file, sb.ToString(), Encoding.Unicode);
        return file;
    }

    /// <summary>
    /// Настоящий путь ключа для reg.exe. 32-битный вид HKLM\SOFTWARE Windows хранит в WOW6432Node,
    /// но часть ключей общая - поэтому смотрим, где ключ реально лежит.
    /// </summary>
    public static string RealSubKey(RegistryHive hive, RegistryView view, string subKey, string? valueName = null)
    {
        const string sw = @"SOFTWARE\";
        if (view != RegistryView.Registry32 || hive != RegistryHive.LocalMachine
            || !subKey.StartsWith(sw, StringComparison.OrdinalIgnoreCase)
            || subKey.StartsWith(@"SOFTWARE\WOW6432Node", StringComparison.OrdinalIgnoreCase))
            return subKey;

        string wow = @"SOFTWARE\WOW6432Node\" + subKey[sw.Length..];
        try
        {
            using var b64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var k = b64.OpenSubKey(wow);
            if (k != null && (valueName is null || k.GetValue(valueName) != null)) return wow;
        }
        catch { }
        return subKey;
    }

    private static string HiveName(RegistryHive h) => h switch
    {
        RegistryHive.CurrentUser => "HKEY_CURRENT_USER",
        RegistryHive.LocalMachine => "HKEY_LOCAL_MACHINE",
        RegistryHive.ClassesRoot => "HKEY_CLASSES_ROOT",
        RegistryHive.Users => "HKEY_USERS",
        RegistryHive.CurrentConfig => "HKEY_CURRENT_CONFIG",
        _ => "HKEY_LOCAL_MACHINE",
    };

    // одна строка значения в формате .reg
    private static string? RegLine(string name, RegistryValueKind kind, object data)
    {
        string n = name.Length == 0 ? "@" : $"\"{Esc(name)}\"";
        return kind switch
        {
            RegistryValueKind.String => $"{n}=\"{Esc((string)data)}\"",
            RegistryValueKind.ExpandString => $"{n}=hex(2):{Hex(Encoding.Unicode.GetBytes((string)data + "\0"))}",
            RegistryValueKind.MultiString => $"{n}=hex(7):{Hex(Encoding.Unicode.GetBytes(string.Concat(((string[])data).Select(s => s + "\0")) + "\0"))}",
            RegistryValueKind.Binary => $"{n}=hex:{Hex((byte[])data)}",
            RegistryValueKind.DWord => $"{n}=dword:{unchecked((uint)(int)data):x8}",
            RegistryValueKind.QWord => $"{n}=hex(b):{Hex(BitConverter.GetBytes((long)data))}",
            _ => null,
        };
    }

    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    private static string Hex(byte[] b) => string.Join(",", b.Select(x => x.ToString("x2")));

    /// <summary>Удаляет записи. Возвращает (удалено, не удалось).</summary>
    public static (int deleted, int failed) Delete(IReadOnlyList<RegistryIssue> issues)
    {
        int deleted = 0, failed = 0;
        foreach (var i in issues)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(i.Hive, RegistryView.Registry64);
                if (i.ValueName is null)
                {
                    // удалить подключ целиком
                    int slash = i.SubKey.LastIndexOf('\\');
                    string parent = slash > 0 ? i.SubKey[..slash] : "";
                    string leaf = slash > 0 ? i.SubKey[(slash + 1)..] : i.SubKey;
                    using var pk = baseKey.OpenSubKey(parent, writable: true);
                    pk?.DeleteSubKeyTree(leaf, throwOnMissingSubKey: false);
                }
                else
                {
                    using var k = baseKey.OpenSubKey(i.SubKey, writable: true);
                    k?.DeleteValue(i.ValueName, throwOnMissingValue: false);
                }
                deleted++;
            }
            catch { failed++; }
        }
        return (deleted, failed);
    }

    /// <summary>Все резервные копии, новые сверху.</summary>
    public static List<RegBackup> ListBackups()
    {
        var list = new List<RegBackup>();
        try
        {
            if (!Directory.Exists(BackupDir)) return list;
            foreach (var f in new DirectoryInfo(BackupDir).EnumerateFiles("*.reg"))
            {
                string kind = "registry";
                int count = -1;
                try
                {
                    // метка во 2-й строке; у старых копий (до 1.6.1) её нет - считаем исправлением реестра
                    foreach (var line in File.ReadLines(f.FullName, Encoding.Unicode).Take(3))
                    {
                        if (!line.StartsWith(MetaPrefix)) continue;
                        foreach (var part in line[MetaPrefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (part.StartsWith("kind=")) kind = part[5..];
                            else if (part.StartsWith("count=") && int.TryParse(part[6..], out var n)) count = n;
                        }
                    }
                }
                catch { }
                list.Add(new RegBackup(f.FullName, ParseWhen(f), kind, count));
            }
        }
        catch { }
        return list.OrderByDescending(b => b.When).ToList();
    }

    private static DateTime ParseWhen(FileInfo f)
    {
        // reg-backup-20260926-141503(-123).reg
        var parts = Path.GetFileNameWithoutExtension(f.Name).Split('-');
        if (parts.Length >= 4 && DateTime.TryParseExact(parts[2] + parts[3], "yyyyMMddHHmmss",
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d))
            return d;
        return f.LastWriteTime;
    }

    /// <summary>Вернуть записи из копии (reg import). Нужны права администратора - они у программы есть.</summary>
    public static bool Restore(string file) => File.Exists(file) && RunReg($"import \"{file}\"");

    private static bool RunReg(string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "reg.exe",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(15000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}
