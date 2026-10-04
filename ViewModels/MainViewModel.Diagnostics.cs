using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using NakClean.Models;
using NakClean.Services;

namespace NakClean.ViewModels;

/// <summary>Строка карточки «Железа»: подпись + значение (+ вторая строка). Без подписи - значение на всю ширину.</summary>
public sealed class HwRowVm
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public string Sub { get; init; } = "";
    public Brush? ValueBrush { get; init; }
    /// <summary>Не копировать и не класть в «Паспорт ПК» (MAC-адрес - личный идентификатор).</summary>
    public bool Private { get; init; }

    public bool HasLabel => Label.Length > 0;
    public bool HasSub => Sub.Length > 0;
    public int ValueColumn => HasLabel ? 1 : 0;
    public int ValueSpan => HasLabel ? 1 : 2;
}

/// <summary>Карточка «Железа»: заголовок, строки и кнопка «Копировать».</summary>
public sealed class HwCardVm
{
    public HwCardVm(string glyph, string title, List<HwRowVm> rows)
    {
        Glyph = glyph; Title = title; Rows = rows;
        CopyCommand = new RelayCommand(() =>
        {
            try
            {
                System.Windows.Clipboard.SetText(ToText());
                ToastService.Ok(string.Format(Loc.I["hw_copied"], Title));
            }
            catch { }
        });
    }

    public string Glyph { get; }
    public string Title { get; }
    public List<HwRowVm> Rows { get; }
    public RelayCommand CopyCommand { get; }

    public string ToText()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(Title);
        foreach (var r in Rows.Where(r => !r.Private))
        {
            string line = r.HasLabel ? $"{r.Label}: {r.Value}" : r.Value;
            if (r.HasSub) line += r.HasLabel ? $" ({r.Sub})" : $" - {r.Sub}";
            sb.AppendLine("  " + line);
        }
        return sb.ToString();
    }
}

/// <summary>Плитка «Сводки»: коротко «всё ли в порядке»; клик открывает нужный раздел.</summary>
public sealed class DiagTileVm
{
    public required string Glyph { get; init; }
    public required string Title { get; init; }
    public required string Value { get; init; }
    public string Note { get; init; } = "";
    public Brush? ValueBrush { get; init; }
    /// <summary>Раздел, который открывает плитка: disks / battery / boot / hw.</summary>
    public required string Target { get; init; }
}

// ---------- Диагностика: сводка, «Железо», общее обновление ----------
public sealed partial class MainViewModel
{
    private static readonly Brush DgGreen = FrozenBrush(0x3D, 0xD6, 0x8C);
    private static readonly Brush DgGold = FrozenBrush(0xDD, 0xB4, 0x4B);
    private static readonly Brush DgRed = FrozenBrush(0xE5, 0x48, 0x4D);

    public ObservableCollection<DiagTileVm> DiagTiles { get; } = new();
    public ObservableCollection<HwCardVm> HwCards { get; } = new();
    public ObservableCollection<HwCardVm> HwColLeft { get; } = new();
    public ObservableCollection<HwCardVm> HwColRight { get; } = new();
    public RelayCommand RefreshDiagCommand { get; private set; } = null!;
    public RelayCommand CopyAllHwCommand { get; private set; } = null!;

    /// <summary>Ноутбук (есть батарея) - тогда показываем раздел «Батарея».</summary>
    public bool IsLaptop { get; } = Native.GetPowerStatus() is { ok: true, percent: >= 0 };

    private HardwareInfo? _hw;
    private int _bootSeconds = -1;

    private bool _diagBusy;
    public bool DiagBusy
    {
        get => _diagBusy;
        private set { if (Set(ref _diagBusy, value)) RefreshDiagCommand.RaiseCanExecuteChanged(); }
    }

    private bool _hwBusy;
    public bool HwBusy { get => _hwBusy; private set => Set(ref _hwBusy, value); }

