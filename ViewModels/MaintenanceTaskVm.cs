using System.Diagnostics;
using System.Windows.Threading;
using NakClean.Services;

namespace NakClean.ViewModels;

/// <summary>
/// Карточка на вкладке «Обслуживание». Поток: Проверить → (результат + Запустить/Отмена) → Выполнить.
/// Во время работы - прогресс-бар (реальный % если Windows отдаёт, иначе анимация) + время + комментарий.
/// </summary>
public sealed class MaintenanceTaskVm : ViewModelBase
{
    private readonly string _titleKey, _descKey;
    private readonly (string file, string args)? _analyze;
    private readonly (string file, string args) _run;
    private readonly string _runStageKey, _checkStageKey, _doneKey;
    private readonly bool _launchOnly;
    private readonly Func<Task<CheckResult>>? _customAnalyze;   // кастомный анализ (для дефрага - WMI-вердикт)
    private readonly Func<string, CheckResult>? _interpret;     // вывод проверки → вердикт «что нашлось»
    private readonly bool _unicode;                             // sfc пишет UTF-16

    private CancellationTokenSource? _cts;
    private bool _isRun;
    private readonly Stopwatch _sw = new();
    private DispatcherTimer? _tick;

    public MaintenanceTaskVm(string glyph, string titleKey, string descKey,
        (string file, string args)? analyze, (string file, string args) run,
        string checkStageKey, string runStageKey, string doneKey,
        bool launchOnly = false, Func<Task<CheckResult>>? customAnalyze = null,
        Func<string, CheckResult>? interpret = null, bool unicode = false)
    {
        Glyph = glyph;
        _titleKey = titleKey; _descKey = descKey;
        _analyze = analyze; _run = run;
        _checkStageKey = checkStageKey; _runStageKey = runStageKey; _doneKey = doneKey;
        _launchOnly = launchOnly; _customAnalyze = customAnalyze;
        _interpret = interpret; _unicode = unicode;

        PrimaryCommand = new RelayCommand(async () => await PrimaryAsync(), () => !IsWorking);
        ConfirmCommand = new RelayCommand(async () => await StartProcess(_run, _runStageKey, review: false), () => !IsWorking && _confirmEnabled);
        CancelCommand = new RelayCommand(Cancel);
    }

    public string Glyph { get; }
    public string Title => Loc.I[_titleKey];
    public string Desc => Loc.I[_descKey];
    private bool HasAnalyze => _analyze != null || _customAnalyze != null;
    public string PrimaryText => Loc.I[HasAnalyze ? "mnt_check" : "mnt_run"];

    public RelayCommand PrimaryCommand { get; }
    public RelayCommand ConfirmCommand { get; }
    public RelayCommand CancelCommand { get; }

    // ---------- состояние UI ----------
    private bool _working;
    public bool IsWorking
    {
        get => _working;
        private set
        {
            if (!Set(ref _working, value)) return;
            OnPropertyChanged(nameof(ShowPrimary));
            PrimaryCommand.RaiseCanExecuteChanged();
            ConfirmCommand.RaiseCanExecuteChanged();
        }
    }

    private bool _reviewing;
    public bool ShowConfirm
    {
        get => _reviewing;
        private set { if (Set(ref _reviewing, value)) OnPropertyChanged(nameof(ShowPrimary)); }
    }

    public bool ShowPrimary => !IsWorking && !ShowConfirm;

    private bool _confirmEnabled = true;   // активна ли кнопка «Запустить» после проверки
    private void SetConfirmEnabled(bool v) { _confirmEnabled = v; ConfirmCommand.RaiseCanExecuteChanged(); }

    private bool _indeterminate = true;
    public bool IsIndeterminate
    {
        get => _indeterminate;
        private set { if (Set(ref _indeterminate, value)) OnPropertyChanged(nameof(ProgressText)); }
    }

    private double _progress;
    public double Progress
    {
        get => _progress;
        private set { if (Set(ref _progress, value)) OnPropertyChanged(nameof(ProgressText)); }
    }

    private string _elapsed = "";
    public string Elapsed
    {
        get => _elapsed;
        private set { if (Set(ref _elapsed, value)) OnPropertyChanged(nameof(ProgressText)); }
    }

    /// <summary>Строка под баром: время + процент (если известен).</summary>
    public string ProgressText =>
        IsIndeterminate ? Elapsed
        : string.IsNullOrEmpty(Elapsed) ? $"{Progress:0}%" : $"{Progress:0}%  ·  {Elapsed}";

