using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using NakClean.Services;

namespace NakClean.ViewModels;

/// <summary>Карточка одного сбоя в разделе «Сбои».</summary>
public sealed class CrashVm
{
    public required string WhenText { get; init; }
    public required string KindText { get; init; }
    public required Brush KindBrush { get; init; }
    public required string Title { get; init; }
    public string CodeText { get; init; } = "";
    public required string Explain { get; init; }
    public string Note { get; init; } = "";
    public required string Advice { get; init; }
    public string DumpText { get; init; } = "";

    public bool HasExplain => Explain.Length > 0;
    public bool HasAdvice => Advice.Length > 0;
    public bool HasCode => CodeText.Length > 0;
    public bool HasNote => Note.Length > 0;
    public bool HasDump => DumpText.Length > 0;
}

// ---------- Диагностика: «Сбои» (синие экраны и внезапные выключения) ----------
public sealed partial class MainViewModel
{
    public ObservableCollection<CrashVm> Crashes { get; } = new();
    private List<CrashEvent>? _crashes;
    private bool _crashReading;

    private string _crashSummary = "";
    public string CrashSummary { get => _crashSummary; private set => Set(ref _crashSummary, value); }

    private string _crashRepeat = "";
    public string CrashRepeat { get => _crashRepeat; private set { if (Set(ref _crashRepeat, value)) OnPropertyChanged(nameof(HasCrashRepeat)); } }
    public bool HasCrashRepeat => _crashRepeat.Length > 0;

    private async Task LoadCrashesAsync()
    {
        _crashReading = true;
        BuildCrashView();
        try { _crashes = await Task.Run(() => CrashService.Read()); }
        catch { _crashes = new(); }
        finally
        {
            _crashReading = false;
            BuildCrashView();
            RebuildDiagTiles();
        }
    }

    /// <summary>Раздел «Сбои» из прочитанного (и при смене языка - без повторного чтения журнала).</summary>
    private void BuildCrashView()
    {
        Crashes.Clear();
        CrashRepeat = "";
        if (_crashReading || _crashes is null) { CrashSummary = Loc.I["cr_reading"]; return; }

        int bsod = _crashes.Count(c => c.Kind == CrashKind.BlueScreen);
        int off = _crashes.Count - bsod;
        CrashSummary = _crashes.Count == 0 ? Loc.I["cr_none"] : string.Format(Loc.I["cr_summary"], bsod, off);

        // одна и та же ошибка несколько раз - главный повод разбираться
        var rep = _crashes.Where(c => c.Kind == CrashKind.BlueScreen).GroupBy(c => c.Code)
                          .Where(g => g.Count() >= 2).OrderByDescending(g => g.Count()).FirstOrDefault();
        if (rep != null) CrashRepeat = string.Format(Loc.I["cr_repeat"], $"0x{rep.Key:X}", rep.Count());

        var culture = CultureInfo.GetCultureInfo(Loc.I.IsEn ? "en-US" : "ru-RU");
        // объяснение и совет - только у самого свежего сбоя каждого вида; повторы - коротко, без одного и того же текста
        var explained = new HashSet<(CrashKind, uint)>();
        foreach (var c in _crashes.Take(60))
        {
            string when = c.When.ToString("d MMMM yyyy, HH:mm", culture);
            bool first = explained.Add((c.Kind, c.Code));
            if (c.Kind == CrashKind.SuddenOff)
            {
                Crashes.Add(new CrashVm
                {
                    WhenText = when, KindText = Loc.I["cr_kind_off"], KindBrush = DgGold,
                    Title = Loc.I["cr_off_title"],
                    Explain = first ? Loc.I["cr_off_text"] : "", Advice = first ? Loc.I["cr_off_adv"] : "",
                });
                continue;
            }

            var (name, cause) = CrashService.Describe(c.Code);
            string causeKey = "cr_c_" + cause.ToString().ToLowerInvariant();
            string hex = $"0x{c.Code:X8}";
            Crashes.Add(new CrashVm
            {
                WhenText = when, KindText = Loc.I["cr_kind_bsod"], KindBrush = DgRed,
                Title = Loc.I[causeKey],
                CodeText = name is null ? hex : $"{hex}  ·  {name}",
                Explain = first ? Loc.I[causeKey + "_t"] : "",
                Note = first && CrashService.NoteKey(c) is { } nk ? Loc.I[nk] : "",
                Advice = first ? string.Format(Loc.I[causeKey + "_a"], hex) : "",
                DumpText = string.IsNullOrEmpty(c.DumpPath) ? "" : string.Format(Loc.I["cr_dump"], c.DumpPath),
            });
        }
    }

    /// <summary>Плитка «Сводки»: сбои за последние 30 дней.</summary>
    private DiagTileVm CrashTile()
    {
        if (_crashes is null) return new DiagTileVm { Glyph = "🛑", Title = Loc.I["dg_crashes"], Target = "crashes", Value = "…" };
        var recent = _crashes.Where(c => c.When >= DateTime.Now.AddDays(-30)).ToList();
        int bsod = recent.Count(c => c.Kind == CrashKind.BlueScreen), off = recent.Count - bsod;
        string last = _crashes.Count > 0
            ? string.Format(Loc.I["dg_cr_last"], _crashes[0].When.ToString("d MMM", CultureInfo.GetCultureInfo(Loc.I.IsEn ? "en-US" : "ru-RU")))
            : "";
        return new DiagTileVm
        {
            Glyph = "🛑", Title = Loc.I["dg_crashes"], Target = "crashes",
            Value = bsod > 0 ? string.Format(Loc.I["dg_cr_bsod"], bsod)
                  : off > 0 ? string.Format(Loc.I["dg_cr_off"], off)
                  : Loc.I["dg_cr_none"],
            ValueBrush = bsod > 0 ? DgRed : off > 0 ? DgGold : DgGreen,
            Note = Loc.I["dg_cr_30"] + last,
        };
    }
}
