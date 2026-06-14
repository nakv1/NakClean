using NakClean.Services;

namespace NakClean.ViewModels;

/// <summary>Видеокарта в «Обзоре системы» (живые загрузка/температура).</summary>
public sealed class GpuVm : ViewModelBase
{
    public GpuVm(GpuStat s)
    {
        _name = s.Name;
        Update(s);
    }

    private string _name;
    public string Name { get => _name; private set => Set(ref _name, value); }

    private double _usage;
    public double UsagePercent { get => _usage; private set => Set(ref _usage, value); }

    private string _usageText = "";
    public string UsageText { get => _usageText; private set => Set(ref _usageText, value); }

    private string _tempText = "-";
    public string TempText { get => _tempText; private set => Set(ref _tempText, value); }

    private string _detail = "";
    public string Detail { get => _detail; private set => Set(ref _detail, value); }

    public void Update(GpuStat s)
    {
        Name = s.Name;
        UsagePercent = s.HasUsage ? s.UsagePercent : 0;
        UsageText = s.HasUsage ? $"{s.UsagePercent:0}%" : Loc.I["na"];
        TempText = s.TempC.HasValue ? $"{s.TempC} °C" : "-";
        Detail = s.Detail;
    }
}
