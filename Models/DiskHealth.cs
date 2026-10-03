using NakClean.Services;

namespace NakClean.Models;

public enum HealthState { Healthy, Warning, Unhealthy, Unknown }

/// <summary>Проблема, найденная по SMART (ключ локализации + число для подстановки).</summary>
public sealed record DiskIssue(string Key, string Arg, bool Severe);

/// <summary>Снимок здоровья одного физического диска (S.M.A.R.T. через WMI + напрямую).</summary>
public sealed class DiskHealth
{
    public string Name { get; init; } = "Диск";
    public int DeviceId { get; init; } = -1;            // номер физического диска
    public string MediaType { get; init; } = "-";      // SSD / HDD / NVMe ...
    public string BusType { get; init; } = "-";
    public long SizeBytes { get; init; }

    public HealthState State { get; init; } = HealthState.Unknown;
    public string StateRaw { get; init; } = "-";

    public int? TemperatureC { get; init; }
    public long? PowerOnHours { get; init; }
    public int? WearPercent { get; init; }              // износ SSD (0 = новый)
    public long? ReadErrors { get; init; }
    public long? WriteErrors { get; init; }

    // ---- подробности в стиле CrystalDiskInfo ----
    public string? Firmware { get; init; }
    public string? Serial { get; init; }
    public string Letters { get; init; } = "";          // «C: D:»
    public string? Interface { get; init; }             // «PCIe 4.0 x4» / «SATA/600»
    public int? Rpm { get; init; }                      // обороты HDD (null - нет/SSD)
    public long? PowerCycles { get; init; }
    public long? UnsafeShutdowns { get; init; }
    public long? BytesWritten { get; init; }
    public long? BytesRead { get; init; }
    public bool IsNvme { get; init; }
    public IReadOnlyList<(string Name, bool On)> Features { get; init; } = Array.Empty<(string, bool)>();
    public IReadOnlyList<SmartAttr> Attributes { get; init; } = Array.Empty<SmartAttr>();
    public IReadOnlyList<DiskIssue> Issues { get; init; } = Array.Empty<DiskIssue>();

    /// <summary>Остаток ресурса SSD в % (как «Хорошо 97%» у CrystalDiskInfo). null - не SSD/нет данных.</summary>
    public int? LifePercent => WearPercent.HasValue && MediaType == "SSD" ? Math.Max(0, 100 - WearPercent.Value) : null;

    /// <summary>true, если данные надёжности недоступны (обычно нужны права админа).</summary>
    public bool ReliabilityUnavailable { get; init; }
}
