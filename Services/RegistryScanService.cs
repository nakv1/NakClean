using System.IO;
using Microsoft.Win32;
using NakClean.Models;

namespace NakClean.Services;

/// <summary>
/// Сканер проблем реестра. Каждая категория ищет «мёртвые» записи -
/// ссылки на файлы/ключи, которых уже нет. Ничего не удаляет, только находит.
/// </summary>
public static class RegistryScanService
{
    public sealed record Category(string Id, string Name);

    public static readonly IReadOnlyList<Category> Categories = new[]
    {
        new Category("shareddll", "Отсутствующие общие DLL"),
        new Category("apppaths", "Пути приложений"),
        new Category("fileext", "Неиспользуемые расширения файлов"),
        new Category("uninstall", "Устаревшие приложения"),
        new Category("startup", "Автозагрузка"),
        new Category("fonts", "Шрифты"),
        new Category("sound", "Звуковые события"),
        new Category("mui", "Кэш MUI"),
        new Category("help", "Файлы справки"),
        new Category("appcompat", "Журнал совместимости"),
        new Category("firewall", "Правила брандмауэра"),
    };

    // локализованное имя категории (RU - из списка выше, EN - из Loc)
    private static string Cat(string id) =>
        Loc.I.IsEn ? Loc.I[$"rcat_{id}"] : Categories.First(c => c.Id == id).Name;

    public static List<RegistryIssue> Scan(ISet<string> enabledIds, CancellationToken ct = default)
    {
        var issues = new List<RegistryIssue>();
        void Run(string id, Action<List<RegistryIssue>> scan)
        {
            if (!enabledIds.Contains(id)) return;
            ct.ThrowIfCancellationRequested();
            try { scan(issues); } catch { /* категория недоступна */ }
        }

        Run("shareddll", ScanSharedDlls);
        Run("apppaths", ScanAppPaths);
        Run("fileext", ScanFileExtensions);
        Run("uninstall", ScanUninstall);
        Run("startup", ScanStartup);
        Run("fonts", ScanFonts);
        Run("sound", ScanSoundEvents);
        Run("mui", ScanMuiCache);
        Run("help", ScanHelpFiles);
        Run("appcompat", ScanAppCompat);
        Run("firewall", ScanFirewall);

        return issues;
    }

    // ---------- helpers ----------
    private static RegistryKey Base(RegistryHive h) => RegistryKey.OpenBaseKey(h, RegistryView.Registry64);

    /// <summary>
    /// true - только если УВЕРЕНЫ, что файла нет: путь полный (буква подключённого локального диска)
    /// и ни одно прочтение команды не ведёт к существующему файлу. Есть сомнения - запись не трогаем.
    /// </summary>
    private static bool FileMissing(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false; // пусто - не считаем проблемой
        string cmd = Environment.ExpandEnvironmentVariables(raw).Trim();
        if (cmd.Contains('%')) return false;              // переменная не раскрылась - не знаем, куда ведёт

        if (cmd.StartsWith('"'))
        {
            int end = cmd.IndexOf('"', 1);
            string quoted = end > 0 ? cmd[1..end] : cmd.Trim('"');
            return IsCheckable(quoted) && !FileOrDirExists(quoted);
        }

        // Без кавычек путь сам может содержать пробелы: «C:\Program Files\App\app.exe /S».
        // Как Windows при запуске: пробуем всё более длинные куски до пробела - хоть один файл есть → запись живая.
        if (FileOrDirExists(cmd)) return false;
        for (int i = cmd.IndexOf(' '); i > 0; i = cmd.IndexOf(' ', i + 1))
            if (FileExists(cmd[..i])) return false;

        int space = cmd.IndexOf(' ');
        return IsCheckable(space > 0 ? cmd[..space] : cmd);
    }

    private static bool FileExists(string p)
    {
        try { return File.Exists(p) || (!p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(p + ".exe")); }
        catch { return false; }
    }

    private static bool FileOrDirExists(string p)
    {
        try { return FileExists(p) || Directory.Exists(p); }
        catch { return false; }
    }