    private string _status = "";
    public string Status
    {
        get => _status;
        private set { if (Set(ref _status, value)) OnPropertyChanged(nameof(HasStatus)); }
    }
    public bool HasStatus => !string.IsNullOrEmpty(_status);

    // постоянная строка-сведение под описанием (например, итог последнего теста памяти)
    private string _info = "";
    public string Info { get => _info; private set { if (Set(ref _info, value)) OnPropertyChanged(nameof(HasInfo)); } }
    public bool HasInfo => !string.IsNullOrEmpty(_info);

    private System.Windows.Media.Brush? _infoBrush;
    public System.Windows.Media.Brush? InfoBrush { get => _infoBrush; private set => Set(ref _infoBrush, value); }

    public void SetInfo(string text, System.Windows.Media.Brush brush)
    {
        InfoBrush = brush;
        Info = text;
    }

    public void RaiseLocalized()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Desc));
        OnPropertyChanged(nameof(PrimaryText));
    }

    // ---------- таймер времени ----------
    private void StartTimer()
    {
        _sw.Restart();
        Elapsed = "0:00";
        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _tick.Tick += (_, _) => Elapsed = $"{(int)_sw.Elapsed.TotalMinutes}:{_sw.Elapsed.Seconds:00}";
        _tick.Start();
    }
    private void StopTimer() { _tick?.Stop(); _tick = null; _sw.Stop(); }

    // ---------- логика ----------
    private async Task PrimaryAsync()
    {
        if (_launchOnly)
        {
            Status = Loc.I[MaintenanceService.LaunchMemoryTest() ? "mnt_ram_launched" : "mnt_fail"];
            return;
        }
        if (_customAnalyze is { } custom) { await StartCustom(custom); return; }
        if (_analyze is { } a) await StartProcess(a, _checkStageKey, review: true);
        else await StartProcess(_run, _runStageKey, review: false);
    }

    /// <summary>Кастомный анализ (WMI и т.п.) - индикатор неопределённый, результат = вердикт + нужно ли действие.</summary>
    private async Task StartCustom(Func<Task<CheckResult>> analyze)
    {
        IsWorking = true; ShowConfirm = false;
        IsIndeterminate = true; Progress = 0;
        Status = Loc.I[_checkStageKey];
        StartTimer();
        try
        {
            var cr = await analyze();
            Status = cr.Text;
            SetConfirmEnabled(cr.ActionNeeded);
        }
        catch { Status = Loc.I["mnt_fail"]; SetConfirmEnabled(false); }
        StopTimer();
        IsWorking = false;
        ShowConfirm = true;     // вердикт показан - даём решить
    }

    private async Task StartProcess((string file, string args) step, string stageKey, bool review)
    {
        IsWorking = true; ShowConfirm = false;
        _isRun = !review;   // идёт сама очистка, а не проверка
        IsIndeterminate = true; Progress = 0;
        Status = Loc.I[stageKey];
        StartTimer();

        _cts = new CancellationTokenSource();
        var prog = new Progress<MaintProgress>(mp =>
        {
            if (mp.Percent is double d) { IsIndeterminate = false; Progress = d * 100; }
        });

        var (ok, text) = await MaintenanceService.RunAsync(step, prog, _cts.Token, _unicode);
        bool cancelled = _cts.IsCancellationRequested;
        _cts.Dispose(); _cts = null;
        StopTimer();
        IsWorking = false;

        if (cancelled) { Status = Loc.I["mnt_cancelled"]; return; }
        if (review)
        {
            // вердикт «что нашлось» из вывода инструмента (или нейтрально, если не распознали)
            var cr = _interpret?.Invoke(text) ?? new CheckResult(Loc.I["mnt_analyzed"], true);
            Status = cr.Text;
            SetConfirmEnabled(cr.ActionNeeded);
            ShowConfirm = true;
        }
        else Status = Loc.I[ok ? _doneKey : "mnt_fail"];
    }

    private void Cancel()
    {
        // очистку WinSxS Microsoft не советует прерывать - переспрашиваем
        if (IsWorking && _titleKey == "mnt_winsxs" && _isRun
            && System.Windows.MessageBox.Show(Loc.I["mnt_cancel_winsxs_q"], "NakClean",
                   System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes)
            return;
        if (IsWorking) _cts?.Cancel();
        else if (ShowConfirm) { ShowConfirm = false; Status = ""; }
    }
}
