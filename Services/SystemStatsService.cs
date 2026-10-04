using System.IO;

namespace NakClean.Services;

public record DiskStat(string Name, string Label, long TotalBytes, long FreeBytes)
{
    public long UsedBytes => TotalBytes - FreeBytes;
    public double UsedPercent => TotalBytes > 0 ? UsedBytes * 100.0 / TotalBytes : 0;
}

/// <summary>Диски для плиток «Обзора».</summary>
public static class SystemStatsService
{
    public static IReadOnlyList<DiskStat> GetDisks()
    {
        var list = new List<DiskStat>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType != DriveType.Fixed)
                    continue;
                string label = string.IsNullOrWhiteSpace(d.VolumeLabel)
                    ? d.Name.TrimEnd('\\')
                    : d.VolumeLabel;
                list.Add(new DiskStat(d.Name.TrimEnd('\\'), label, d.TotalSize, d.AvailableFreeSpace));
            }
            catch { /* недоступный диск - пропускаем */ }
        }
        return list;
    }
}
