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

    /// <summary>Включить: автозапуск + запустить.</summary>
    public static bool Enable(ServiceEntry e)
    {
        bool ok = RunSc($"config \"{e.Name}\" start= auto");
        RunSc($"start \"{e.Name}\"");
        return ok;
    }

    /// <summary>Выключить: остановить + запретить запуск.</summary>
    public static bool Disable(ServiceEntry e)
    {
        RunSc($"stop \"{e.Name}\"");
        return RunSc($"config \"{e.Name}\" start= disabled");
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
