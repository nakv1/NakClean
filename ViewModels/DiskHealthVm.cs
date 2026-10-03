using System.Windows.Input;
using System.Windows.Media;
using NakClean.Models;
using NakClean.Services;

namespace NakClean.ViewModels;

public sealed class DiskHealthVm : ViewModelBase
{
    private static readonly Brush Green = Frozen(0x3D, 0xD6, 0x8C);
    private static readonly Brush Gold = Frozen(0xDD, 0xB4, 0x4B);
    private static readonly Brush Red = Frozen(0xE5, 0x48, 0x4D);
    private static readonly Brush Gray = Frozen(0x8C, 0x8C, 0x95);

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    private readonly DiskHealth _d;

    public DiskHealthVm(DiskHealth d)
    {
        _d = d;
        Features = d.Features
            .Select(f => new DiskFeatureVm(f.Name == "cache" ? Loc.I["dh_cache"] : f.Name, f.On))
            .ToList();
        Issues = d.Issues
            .Select(i => new DiskIssueVm(string.Format(Loc.I[i.Key], i.Arg), i.Severe ? Red : Gold))
            .ToList();
        Attributes = d.Attributes.Select(a => new SmartAttrVm(a, _d.Attributes)).ToList();
        ToggleSerialCommand = new RelayCommand(() => SerialShown = !SerialShown);
        ToggleAttrsCommand = new RelayCommand(() => AttrsExpanded = !AttrsExpanded);
    }

    public string Name => _d.Name;
    public string TypeBadge =>
        _d.MediaType == "-" ? _d.BusType
        : _d.BusType == "-" ? _d.MediaType
        : $"{_d.MediaType} · {_d.BusType}";

    public string SizeText => Format.Capacity(_d.SizeBytes);

    public string StateText
    {
        get
        {
            string s = _d.State switch
            {
                HealthState.Healthy => Loc.I["st_healthy"],
                HealthState.Warning => Loc.I["st_warning"],
                HealthState.Unhealthy => Loc.I["st_unhealthy"],
                _ => Loc.I["st_unknown"],
            };
            return _d.LifePercent is int life ? $"{s} · {string.Format(Loc.I["dh_life_badge"], life)}" : s;
        }
    }

    public Brush StateBrush => _d.State switch
    {
        HealthState.Healthy => Green,
        HealthState.Warning => Gold,
        HealthState.Unhealthy => Red,
        _ => Gray,
    };

    // если доступ к счётчику надёжности есть, но конкретный показатель пуст -
    // значит сам диск/драйвер его не сообщает (а не дело в правах)
    private string Na => _d.ReliabilityUnavailable ? "-" : Loc.I["dh_na"];

    public string TempText => _d.TemperatureC.HasValue ? $"{_d.TemperatureC} °C" : Na;

    /// <summary>До 50° - норма, до 60° - тепло, выше - перегрев.</summary>
    public Brush TempBrush => _d.TemperatureC switch
    {
        null => Gray,
        < 50 => Green,
        < 60 => Gold,
        _ => Red,
    };

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

    /// <summary>
    /// Вердикт по остатку ресурса SSD (отдельно от надёжности):
    /// 90+ как новый, 70+ норма, 40+ заметный износ, 11+ сильный износ, 10 и меньше - пора менять.
    /// </summary>
    public string WearVerdict => _d.LifePercent switch
    {
        null => "",
        >= 90 => Loc.I["dh_wv_new"],
        >= 70 => Loc.I["dh_wv_ok"],
        >= 40 => Loc.I["dh_wv_noticeable"],
        > 10 => Loc.I["dh_wv_heavy"],
        _ => Loc.I["dh_wv_replace"],
    };

    public bool HasWearVerdict => _d.LifePercent.HasValue;

    public Brush WearBrush => _d.LifePercent switch
    {
        null => Gray, // не используется: без вердикта XAML оставляет обычный цвет текста
        >= 70 => Green,
        > 10 => Gold,
        _ => Red,
    };

    public string ErrorsText =>
        _d.ReadErrors.HasValue || _d.WriteErrors.HasValue
            ? $"{Loc.I["dh_read"]} {_d.ReadErrors ?? 0} / {Loc.I["dh_write"]} {_d.WriteErrors ?? 0}"
            : Na;

    public bool ReliabilityUnavailable => _d.ReliabilityUnavailable;

    // ---------- паспорт ----------
    /// <summary>«Прошивка 3B4QFXO7 · C: D: · PCIe 4.0 x4 · 7200 об/мин».</summary>
    public string PassportText
    {
        get
        {
            var parts = new List<string>();
            if (_d.Firmware is { } fw) parts.Add($"{Loc.I["dh_fw"]} {fw}");
            if (_d.Letters.Length > 0) parts.Add(_d.Letters);
            if (_d.Interface is { } i) parts.Add(i);
            if (_d.Rpm is int rpm) parts.Add(string.Format(Loc.I["dh_rpm"], rpm.ToString("N0")));
            return string.Join("  ·  ", parts);
        }
    }

    public bool HasSerial => _d.Serial is not null;

