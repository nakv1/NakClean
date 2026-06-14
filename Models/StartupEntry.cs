namespace NakClean.Models;

public enum StartupSource { RunHKCU, RunHKLM, RunHKLM32, FolderUser, FolderCommon }

/// <summary>Запись автозагрузки (реестр Run или папка «Автозагрузка»).</summary>
public sealed class StartupEntry
{
    public required string Name { get; init; }
    public required string Command { get; init; }
    public string Publisher { get; init; } = "";
    public bool Enabled { get; set; }
    public StartupSource Source { get; init; }

    /// <summary>Имя значения в Run-ключе (для реестра) или имя .lnk (для папки).</summary>
    public string ValueName { get; init; } = "";
    /// <summary>Полный путь к .lnk (только для папок автозагрузки).</summary>
    public string LnkPath { get; init; } = "";

    public string LocationLabel => Source switch
    {
        StartupSource.RunHKCU => "HKCU:Run",
        StartupSource.RunHKLM => "HKLM:Run",
        StartupSource.RunHKLM32 => "HKLM:Run32",
        StartupSource.FolderUser => Services.Loc.I["su_folder_user"],
        StartupSource.FolderCommon => Services.Loc.I["su_folder_common"],
        _ => "-",
    };

    public bool IsRegistry => Source is StartupSource.RunHKCU or StartupSource.RunHKLM or StartupSource.RunHKLM32;
    public bool NeedsAdmin => Source is StartupSource.RunHKLM or StartupSource.RunHKLM32 or StartupSource.FolderCommon;
}