    /// <summary>Готовый полный путь (без аргументов) точно указывает на несуществующий файл.</summary>
    private static bool PathMissing(string path)
    {
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (path.Contains('%') || IsWindowsApps(path)) return false;
        return IsCheckable(path) && !FileOrDirExists(path);
    }

    // Program Files\WindowsApps закрыта даже для администратора: файл может быть на месте,
    // а Windows скажет «нет». Проверить не можем - значит, не трогаем.
    private static bool IsWindowsApps(string path)
        => path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);

    // Проверять можно только полный путь на подключённом локальном диске.
    // Короткое имя (rundll32.exe), сетевой путь, отключённая флешка - не можем быть уверены.
    private static bool IsCheckable(string path)
    {
        if (path.Length < 3 || path[1] != ':' || path[2] != '\\' || !char.IsLetter(path[0])) return false;
        try
        {
            var d = new DriveInfo(path[..1]);
            return d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable;
        }
        catch { return false; }
    }

    // ---------- сканеры ----------
    private static void ScanSharedDlls(List<RegistryIssue> issues)
    {
        const string path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs";
        using var k = Base(RegistryHive.LocalMachine).OpenSubKey(path);
        if (k is null) return;
        foreach (var file in k.GetValueNames())
        {
            if (string.IsNullOrEmpty(file)) continue;
            if (!File.Exists(Environment.ExpandEnvironmentVariables(file)))
                issues.Add(new RegistryIssue
                {
                    Category = Cat("shareddll"),
                    Problem = Loc.I["rp_dll"],
                    Target = file,
                    Hive = RegistryHive.LocalMachine,
                    SubKey = path,
                    ValueName = file,
                });
        }
    }

    private static void ScanAppPaths(List<RegistryIssue> issues)
    {
        foreach (var path in new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths",
        })
        {
            using var root = Base(RegistryHive.LocalMachine).OpenSubKey(path);
            if (root is null) continue;
            foreach (var sub in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(sub);
                var def = k?.GetValue(null) as string;
                if (FileMissing(def))
                    issues.Add(new RegistryIssue
                    {
                        Category = Cat("apppaths"),
                        Problem = Loc.I["rp_prog"],
                        Target = $"{sub} → {def}",
                        Hive = RegistryHive.LocalMachine,
                        SubKey = $"{path}\\{sub}",
                        ValueName = null,
                    });
            }
        }
    }

    private static void ScanFileExtensions(List<RegistryIssue> issues)
    {
        using var root = Base(RegistryHive.ClassesRoot);
        foreach (var ext in root.GetSubKeyNames())
        {
            if (!ext.StartsWith('.')) continue;
            using var k = root.OpenSubKey(ext);
            var progId = k?.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(progId)) continue;
            using var pk = root.OpenSubKey(progId);
            if (pk is null)
                issues.Add(new RegistryIssue
                {
                    Category = Cat("fileext"),
                    Problem = string.Format(Loc.I["rp_handler"], progId),
                    Target = ext,
                    Hive = RegistryHive.ClassesRoot,
                    SubKey = ext,
                    // только «битая» ссылка на тип (значение по умолчанию); остальное в ключе
                    // (OpenWithProgids, Content Type, ShellNew...) рабочее - его не трогаем
                    ValueName = "",
                });
        }
    }

    private static void ScanUninstall(List<RegistryIssue> issues)
    {
        var roots = new (RegistryHive hive, string path)[]
        {
            (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        };
        foreach (var (hive, path) in roots)
        {
            using var root = Base(hive).OpenSubKey(path);
            if (root is null) continue;
            foreach (var sub in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(sub);
                var name = k?.GetValue("DisplayName") as string;
                if (string.IsNullOrWhiteSpace(name)) continue;
                var us = k?.GetValue("UninstallString") as string;
                if (string.IsNullOrWhiteSpace(us)) continue;
                if (us.Contains("msiexec", StringComparison.OrdinalIgnoreCase)) continue; // MSI - не проверяем
                if (FileMissing(us))
                    issues.Add(new RegistryIssue
                    {
                        Category = Cat("uninstall"),
                        Problem = string.Format(Loc.I["rp_uninst"], name),
                        Target = us,
                        Hive = hive,
                        SubKey = $"{path}\\{sub}",
                        ValueName = null,
                    });
            }
        }
    }

    private static void ScanStartup(List<RegistryIssue> issues)
    {
        var roots = new (RegistryHive hive, string path)[]
        {
            (RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
            (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"),
        };
        foreach (var (hive, path) in roots)
        {
            using var k = Base(hive).OpenSubKey(path);
            if (k is null) continue;
            foreach (var name in k.GetValueNames())
            {
                var cmd = k.GetValue(name) as string;
                if (FileMissing(cmd))
                    issues.Add(new RegistryIssue
                    {
                        Category = Cat("startup"),
                        Problem = Loc.I["rp_startup"],
                        Target = $"{name} → {cmd}",
                        Hive = hive,
                        SubKey = path,
                        ValueName = name,
                    });
            }
        }
    }

    private static void ScanFonts(List<RegistryIssue> issues)
    {
        const string path = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";
        string fontsDir = Path.Combine(Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows", "Fonts");
        using var k = Base(RegistryHive.LocalMachine).OpenSubKey(path);
        if (k is null) return;
        foreach (var name in k.GetValueNames())
        {
            var data = k.GetValue(name) as string;
            if (string.IsNullOrWhiteSpace(data)) continue;
            string file = Path.IsPathRooted(data) ? data : Path.Combine(fontsDir, data);
            if (!File.Exists(Environment.ExpandEnvironmentVariables(file)))
                issues.Add(new RegistryIssue
                {
                    Category = Cat("fonts"),
                    Problem = Loc.I["rp_font"],
                    Target = $"{name} → {data}",
                    Hive = RegistryHive.LocalMachine,
                    SubKey = path,
                    ValueName = name,
                });
        }
    }

    private static void ScanSoundEvents(List<RegistryIssue> issues)
    {
        const string appsPath = @"AppEvents\Schemes\Apps";
        using var apps = Base(RegistryHive.CurrentUser).OpenSubKey(appsPath);
        if (apps is null) return;
        foreach (var app in apps.GetSubKeyNames())
        {
            using var appKey = apps.OpenSubKey(app);
            if (appKey is null) continue;
            foreach (var evt in appKey.GetSubKeyNames())
            {
                string curPath = $"{appsPath}\\{app}\\{evt}\\.Current";
                using var cur = Base(RegistryHive.CurrentUser).OpenSubKey(curPath);
                var wav = cur?.GetValue(null) as string;
                if (FileMissing(wav))
                    issues.Add(new RegistryIssue
                    {
                        Category = Cat("sound"),
                        Problem = Loc.I["rp_sound"],
                        Target = $"{app}\\{evt} → {wav}",
                        Hive = RegistryHive.CurrentUser,
                        SubKey = curPath,
                        ValueName = "",
                    });
            }
        }
    }

    private static void ScanMuiCache(List<RegistryIssue> issues)
    {
        const string path = @"SOFTWARE\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache";
        using var k = Base(RegistryHive.CurrentUser).OpenSubKey(path);
        if (k is null) return;
        foreach (var name in k.GetValueNames())
        {
            int idx = name.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (idx <= 0) continue;
            string exe = name[..(idx + 4)];
            if (PathMissing(exe))
                issues.Add(new RegistryIssue
                {
                    Category = Cat("mui"),
                    Problem = Loc.I["rp_prog"],
                    Target = exe,
                    Hive = RegistryHive.CurrentUser,
                    SubKey = path,
                    ValueName = name,
                });
        }
    }

    // Журнал совместимости: Windows записывает каждую запущенную программу (и выбранный для неё
    // режим совместимости). Программы больше нет - запись просто история, удалять безопасно.
    private static void ScanAppCompat(List<RegistryIssue> issues)
    {
        const string flags = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags";
        var places = new (RegistryHive hive, string path)[]
        {
            (RegistryHive.CurrentUser, flags + @"\Compatibility Assistant\Store"),
            (RegistryHive.CurrentUser, flags + @"\Layers"),
            (RegistryHive.LocalMachine, flags + @"\Layers"),
        };
        foreach (var (hive, path) in places)
        {
            using var k = Base(hive).OpenSubKey(path);
            if (k is null) continue;
            foreach (var name in k.GetValueNames())
            {
                if (!PathMissing(name)) continue;   // имя значения = полный путь к программе
                issues.Add(new RegistryIssue
                {
                    Category = Cat("appcompat"),
                    Problem = Loc.I["rp_compat"],
                    Target = name,
                    Hive = hive,
                    SubKey = path,
                    ValueName = name,
                });
            }
        }
    }

    // Правила брандмауэра для программ, которых больше нет (старые версии, удалённые проекты).
    // Удаляем штатно через netsh - служба брандмауэра сразу забывает правило (правка реестра
    // подействовала бы только после перезагрузки). Копия ключа с правилами - как для всего остального.
    private static void ScanFirewall(List<RegistryIssue> issues)
    {
        const string path = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules";
        using var k = Base(RegistryHive.LocalMachine).OpenSubKey(path);
        if (k is null) return;
        foreach (var id in k.GetValueNames())
        {
            if (k.GetValue(id) is not string data) continue;
            string? app = null, name = null;
            foreach (var part in data.Split('|'))
            {
                if (part.StartsWith("App=", StringComparison.OrdinalIgnoreCase)) app = part[4..];
                else if (part.StartsWith("Name=", StringComparison.OrdinalIgnoreCase)) name = part[5..];
            }
            // имя-ссылка на ресурс (@...) или с кавычками netsh не примет - такие не трогаем
            if (app is null || string.IsNullOrWhiteSpace(name) || name.StartsWith('@') || name.Contains('"') || app.Contains('"'))
                continue;
            if (!PathMissing(app)) continue;

            string ruleName = name, ruleApp = app;
            issues.Add(new RegistryIssue
            {
                Category = Cat("firewall"),
                Problem = Loc.I["rp_firewall"],
                Target = $"{ruleName} → {ruleApp}",
                Hive = RegistryHive.LocalMachine,
                SubKey = path,
                ValueName = id,
                // итог проверяем по факту: правила больше нет в реестре брандмауэра
                // (одинаковые правила netsh удаляет разом - второе тогда уже считается удалённым)
                CustomDelete = () =>
                {
                    RunNetsh($"advfirewall firewall delete rule name=\"{ruleName}\" program=\"{ruleApp}\"");
                    return !ValueExists(path, id);
                },
            });
        }
    }

    private static bool ValueExists(string subKey, string valueName)
    {
        try
        {
            using var k = Base(RegistryHive.LocalMachine).OpenSubKey(subKey);
            return k?.GetValue(valueName) != null;
        }
        catch { return true; }
    }

    private static void RunNetsh(string args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("netsh.exe", args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return;
            _ = p.StandardError.ReadToEndAsync();   // читаем оба потока, чтобы netsh не встал на полном буфере
            p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
        }
        catch { }
    }

    private static void ScanHelpFiles(List<RegistryIssue> issues)
    {
        const string path = @"SOFTWARE\Microsoft\Windows\Help";
        using var k = Base(RegistryHive.LocalMachine).OpenSubKey(path);
        if (k is null) return;
        foreach (var name in k.GetValueNames())
        {
            var data = k.GetValue(name) as string;
            if (FileMissing(data))
                issues.Add(new RegistryIssue
                {
                    Category = Cat("help"),
                    Problem = Loc.I["rp_help"],
                    Target = $"{name} → {data}",
                    Hive = RegistryHive.LocalMachine,
                    SubKey = path,
                    ValueName = name,
                });
        }
    }
}
