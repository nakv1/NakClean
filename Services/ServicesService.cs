using System.Diagnostics;
using System.Management;

namespace NakClean.Services;

public sealed class ServiceEntry
{
    public required string Name { get; init; }        // системное имя службы
    public required string DisplayName { get; init; }
    public string State { get; set; } = "";           // Running / Stopped
    public string StartMode { get; set; } = "";        // Auto / Manual / Disabled
    public string Path { get; init; } = "";
}

/// <summary>
/// Службы Windows: список через WMI, управление через sc.exe (нужен админ).
/// ВНИМАНИЕ: отключение системных служб может навредить - поэтому в UI стоит
/// подтверждение, а сама программа ничего не выключает без явного действия.
/// </summary>
public static class ServicesService
{
    public static List<ServiceEntry> GetServices()
    {
        var list = new List<ServiceEntry>();
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT Name, DisplayName, State, StartMode, PathName FROM Win32_Service");
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                {
                    list.Add(new ServiceEntry
                    {
                        Name = o["Name"]?.ToString() ?? "",
                        DisplayName = o["DisplayName"]?.ToString() ?? o["Name"]?.ToString() ?? "",
                        State = o["State"]?.ToString() ?? "",
                        StartMode = o["StartMode"]?.ToString() ?? "",
                        Path = o["PathName"]?.ToString() ?? "",
                    });
                }
            }
        }
        catch { }
        return list.OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Перечитывает реальное состояние и тип запуска службы из системы.</summary>
    public static (string state, string startMode)? QueryState(string name)
    {
        try
        {
            string safe = name.Replace("'", "''");
            using var s = new ManagementObjectSearcher(
                $"SELECT State, StartMode FROM Win32_Service WHERE Name='{safe}'");
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                    return (o["State"]?.ToString() ?? "", o["StartMode"]?.ToString() ?? "");
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Включить. Служба не отключена - только запускаем, её тип запуска не трогаем.
    /// Отключена - возвращаем тип, который был до отключения через NakClean (не знаем - «автоматически»), и запускаем.
    /// </summary>
    public static bool Enable(ServiceEntry e)
    {
        bool ok = true;
        if (string.Equals(QueryState(e.Name)?.startMode, "Disabled", StringComparison.OrdinalIgnoreCase))
        {
            var mem = LoadMemory();
            string mode = mem.TryGetValue(e.Name, out var m) ? m : "auto";
            ok = RunSc($"config \"{e.Name}\" start= {mode}");
            if (ok && mem.Remove(e.Name)) SaveMemory(mem);
        }
        RunSc($"start \"{e.Name}\"");
        return ok;
    }

    /// <summary>Выключить: запомнить прежний тип запуска, остановить, запретить запуск.</summary>
    public static bool Disable(ServiceEntry e)
    {
        string? before = CurrentMode(e.Name);
        if (before != null && before != "disabled")
        {
            var mem = LoadMemory();
            mem[e.Name] = before;
            SaveMemory(mem);
        }
        RunSc($"stop \"{e.Name}\"");
        return RunSc($"config \"{e.Name}\" start= disabled");
    }

    /// <summary>Тип запуска в словах sc.exe: auto / delayed-auto / demand / disabled. null - не прочитали.</summary>
    private static string? CurrentMode(string name)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
            if (k?.GetValue("Start") is not int start) return null;
            bool delayed = k.GetValue("DelayedAutostart") is int d && d == 1;
            return start switch
            {
                2 => delayed ? "delayed-auto" : "auto",
                3 => "demand",
                4 => "disabled",
                _ => null,   // загрузочные/системные драйверы - не наш случай
            };
        }
        catch { return null; }
    }

    // ---------- память «как было до отключения» (%AppData%\NakClean\services.json) ----------
    private static string MemoryFile => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NakClean", "services.json");

    private static Dictionary<string, string> LoadMemory()
    {
        try
        {
            if (System.IO.File.Exists(MemoryFile)
                && System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(System.IO.File.ReadAllText(MemoryFile)) is { } d)
                return new(d, StringComparer.OrdinalIgnoreCase);   // имена служб в Windows без учёта регистра
        }
        catch { }
        return new(StringComparer.OrdinalIgnoreCase);
    }

    private static void SaveMemory(Dictionary<string, string> mem)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(MemoryFile)!);
            System.IO.File.WriteAllText(MemoryFile, System.Text.Json.JsonSerializer.Serialize(mem));
        }
        catch { }
    }

    private static bool RunSc(string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(10000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}
