using System.Windows.Media;
using NakClean.Services;

namespace NakClean.ViewModels;

public sealed class OptTweakVm : ViewModelBase
{
    public Tweak Tweak { get; }

    public OptTweakVm(Tweak t)
    {
        Tweak = t;
        _applied = t.IsApplied();
        _selected = t.TierKey == OptimizationService.Safe; // безопасные предвыбраны
    }

    public string Name => Loc.I[Tweak.NameKey];
    public string Description => Loc.I[Tweak.DescKey];
    public string Tier => Loc.I[Tweak.TierKey];

    /// <summary>Честная метка реального эффекта (заметный / небольшой / удобство / снижает безопасность).</summary>
    public string EffectText => Loc.I[Tweak.EffectKey];
    /// <summary>Расшифровка метки эффекта (в тултипе).</summary>
    public string EffectTip => Loc.I[Tweak.EffectKey + "_tip"];
    public Brush EffectBrush => Tweak.EffectKey switch
    {
        OptimizationService.EfNotable => Frozen(0x3D, 0xD6, 0x8C),    // зелёный
        OptimizationService.EfSmall => Frozen(0x5B, 0x9B, 0xD5),      // синий
        OptimizationService.EfSpace => Frozen(0x4A, 0xC8, 0xC0),      // бирюзовый
        OptimizationService.EfSecurity => Frozen(0xE0, 0x8A, 0x3A),   // оранжевый - снижает безопасность
        _ => Frozen(0x8C, 0x8C, 0x95),                                // серый - удобство/приватность
    };

    private static Brush Frozen(byte r, byte g, byte b)
    { var br = new SolidColorBrush(Color.FromRgb(r, g, b)); br.Freeze(); return br; }

    /// <summary>Перечитать локализованные строки (при смене языка) - без обращения к WMI/реестру.</summary>
    public void RaiseLocalized()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(Tier));
        OnPropertyChanged(nameof(EffectText));
        OnPropertyChanged(nameof(EffectTip));
        OnPropertyChanged(nameof(StatusText));
    }

    private bool _selected;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }

    private bool _applied;
    public bool IsApplied
    {
        get => _applied;
        set { if (Set(ref _applied, value)) { OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(StatusBrush)); } }
    }

    public string StatusText => IsApplied ? Loc.I["opt_applied"] : "-";
    public Brush StatusBrush => IsApplied
        ? new SolidColorBrush(Color.FromRgb(0x3D, 0xD6, 0x8C))
        : new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x95));

    public void RefreshStatus() => IsApplied = Tweak.IsApplied();
}
