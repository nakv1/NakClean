using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using NakClean.Models;

namespace NakClean.Services;

public enum LeftoverKind { Folder, Shortcut, Registry, Startup, Task }

/// <summary>Один найденный остаток удалённой программы.</summary>
public sealed class Leftover
{
    public required LeftoverKind Kind { get; init; }
    public required string Display { get; init; }      // что показать человеку (путь / ключ / имя)
    public long Size { get; init; }                     // для папок и ярлыков
    public string? Path { get; init; }                  // папка или файл
    public RegistryIssue? Reg { get; init; }
    public StartupEntry? Startup { get; init; }
    public string? TaskPath { get; init; }
}

/// <summary>
/// Что осталось от программы после её деинсталлятора. Ищем ТОЛЬКО по тому, что точно известно об этой
/// программе (снимок до удаления): папка установки, имя, издатель. Совпадения строгие - имя папки/ключа
/// равно имени программы, а не «содержит». Чужое не трогаем: системные папки защищены, папки и ключи,
/// которые совпадают с ещё установленными программами, пропускаем. Удаление - в корзину / с резервной копией.
/// </summary>
public static class LeftoverService
{
    /// <summary>Снимок программы ДО удаления: потом её записи в реестре уже не будет.</summary>
    public sealed record AppPrint(string Name, string Publisher, string? InstallDir);

    public static AppPrint Print(InstalledApp app) =>
        new(app.Name, app.Publisher, GuessInstallDir(app));

