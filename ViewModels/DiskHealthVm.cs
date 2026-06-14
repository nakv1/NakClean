using System.Windows.Media;
using NakClean.Models;
using NakClean.Services;

namespace NakClean.ViewModels;

public sealed class DiskHealthVm
{
    private readonly DiskHealth _d;
    public DiskHealthVm(DiskHealth d) => _d = d;

    public string Name => _d.Name;
    public string TypeBadge =>
        _d.MediaType == "-" ? _d.BusType
        : _d.BusType == "-" ? _d.MediaType
        : $"{_d.MediaType} · {_d.BusType}";

    public string SizeText => Format.Capacity(_d.SizeBytes);
    public string StateText => _d.State switch
    {
        HealthState.Healthy => Loc.I["st_healthy"],
        HealthState.Warning => Loc.I["st_warning"],
        HealthState.Unhealthy => Loc.I["st_unhealthy"],
        _ => Loc.I["st_unknown"],
    };

    public Brush StateBrush => _d.State switch
    {
        HealthState.Healthy => new SolidColorBrush(Color.FromRgb(0x3D, 0xD6, 0x8C)),
        HealthState.Warning => new SolidColorBrush(Color.FromRgb(0xDD, 0xB4, 0x4B)),
        HealthState.Unhealthy => new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)),
        _ => new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x95)),
    };

    // если доступ к счётчику надёжности есть, но конкретный показатель пуст -
    // значит сам диск/драйвер его не сообщает (а не дело в правах)
    private string Na => _d.ReliabilityUnavailable ? "-" : Loc.I["dh_na"];

    public string TempText => _d.TemperatureC.HasValue ? $"{_d.TemperatureC} °C" : Na;

    public string PowerOnText
    {
        get
        {
            if (!_d.PowerOnHours.HasValue) return Na;
            long h = _d.PowerOnHours.Value;
            double days = h / 24.0;
            return days >= 365
                ? $"{h:N0} {Loc.I["u_h"]} (~{days / 365:0.0} {Loc.I["u_years"]})"
                : $"{h:N0} {Loc.I["u_h"]} (~{days:0} {Loc.I["u_days"]})";
        }
    }

    public string WearText => _d.WearPercent.HasValue ? $"{_d.WearPercent}%" : Na;

    public string ErrorsText =>
        _d.ReadErrors.HasValue || _d.WriteErrors.HasValue
            ? $"{Loc.I["dh_read"]} {_d.ReadErrors ?? 0} / {Loc.I["dh_write"]} {_d.WriteErrors ?? 0}"
            : Na;

    public bool ReliabilityUnavailable => _d.ReliabilityUnavailable;
}
