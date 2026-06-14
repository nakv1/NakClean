using Microsoft.Win32;

namespace NakClean.Models;

/// <summary>Одна найденная проблема в реестре.</summary>
public sealed class RegistryIssue
{
    public required string Category { get; init; }
    public required string Problem { get; init; }   // что не так (для человека)
    public required string Target { get; init; }    // на что указывает запись

    public RegistryHive Hive { get; init; }
    public required string SubKey { get; init; }     // путь под кустом (вид Registry64)

    /// <summary>Имя значения для удаления. null = удалить подключ целиком.</summary>
    public string? ValueName { get; init; }

    /// <summary>Короткий префикс куста для reg.exe (HKLM/HKCU/HKCR…).</summary>
    public string HiveShort => Hive switch
    {
        RegistryHive.LocalMachine => "HKLM",
        RegistryHive.CurrentUser => "HKCU",
        RegistryHive.ClassesRoot => "HKCR",
        RegistryHive.Users => "HKU",
        RegistryHive.CurrentConfig => "HKCC",
        _ => "HKLM",
    };

    public string FullPath => $"{HiveShort}\\{SubKey}";
}
