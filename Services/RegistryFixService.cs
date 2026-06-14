using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;
using NakClean.Models;

namespace NakClean.Services;

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

    /// <summary>Экспортирует затронутые ключи в один .reg-файл. Возвращает путь.</summary>
    public static string Backup(IReadOnlyList<RegistryIssue> issues)
    {
        Directory.CreateDirectory(BackupDir);
        string file = Path.Combine(BackupDir, $"reg-backup-{DateTime.Now:yyyyMMdd-HHmmss}.reg");

        var sb = new StringBuilder();
        sb.AppendLine("Windows Registry Editor Version 5.00");
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

        File.WriteAllText(file, sb.ToString(), Encoding.Unicode);
        return file;
    }

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
