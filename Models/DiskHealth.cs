namespace NakClean.Models;

public enum HealthState { Healthy, Warning, Unhealthy, Unknown }

/// <summary>Снимок здоровья одного физического диска (S.M.A.R.T. через WMI).</summary>
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

    /// <summary>true, если данные надёжности недоступны (обычно нужны права админа).</summary>
    public bool ReliabilityUnavailable { get; init; }
}