    // общие слова: папка или ключ с таким именем может принадлежать кому угодно
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft", "windows", "intel", "nvidia", "amd", "google", "apple", "adobe", "common files", "packages",
        "programs", "program files", "temp", "tmp", "update", "updater", "driver", "drivers", "tools", "tool",
        "app", "apps", "application", "applications", "data", "cache", "logs", "log", "software", "system",
        "launcher", "setup", "installer", "service", "services", "client", "helper", "plugins", "settings",
        "default", "user", "users", "public", "shared", "config", "files", "documents", "downloads", "games",
        "classes", "policies", "wow6432node", "clients", "realtek", "oracle", "java", "python", "mozilla",
    };

    private static bool Usable(string s) => s.Length >= 3 && !Generic.Contains(s);

    /// <summary>Варианты имени: «7-Zip 23.01 (x64)» → «7-Zip»; «Mozilla Firefox» при издателе Mozilla → ещё «Firefox».</summary>
    internal static List<string> NameVariants(string name, string publisher)
    {
        var list = new List<string>();
        string n = Regex.Replace(name, @"\s*\([^)]*\)", "");                                  // (x64), (64-bit), (ru)
        n = Regex.Replace(n, @"\s+(v|version\s*)?\d+([.,]\d+)+.*$", "", RegexOptions.IgnoreCase); // 23.01, v1.2.3 и всё после
        n = Regex.Replace(n, @"\s+(x64|x86|64-bit|32-bit|64 bit|32 bit)\b.*$", "", RegexOptions.IgnoreCase);
        n = Regex.Replace(n, @"\s+", " ").Trim(' ', '-', '_', '.');
        void Add(string s) { s = s.Trim(); if (Usable(s) && !list.Contains(s, StringComparer.OrdinalIgnoreCase)) list.Add(s); }
        Add(n);
        Add(n.Replace(" ", ""));
        string pub = CleanPublisher(publisher);
        if (pub.Length > 0 && n.StartsWith(pub + " ", StringComparison.OrdinalIgnoreCase)) Add(n[(pub.Length + 1)..]);
        return list;
    }

    internal static string CleanPublisher(string p)
    {
        p = Regex.Replace(p ?? "", @"\s+", " ").Trim();   // в реестре встречается «Microsoft» с неразрывным пробелом
        if (p.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) || p.Contains("Майкрософт", StringComparison.OrdinalIgnoreCase)) return "";
        p = Regex.Replace(p, @"[,\s]+(inc\.?|llc|ltd\.?|limited|corporation|corp\.?|gmbh|co\.?|s\.r\.o\.?|ag|ab|sa|bv|oy|pty|software)\b\.?", "",
            RegexOptions.IgnoreCase).Trim(' ', ',', '.');
        return Usable(p) ? p : "";
    }

    /// <summary>Папка установки: InstallLocation, иначе папка значка или деинсталлятора (если это не системная папка).</summary>
    private static string? GuessInstallDir(InstalledApp app)
    {
        static string? Dir(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = Environment.ExpandEnvironmentVariables(s.Trim().Trim('"'));
            int comma = s.LastIndexOf(','); if (comma > 2 && !s[comma..].Contains('\\')) s = s[..comma];   // «app.exe,0»
            s = s.Trim('"');
            // только полный путь: короткое имя («MsiExec.exe») превратилось бы в ТЕКУЩУЮ папку программы
            if (!System.IO.Path.IsPathFullyQualified(s)) return null;
            try
            {
                string full = System.IO.Path.GetFullPath(s);
                return Directory.Exists(full) ? full : System.IO.Path.GetDirectoryName(full);
            }
            catch { return null; }
        }
        string? dir = Dir(app.InstallLocation);
        if (dir is null || IsSystemDir(dir)) dir = Dir(app.DisplayIcon);
        if (dir is null || IsSystemDir(dir)) dir = Dir(InstalledAppsService.SplitCommand(Environment.ExpandEnvironmentVariables(app.UninstallString)).File);
        return dir is null || IsSystemDir(dir) || RecycleBin.IsProtected(dir) ? null : dir.TrimEnd('\\');
    }

    // папки, которые не могут быть «папкой программы»: Windows, корни Program Files / ProgramData / профиля и т.п.
    private static bool IsSystemDir(string dir)
    {
        string d = dir.TrimEnd('\\');
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (d.StartsWith(win, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var root in Roots().Append(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)))
            if (d.Equals(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return true;
        string self = AppContext.BaseDirectory.TrimEnd('\\');
        if (self.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase) || self.Equals(d, StringComparison.OrdinalIgnoreCase)) return true;   // папку с самой NakClean - никогда
        return d.Contains(@"\Installer\", StringComparison.OrdinalIgnoreCase) || d.Contains(@"\Installer2\", StringComparison.OrdinalIgnoreCase)
            || d.Contains(@"\Package Cache\", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> Roots()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            local,
            System.IO.Path.Combine(local, "Programs"),
            System.IO.Path.Combine(System.IO.Path.GetDirectoryName(local) ?? local, "LocalLow"),
        }.Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public static List<Leftover> Find(AppPrint app)
    {
        var found = new List<Leftover>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = NameVariants(app.Name, app.Publisher);
        string pub = CleanPublisher(app.Publisher);

        // что принадлежит ещё установленным программам - не трогаем
        var others = InstalledAppsService.GetRegistryApps().Where(a => !a.Name.Equals(app.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        var otherDirs = others.Select(GuessInstallDir).Where(d => d != null).Select(d => d!).ToList();
        var otherNames = new HashSet<string>(others.SelectMany(a => NameVariants(a.Name, a.Publisher)), StringComparer.OrdinalIgnoreCase);
        names.RemoveAll(otherNames.Contains);   // имя совпадает с другой программой - по имени не ищем вовсе

        bool BelongsToOther(string path) => otherDirs.Any(o =>
            o.Equals(path, StringComparison.OrdinalIgnoreCase) ||
            o.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(o + "\\", StringComparison.OrdinalIgnoreCase));

        void AddFolder(string path, LeftoverKind kind = LeftoverKind.Folder)
        {
            try
            {
                path = System.IO.Path.GetFullPath(path).TrimEnd('\\');
                if (!Directory.Exists(path) || !seenPaths.Add(path)) return;
                if (RecycleBin.IsProtected(path) || IsSystemDir(path) || BelongsToOther(path)) return;
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return;   // ссылка на другое место
                var (bytes, _) = FileOps.Measure(new[] { path }, "*", true);
                found.Add(new Leftover { Kind = kind, Display = path, Path = path, Size = bytes });
            }
            catch { }
        }

        // 1. папка установки
        if (app.InstallDir != null) AddFolder(app.InstallDir);

        // 2. папки с точным именем программы (или «Издатель\Программа») в типичных местах
        foreach (var root in Roots())
            foreach (var n in names)
            {
                AddFolder(System.IO.Path.Combine(root, n));
                if (pub.Length > 0) AddFolder(System.IO.Path.Combine(root, pub, n));
            }

        // 3. меню «Пуск»: папка программы + ярлыки, ведущие в удалённую папку
        var startMenus = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        };
        foreach (var sm in startMenus.Where(Directory.Exists))
            foreach (var n in names) AddFolder(System.IO.Path.Combine(sm, n), LeftoverKind.Shortcut);
        if (app.InstallDir != null)
        {
            var lnkDirs = startMenus.Concat(new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            }).Where(Directory.Exists);
            foreach (var d in lnkDirs)
            {
                IEnumerable<string> lnks;
                try { lnks = Directory.EnumerateFiles(d, "*.lnk", SearchOption.TopDirectoryOnly).ToList(); } catch { continue; }
                foreach (var lnk in lnks)
                {
                    string? target = ShortcutTarget(lnk);
                    if (target is null || !Inside(target, app.InstallDir) || File.Exists(target) || !seenPaths.Add(lnk)) continue;
                    long size = 0; try { size = new FileInfo(lnk).Length; } catch { }
                    found.Add(new Leftover { Kind = LeftoverKind.Shortcut, Display = lnk, Path = lnk, Size = size });
                }
            }
        }

        // 4. ключи реестра Software\Программа и Software\Издатель\Программа
        var regRoots = new (RegistryHive Hive, string Sub)[]
        {
            (RegistryHive.CurrentUser, "Software"),
            (RegistryHive.LocalMachine, "SOFTWARE"),
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node"),
        };
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, sub) in regRoots)
            foreach (var n in names)
            {
                AddKey(hive, $@"{sub}\{n}");
                if (pub.Length > 0) AddKey(hive, $@"{sub}\{pub}\{n}");
            }

        void AddKey(RegistryHive hive, string subKey)
        {
            try
            {
                using var bk = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var k = bk.OpenSubKey(subKey);
                if (k is null) return;
                var issue = new RegistryIssue { Category = "leftover", Problem = "", Target = app.Name, Hive = hive, SubKey = subKey };
                if (!seenKeys.Add(issue.FullPath)) return;
                found.Add(new Leftover { Kind = LeftoverKind.Registry, Display = issue.FullPath, Reg = issue });
            }
            catch { }
        }

        // 5. автозагрузка и задачи планировщика, которые запускают файл из удалённой папки (файла уже нет)
        if (app.InstallDir != null)
        {
            try
            {
                foreach (var e in StartupService.GetEntries())
                {
                    string exe = e.IsRegistry ? ExeOf(e.Command) : (ShortcutTarget(e.LnkPath) ?? "");
                    if (exe.Length > 0 && Inside(exe, app.InstallDir) && !File.Exists(exe))
                        found.Add(new Leftover { Kind = LeftoverKind.Startup, Display = $"{e.Name}  ({e.LocationLabel})", Startup = e });
                }
            }
            catch { }
            try
            {
                foreach (var t in ScheduledTaskService.GetTasks())
                {
                    string exe = ExeOf(t.Command);
                    if (exe.Length > 0 && Inside(exe, app.InstallDir) && !File.Exists(exe))
                        found.Add(new Leftover { Kind = LeftoverKind.Task, Display = t.Path, TaskPath = t.Path });
                }
            }
            catch { }
        }
        return found;
    }

    private static bool Inside(string path, string dir)
    {
        try
        {
            string p = System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
            return p.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string ExeOf(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return "";
        string c = Environment.ExpandEnvironmentVariables(command.Trim());
        if (c.StartsWith('"')) { int end = c.IndexOf('"', 1); return end > 0 ? c[1..end] : c.Trim('"'); }
        int exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? c[..(exe + 4)] : c.Split(' ')[0];
    }

    /// <summary>Куда ведёт ярлык (.lnk) - через стандартный объект Windows WScript.Shell.</summary>
    private static string? ShortcutTarget(string lnk)
    {
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t is null) return null;
            dynamic shell = Activator.CreateInstance(t)!;
            try
            {
                dynamic sc = shell.CreateShortcut(lnk);
                string target = sc.TargetPath;
                return string.IsNullOrWhiteSpace(target) ? null : target;
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        }
        catch { return null; }
    }

    /// <summary>Удаляет выбранное: папки и ярлыки - в корзину, ключи реестра - после резервной копии.</summary>
    public static (int ok, int fail) Remove(IReadOnlyList<Leftover> items, IntPtr owner)
    {
        int ok = 0, fail = 0;

        // реестр: сначала копия ВСЕХ ключей; удаляем только то, что реально сохранилось
        var regs = items.Where(i => i.Reg != null).Select(i => i.Reg!).ToList();
        if (regs.Count > 0)
        {
            try
            {
                var (_, saved) = RegistryFixService.Backup(regs, "leftover");
                var (d, f) = RegistryFixService.Delete(saved);
                ok += d; fail += f + (regs.Count - saved.Count);
            }
            catch { fail += regs.Count; }
        }

        foreach (var i in items.Where(i => i.Reg is null))
        {
            bool done = i.Kind switch
            {
                LeftoverKind.Folder or LeftoverKind.Shortcut when i.Path != null => RecycleBin.Move(i.Path, owner) == RecycleBin.Result.Moved,
                LeftoverKind.Startup when i.Startup != null => StartupService.Delete(i.Startup),
                LeftoverKind.Task when i.TaskPath != null => ScheduledTaskService.Delete(i.TaskPath),
                _ => false,
            };
            if (done) ok++; else fail++;
        }
        return (ok, fail);
    }
}
