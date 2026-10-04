using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using NakClean.Models;

namespace NakClean.Services;

/// <summary>
/// Список установленных программ из ключей Uninstall реестра
/// (HKLM 64-бит, HKLM 32-бит WOW6432Node, HKCU). Деинсталляция, восстановление,
/// переименование и удаление записи.
/// </summary>
public static class InstalledAppsService
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public static List<InstalledApp> GetApps()
    {
        var byName = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);

        Read(RegistryHive.LocalMachine, RegistryView.Registry64, byName);
        Read(RegistryHive.LocalMachine, RegistryView.Registry32, byName);
        Read(RegistryHive.CurrentUser, RegistryView.Default, byName);

        var result = byName.Values.ToList();
        result.AddRange(ReadUwp());

        return result
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>UWP/Store-приложения через Get-AppxPackage.</summary>
    private static List<InstalledApp> ReadUwp()
    {
        var list = new List<InstalledApp>();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -Command \"Get-AppxPackage | Where-Object {!$_.IsFramework -and !$_.NonRemovable} | Select-Object Name,Publisher,Version,PackageFullName | ConvertTo-Json -Compress\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p is null) return list;
            string json = p.StandardOutput.ReadToEnd();
            p.WaitForExit(20000);
            if (string.IsNullOrWhiteSpace(json)) return list;

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            var items = new List<System.Text.Json.JsonElement>();
            if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var x in root.EnumerateArray()) items.Add(x);
            else if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
                items.Add(root);

            foreach (var e in items)
            {
                string name = Str(e, "Name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                list.Add(new InstalledApp
                {
                    Name = name,
                    Publisher = CleanPublisher(Str(e, "Publisher")),
                    Version = Str(e, "Version"),
                    IsUwp = true,
                    PackageFullName = Str(e, "PackageFullName"),
                });
            }
        }
        catch { }
        return list;

        static string Str(System.Text.Json.JsonElement e, string p)
            => e.TryGetProperty(p, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : "";
    }

    private static string CleanPublisher(string cn)
    {
        // "CN=Microsoft Corporation, O=..., L=..." → "Microsoft Corporation"
        if (string.IsNullOrEmpty(cn)) return "";
        int i = cn.IndexOf("CN=", StringComparison.OrdinalIgnoreCase);
        string s = i >= 0 ? cn[(i + 3)..] : cn;
        int comma = s.IndexOf(',');
        return (comma > 0 ? s[..comma] : s).Trim();
    }

    private static void Read(RegistryHive hive, RegistryView view, Dictionary<string, InstalledApp> byName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var root = baseKey.OpenSubKey(UninstallPath);
            if (root is null) return;

            foreach (var sub in root.GetSubKeyNames())
            {
                try
                {
                    using var k = root.OpenSubKey(sub);
                    if (k is null) continue;

                    string? name = k.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    // прячем только настоящие системные компоненты
                    if (ToInt(k.GetValue("SystemComponent")) == 1) continue;

                    long sizeKb = ToLong(k.GetValue("EstimatedSize"));
                    var date = ParseDate(k.GetValue("InstallDate") as string) ?? KeyWriteTime(k);
                    var app = new InstalledApp
                    {
                        Name = name.Trim(),
                        Publisher = k.GetValue("Publisher") as string ?? "",
                        Version = k.GetValue("DisplayVersion") as string ?? "",
                        InstallDate = date,
                        SizeBytes = sizeKb * 1024,
                        UninstallString = k.GetValue("UninstallString") as string ?? "",
                        QuietUninstallString = k.GetValue("QuietUninstallString") as string ?? "",
                        ModifyPath = k.GetValue("ModifyPath") as string ?? "",
                        Hive = hive,
                        View = view,
                        SubKey = $"{UninstallPath}\\{sub}",
                    };
                    byName[app.Name] = app; // дедуп по имени
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>Запускает штатный деинсталлятор программы. true = удалось запустить.</summary>
    public static bool LaunchUninstall(InstalledApp app)
    {
        if (app.IsUwp)
        {
            if (string.IsNullOrWhiteSpace(app.PackageFullName)) return false;
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -Command \"Remove-AppxPackage -Package '{app.PackageFullName}'\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                return true;
            }
            catch { return false; }
        }

        string cmd = !string.IsNullOrWhiteSpace(app.UninstallString)
            ? app.UninstallString
            : app.QuietUninstallString;
        return RunCommand(cmd);
    }

    /// <summary>Восстановление: MSI-починка или ModifyPath.</summary>
    public static bool Repair(InstalledApp app)
    {
        if (app.MsiProductCode is { } code)
            return RunCommand($"msiexec /f {code}");
        if (!string.IsNullOrWhiteSpace(app.ModifyPath))
            return RunCommand(app.ModifyPath);
        return false;
    }

    /// <summary>Переименовать запись (меняет DisplayName в реестре).</summary>
    public static bool Rename(InstalledApp app, string newName)
    {
        if (app.IsUwp || string.IsNullOrEmpty(app.SubKey)) return false;
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(app.Hive, app.View);
            using var k = baseKey.OpenSubKey(app.SubKey, writable: true);
            if (k is null) return false;
            k.SetValue("DisplayName", newName);
            app.Name = newName;
            return true;
        }
        catch { return false; }
    }

    /// <summary>Удаляет запись из списка (ключ Uninstall) - только после резервной копии. Программа НЕ удаляется.</summary>
    public static bool RemoveEntry(InstalledApp app)
    {
        if (app.IsUwp || string.IsNullOrEmpty(app.SubKey)) return false;
        try
        {
            RegistryFixService.Backup(new[]
            {
                new RegistryIssue
                {
                    Category = "uninstall", Problem = "", Target = app.Name,
                    Hive = app.Hive,
                    SubKey = RegistryFixService.RealSubKey(app.Hive, app.View, app.SubKey),
                },
            }, "uninstall");   // копия не сохранилась - исключение, запись не удаляем

            using var baseKey = RegistryKey.OpenBaseKey(app.Hive, app.View);
            int slash = app.SubKey.LastIndexOf('\\');
            string parent = slash > 0 ? app.SubKey[..slash] : "";
            string leaf = slash > 0 ? app.SubKey[(slash + 1)..] : app.SubKey;
            using var pk = baseKey.OpenSubKey(parent, writable: true);
            pk?.DeleteSubKeyTree(leaf, throwOnMissingSubKey: false);
            return true;
        }
        catch { return false; }
    }

    private static bool RunCommand(string cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return false;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c " + cmd,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            return true;
        }
        catch { return false; }
    }

    private static DateTime? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateTime.TryParseExact(s, "yyyyMMdd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var d) ? d : null;
    }

    [DllImport("advapi32.dll")]
    private static extern int RegQueryInfoKey(SafeRegistryHandle hKey,
        IntPtr a, IntPtr b, IntPtr c, IntPtr d, IntPtr e, IntPtr f,
        IntPtr g, IntPtr h, IntPtr i, IntPtr j, out long lastWriteTime);

    /// <summary>Время последней записи ключа - запасная «дата установки».</summary>
    private static DateTime? KeyWriteTime(RegistryKey k)
    {
        try
        {
            if (RegQueryInfoKey(k.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out long ft) == 0 && ft > 0)
                return DateTime.FromFileTimeUtc(ft).ToLocalTime();
        }
        catch { }
        return null;
    }

    private static int ToInt(object? v) { try { return v is null ? 0 : Convert.ToInt32(v); } catch { return 0; } }
    private static long ToLong(object? v) { try { return v is null ? 0 : Convert.ToInt64(v); } catch { return 0; } }
}