    private bool _serialShown;
    public bool SerialShown
    {
        get => _serialShown;
        private set { if (Set(ref _serialShown, value)) OnPropertyChanged(nameof(SerialText)); }
    }

    public string SerialText => "S/N " + (SerialShown ? _d.Serial : "••••••••");
    public ICommand ToggleSerialCommand { get; }

    // ---------- второй ряд метрик ----------
    public string WrittenText => _d.BytesWritten is long w ? Format.Capacity(w) : Na;
    public string ReadText => _d.BytesRead is long r ? Format.Capacity(r) : Na;
    public string CyclesText => _d.PowerCycles is long c ? c.ToString("N0") : Na;
    public string UnsafeText => _d.UnsafeShutdowns is long u ? u.ToString("N0") : Na;

    /// <summary>Второй ряд показываем, только если SMART прочитан (иначе там одни прочерки).</summary>
    public bool HasExtra => _d.Attributes.Count > 0;

    // ---------- функции, проблемы, атрибуты ----------
    public List<DiskFeatureVm> Features { get; }
    public bool HasFeatures => Features.Count > 0;

    public List<DiskIssueVm> Issues { get; }
    public bool HasIssues => Issues.Count > 0;

    public List<SmartAttrVm> Attributes { get; }
    public bool HasAttributes => Attributes.Count > 0;
    public bool ShowThresholds => !_d.IsNvme;
    public string AttrsHeader => string.Format(Loc.I["dh_attrs"], Attributes.Count);

    private bool _attrsExpanded;
    public bool AttrsExpanded
    {
        get => _attrsExpanded;
        private set { if (Set(ref _attrsExpanded, value)) OnPropertyChanged(nameof(AttrsChevron)); }
    }
    public string AttrsChevron => AttrsExpanded ? "▾" : "▸";
    public ICommand ToggleAttrsCommand { get; }

    internal static Brush DotGreen => Green;
    internal static Brush DotGold => Gold;
    internal static Brush DotRed => Red;
}

/// <summary>Плашка функции диска (TRIM, NCQ...): включена - подсвечена, нет - приглушена.</summary>
public sealed record DiskFeatureVm(string Name, bool On)
{
    public double Opacity => On ? 1.0 : 0.4;
}

public sealed record DiskIssueVm(string Text, Brush Brush);

/// <summary>Строка таблицы атрибутов SMART.</summary>
public sealed class SmartAttrVm
{
    private readonly SmartAttr _a;
    private readonly IReadOnlyList<SmartAttr> _all;
    public SmartAttrVm(SmartAttr a, IReadOnlyList<SmartAttr> all) { _a = a; _all = all; }

    public string IdText => _a.Id.ToString("X2");
    public string Name => SmartNames.Name(_a.Id, _a.IsNvme);
    public string CurText => _a.Cur?.ToString() ?? "";
    public string WorstText => _a.Worst?.ToString() ?? "";
    public string ThreshText => _a.Thresh?.ToString() ?? "";

    public string RawText
    {
        get
        {
            long low = _a.Raw & 0xFFFFFFFF;
            if (_a.IsNvme)
                return _a.Id switch
                {
                    0x02 => _a.Raw > 0 ? $"{_a.Raw - 273} °C" : "-",
                    0x03 or 0x04 or 0x05 => $"{_a.Raw}%",
                    0x06 or 0x07 => $"{_a.Raw:N0}  ({Format.Capacity(_a.Raw * 512_000)})",
                    0x0A => $"{_a.Raw:N0} {Loc.I["u_min"]}",
                    0x0C => $"{_a.Raw:N0} {Loc.I["u_h"]}",
                    _ => _a.Raw.ToString("N0"),
                };
            return _a.Id switch
            {
                194 or 190 => _a.RawBytes.Length > 0 && _a.RawBytes[0] is > 0 and < 110 ? $"{_a.RawBytes[0]} °C" : low.ToString("N0"),
                9 => $"{low:N0} {Loc.I["u_h"]}",
                _ => _a.Raw.ToString("N0"),
            };
        }
    }

    /// <summary>Цвет точки - по тем же правилам, что и общая оценка диска.</summary>
    public Brush DotBrush
    {
        get
        {
            if (_a.IsNvme)
            {
                var thr = _all.FirstOrDefault(x => x.Id == 0x04)?.Raw ?? 0;
                return _a.Id switch
                {
                    0x01 when _a.Raw > 0 => DiskHealthVm.DotRed,
                    0x03 when thr > 0 && _a.Raw < thr => DiskHealthVm.DotRed,
                    0x05 when _a.Raw >= 90 => DiskHealthVm.DotGold,
                    0x0E when _a.Raw > 0 => DiskHealthVm.DotGold,
                    _ => DiskHealthVm.DotGreen,
                };
            }
            if (_a.Thresh is > 0 && _a.Cur is > 0 && _a.Cur <= _a.Thresh) return DiskHealthVm.DotRed;
            if (_a.Id is 5 or 197 or 198 && (_a.Raw & 0xFFFFFFFF) > 0) return DiskHealthVm.DotGold;
            return DiskHealthVm.DotGreen;
        }
    }
}