    private void InitDiagnostics()
    {
        RefreshDiagCommand = new RelayCommand(async () => await RefreshDiagnosticsAsync(), () => !DiagBusy);
        CopyAllHwCommand = new RelayCommand(() =>
        {
            if (HwCards.Count == 0) return;
            try
            {
                System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, HwCards.Select(c => c.ToText())));
                ToastService.Ok(Loc.I["hw_copied_all"]);
            }
            catch { }
        });
    }

    /// <summary>Перечитать всё сразу: диски, батарею, загрузку, железо (параллельно).</summary>
    private async Task RefreshDiagnosticsAsync()
    {
        if (DiagBusy) return;
        DiagBusy = true;
        try
        {
            var tasks = new List<Task> { LoadDiskHealthAsync(), LoadBootAsync(), LoadHardwareAsync() };
            if (IsLaptop) tasks.Add(LoadBatteryAsync());
            await Task.WhenAll(tasks);
        }
        catch { }
        finally { DiagBusy = false; }
    }

    private async Task LoadHardwareAsync()
    {
        HwBusy = true;
        try { _hw = await Task.Run(HardwareInfoService.Collect); }
        catch { _hw = null; }
        finally { HwBusy = false; }
        BuildHardwareCards();
        RebuildDiagTiles();
    }

    // ---------- «Железо» ----------
    private static string Gb(long bytes) => bytes <= 0 ? "" : Format.Bytes(bytes);

    private static string D(DateTime? d)
        => d is { } x ? x.ToString("d", CultureInfo.GetCultureInfo(Loc.I.IsEn ? "en-US" : "ru-RU")) : "";

    private static string OnOff(bool? v, string on, string off) => v switch { true => Loc.I[on], false => Loc.I[off], _ => Loc.I["hw_na"] };
    private static Brush? OnOffBrush(bool? v) => v switch { true => DgGreen, false => DgGold, _ => null };

    private static void Add(List<HwRowVm> rows, string labelKey, string value, string sub = "", Brush? brush = null)
    {
        if (string.IsNullOrWhiteSpace(value)) return;   // нет данных - строку не показываем
        rows.Add(new HwRowVm { Label = Loc.I[labelKey], Value = value, Sub = sub, ValueBrush = brush });
    }

    private void BuildHardwareCards()
    {
        HwCards.Clear(); HwColLeft.Clear(); HwColRight.Clear();
        if (_hw is not { } hw) return;
        var cards = new List<HwCardVm>();

        // устройства с ошибками - первыми, если есть настоящие проблемы (отключённые вручную - не проблема)
        var real = hw.Problems.Where(p => p.Code != 22).ToList();
        var devRows = new List<HwRowVm>();
        foreach (var p in real)
            devRows.Add(new HwRowVm { Value = p.Name, Sub = DeviceProblem(p.Code), ValueBrush = DgRed });
        foreach (var p in hw.Problems.Where(p => p.Code == 22))
            devRows.Add(new HwRowVm { Value = p.Name, Sub = Loc.I["dev_c_22"] });
        if (devRows.Count == 0) devRows.Add(new HwRowVm { Value = Loc.I["hw_dev_ok"], ValueBrush = DgGreen });
        else if (real.Count > 0) devRows.Add(new HwRowVm { Value = Loc.I["hw_dev_hint"] });
        var devCard = new HwCardVm(real.Count > 0 ? "⚠" : "✓", Loc.I["hw_dev"], devRows);
        if (real.Count > 0) cards.Add(devCard);

        // Windows
        var os = new List<HwRowVm>();
        Add(os, "hw_edition", hw.Os.Caption);
        Add(os, "hw_version", hw.Os.Version.Length > 0 ? string.Format(Loc.I["hw_version_v"], hw.Os.Version, hw.Os.Build) : hw.Os.Build);
        Add(os, "hw_arch", hw.Os.Arch);
        Add(os, "hw_installed", D(hw.Os.Installed));
        if (os.Count > 0) cards.Add(new HwCardVm("💻", "Windows", os));

        // плата, BIOS, безопасность
        var b = hw.Board;
        var board = new List<HwRowVm>();
        string pc = b.Model.StartsWith(b.Vendor, StringComparison.OrdinalIgnoreCase) || b.Vendor.Length == 0
            ? b.Model : $"{b.Vendor} {b.Model}";
        Add(board, "hw_pc", pc.Trim());
        Add(board, "hw_mb", b.Board);
        Add(board, "hw_bios", b.Bios, D(b.BiosDate));
        Add(board, "hw_bootmode", b.Uefi switch { true => "UEFI", false => Loc.I["hw_legacy"], _ => "" });
        Add(board, "hw_secure", OnOff(b.SecureBoot, "hw_on", "hw_off"), "", OnOffBrush(b.SecureBoot));
        Add(board, "hw_tpm",
            b.TpmPresent switch
            {
                true => string.Format(Loc.I["hw_tpm_on"], b.TpmVersion),
                false => Loc.I["hw_tpm_none"],
                _ => Loc.I["hw_na"],
            }, "", OnOffBrush(b.TpmPresent));
        cards.Add(new HwCardVm("🧩", Loc.I["hw_board"], board));

        // процессор
        var c = hw.Cpu;
        var cpu = new List<HwRowVm>();
        Add(cpu, "hw_model", c.Name);
        if (c.Cores > 0) Add(cpu, "hw_cores", $"{c.Cores} / {c.Threads}");
        if (c.MaxMhz > 0) Add(cpu, "hw_base", $"{c.MaxMhz / 1000.0:0.0#} {Loc.I["u_ghz"]}");
        var cache = new List<string>();
        if (c.L2Kb > 0) cache.Add($"L2 {Format.Bytes(c.L2Kb * 1024L)}");
        if (c.L3Kb > 0) cache.Add($"L3 {Format.Bytes(c.L3Kb * 1024L)}");
        Add(cpu, "hw_cache", string.Join("  ·  ", cache));
        Add(cpu, "hw_socket", c.Socket);
        Add(cpu, "hw_virt", OnOff(c.Virtualization, "hw_virt_on", "hw_virt_off"), "", OnOffBrush(c.Virtualization));
        Add(cpu, "hw_instr", string.Join(", ", c.Instructions));
        cards.Add(new HwCardVm("⚙", Loc.I["hw_cpu"], cpu));

        // память по слотам
        var m = hw.Memory;
        var mem = new List<HwRowVm>();
        long total = m.Slots.Sum(s => s.Bytes);
        string type = m.Slots.Select(s => s.Type).FirstOrDefault(t => t.Length > 0) ?? "";
        Add(mem, "hw_total", string.Join("  ·  ", new[] { Gb(total), type }.Where(s => s.Length > 0)));
        if (m.TotalSlots > 0)
            Add(mem, "hw_slots", string.Format(Loc.I["hw_slots_v"], m.Slots.Count, m.TotalSlots),
                m.MaxBytes > 0 ? string.Format(Loc.I["hw_maxmem"], Gb(m.MaxBytes)) : "");
        bool slow = false;
        foreach (var s in m.Slots)
        {
            int mts = s.ConfiguredMts > 0 ? s.ConfiguredMts : s.RatedMts;
            var parts = new[] { Gb(s.Bytes), $"{s.Vendor} {s.Part}".Trim(), mts > 0 ? $"{mts} {Loc.I["u_mts"]}" : "" };
            mem.Add(new HwRowVm { Label = s.Slot, Value = string.Join("  ·  ", parts.Where(p => p.Length > 0)) });
            if (s.ConfiguredMts > 0 && s.RatedMts > s.ConfiguredMts) slow = true;
        }
        if (slow)
        {
            var any = m.Slots.First(s => s.ConfiguredMts > 0 && s.RatedMts > s.ConfiguredMts);
            mem.Add(new HwRowVm
            {
                Value = string.Format(Loc.I[IsLaptop ? "hw_mem_slow_laptop" : "hw_mem_slow_pc"], any.ConfiguredMts, any.RatedMts),
            });
        }
        cards.Add(new HwCardVm("🧠", Loc.I["hw_mem"], mem));

        // видеокарты
        foreach (var g in hw.Gpus)
        {
            var gr = new List<HwRowVm>();
            Add(gr, "hw_model", g.Name);
            Add(gr, "hw_vram", Gb(g.Vram));
            Add(gr, "hw_driver", g.Driver, D(g.DriverDate));
            if (g.Width > 0) Add(gr, "hw_screen", g.Hz > 1 ? $"{g.Width}×{g.Height}, {g.Hz} {Loc.I["u_hz"]}" : $"{g.Width}×{g.Height}");
            cards.Add(new HwCardVm("🎮", Loc.I["hw_gpu"], gr));
        }

        // мониторы
        var mon = new List<HwRowVm>();
        int n = 0;
        foreach (var mo in hw.Monitors)
        {
            n++;
            var parts = new[]
            {
                $"{mo.Vendor} {mo.Model}".Trim(),
                mo.Inches > 0 ? $"{Diagonal(mo.Inches)}″" : "",
                mo.Year > 1990 ? string.Format(Loc.I["hw_year"], mo.Year) : "",
            };
            mon.Add(new HwRowVm { Label = string.Format(Loc.I["hw_monitor_n"], n), Value = string.Join("  ·  ", parts.Where(p => p.Length > 0)) });
        }
        if (mon.Count > 0) cards.Add(new HwCardVm("🖥", Loc.I["hw_mon"], mon));

        // сеть
        var net = new List<HwRowVm>();
        foreach (var a in hw.Network)
        {
            var parts = new List<string> { Loc.I[a.Connected ? "hw_net_on" : "hw_net_off"] };
            if (a.SpeedBps > 0) parts.Add(NetSpeed(a.SpeedBps));
            if (a.Ip.Length > 0) parts.Add(a.Ip);
            net.Add(new HwRowVm { Value = a.Name, Sub = string.Join("  ·  ", parts) });
            if (a.Mac.Length > 0) net.Add(new HwRowVm { Label = "MAC", Value = a.Mac, Private = true });
        }
        if (net.Count > 0) cards.Add(new HwCardVm("🌐", Loc.I["hw_net"], net));

        // звук
        var audio = hw.Audio.Select(a => new HwRowVm { Value = a }).ToList();
        if (audio.Count > 0) cards.Add(new HwCardVm("🔊", Loc.I["hw_audio"], audio));

        if (real.Count == 0) cards.Add(devCard);

        // две колонки: каждую карточку - в ту, где сейчас меньше строк
        int left = 0, right = 0;
        foreach (var card in cards)
        {
            HwCards.Add(card);
            int h = card.Rows.Count + 2;
            if (left <= right) { HwColLeft.Add(card); left += h; }
            else { HwColRight.Add(card); right += h; }
        }
    }

    // диагональ: к ближайшему стандартному размеру (EDID даёт видимую область - 15.3″ у экрана 15.6″)
    private static string Diagonal(double inches)
    {
        double[] std = { 11.6, 12.5, 13.3, 14, 15.6, 16, 17.3, 18.5, 19.5, 21.5, 23.8, 24, 24.5, 27, 28, 31.5, 32, 34, 38, 43, 49 };
        double best = std.OrderBy(s => Math.Abs(s - inches)).First();
        double v = Math.Abs(best - inches) <= 0.35 ? best : Math.Round(inches, 1);
        return v.ToString("0.#", Loc.I.IsEn ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo("ru-RU"));
    }

    private static string NetSpeed(long bps) => bps >= 1_000_000_000
        ? $"{bps / 1_000_000_000.0:0.#} {Loc.I["u_gbps"]}"
        : $"{bps / 1_000_000} {Loc.I["u_mbps"]}";

    /// <summary>Код ошибки из Диспетчера устройств - простыми словами.</summary>
    private static string DeviceProblem(int code)
    {
        string key = $"dev_c_{code}";
        string text = Loc.I[key];
        return text == key ? string.Format(Loc.I["dev_c_other"], code) : text;
    }

    // ---------- «Сводка» ----------
    private void RebuildDiagTiles()
    {
        DiagTiles.Clear();

        // диски: худшее состояние
        if (_diskModels.Count > 0)
        {
            var worst = _diskModels.Select(d => d.State).OrderByDescending(s => s switch
            {
                HealthState.Unhealthy => 3, HealthState.Warning => 2, HealthState.Healthy => 1, _ => 0,
            }).First();
            DiagTiles.Add(new DiagTileVm
            {
                Glyph = "💽", Title = Loc.I["dg_disks"], Target = "disks",
                Value = Loc.I[worst switch
                {
                    HealthState.Healthy => "dg_disks_ok", HealthState.Warning => "dg_disks_warn",
                    HealthState.Unhealthy => "dg_disks_bad", _ => "hw_na",
                }],
                ValueBrush = worst switch { HealthState.Healthy => DgGreen, HealthState.Warning => DgGold, HealthState.Unhealthy => DgRed, _ => null },
                Note = string.Format(Loc.I["dg_disks_n"], _diskModels.Count),
            });
        }
        else DiagTiles.Add(new DiagTileVm { Glyph = "💽", Title = Loc.I["dg_disks"], Target = "disks", Value = "…" });

        // батарея (только ноутбук)
        if (IsLaptop)
            DiagTiles.Add(BatteryPresent
                ? new DiagTileVm
                {
                    Glyph = "🔋", Title = Loc.I["dg_battery"], Target = "battery",
                    Value = string.Format(Loc.I["dg_bat_wear"], BatWear), ValueBrush = BatWearBrush, Note = BatVerdict,
                }
                : new DiagTileVm { Glyph = "🔋", Title = Loc.I["dg_battery"], Target = "battery", Value = BatteryBusy ? "…" : Loc.I["hw_na"] });

        // загрузка
        DiagTiles.Add(new DiagTileVm
        {
            Glyph = "🚀", Title = Loc.I["dg_boot"], Target = "boot",
            Value = _bootSeconds > 0 ? $"{_bootSeconds} {Loc.I["u_sec"]}" : Loc.I["hw_na"],
            Note = _bootSeconds > 0 ? Loc.I["dg_boot_note"] : "",
        });

        if (_hw is { } hw)
        {
            // устройства
            int bad = hw.Problems.Count(p => p.Code != 22), off = hw.Problems.Count(p => p.Code == 22);
            DiagTiles.Add(new DiagTileVm
            {
                Glyph = bad > 0 ? "⚠" : "✓", Title = Loc.I["dg_devices"], Target = "hw",
                Value = bad > 0 ? string.Format(Loc.I["dg_dev_bad"], bad) : Loc.I["dg_dev_ok"],
                ValueBrush = bad > 0 ? DgRed : DgGreen,
                Note = off > 0 ? string.Format(Loc.I["dg_dev_off"], off) : "",
            });

            // безопасность: Secure Boot + TPM
            // «не полностью» - только если что-то точно выключено; неизвестное - честно «нет данных»
            var bd = hw.Board;
            bool allOn = bd.SecureBoot == true && bd.TpmPresent == true;
            bool anyOff = bd.SecureBoot == false || bd.TpmPresent == false;
            DiagTiles.Add(new DiagTileVm
            {
                Glyph = "🛡", Title = Loc.I["dg_security"], Target = "hw",
                Value = allOn ? Loc.I["dg_sec_ok"] : anyOff ? Loc.I["dg_sec_partial"] : Loc.I["hw_na"],
                ValueBrush = allOn ? DgGreen : anyOff ? DgGold : null,
                Note = $"Secure Boot: {OnOff(bd.SecureBoot, "hw_on_s", "hw_off_s")}  ·  TPM: {OnOff(bd.TpmPresent, "hw_on_s", "hw_off_s")}",
            });

            // память
            var mm = hw.Memory;
            if (mm.Slots.Count > 0)
                DiagTiles.Add(new DiagTileVm
                {
                    Glyph = "🧠", Title = Loc.I["dg_memory"], Target = "hw",
                    Value = Gb(mm.Slots.Sum(s => s.Bytes)),
                    Note = mm.TotalSlots > 0 ? string.Format(Loc.I["hw_slots_v"], mm.Slots.Count, mm.TotalSlots) : "",
                });
        }
        else
        {
            DiagTiles.Add(new DiagTileVm { Glyph = "✓", Title = Loc.I["dg_devices"], Target = "hw", Value = "…" });
            DiagTiles.Add(new DiagTileVm { Glyph = "🛡", Title = Loc.I["dg_security"], Target = "hw", Value = "…" });
        }
    }
}
