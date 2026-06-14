using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using NakClean.Models;
using NakClean.Services;

namespace NakClean.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _cts;

    public ObservableCollection<DiskVm> Disks { get; } = new();
    public ObservableCollection<CleanCategory> Categories { get; } = new();
    public ICollectionView CategoriesView { get; }
    public ObservableCollection<DiskHealthVm> DiskHealth { get; } = new();
    public ObservableCollection<HealthItem> Health { get; } = new();
    // Секции по категориям, разложенные по двум колонкам с балансом высоты.
    public ObservableCollection<HealthSection> HealthColLeft { get; } = new();
    public ObservableCollection<HealthSection> HealthColRight { get; } = new();
    public ObservableCollection<StartupEntryVm> StartupEntries { get; } = new();
    public ObservableCollection<InstalledAppVm> Apps { get; } = new();
    public ICollectionView AppsView { get; }
    public ObservableCollection<RegScanOption> RegOptions { get; } = new();
    public ObservableCollection<RegistryIssueVm> RegIssues { get; } = new();
    public ICollectionView RegIssuesView { get; }
    public ObservableCollection<TaskEntryVm> Tasks { get; } = new();
    public ObservableCollection<ServiceEntryVm> Services { get; } = new();
    public ICollectionView ServicesView { get; }
    public ObservableCollection<ContextMenuEntryVm> ContextItems { get; } = new();
    public ObservableCollection<OptTweakVm> Tweaks { get; } = new();
    public ICollectionView TweaksView { get; }
    public ObservableCollection<DriveItemVm> Drives { get; } = new();
    public ObservableCollection<TreeRowVm> TreeRows { get; } = new();
    public ObservableCollection<ExtRowVm> Extensions { get; } = new();
    public ObservableCollection<MaintenanceTaskVm> MaintenanceTasks { get; } = new();
    public ObservableCollection<BootSlowVm> BootSlow { get; } = new();
    public ObservableCollection<ChangeEntryVm> ChangeLog { get; } = new();

    public RelayCommand ScanCommand { get; }
    public RelayCommand CleanCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectNoneCommand { get; }
    public RelayCommand RefreshHealthCommand { get; }
    public RelayCommand RunHealthCheckCommand { get; }
    public RelayCommand RefreshStartupCommand { get; }
    public RelayCommand EnableStartupCommand { get; }
    public RelayCommand DisableStartupCommand { get; }
    public RelayCommand DeleteStartupCommand { get; }
    public RelayCommand RefreshAppsCommand { get; }
    public RelayCommand UninstallCommand { get; }
    public RelayCommand RepairCommand { get; }
    public RelayCommand RenameCommand { get; }
    public RelayCommand RemoveEntryCommand { get; }
    public RelayCommand SortAppsCommand { get; }
    public RelayCommand ScanRegistryCommand { get; }
    public RelayCommand FixRegistryCommand { get; }
    public RelayCommand RefreshTasksCommand { get; }
    public RelayCommand EnableTaskCommand { get; }
    public RelayCommand DisableTaskCommand { get; }
    public RelayCommand DeleteTaskCommand { get; }
    public RelayCommand RefreshServicesCommand { get; }
    public RelayCommand EnableServiceCommand { get; }
    public RelayCommand DisableServiceCommand { get; }
    public RelayCommand RefreshContextCommand { get; }
    public RelayCommand EnableContextCommand { get; }
    public RelayCommand DisableContextCommand { get; }
    public RelayCommand DeleteContextCommand { get; }
    public RelayCommand ApplyOptCommand { get; }
    public RelayCommand RevertOptCommand { get; }
    public RelayCommand RefreshOptCommand { get; }
    public RelayCommand ApplyProfileCommand { get; }
    public RelayCommand RevertAllCommand { get; }
    public RelayCommand AnalyzeCommand { get; }
    public RelayCommand BrowseFolderCommand { get; }
    public RelayCommand OpenFileCommand { get; }
    public RelayCommand MapUpCommand { get; }
    public RelayCommand RefreshBatteryCommand { get; }
    public RelayCommand RefreshBootCommand { get; }
    public RelayCommand ExportReportCommand { get; }

    public MainViewModel()
    {
        foreach (var c in CleanEngine.BuildCategories())
        {
            c.PropertyChanged += OnCategoryChanged;
            Categories.Add(c);
        }

        CategoriesView = CollectionViewSource.GetDefaultView(Categories);
        CategoriesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CleanCategory.GroupText)));

        ScanCommand = new RelayCommand(async () => await ScanAsync(), () => !IsBusy);
        CleanCommand = new RelayCommand(async () => await CleanAsync(), () => !IsBusy && SelectedBytes > 0);
        RefreshCommand = new RelayCommand(RefreshDisks, () => !IsBusy);
        SelectAllCommand = new RelayCommand(() => SetAll(true));
        SelectNoneCommand = new RelayCommand(() => SetAll(false));
        RefreshHealthCommand = new RelayCommand(async () => await LoadDiskHealthAsync());
        RunHealthCheckCommand = new RelayCommand(async () => await RunHealthCheckAsync());
        RefreshStartupCommand = new RelayCommand(async () => await LoadStartupAsync());
        EnableStartupCommand = new RelayCommand(() => ToggleStartup(true), () => SelectedStartup != null);
        DisableStartupCommand = new RelayCommand(() => ToggleStartup(false), () => SelectedStartup != null);
        DeleteStartupCommand = new RelayCommand(DeleteStartup, () => SelectedStartup != null);
        RefreshAppsCommand = new RelayCommand(async () => await LoadAppsAsync());
        UninstallCommand = new RelayCommand(Uninstall, () => SelectedApp is { CanUninstall: true });
        RepairCommand = new RelayCommand(Repair, () => SelectedApp is { CanRepair: true });
        RenameCommand = new RelayCommand(RenameApp, () => SelectedApp != null);
        RemoveEntryCommand = new RelayCommand(RemoveAppEntry, () => SelectedApp != null);
        SortAppsCommand = new RelayCommand(p => SortApps(p as string ?? "name"));
        ScanRegistryCommand = new RelayCommand(async () => await ScanRegistryAsync(), () => !RegBusy);
        FixRegistryCommand = new RelayCommand(async () => await FixRegistryAsync(), () => !RegBusy && RegIssues.Count > 0);


        AppsView = CollectionViewSource.GetDefaultView(Apps);
        AppsView.Filter = AppFilter;

        foreach (var c in RegistryScanService.Categories)
            RegOptions.Add(new RegScanOption { Id = c.Id, Name = c.Name });
        RegIssuesView = CollectionViewSource.GetDefaultView(RegIssues);
        RegIssuesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(RegistryIssueVm.Category)));

        RefreshTasksCommand = new RelayCommand(async () => await LoadTasksAsync());
        EnableTaskCommand = new RelayCommand(() => ToggleTask(true), () => SelectedTask != null);
        DisableTaskCommand = new RelayCommand(() => ToggleTask(false), () => SelectedTask != null);
        DeleteTaskCommand = new RelayCommand(DeleteTask, () => SelectedTask != null);
        RefreshServicesCommand = new RelayCommand(async () => await LoadServicesAsync());
        EnableServiceCommand = new RelayCommand(() => ToggleService(true), () => SelectedService != null);
        DisableServiceCommand = new RelayCommand(() => ToggleService(false), () => SelectedService != null);

        ServicesView = CollectionViewSource.GetDefaultView(Services);
        ServicesView.Filter = ServiceFilter;

        RefreshContextCommand = new RelayCommand(async () => await LoadContextAsync());
        EnableContextCommand = new RelayCommand(() => ToggleContext(true), () => SelectedContext != null);
        DisableContextCommand = new RelayCommand(() => ToggleContext(false), () => SelectedContext != null);
        DeleteContextCommand = new RelayCommand(DeleteContext, () => SelectedContext != null);

        ApplyOptCommand = new RelayCommand(async () => await ApplyOptAsync(true), () => !OptBusy);
        RevertOptCommand = new RelayCommand(async () => await ApplyOptAsync(false), () => !OptBusy);
        RefreshOptCommand = new RelayCommand(() => { foreach (var t in Tweaks) t.RefreshStatus(); });
        ApplyProfileCommand = new RelayCommand(p => SelectProfile(p as string));
        RevertAllCommand = new RelayCommand(async () => await RevertAllAsync(), () => !OptBusy && ChangeLog.Count > 0);
        ReloadChangeLog();
        RefreshBatteryCommand = new RelayCommand(async () => await LoadBatteryAsync());
        RefreshBootCommand = new RelayCommand(async () => await LoadBootAsync());
        ExportReportCommand = new RelayCommand(ExportReport);

        foreach (var t in OptimizationService.BuildTweaks())
            Tweaks.Add(new OptTweakVm(t));
        TweaksView = CollectionViewSource.GetDefaultView(Tweaks);
        TweaksView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(OptTweakVm.Tier)));

        AnalyzeCommand = new RelayCommand(async () => await AnalyzeAsync(), () => !FilesBusy);
        BrowseFolderCommand = new RelayCommand(BrowseFolder, () => !FilesBusy);
        OpenFileCommand = new RelayCommand(OpenSelectedFile, () => SelectedFile != null);
        MapUpCommand = new RelayCommand(() => { if (MapRoot?.Parent is { } p) MapRoot = p; },
            () => MapRoot?.Parent != null);
        LoadDrives();

        BuildMaintenanceTasks();

        // На старте грузим только то, что нужно для стартовой вкладки «Обзор».
        // Остальные вкладки - лениво при первом открытии (см. Ensure*), чтобы старт был быстрым.
        RefreshDisks();
        _ = LoadSystemInfoAsync();
        Loc.I.PropertyChanged += (_, _) => OnLanguageChanged();
        Native.GetCpuUsage(); // первый замер-«нулёвка» для CPU

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => UpdateLiveStats();
        _timer.Start();
    }

    // ---------- Живой опрос: пауза, когда «Обзор» не виден / окно свёрнуто ----------
    private bool _liveActive = true;
    public void SetLiveActive(bool active)
    {
        if (active == _liveActive) return;
        _liveActive = active;
        if (active) { UpdateLiveStats(); _timer.Start(); }
        else _timer.Stop();
    }

    // ---------- Ленивая загрузка данных вкладок (один раз при первом открытии) ----------
    private bool _healthLoaded, _startupLoaded, _appsLoaded, _diagLoaded;

    public void EnsureHealthLoaded()
    {
        if (_healthLoaded) return;
        _healthLoaded = true;
        _ = RunHealthCheckAsync();
    }

    public void EnsureStartupLoaded()
    {
        if (_startupLoaded) return;
        _startupLoaded = true;
        _ = LoadStartupAsync();
        _ = LoadTasksAsync();
        _ = LoadServicesAsync();
        _ = LoadContextAsync();
    }

    public void EnsureAppsLoaded()
    {
        if (_appsLoaded) return;
        _appsLoaded = true;
        _ = LoadAppsAsync();
    }

    public void EnsureDiagnosticsLoaded()
    {
        if (_diagLoaded) return;
        _diagLoaded = true;
        _ = LoadDiskHealthAsync();
        _ = LoadBatteryAsync();
        _ = LoadBootAsync();
    }

    // ---------- Обслуживание системы ----------
    private void BuildMaintenanceTasks()
    {
        MaintenanceTasks.Add(new MaintenanceTaskVm("🛡", "mnt_sfc", "mnt_sfc_d",
            MaintenanceService.SfcAnalyze, MaintenanceService.SfcRun,
            "mnt_st_sfc_check", "mnt_st_sfc_run", "mnt_done",
            interpret: MaintenanceService.InterpretSfc, unicode: true));
        MaintenanceTasks.Add(new MaintenanceTaskVm("🩺", "mnt_dism", "mnt_dism_d",
            MaintenanceService.DismAnalyze, MaintenanceService.DismRun,
            "mnt_st_dism_check", "mnt_st_dism_run", "mnt_done",
            interpret: MaintenanceService.InterpretDism));
        MaintenanceTasks.Add(new MaintenanceTaskVm("📦", "mnt_winsxs", "mnt_winsxs_d",
            MaintenanceService.WinsxsAnalyze, MaintenanceService.WinsxsRun,
            "mnt_st_winsxs_check", "mnt_st_winsxs_run", "mnt_done_freed",
            interpret: MaintenanceService.InterpretWinsxs));
        MaintenanceTasks.Add(new MaintenanceTaskVm("💽", "mnt_defrag", "mnt_defrag_d",
            null, MaintenanceService.DefragRun,
            "mnt_st_defrag_check", "mnt_st_defrag_run", "mnt_done",
            customAnalyze: DefragService.AnalyzeAll));
        MaintenanceTasks.Add(new MaintenanceTaskVm("🧠", "mnt_ram", "mnt_ram_d",
            null, MaintenanceService.SfcRun /*не используется*/,
            "", "", "mnt_done", launchOnly: true));
    }

    // ---------- Профили оптимизации ----------
    private void SelectProfile(string? id)
    {
        if (id == "none")
        {
            foreach (var t in Tweaks) t.Selected = false;
            OptNote = Loc.I["opt_profile_cleared"];
            return;
        }
        if (id is null || !OptimizationService.Profiles.TryGetValue(id, out var ids)) return;
        var set = ids.ToHashSet();
        int n = 0;
        foreach (var t in Tweaks) { t.Selected = set.Contains(t.Tweak.Id); if (t.Selected) n++; }
        OptNote = string.Format(Loc.I["opt_profile_set"], n);
    }

    // ---------- Диагностика: батарея ----------
    private bool _batteryPresent;
    public bool BatteryPresent { get => _batteryPresent; private set => Set(ref _batteryPresent, value); }

    private bool _batteryBusy;
    public bool BatteryBusy { get => _batteryBusy; private set => Set(ref _batteryBusy, value); }

    private string _batteryNote = "";
    public string BatteryNote { get => _batteryNote; private set => Set(ref _batteryNote, value); }

    private string _batWear = "", _batDesign = "", _batFull = "", _batCycles = "";
    public string BatWear { get => _batWear; private set => Set(ref _batWear, value); }
    public string BatDesign { get => _batDesign; private set => Set(ref _batDesign, value); }
    public string BatFull { get => _batFull; private set => Set(ref _batFull, value); }
    public string BatCycles { get => _batCycles; private set => Set(ref _batCycles, value); }

    // текущий заряд + состояние
    private bool _batHasCharge;
    public bool BatHasCharge { get => _batHasCharge; private set => Set(ref _batHasCharge, value); }
    private int _batChargePercent;
    public int BatChargePercent { get => _batChargePercent; private set => Set(ref _batChargePercent, value); }
    private string _batCharge = "", _batState = "";
    public string BatCharge { get => _batCharge; private set => Set(ref _batCharge, value); }
    public string BatState { get => _batState; private set => Set(ref _batState, value); }
    private Brush _batChargeBrush = FrozenBrush(0x3D, 0xD6, 0x8C);
    public Brush BatChargeBrush { get => _batChargeBrush; private set => Set(ref _batChargeBrush, value); }

    // паспорт батареи
    private bool _batHasDevice;
    public bool BatHasDevice { get => _batHasDevice; private set => Set(ref _batHasDevice, value); }
    private string _batDevice = "", _batSerial = "";
    public string BatDevice { get => _batDevice; private set => Set(ref _batDevice, value); }
    public string BatSerial { get => _batSerial; private set => Set(ref _batSerial, value); }

    // вердикт по износу + цвет
    private string _batVerdict = "";
    public string BatVerdict { get => _batVerdict; private set => Set(ref _batVerdict, value); }
    private Brush _batWearBrush = FrozenBrush(0xDD, 0xB4, 0x4B);
    public Brush BatWearBrush { get => _batWearBrush; private set => Set(ref _batWearBrush, value); }

    private static string Wh(long mwh) => $"{mwh / 1000.0:0.0} {Loc.I["u_wh"]}";

    private static Brush FrozenBrush(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    private async Task LoadBatteryAsync()
    {
        BatteryBusy = true;
        BatteryNote = Loc.I["bat_reading"];
        try
        {
            var b = await BatteryService.Get();
            BatteryPresent = b.Present;
            if (!b.Present) { BatteryNote = Loc.I["bat_none"]; return; }

            BatWear = $"{b.WearPercent}%";
            BatDesign = Wh(b.DesignMwh);
            BatFull = Wh(b.FullMwh);
            BatCycles = b.CycleCount > 0 ? b.CycleCount.ToString() : Loc.I["bat_cyc_na"];

            // паспорт батареи: производитель · модель · химия (+ серийный номер отдельно)
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(b.Manufacturer)) parts.Add(b.Manufacturer);
            if (!string.IsNullOrWhiteSpace(b.Name)) parts.Add(b.Name);
            if (!string.IsNullOrWhiteSpace(b.Chemistry)) parts.Add(b.Chemistry);
            BatDevice = string.Join("   ·   ", parts);
            BatSerial = string.IsNullOrWhiteSpace(b.Serial) ? "" : string.Format(Loc.I["bat_serial"], b.Serial);
            BatHasDevice = parts.Count > 0 || BatSerial.Length > 0;

            // вердикт по износу: <=15 отлично (зел), <=30 норма (зол), иначе износ (красн)
            int w = b.WearPercent;
            if (w <= 15) { BatVerdict = Loc.I["bat_health_great"]; BatWearBrush = FrozenBrush(0x3D, 0xD6, 0x8C); }
            else if (w <= 30) { BatVerdict = Loc.I["bat_health_ok"]; BatWearBrush = FrozenBrush(0xDD, 0xB4, 0x4B); }
            else { BatVerdict = Loc.I["bat_health_bad"]; BatWearBrush = FrozenBrush(0xE5, 0x48, 0x4D); }
            BatteryNote = "";

            // текущий заряд + состояние питания
            var ps = Native.GetPowerStatus();
            if (ps.ok && ps.percent >= 0)
            {
                BatChargePercent = ps.percent;
                BatCharge = $"{ps.percent}%";
                BatState = ps.charging ? Loc.I["bat_st_charging"]
                    : ps.onAc ? Loc.I["bat_st_ac"] : Loc.I["bat_st_batt"];
                BatChargeBrush = ps.percent <= 15 ? FrozenBrush(0xE5, 0x48, 0x4D)
                    : ps.percent <= 40 ? FrozenBrush(0xDD, 0xB4, 0x4B) : FrozenBrush(0x3D, 0xD6, 0x8C);
                BatHasCharge = true;
            }
            else BatHasCharge = false;
        }
        catch { BatteryNote = Loc.I["bat_none"]; BatteryPresent = false; }
        finally { BatteryBusy = false; }
    }

    // ---------- Диагностика: время загрузки ----------
    private bool _bootHasSlow;
    public bool BootHasSlow { get => _bootHasSlow; private set => Set(ref _bootHasSlow, value); }

    private string _bootNote = "";
    public string BootNote { get => _bootNote; private set => Set(ref _bootNote, value); }

    private async Task LoadBootAsync()
    {
        BootNote = Loc.I["boot_reading"];
        BootSlow.Clear();
        BootHasSlow = false;
        try
        {
            var b = await BootService.Analyze();
            if (!b.Available) { BootNote = Loc.I["boot_none"]; return; }

            BootNote = b.BootSeconds > 0
                ? string.Format(Loc.I["boot_time"], b.BootSeconds)
                : Loc.I["boot_slow_head"];

            foreach (var s in b.Slow)
                BootSlow.Add(new BootSlowVm
                {
                    Name = s.Name,
                    Info = $"{s.Seconds} {Loc.I["u_sec"]} · {Loc.I["boot_kind_" + s.Kind]}",
                });
            BootHasSlow = BootSlow.Count > 0;
            if (!BootHasSlow && b.BootSeconds > 0) BootNote += "  " + Loc.I["boot_noslow"];
        }
        catch { BootNote = Loc.I["boot_none"]; }
    }

    // ---------- Паспорт ПК (HTML-отчёт) ----------
    private string _reportNote = "";
    public string ReportNote { get => _reportNote; private set => Set(ref _reportNote, value); }

    private void ExportReport()
    {
        try
        {
            // выбор места сохранения
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NakClean");
            try { System.IO.Directory.CreateDirectory(dir); } catch { }
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = Loc.I["rep_title"],
                FileName = $"NakClean-report-{DateTime.Now:yyyyMMdd-HHmm}.html",
                DefaultExt = ".html",
                Filter = "HTML (*.html)|*.html",
                InitialDirectory = dir,
            };
            if (dlg.ShowDialog() != true) return;

            static string E(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
            var sb = new System.Text.StringBuilder();
            sb.Append("<!doctype html><html><head><meta charset='utf-8'><title>NakClean</title><style>");
            sb.Append("body{background:#141418;color:#ededed;font-family:'Segoe UI',Arial,sans-serif;margin:0;padding:34px}");
            sb.Append("h1{color:#ddb44b;margin:0 0 4px}h2{color:#ddb44b;border-bottom:1px solid #2a2a32;padding-bottom:6px;margin:30px 0 10px;font-size:18px}");
            sb.Append("table{border-collapse:collapse;width:100%;max-width:920px}td{padding:7px 10px;border-bottom:1px solid #24242c;vertical-align:top}");
            sb.Append("td.k{color:#9a9aa0;width:260px}.muted{color:#8a8a94;font-size:13px}.b{color:#ddb44b;font-weight:600}</style></head><body>");
            sb.Append($"<h1>NakClean — {E(Loc.I["rep_title"])}</h1>");
            sb.Append($"<div class='muted'>{E(Loc.I["rep_generated"])}: {DateTime.Now:yyyy-MM-dd HH:mm}</div>");

            sb.Append($"<h2>{E(Loc.I["rep_os"])}</h2><table>");
            Row(sb, Loc.I["ho_t"], System.Runtime.InteropServices.RuntimeInformation.OSDescription);
            sb.Append("</table>");

            sb.Append($"<h2>{E(Loc.I["tile_cpu"])}</h2><table>");
            Row(sb, CpuName, CpuSpec);
            sb.Append("</table>");

            sb.Append($"<h2>{E(Loc.I["tile_ram"])}</h2><table>");
            Row(sb, RamSpec, RamDetail);
            sb.Append("</table>");

            if (Gpus.Count > 0)
            {
                sb.Append($"<h2>{E(Loc.I["tile_gpu"])}</h2><table>");
                foreach (var g in Gpus) Row(sb, g.Name, $"{g.Detail}  ·  {g.TempText}");
                sb.Append("</table>");
            }

            if (DiskHealth.Count > 0)
            {
                sb.Append($"<h2>{E(Loc.I["nav_health"])}</h2><table>");
                foreach (var d in DiskHealth)
                    Row(sb, $"{d.Name} ({d.TypeBadge})",
                        $"{d.StateText} · {d.SizeText} · {Loc.I["dh_temp"]}: {d.TempText} · {Loc.I["dh_hours"]}: {d.PowerOnText} · {Loc.I["dh_wear"]}: {d.WearText}");
                sb.Append("</table>");
            }

            if (BatteryPresent)
            {
                sb.Append($"<h2>{E(Loc.I["bat_title"])}</h2><table>");
                Row(sb, Loc.I["bat_wear"], BatWear);
                Row(sb, Loc.I["bat_cycles"], BatCycles);
                Row(sb, Loc.I["bat_design"], BatDesign);
                Row(sb, Loc.I["bat_full"], BatFull);
                sb.Append("</table>");
            }

            sb.Append($"<p class='muted' style='margin-top:34px'>© 2026 nak (github.com/nakv1) · NakClean</p></body></html>");

            string file = dlg.FileName;
            System.IO.File.WriteAllText(file, sb.ToString(), System.Text.Encoding.UTF8);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = true });
            ReportNote = string.Format(Loc.I["rep_saved"], file);
        }
        catch (Exception ex) { ReportNote = string.Format(Loc.I["err"], ex.Message); }

        static void Row(System.Text.StringBuilder sb, string? k, string? v)
            => sb.Append($"<tr><td class='k'>{System.Net.WebUtility.HtmlEncode(k ?? "")}</td><td class='b'>{System.Net.WebUtility.HtmlEncode(v ?? "")}</td></tr>");
    }

    // ---------- О программе ----------
    public string AppName => "NakClean";
    public string AppTagline => Loc.I["about_tagline"];
    public string AppVersionText => string.Format(Loc.I["about_version_fmt"], Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");
    public string AppVersionShort => "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");
    public string AppAuthor => Loc.I["about_author"];

    // ---------- Живая статистика ----------
    private double _cpuPercent;
    public double CpuPercent { get => _cpuPercent; private set => Set(ref _cpuPercent, value); }

    private double _ramPercent;
    public double RamPercent { get => _ramPercent; private set => Set(ref _ramPercent, value); }

    private string _ramDetail = "";
    public string RamDetail { get => _ramDetail; private set => Set(ref _ramDetail, value); }

    private string _cpuName = Loc.I["tile_cpu"];
    public string CpuName { get => _cpuName; private set => Set(ref _cpuName, value); }

    private string _cpuSpec = "";
    public string CpuSpec { get => _cpuSpec; private set => Set(ref _cpuSpec, value); }

    private string _ramSpec = "";
    public string RamSpec { get => _ramSpec; private set => Set(ref _ramSpec, value); }

    private CpuInfo? _cpuInfo;
    private RamInfo? _ramInfo;

    private async Task LoadSystemInfoAsync()
    {
        _cpuInfo = await Task.Run(SystemInfoService.GetCpu);
        _ramInfo = await Task.Run(SystemInfoService.GetRam);
        FormatSystemSpecs();
    }

    private void FormatSystemSpecs()
    {
        if (_cpuInfo is { } c)
        {
            CpuName = c.Name;
            string cores = $"{c.Cores} {Loc.I.Plural(c.Cores, "w_cores")}";
            string threads = $"{c.Threads} {Loc.I.Plural(c.Threads, "w_threads")}";
            CpuSpec = c.BaseGhz > 0 ? $"{cores} · {threads} · {c.BaseGhz:0.0} {Loc.I["u_ghz"]}" : $"{cores} · {threads}";
        }
        if (_ramInfo is { Count: > 0 } r)
        {
            string gb = Loc.I["u_gb"];
            string cap = r.Same
                ? $"{r.Count}×{r.EachGib} {gb}"
                : $"{r.TotalGib} {gb} ({r.Count} {Loc.I.Plural(r.Count, "w_modules")})";
            var parts = new List<string>();
            if (r.Type.Length > 0) parts.Add(r.Type);
            parts.Add(cap);
            if (r.Speed > 0) parts.Add($"{r.Speed} {Loc.I["u_mhz"]}");
            RamSpec = string.Join(" · ", parts);
        }
    }

    private void OnLanguageChanged()
    {
        FormatSystemSpecs();
        UpdateTileValues();
        RebuildDiskHealthVms();

        // оптимизация: перечитать строки без обращения к WMI/реестру (мгновенно)
        foreach (var t in Tweaks) t.RaiseLocalized();
        TweaksView.Refresh();           // перегруппировать по новым названиям уровней
        OptNote = Loc.I["opt_note"];

        // очистка: перечитать названия/описания категорий
        foreach (var c in Categories) c.RaiseLocalized();
        CategoriesView.Refresh();       // перегруппировать (Система/Браузеры)

        // реестр: галочки категорий
        foreach (var r in RegOptions) r.RaiseLocalized();

        // обслуживание: названия/описания карточек
        foreach (var m in MaintenanceTasks) m.RaiseLocalized();

        // запуск: колонка «Вкл» (Да/Нет), задачи, службы, контекстное меню
        foreach (var s in StartupEntries) s.RaiseLocalized();
        foreach (var t in Tasks) t.RaiseLocalized();
        foreach (var s in Services) s.Refresh();
        foreach (var c in ContextItems) c.RaiseLocalized();
        OnPropertyChanged(nameof(SessionFreedLine));
        ReloadChangeLog();   // журнал — перечитать локализованные имена

        // проверка ПК: вердикт + перезапуск проверки на новом языке (фон)
        OnPropertyChanged(nameof(ScoreVerdict));
        _ = RunHealthCheckAsync();

        // о программе
        OnPropertyChanged(nameof(AppTagline));
        OnPropertyChanged(nameof(AppVersionText));
        OnPropertyChanged(nameof(AppAuthor));

        // подписи-подсказки по умолчанию
        Status = Loc.I["note_status"];
        RegNote = Loc.I["note_reg"];
        ScanStatus = Loc.I["note_scan"];
        StartupNote = Loc.I["su_default"];
        AppsNote = Loc.I["ap_default"];
        TasksNote = Loc.I["tk_default"];
        ServicesNote = Loc.I["sv_default"];
        ContextNote = Loc.I["cx_default"];
        OnPropertyChanged(nameof(TargetDisplay));
        OnPropertyChanged(nameof(MapTitle));
        LoadDrives();                   // пересобрать подписи дисков на новом языке
    }

    public ObservableCollection<GpuVm> Gpus { get; } = new();
    public ObservableCollection<TileVm> Tiles { get; } = new();
    private bool _gpuBusy;
    private int _gpuTick;

    private OsInfo? _os;
    private string _overviewSummary = "";
    public string OverviewSummary { get => _overviewSummary; private set => Set(ref _overviewSummary, value); }

    private void UpdateOverviewSummary()
    {
        _os ??= SystemInfoService.GetOs();
        var up = DateTime.Now - _os.BootTime;
        string uptime = up.TotalDays >= 1
            ? string.Format(Loc.I["hu_vd"], (int)up.TotalDays, up.Hours)
            : string.Format(Loc.I["hu_vh"], up.Hours, up.Minutes);
        string osLine = _os.Build.Length > 0 ? string.Format(Loc.I["ov_os"], _os.Caption, _os.Build) : _os.Caption;
        OverviewSummary = $"{osLine}     ·     {Environment.MachineName}     ·     {string.Format(Loc.I["ov_uptime"], uptime)}";
    }

    private void UpdateLiveStats()
    {
        var (total, used, ramPct) = Native.GetMemory();
        CpuPercent = Native.GetCpuUsage();
        RamPercent = ramPct;
        RamDetail = $"{Format.Bytes((long)used)} / {Format.Bytes((long)total)}";
        UpdateOverviewSummary();
        UpdateTileValues();

        // GPU тяжелее (WMI + nvidia-smi) - обновляем реже и в фоне
        if (_gpuTick++ % 3 == 0) _ = RefreshGpuAsync();
    }

    private async Task RefreshGpuAsync()
    {
        if (_gpuBusy) return;
        _gpuBusy = true;
        try
        {
            var stats = await Task.Run(GpuService.Read);
            if (stats.Count != Gpus.Count)
            {
                Gpus.Clear();
                foreach (var s in stats) Gpus.Add(new GpuVm(s));
                RebuildTiles();              // изменилось число плиток
            }
            else
            {
                for (int i = 0; i < stats.Count; i++) Gpus[i].Update(stats[i]);
                UpdateTileValues();
            }
        }
        catch { /* GPU-данные не критичны */ }
        finally { _gpuBusy = false; }
    }

    // ---------- единая сетка плиток «Обзора» ----------
    private void RebuildTiles()
    {
        Tiles.Clear();

        Tiles.Add(new TileVm("", "")
        { Refresh = t => { t.Title = Loc.I["tile_cpu"]; t.Caption = Loc.I["cap_load"]; t.Percent = CpuPercent; t.Line1 = CpuName; t.Line2 = CpuSpec; } });

        Tiles.Add(new TileVm("", "")
        { Refresh = t => { t.Title = Loc.I["tile_ram"]; t.Caption = Loc.I["cap_used"]; t.Percent = RamPercent; t.Line1 = RamDetail; t.Line2 = RamSpec; } });

        foreach (var g in Gpus)
        {
            var gpu = g;
            Tiles.Add(new TileVm("", "")
            {
                Refresh = t =>
                {
                    t.Title = Loc.I["tile_gpu"];
                    t.Caption = Loc.I["cap_load"];
                    t.Line1 = gpu.Name;
                    t.Percent = gpu.UsagePercent;
                    t.Line2 = gpu.Detail.Length > 0 ? $"🌡 {gpu.TempText}  ·  {gpu.Detail}" : $"🌡 {gpu.TempText}";
                }
            });
        }

        foreach (var d in Disks)
        {
            var disk = d;
            Tiles.Add(new TileVm("", "")
            { Refresh = t => { t.Title = $"{Loc.I["tile_disk"]} {disk.Name}"; t.Caption = Loc.I["cap_used"]; t.Percent = disk.Percent; t.Line1 = disk.Detail; } });
        }

        // батарея - только если она есть (ноутбук)
        var pwr = Native.GetPowerStatus();
        if (pwr.ok && pwr.percent >= 0)
        {
            Tiles.Add(new TileVm("", "")
            {
                Refresh = t =>
                {
                    var ps = Native.GetPowerStatus();
                    int pct = ps.percent < 0 ? 0 : ps.percent;
                    t.Title = Loc.I["tile_battery"];
                    t.Caption = Loc.I["bat_charge"];
                    t.Percent = pct;
                    t.Line1 = ps.charging ? Loc.I["bat_st_charging"] : ps.onAc ? Loc.I["bat_st_ac"] : Loc.I["bat_st_batt"];
                    t.Line2 = BatteryPresent ? $"{Loc.I["bat_wear"]} {BatWear}" : "";
                    // мало заряда = плохо (обратная логика)
                    t.AccentOverride = pct <= 15 ? FrozenBrush(0xE5, 0x48, 0x4D)
                        : pct <= 40 ? FrozenBrush(0xDD, 0xB4, 0x4B) : FrozenBrush(0x3D, 0xD6, 0x8C);
                }
            });
        }

        UpdateTileValues();
    }

    private void UpdateTileValues()
    {
        foreach (var t in Tiles) t.Refresh?.Invoke(t);
    }

    private void RefreshDisks()
    {
        var stats = SystemStatsService.GetDisks();
        Disks.Clear();
        foreach (var s in stats)
            Disks.Add(new DiskVm(s));
        RebuildTiles();
        UpdateLiveStats();
    }

    // ---------- Здоровье дисков (S.M.A.R.T.) ----------
    private string _healthNote = "…";
    public string HealthNote { get => _healthNote; private set => Set(ref _healthNote, value); }

    private List<Models.DiskHealth> _diskModels = new();

    private async Task LoadDiskHealthAsync()
    {
        try
        {
            _diskModels = await Task.Run(DiskHealthService.GetDisks);
        }
        catch
        {
            _diskModels = new();
        }
        RebuildDiskHealthVms();
    }

    private void RebuildDiskHealthVms()
    {
        DiskHealth.Clear();
        foreach (var d in _diskModels)
            DiskHealth.Add(new DiskHealthVm(d));

        HealthNote = _diskModels.Count == 0
            ? Loc.I["dh_note_empty"]
            : string.Format(Loc.I["dh_note"], _diskModels.Count);
    }

    // ---------- Проверка состояния ПК ----------
    private int _score;
    public int Score { get => _score; private set { if (Set(ref _score, value)) { OnPropertyChanged(nameof(ScoreText)); OnPropertyChanged(nameof(ScoreVerdict)); OnPropertyChanged(nameof(ScoreBrush)); } } }
    public string ScoreText => $"{Score}";
    public string ScoreVerdict => Score >= 85 ? Loc.I["verdict_great"]
        : Score >= 65 ? Loc.I["verdict_good"]
        : Score >= 40 ? Loc.I["verdict_attention"] : Loc.I["verdict_bad"];
    public Brush ScoreBrush => Score >= 85
        ? new SolidColorBrush(Color.FromRgb(0x3D, 0xD6, 0x8C))
        : Score >= 50
            ? new SolidColorBrush(Color.FromRgb(0xDD, 0xB4, 0x4B))
            : new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D));

    private bool _healthChecking;
    public bool HealthChecking { get => _healthChecking; private set => Set(ref _healthChecking, value); }

    // Сводка по уровням (под кругом-баллом)
    private int _healthBad, _healthWarn, _healthOk;
    public int HealthBad { get => _healthBad; private set => Set(ref _healthBad, value); }
    public int HealthWarn { get => _healthWarn; private set => Set(ref _healthWarn, value); }
    public int HealthOk { get => _healthOk; private set => Set(ref _healthOk, value); }

    private string _healthCheckNote = Loc.I["note_healthcheck"];
    public string HealthCheckNote { get => _healthCheckNote; private set => Set(ref _healthCheckNote, value); }

    private async Task RunHealthCheckAsync()
    {
        HealthChecking = true;
        HealthCheckNote = Loc.I["note_healthcheck"];
        try
        {
            var (score, items) = await Task.Run(HealthCheckService.Run);
            Score = score;
            HealthBad = items.Count(i => i.Level == CheckLevel.Bad);
            HealthWarn = items.Count(i => i.Level == CheckLevel.Warning);
            HealthOk = items.Count(i => i.Level is CheckLevel.Good or CheckLevel.Info);

            static int Rank(CheckLevel l) => l switch
            {
                CheckLevel.Bad => 0, CheckLevel.Warning => 1, CheckLevel.Good => 2, _ => 3,
            };
            string Glyph(string cat) =>
                cat == Loc.I["hc_mem"] ? "🧠"
                : cat == Loc.I["hc_disks"] ? "💽"
                : cat == Loc.I["hc_sec"] ? "🛡"
                : cat == Loc.I["hc_hw"] ? "🔧"
                : cat == Loc.I["hc_sys"] ? "💻" : "•";

            // секции по категориям: категории с проблемами выше, внутри секции - проблемы выше
            var sections = items
                .GroupBy(i => i.Category)
                .Select(g =>
                {
                    var sec = new HealthSection { Title = g.Key, Glyph = Glyph(g.Key) };
                    foreach (var it in g.OrderBy(i => Rank(i.Level))) sec.Items.Add(it);
                    return (sec, worst: g.Min(i => Rank(i.Level)), count: g.Count());
                })
                .OrderBy(t => t.worst).ThenByDescending(t => t.count)
                .Select(t => t.sec)
                .ToList();

            // раскладка по двум колонкам: каждую секцию - в колонку с меньшим числом пунктов
            Health.Clear(); HealthColLeft.Clear(); HealthColRight.Clear();
            int leftCount = 0, rightCount = 0;
            foreach (var sec in sections)
            {
                foreach (var it in sec.Items) Health.Add(it);
                if (leftCount <= rightCount) { HealthColLeft.Add(sec); leftCount += sec.Items.Count; }
                else { HealthColRight.Add(sec); rightCount += sec.Items.Count; }
            }
            int problems = HealthBad + HealthWarn;
            HealthCheckNote = problems == 0
                ? Loc.I["hc_ok"]
                : string.Format(Loc.I["hc_found"], problems);
        }
        catch (Exception ex)
        {
            HealthCheckNote = string.Format(Loc.I["hc_err"], ex.Message);
        }
        finally { HealthChecking = false; }
    }

    // ---------- Автозагрузка ----------
    private StartupEntryVm? _selectedStartup;
    public StartupEntryVm? SelectedStartup
    {
        get => _selectedStartup;
        set
        {
            if (!Set(ref _selectedStartup, value)) return;
            EnableStartupCommand.RaiseCanExecuteChanged();
            DisableStartupCommand.RaiseCanExecuteChanged();
            DeleteStartupCommand.RaiseCanExecuteChanged();
        }
    }

    private string _startupNote = Loc.I["su_default"];
    public string StartupNote { get => _startupNote; private set => Set(ref _startupNote, value); }

    private async Task LoadStartupAsync()
    {
        List<Models.StartupEntry> entries;
        try { entries = await Task.Run(StartupService.GetEntries); }
        catch (Exception ex) { StartupNote = string.Format(Loc.I["err"], ex.Message); return; }

        StartupEntries.Clear();
        foreach (var e in entries) StartupEntries.Add(new StartupEntryVm(e));
        SelectedStartup = null;
        int on = StartupEntries.Count(x => x.Enabled);
        StartupNote = string.Format(Loc.I["su_count"], StartupEntries.Count, on);
    }

    private void ToggleStartup(bool enable)
    {
        var sel = SelectedStartup;
        if (sel is null) return;
        bool ok = StartupService.SetEnabled(sel.Entry, enable);
        if (ok)
        {
            sel.Enabled = enable;
            StartupNote = string.Format(Loc.I[enable ? "su_on" : "su_off"], sel.Name);
        }
        else
        {
            StartupNote = string.Format(Loc.I[sel.NeedsAdmin ? "su_fail_admin" : "su_fail"], sel.Name);
        }
    }

    private void DeleteStartup()
    {
        var sel = SelectedStartup;
        if (sel is null) return;
        if (System.Windows.MessageBox.Show(
                string.Format(Loc.I["su_del_q"], sel.Name),
                "NakClean", System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.Yes)
            return;

        bool ok = StartupService.Delete(sel.Entry);
        if (ok) { StartupEntries.Remove(sel); StartupNote = string.Format(Loc.I["su_deleted"], sel.Name); }
        else StartupNote = string.Format(Loc.I[sel.NeedsAdmin ? "su_del_fail_admin" : "su_del_fail"], sel.Name);
    }

    // ---------- Удаление программ ----------
    private InstalledAppVm? _selectedApp;
    public InstalledAppVm? SelectedApp
    {
        get => _selectedApp;
        set
        {
            if (!Set(ref _selectedApp, value)) return;
            UninstallCommand.RaiseCanExecuteChanged();
            RepairCommand.RaiseCanExecuteChanged();
            RenameCommand.RaiseCanExecuteChanged();
            RemoveEntryCommand.RaiseCanExecuteChanged();
        }
    }

    private string _appSortKey = "name";
    private bool _appSortAsc = true;

    private void SortApps(string key)
    {
        if (_appSortKey == key) _appSortAsc = !_appSortAsc;
        else { _appSortKey = key; _appSortAsc = true; }

        string prop = key switch
        {
            "pub" => nameof(InstalledAppVm.Publisher),
            "date" => nameof(InstalledAppVm.SortDate),
            "size" => nameof(InstalledAppVm.SortSize),
            "ver" => nameof(InstalledAppVm.Version),
            _ => nameof(InstalledAppVm.Name),
        };
        var dir = _appSortAsc ? ListSortDirection.Ascending : ListSortDirection.Descending;
        AppsView.SortDescriptions.Clear();
        AppsView.SortDescriptions.Add(new SortDescription(prop, dir));
        AppsView.Refresh();
    }

    private void Repair()
    {
        var sel = SelectedApp;
        if (sel is null || !sel.CanRepair) return;
        AppsNote = string.Format(Loc.I[InstalledAppsService.Repair(sel.App) ? "ap_repair_ok" : "ap_repair_no"], sel.Name);
    }

    private void RenameApp()
    {
        var sel = SelectedApp;
        if (sel is null) return;
        string newName = Microsoft.VisualBasic.Interaction.InputBox(
            Loc.I["ap_rename_prompt"], Loc.I["un_rename"], sel.Name);
        if (string.IsNullOrWhiteSpace(newName) || newName == sel.Name) return;

        if (InstalledAppsService.Rename(sel.App, newName.Trim()))
        {
            sel.RefreshName();
            AppsView.Refresh();
            AppsNote = Loc.I["ap_renamed"];
        }
        else AppsNote = Loc.I["ap_rename_fail"];
    }

    private void RemoveAppEntry()
    {
        var sel = SelectedApp;
        if (sel is null) return;
        if (System.Windows.MessageBox.Show(
                string.Format(Loc.I["ap_remove_q"], sel.Name),
                "NakClean", System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.Yes)
            return;

        if (InstalledAppsService.RemoveEntry(sel.App))
        {
            Apps.Remove(sel);
            AppsView.Refresh();
            AppsNote = Loc.I["ap_removed"];
        }
        else AppsNote = Loc.I["ap_remove_fail"];
    }

    private string _appSearch = "";
    public string AppSearch
    {
        get => _appSearch;
        set { if (Set(ref _appSearch, value)) AppsView.Refresh(); }
    }

    private string _appsNote = Loc.I["ap_default"];
    public string AppsNote { get => _appsNote; private set => Set(ref _appsNote, value); }

    private bool AppFilter(object o)
    {
        if (string.IsNullOrWhiteSpace(AppSearch)) return true;
        var a = (InstalledAppVm)o;
        return a.Name.Contains(AppSearch, StringComparison.OrdinalIgnoreCase)
            || a.Publisher.Contains(AppSearch, StringComparison.OrdinalIgnoreCase);
    }

    private async Task LoadAppsAsync()
    {
        List<Models.InstalledApp> apps;
        try { apps = await Task.Run(InstalledAppsService.GetApps); }
        catch (Exception ex) { AppsNote = string.Format(Loc.I["err"], ex.Message); return; }

        Apps.Clear();
        foreach (var a in apps) Apps.Add(new InstalledAppVm(a));
        AppsView.Refresh();
        AppsNote = string.Format(Loc.I["ap_count"], Apps.Count);
    }

    private void Uninstall()
    {
        var sel = SelectedApp;
        if (sel is null || !sel.CanUninstall) return;
        if (System.Windows.MessageBox.Show(
                string.Format(Loc.I["ap_uninst_q"], sel.Name),
                "NakClean", System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.Yes)
            return;

        bool ok = InstalledAppsService.LaunchUninstall(sel.App);
        AppsNote = string.Format(Loc.I[ok ? "ap_uninst_ok" : "ap_uninst_fail"], sel.Name);
    }

    // ---------- Реестр ----------
    private bool _regBusy;
    public bool RegBusy
    {
        get => _regBusy;
        private set
        {
            if (!Set(ref _regBusy, value)) return;
            ScanRegistryCommand.RaiseCanExecuteChanged();
            FixRegistryCommand.RaiseCanExecuteChanged();
        }
    }

    private string _regNote = Loc.I["note_reg"];
    public string RegNote { get => _regNote; private set => Set(ref _regNote, value); }

    private async Task ScanRegistryAsync()
    {
        var ids = RegOptions.Where(o => o.Selected).Select(o => o.Id).ToHashSet();
        if (ids.Count == 0) { RegNote = Loc.I["reg_nocat"]; return; }

        RegBusy = true;
        RegNote = Loc.I["reg_scanning"];
        try
        {
            var found = await Task.Run(() => RegistryScanService.Scan(ids));
            RegIssues.Clear();
            foreach (var i in found) RegIssues.Add(new RegistryIssueVm(i));
            RegNote = found.Count == 0
                ? Loc.I["reg_clean"]
                : string.Format(Loc.I["reg_found"], found.Count);
        }
        catch (Exception ex) { RegNote = string.Format(Loc.I["reg_scan_err"], ex.Message); }
        finally
        {
            RegBusy = false;
            FixRegistryCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task FixRegistryAsync()
    {
        var selected = RegIssues.Where(i => i.Selected).ToList();
        if (selected.Count == 0) { RegNote = Loc.I["reg_nofix"]; return; }

        if (System.Windows.MessageBox.Show(
                string.Format(Loc.I["reg_fix_q"], selected.Count),
                Loc.I["reg_fix_title"],
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes)
            return;

        RegBusy = true;
        RegNote = Loc.I["reg_fixing"];
        var issues = selected.Select(s => s.Issue).ToList();
        try
        {
            var (backup, del, fail) = await Task.Run(() =>
            {
                string b = RegistryFixService.Backup(issues);
                var (d, f) = RegistryFixService.Delete(issues);
                return (b, d, f);
            });

            foreach (var vm in selected) RegIssues.Remove(vm);
            RegNote = string.Format(Loc.I["reg_removed"], del)
                      + (fail > 0 ? string.Format(Loc.I["reg_failed"], fail) : "")
                      + Loc.I["reg_backup_saved"];

            System.Windows.MessageBox.Show(
                string.Format(Loc.I["reg_done_box"], del, fail, backup),
                "NakClean", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
        catch (Exception ex) { RegNote = string.Format(Loc.I["reg_fix_err"], ex.Message); }
        finally
        {
            RegBusy = false;
            FixRegistryCommand.RaiseCanExecuteChanged();
        }
    }

    // ---------- Запланированные задачи ----------
    private TaskEntryVm? _selectedTask;
    public TaskEntryVm? SelectedTask { get => _selectedTask; set => Set(ref _selectedTask, value); }

    private string _tasksNote = Loc.I["tk_default"];
    public string TasksNote { get => _tasksNote; private set => Set(ref _tasksNote, value); }

    private async Task LoadTasksAsync()
    {
        List<Services.ScheduledTaskEntry> tasks;
        try { tasks = await Task.Run(ScheduledTaskService.GetTasks); }
        catch (Exception ex) { TasksNote = string.Format(Loc.I["err"], ex.Message); return; }
        Tasks.Clear();
        foreach (var t in tasks) Tasks.Add(new TaskEntryVm(t));
        SelectedTask = null;
        TasksNote = string.Format(Loc.I["tk_count"], Tasks.Count, Tasks.Count(x => x.Enabled));
    }

    private void ToggleTask(bool enable)
    {
        var sel = SelectedTask;
        if (sel is null) return;
        if (ScheduledTaskService.SetEnabled(sel.Path, enable))
        {
            sel.Enabled = enable;
            TasksNote = string.Format(Loc.I[enable ? "tk_on" : "tk_off"], sel.Name);
        }
        else TasksNote = string.Format(Loc.I["tk_fail"], sel.Name);
    }

    private void DeleteTask()
    {
        var sel = SelectedTask;
        if (sel is null) return;
        if (System.Windows.MessageBox.Show(
                string.Format(Loc.I["tk_del_q"], sel.Name), "NakClean",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question)
            != System.Windows.MessageBoxResult.Yes) return;
        if (ScheduledTaskService.Delete(sel.Path)) { Tasks.Remove(sel); TasksNote = Loc.I["tk_deleted"]; }
        else TasksNote = Loc.I["tk_del_fail"];
    }

    // ---------- Службы Windows ----------
    private ServiceEntryVm? _selectedService;
    public ServiceEntryVm? SelectedService { get => _selectedService; set => Set(ref _selectedService, value); }

    private string _servicesSearch = "";
    public string ServicesSearch
    {
        get => _servicesSearch;
        set { if (Set(ref _servicesSearch, value)) ServicesView.Refresh(); }
    }

    private string _servicesNote = Loc.I["sv_default"];
    public string ServicesNote { get => _servicesNote; private set => Set(ref _servicesNote, value); }

    private bool ServiceFilter(object o)
    {
        if (string.IsNullOrWhiteSpace(ServicesSearch)) return true;
        var s = (ServiceEntryVm)o;
        return s.DisplayName.Contains(ServicesSearch, StringComparison.OrdinalIgnoreCase)
            || s.Name.Contains(ServicesSearch, StringComparison.OrdinalIgnoreCase);
    }

    private async Task LoadServicesAsync()
    {
        List<Services.ServiceEntry> svcs;
        try { svcs = await Task.Run(ServicesService.GetServices); }
        catch (Exception ex) { ServicesNote = string.Format(Loc.I["err"], ex.Message); return; }
        Services.Clear();
        foreach (var s in svcs) Services.Add(new ServiceEntryVm(s));
        ServicesView.Refresh();
        ServicesNote = string.Format(Loc.I["sv_count"], Services.Count);
    }

    private async void ToggleService(bool enable)
    {
        var sel = SelectedService;
        if (sel is null) return;
        if (System.Windows.MessageBox.Show(
                string.Format(Loc.I[enable ? "sv_enable_q" : "sv_disable_q"], sel.DisplayName),
                Loc.I["sv_title"], System.Windows.MessageBoxButton.YesNo,
                enable ? System.Windows.MessageBoxImage.Question : System.Windows.MessageBoxImage.Warning)
            != System.Windows.MessageBoxResult.Yes) return;

        ServicesNote = Loc.I["sv_applying"];
        bool ok = await Task.Run(() => enable ? ServicesService.Enable(sel.Entry) : ServicesService.Disable(sel.Entry));

        // перечитываем НАСТОЯЩЕЕ состояние из системы - без догадок
        var fresh = await Task.Run(() => ServicesService.QueryState(sel.Name));
        if (fresh is { } f)
        {
            sel.Entry.State = f.state;
            sel.Entry.StartMode = f.startMode;
            sel.Refresh();
        }

        if (!ok)
        {
            ServicesNote = string.Format(Loc.I["sv_fail"], sel.DisplayName);
            return;
        }

        bool running = sel.Entry.State.Equals("Running", StringComparison.OrdinalIgnoreCase);
        ServicesNote = string.Format(Loc.I[enable
            ? (running ? "sv_on_run" : "sv_on_notrun")
            : (running ? "sv_off_run" : "sv_off_stop")], sel.DisplayName);
    }

    // ---------- Контекстное меню ----------
    private ContextMenuEntryVm? _selectedContext;
    public ContextMenuEntryVm? SelectedContext { get => _selectedContext; set => Set(ref _selectedContext, value); }

    private string _contextNote = Loc.I["cx_default"];
    public string ContextNote { get => _contextNote; private set => Set(ref _contextNote, value); }

    private async Task LoadContextAsync()
    {
        List<Services.ContextMenuEntry> items;
        try { items = await Task.Run(ContextMenuService.GetEntries); }
        catch (Exception ex) { ContextNote = string.Format(Loc.I["err"], ex.Message); return; }
        ContextItems.Clear();
        foreach (var i in items) ContextItems.Add(new ContextMenuEntryVm(i));
        SelectedContext = null;
        ContextNote = string.Format(Loc.I["cx_count"], ContextItems.Count, ContextItems.Count(x => x.Enabled));
    }

    private void ToggleContext(bool enable)
    {
        var sel = SelectedContext;
        if (sel is null) return;
        if (ContextMenuService.SetEnabled(sel.Entry, enable))
        {
            sel.Enabled = enable;
            ContextNote = string.Format(Loc.I[enable ? "cx_on" : "cx_off"], sel.DisplayName);
        }
        else ContextNote = string.Format(Loc.I["cx_fail"], sel.DisplayName);
    }

    private void DeleteContext()
    {
        var sel = SelectedContext;
        if (sel is null) return;
        if (System.Windows.MessageBox.Show(
                string.Format(Loc.I["cx_del_q"], sel.DisplayName),
                "NakClean", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question)
            != System.Windows.MessageBoxResult.Yes) return;
        if (ContextMenuService.Delete(sel.Entry)) { ContextItems.Remove(sel); ContextNote = Loc.I["cx_deleted"]; }
        else ContextNote = Loc.I["cx_del_fail"];
    }

    // ---------- Оптимизация ----------
    private bool _optBusy;
    public bool OptBusy
    {
        get => _optBusy;
        private set { if (Set(ref _optBusy, value)) { ApplyOptCommand.RaiseCanExecuteChanged(); RevertOptCommand.RaiseCanExecuteChanged(); RevertAllCommand.RaiseCanExecuteChanged(); } }
    }

    private bool _createRestore = true;
    public bool CreateRestore { get => _createRestore; set => Set(ref _createRestore, value); }

    private string _optNote = Loc.I["opt_note"];
    public string OptNote { get => _optNote; private set => Set(ref _optNote, value); }

    private async Task ApplyOptAsync(bool apply)
    {
        var sel = Tweaks.Where(t => t.Selected).ToList();
        if (sel.Count == 0) { OptNote = Loc.I["opt_none"]; return; }

        if (apply && sel.Any(t => t.Tier == OptimizationService.Max))
        {
            if (System.Windows.MessageBox.Show(
                    Loc.I["opt_max_q"],
                    Loc.I["opt_title"], System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes)
                return;
        }

        OptBusy = true;
        try
        {
            string restoreNote = "";
            if (apply && CreateRestore)
            {
                OptNote = Loc.I["opt_rp_creating"];
                bool rp = await Task.Run(() => OptimizationService.CreateRestorePoint(Loc.I["opt_rp_name"]));
                restoreNote = rp ? Loc.I["opt_rp_ok"] : Loc.I["opt_rp_fail"];
            }

            OptNote = Loc.I[apply ? "opt_applying" : "opt_reverting"];
            int done = 0;
            foreach (var t in sel)
            {
                bool ok = await Task.Run(() => apply ? t.Tweak.Apply() : t.Tweak.Revert());
                if (ok)
                {
                    done++;
                    // журнал изменений: применённые добавляем, откатанные убираем
                    if (apply) ChangeLogService.Add(t.Tweak.Id, t.Tweak.NameKey);
                    else ChangeLogService.Remove(t.Tweak.Id);
                }
                t.RefreshStatus();
            }
            ReloadChangeLog();

            OptNote = string.Format(Loc.I[apply ? "opt_applied_n" : "opt_reverted_n"], done, sel.Count)
                      + restoreNote + Loc.I["opt_after"];
        }
        catch (Exception ex) { OptNote = string.Format(Loc.I["err"], ex.Message); }
        finally { OptBusy = false; }
    }

    // ---------- Журнал изменений + откат всего ----------
    public bool HasChanges => ChangeLog.Count > 0;

    private string _changesNote = "";
    public string ChangesNote { get => _changesNote; private set => Set(ref _changesNote, value); }

    private void ReloadChangeLog()
    {
        ChangeLog.Clear();
        foreach (var e in ChangeLogService.Load()) ChangeLog.Add(new ChangeEntryVm(e));
        OnPropertyChanged(nameof(HasChanges));
        ChangesNote = string.Format(Loc.I["chg_count"], ChangeLog.Count);
        RevertAllCommand.RaiseCanExecuteChanged();
    }

    private async Task RevertAllAsync()
    {
        var entries = ChangeLogService.Load();
        if (entries.Count == 0) return;
        if (System.Windows.MessageBox.Show(
                string.Format(Loc.I["chg_confirm"], entries.Count), Loc.I["opt_title"],
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning)
            != System.Windows.MessageBoxResult.Yes) return;

        OptBusy = true;
        try
        {
            var byId = OptimizationService.BuildTweaks().ToDictionary(t => t.Id);
            int done = 0;
            await Task.Run(() =>
            {
                foreach (var e in entries)
                    if (byId.TryGetValue(e.Id, out var t) && t.Revert()) done++;
            });
            ChangeLogService.Clear();
            ReloadChangeLog();
            foreach (var t in Tweaks) t.RefreshStatus();
            OptNote = string.Format(Loc.I["chg_reverted"], done) + Loc.I["opt_after"];
        }
        catch (Exception ex) { OptNote = string.Format(Loc.I["err"], ex.Message); }
        finally { OptBusy = false; }
    }

    // ---------- Поиск файлов (анализ диска в стиле WizTree) ----------
    private string _scanPath = "";
    public string ScanPath { get => _scanPath; set { if (Set(ref _scanPath, value)) OnPropertyChanged(nameof(TargetDisplay)); } }

    public string TargetDisplay => string.IsNullOrWhiteSpace(ScanPath) ? Loc.I["fs_notarget"] : ScanPath;

    private DriveItemVm? _selectedDrive;
    public DriveItemVm? SelectedDrive
    {
        get => _selectedDrive;
        set { if (Set(ref _selectedDrive, value) && value != null) ScanPath = value.Path; }
    }

    private bool _filesBusy;
    public bool FilesBusy
    {
        get => _filesBusy;
        private set
        {
            if (!Set(ref _filesBusy, value)) return;
            OnPropertyChanged(nameof(FilesIdle));
            AnalyzeCommand.RaiseCanExecuteChanged();
            BrowseFolderCommand.RaiseCanExecuteChanged();
        }
    }
    public bool FilesIdle => !FilesBusy;

    private string _scanStatus = Loc.I["note_scan"];
    public string ScanStatus { get => _scanStatus; private set => Set(ref _scanStatus, value); }

    private string _scanStats = "";
    public string ScanStats { get => _scanStats; private set => Set(ref _scanStats, value); }

    private string _scanDrive = "";
    public string ScanDrive { get => _scanDrive; private set => Set(ref _scanDrive, value); }

    private long _scanTotalSize;

    private IReadOnlyList<FileRowVm> _filesList = Array.Empty<FileRowVm>();
    public IReadOnlyList<FileRowVm> FilesList { get => _filesList; private set => Set(ref _filesList, value); }

    private FileRowVm? _selectedFile;
    public FileRowVm? SelectedFile { get => _selectedFile; set { if (Set(ref _selectedFile, value)) OpenFileCommand.RaiseCanExecuteChanged(); } }

    private void LoadDrives()
    {
        Drives.Clear();
        DriveItemVm? system = null;
        string sysRoot = System.IO.Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        foreach (var d in System.IO.DriveInfo.GetDrives())
        {
            if (!d.IsReady) continue;
            if (d.DriveType is not (System.IO.DriveType.Fixed or System.IO.DriveType.Removable)) continue;
            string used = d.TotalSize > 0 ? $"{Format.Bytes(d.TotalSize - d.TotalFreeSpace)} / {Format.Bytes(d.TotalSize)}" : "";
            string label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? Loc.I["fs_localdisk"] : d.VolumeLabel;
            var vm = new DriveItemVm { Path = d.RootDirectory.FullName, Display = $"{d.Name.TrimEnd('\\')} - {label} ({used})" };
            Drives.Add(vm);
            if (string.Equals(d.RootDirectory.FullName, sysRoot, StringComparison.OrdinalIgnoreCase)) system = vm;
        }
        SelectedDrive = system ?? (Drives.Count > 0 ? Drives[0] : null);
    }

    private void BrowseFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Loc.I["fs_pickfolder"] };
        if (dlg.ShowDialog() == true)
        {
            _selectedDrive = null;
            OnPropertyChanged(nameof(SelectedDrive));
            ScanPath = dlg.FolderName;
        }
    }

    private async Task AnalyzeAsync()
    {
        string target = ScanPath;
        if (string.IsNullOrWhiteSpace(target) || !System.IO.Directory.Exists(target))
        { ScanStatus = Loc.I["fs_badpath"]; return; }

        FilesBusy = true;
        ScanStatus = Loc.I["fs_analyzing"];
        TreeRows.Clear();
        Extensions.Clear();
        FilesList = Array.Empty<FileRowVm>();
        MapRoot = null;
        try
        {
            var (res, files) = await Task.Run(() =>
            {
                var r = FileScanService.Scan(target, CancellationToken.None);
                var list = r.Files.Select(f => new FileRowVm(f)).ToList();
                return (r, list);
            });

            _scanTotalSize = res.TotalSize <= 0 ? 1 : res.TotalSize;

            var rootRow = new TreeRowVm(new Child(res.Root), 0, _scanTotalSize, ToggleRow);
            TreeRows.Add(rootRow);
            ToggleRow(rootRow);              // авто-разворот первого уровня

            int i = 0;
            foreach (var e in res.Extensions) Extensions.Add(new ExtRowVm(e, _scanTotalSize, i++));

            FilesList = files;
            MapRoot = res.Root;

            ScanStats = string.Format(Loc.I["fs_stats"], res.FileCount.ToString("N0"), Format.Bytes(res.TotalSize), Format.Bytes(res.TotalAlloc));
            ScanDrive = res.DriveTotal > 0
                ? string.Format(Loc.I["fs_drive"], Format.Bytes(res.DriveUsed), Format.Bytes(res.DriveTotal), (res.DriveUsed * 100.0 / res.DriveTotal).ToString("0"), Format.Bytes(res.DriveFree))
                : "";
            ScanStatus = string.Format(Loc.I["fs_done"], res.Elapsed.TotalSeconds.ToString("0.0"));
        }
        catch (Exception ex) { ScanStatus = string.Format(Loc.I["err"], ex.Message); }
        finally
        {
            FilesBusy = false;
            // вернуть ОС большие временные буферы скана (MFT, списки путей) -
            // иначе .NET держит их в рабочем наборе и в Диспетчере задач видно много памяти
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
    }

    // разворот/сворачивание строки в плоском дереве (как в WizTree)
    private void ToggleRow(TreeRowVm row)
    {
        int idx = TreeRows.IndexOf(row);
        if (idx < 0 || !row.HasChildren) return;

        if (row.IsExpanded)
        {
            // убрать все следующие строки глубже текущей
            int j = idx + 1;
            while (j < TreeRows.Count && TreeRows[j].Depth > row.Depth) TreeRows.RemoveAt(j);
            row.IsExpanded = false;
        }
        else
        {
            var children = row.Folder!.SortedChildren();
            int insert = idx + 1;
            foreach (var child in children)
                TreeRows.Insert(insert++, new TreeRowVm(child, row.Depth + 1, _scanTotalSize, ToggleRow));
            row.IsExpanded = true;
        }
    }

    private void OpenSelectedFile()
    {
        var sel = SelectedFile;
        if (sel is null) return;
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{sel.Path}\""); } catch { }
    }

    /// <summary>Разворачивает дерево до узла по полному пути и возвращает его строку (для выделения).</summary>
    public TreeRowVm? RevealInTree(string fullPath)
    {
        if (TreeRows.Count == 0 || string.IsNullOrEmpty(fullPath)) return null;

        var row = TreeRows[0];                   // корень дерева
        string rootPath = row.FullPath;
        if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)) return null;

        string rest = fullPath.Substring(rootPath.Length).TrimStart('\\');
        if (rest.Length == 0) return row;

        foreach (var seg in rest.Split('\\'))
        {
            if (row.HasChildren && !row.IsExpanded) ToggleRow(row);
            row = FindChildRow(row, seg);
            if (row is null) return null;
        }
        return row;
    }

    private TreeRowVm? FindChildRow(TreeRowVm parent, string childName)
    {
        int idx = TreeRows.IndexOf(parent);
        if (idx < 0) return null;
        for (int j = idx + 1; j < TreeRows.Count && TreeRows[j].Depth > parent.Depth; j++)
            if (TreeRows[j].Depth == parent.Depth + 1 &&
                string.Equals(TreeRows[j].Name, childName, StringComparison.OrdinalIgnoreCase))
                return TreeRows[j];
        return null;
    }

    // карта диска (treemap) - питается от того же результата анализа
    private TreeNode? _mapRoot;
    public TreeNode? MapRoot
    {
        get => _mapRoot;
        set { if (Set(ref _mapRoot, value)) { OnPropertyChanged(nameof(MapTitle)); MapUpCommand.RaiseCanExecuteChanged(); } }
    }

    public string MapTitle => MapRoot is null ? Loc.I["map_title"] : $"{MapRoot.FullPath} - {Format.Bytes(MapRoot.Size)}";

    // ---------- Состояние / итоги ----------
    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            ScanCommand.RaiseCanExecuteChanged();
            CleanCommand.RaiseCanExecuteChanged();
            RefreshCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(IsIdle));
        }
    }
    public bool IsIdle => !IsBusy;

    private string _status = Loc.I["note_status"];
    public string Status { get => _status; private set => Set(ref _status, value); }

    public long SelectedBytes => Categories.Where(c => c.Selected).Sum(c => c.SizeBytes);
    public string SelectedText => Format.Bytes(SelectedBytes);

    public long TotalScannedBytes => Categories.Sum(c => c.SizeBytes);
    public string TotalScannedText => Format.Bytes(TotalScannedBytes);

    private void OnCategoryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CleanCategory.Selected)
            or nameof(CleanCategory.SizeBytes))
        {
            OnPropertyChanged(nameof(SelectedBytes));
            OnPropertyChanged(nameof(SelectedText));
            OnPropertyChanged(nameof(TotalScannedBytes));
            OnPropertyChanged(nameof(TotalScannedText));
            CleanCommand.RaiseCanExecuteChanged();
        }
    }

    private void SetAll(bool value)
    {
        foreach (var c in Categories) c.Selected = value;
    }

    // ---------- Операции ----------
    private async Task ScanAsync()
    {
        IsBusy = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Status = Loc.I["cl_scanning"];
        try
        {
            foreach (var c in Categories)
            {
                c.IsBusy = true;
                // считаем в фоне, результат пишем уже в UI-потоке
                var (bytes, files) = await Task.Run(() => c.ScanCore(ct), ct);
                c.SizeBytes = bytes;
                c.FileCount = files;
                c.IsScanned = true;
                c.IsBusy = false;
            }
            Status = string.Format(Loc.I["cl_found"], TotalScannedText);
        }
        catch (OperationCanceledException)
        {
            Status = Loc.I["cl_cancelled"];
        }
        catch (Exception ex)
        {
            Status = string.Format(Loc.I["cl_scan_err"], ex.Message);
        }
        finally
        {
            foreach (var c in Categories) c.IsBusy = false;
            IsBusy = false;
        }
    }

    private async Task CleanAsync()
    {
        var selected = Categories.Where(c => c.Selected && c.SizeBytes > 0).ToList();
        if (selected.Count == 0) return;

        IsBusy = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        long freedTotal = 0;
        Status = Loc.I["cl_cleaning"];
        try
        {
            foreach (var c in selected)
            {
                c.IsBusy = true;
                freedTotal += await Task.Run(() => c.CleanCore(ct), ct);
                // пересчёт остатка - тоже в фоне, запись в UI-потоке
                var (bytes, files) = await Task.Run(() => c.ScanCore(ct), ct);
                c.SizeBytes = bytes;
                c.FileCount = files;
                c.IsBusy = false;
            }
            Status = string.Format(Loc.I["cl_done"], Format.Bytes(freedTotal));
        }
        catch (OperationCanceledException)
        {
            Status = string.Format(Loc.I["cl_interrupted"], Format.Bytes(freedTotal));
        }
        catch (Exception ex)
        {
            Status = string.Format(Loc.I["cl_clean_err"], ex.Message);
        }
        finally
        {
            foreach (var c in selected) c.IsBusy = false;
            IsBusy = false;
            RefreshDisks();
            if (freedTotal > 0)
            {
                _sessionFreed += freedTotal;
                OnPropertyChanged(nameof(HasSessionFreed));
                OnPropertyChanged(nameof(SessionFreedLine));
            }
        }
    }

    // ---------- Освобождено за сессию (до/после) ----------
    private long _sessionFreed;
    public bool HasSessionFreed => _sessionFreed > 0;
    public string SessionFreedLine => string.Format(Loc.I["sess_freed"], Format.Bytes(_sessionFreed));
}
