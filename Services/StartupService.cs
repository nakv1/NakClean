using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using NakClean.Models;

namespace NakClean.Services;

/// <summary>
/// Менеджер автозагрузки: чтение Run-ключей реестра и папок «Автозагрузка»,
/// включение/выключение через штатный механизм StartupApproved (как в Windows),
/// удаление записей.
/// </summary>
public static class StartupService
{
    private const string RunPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedRun = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ApprovedRun32 = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";
    private const string ApprovedFolder = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    private static readonly byte[] EnabledBytes = { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
    private static readonly byte[] DisabledBytes = { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

    public static List<StartupEntry> GetEntries()
    {
        var list = new List<StartupEntry>();

        ReadRunKey(RegistryHive.CurrentUser, RegistryView.Default, RunPath,
            RegistryHive.CurrentUser, ApprovedRun, StartupSource.RunHKCU, list);
        ReadRunKey(RegistryHive.LocalMachine, RegistryView.Registry64, RunPath,
            RegistryHive.LocalMachine, ApprovedRun, StartupSource.RunHKLM, list);
        ReadRunKey(RegistryHive.LocalMachine, RegistryView.Registry32, RunPath,
            RegistryHive.LocalMachine, ApprovedRun32, StartupSource.RunHKLM32, list);

        ReadFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            StartupSource.FolderUser, list);
        ReadFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
            StartupSource.FolderCommon, list);

        return list;
    }

    private static void ReadRunKey(RegistryHive hive, RegistryView view, string runPath,
        RegistryHive approvedHive, string approvedPath, StartupSource source, List<StartupEntry> list)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var run = baseKey.OpenSubKey(runPath);
            if (run is null) return;

            using var approvedBase = RegistryKey.OpenBaseKey(approvedHive, view);
            using var approved = approvedBase.OpenSubKey(approvedPath);

            foreach (var name in run.GetValueNames())
            {
                string cmd = run.GetValue(name)?.ToString() ?? "";
                bool enabled = IsApprovedEnabled(approved, name);
                list.Add(new StartupEntry
                {
                    Name = name,
                    Command = cmd,
                    Publisher = PublisherOf(cmd),
                    Enabled = enabled,
                    Source = source,
                    ValueName = name,
                });
            }
        }
        catch { /* ключ недоступен */ }
    }

    private static void ReadFolder(string folder, StartupSource source, List<StartupEntry> list)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        try
        {
            foreach (var lnk in Directory.GetFiles(folder, "*.lnk"))
            {
                string name = Path.GetFileNameWithoutExtension(lnk);
                using var approvedBase = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
                using var approved = approvedBase.OpenSubKey(ApprovedFolder);
                bool enabled = IsApprovedEnabled(approved, Path.GetFileName(lnk));
                list.Add(new StartupEntry
                {
                    Name = name,
                    Command = lnk,
                    Publisher = "",
                    Enabled = enabled,
                    Source = source,
                    ValueName = Path.GetFileName(lnk),
                    LnkPath = lnk,
                });
            }
        }
        catch { }
    }

    private static bool IsApprovedEnabled(RegistryKey? approved, string valueName)
    {
        // нет записи в StartupApproved → запись включена по умолчанию
        if (approved?.GetValue(valueName) is byte[] b && b.Length > 0)
            return b[0] == 2;
        return true;
    }

    private static string PublisherOf(string command)
    {
        try
        {
            string path = ExtractExePath(command);
            if (File.Exists(path))
                return FileVersionInfo.GetVersionInfo(path).CompanyName ?? "";
        }
        catch { }
        return "";
    }

    private static string ExtractExePath(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            return end > 0 ? command[1..end] : command.Trim('"');
        }
        int space = command.IndexOf(' ');
        return space > 0 ? command[..space] : command;
    }

    // ---------- Действия ----------
    /// <summary>Включает/выключает запись через StartupApproved. true = успех.</summary>
    public static bool SetEnabled(StartupEntry e, bool enabled)
    {
        try
        {
            var (hive, view, approvedPath) = ApprovedTarget(e);
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var approved = baseKey.CreateSubKey(approvedPath, writable: true);
            if (approved is null) return false;
            approved.SetValue(e.ValueName, enabled ? EnabledBytes : DisabledBytes, RegistryValueKind.Binary);
            e.Enabled = enabled;
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Удаляет запись автозагрузки. Из реестра - только после резервной копии (без копии не удаляем),
    /// ярлык из папки «Автозагрузка» - в корзину. true = успех.
    /// </summary>
    public static bool Delete(StartupEntry e)
    {
        try
        {
            if (e.IsRegistry)
            {
                var (hive, view) = RunTarget(e);
                var (ahive, aview, apath) = ApprovedTarget(e);
                // сама команда + отметка вкл/выкл: после восстановления запись вернётся в том же состоянии
                RegistryFixService.BackupValues(new[]
                {
                    (hive, view, RunPath, e.ValueName),
                    (ahive, aview, apath, e.ValueName),
                }, "startup", 1);

                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var run = baseKey.OpenSubKey(RunPath, writable: true);
                run?.DeleteValue(e.ValueName, throwOnMissingValue: false);

                // подчистим запись в StartupApproved
                using var aBase = RegistryKey.OpenBaseKey(ahive, aview);
                using var approved = aBase.OpenSubKey(apath, writable: true);
                approved?.DeleteValue(e.ValueName, throwOnMissingValue: false);
            }
            else if (File.Exists(e.LnkPath))
            {
                return DuplicateService.DeleteToRecycle(e.LnkPath);   // ярлык можно вернуть из корзины
            }
            return true;
        }
        catch { return false; }
    }

    private static (RegistryHive, RegistryView) RunTarget(StartupEntry e) => e.Source switch
    {
        StartupSource.RunHKCU => (RegistryHive.CurrentUser, RegistryView.Default),
        StartupSource.RunHKLM => (RegistryHive.LocalMachine, RegistryView.Registry64),
        StartupSource.RunHKLM32 => (RegistryHive.LocalMachine, RegistryView.Registry32),
        _ => (RegistryHive.CurrentUser, RegistryView.Default),
    };

    private static (RegistryHive, RegistryView, string) ApprovedTarget(StartupEntry e) => e.Source switch
    {
        StartupSource.RunHKCU => (RegistryHive.CurrentUser, RegistryView.Default, ApprovedRun),
        StartupSource.RunHKLM => (RegistryHive.LocalMachine, RegistryView.Registry64, ApprovedRun),
        StartupSource.RunHKLM32 => (RegistryHive.LocalMachine, RegistryView.Registry32, ApprovedRun32),
        _ => (RegistryHive.CurrentUser, RegistryView.Default, ApprovedFolder),
    };
}
