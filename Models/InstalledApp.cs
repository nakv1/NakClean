using Microsoft.Win32;

namespace NakClean.Models;

/// <summary>Установленная программа (из ключей Uninstall реестра).</summary>
public sealed class InstalledApp
{
    public required string Name { get; set; }
    public string Publisher { get; init; } = "";
    public string Version { get; init; } = "";
    public DateTime? InstallDate { get; init; }
    public long SizeBytes { get; init; }
    public string UninstallString { get; init; } = "";
    public string QuietUninstallString { get; init; } = "";
    public string ModifyPath { get; init; } = "";

    // где запись лежит в реестре (для переименования/удаления записи)
    public RegistryHive Hive { get; init; }
    public RegistryView View { get; init; }
    public string SubKey { get; init; } = "";  // полный путь под кустом, включая имя подключа

    // UWP / Store-приложение
    public bool IsUwp { get; init; }
    public string PackageFullName { get; init; } = "";

    public bool CanUninstall => IsUwp
                                || !string.IsNullOrWhiteSpace(UninstallString)
                                || !string.IsNullOrWhiteSpace(QuietUninstallString);

    /// <summary>Можно ли восстановить (есть путь восстановления или это MSI).</summary>
    public bool CanRepair => !string.IsNullOrWhiteSpace(ModifyPath) || MsiProductCode is not null;

    /// <summary>Код продукта MSI, если запись - это MSI (имя подключа в фигурных скобках).</summary>
    public string? MsiProductCode
    {
        get
        {
            int slash = SubKey.LastIndexOf('\\');
            string leaf = slash >= 0 ? SubKey[(slash + 1)..] : SubKey;
            return leaf.StartsWith('{') && leaf.EndsWith('}') ? leaf : null;
        }
    }
}
