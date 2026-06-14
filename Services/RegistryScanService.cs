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

        return issues;
    }

    // ---------- helpers ----------
    private static RegistryKey Base(RegistryHive h) => RegistryKey.OpenBaseKey(h, RegistryView.Registry64);

    private static bool FileMissing(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false; // пусто - не считаем проблемой
        string path = ExtractExePath(Environment.ExpandEnvironmentVariables(raw));
        if (string.IsNullOrWhiteSpace(path)) return false;
        return !File.Exists(path) && !Directory.Exists(path);
    }

    private static string ExtractExePath(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            return end > 0 ? command[1..end] : command.Trim('"');
        }
        // путь без кавычек: режем по первому пробелу, после которого идёт ключ/аргумент
        int space = command.IndexOf(' ');
        return space > 0 ? command[..space] : command;
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
                    ValueName = null,
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
            if (!File.Exists(Environment.ExpandEnvironmentVariables(exe)))
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
