using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Win32;

namespace NakClean.Services;

/// <summary>Один твик оптимизации - с проверкой состояния, применением и откатом.</summary>
public sealed class Tweak
{
    public required string Id { get; init; }
    public required string NameKey { get; init; }
    public required string DescKey { get; init; }
    public required string TierKey { get; init; }     // "tier_safe" | "tier_medium" | "tier_max"
    public required string EffectKey { get; init; }   // честная метка реального эффекта: "eff_*"
    public required Func<bool> IsApplied { get; init; }
    public required Func<bool> Apply { get; init; }
    /// <summary>Вернуть стандартное значение Windows (для записей журнала без снимка).</summary>
    public required Func<bool> Revert { get; init; }
    /// <summary>Снимок текущего состояния (JSON) - снимается ПЕРЕД применением.</summary>
    public required Func<string> Snapshot { get; init; }
    /// <summary>Вернуть состояние из снимка - ровно то, что было у человека до нас.</summary>
    public required Func<string, bool> Restore { get; init; }
}

public enum RevertResult { Reverted, NotApplied, Failed }

public enum RestorePointResult { Created, RecentExists, Failed }

/// <summary>
/// Безопасная оптимизация Windows с уровнями. Все твики обратимы, перед
/// применением можно создать точку восстановления. Защитник и критичные
/// компоненты НЕ трогаются. У каждого твика - ЧЕСТНАЯ метка эффекта
/// (заметный / небольшой / удобство / снижает безопасность), без плацебо.
/// </summary>
public static class OptimizationService
{
    public const string Safe = "tier_safe";
    public const string Medium = "tier_medium";
    public const string Max = "tier_max";

    // честные метки эффекта
    public const string EfNotable = "eff_notable";       // заметный прирост
    public const string EfSmall = "eff_small";           // небольшой эффект
    public const string EfSpace = "eff_space";           // освобождает место
    public const string EfSecurity = "eff_security";     // снижает безопасность
    public const string EfConvenience = "eff_convenience"; // удобство / приватность

    private static RegistryKey Base(RegistryHive h) => RegistryKey.OpenBaseKey(h, RegistryView.Registry64);

    /// <summary>Профили оптимизации: пресет → набор Id твиков. Отмечают твики, применяет пользователь.</summary>
    public static readonly Dictionary<string, string[]> Profiles = new()
    {
        // безопасный набор по умолчанию, без рисков
        ["balanced"] = new[] { "anim", "transp", "tips", "startdelay", "explorerads", "menudelay", "recording", "widgets", "startads" },
        // максимум отзывчивости/FPS (отключение фоновых, троттлинга, VBS)
        ["gaming"] = new[] { "menudelay", "anim", "transp", "startdelay", "recording", "gamedvr", "bgapps", "bgsearch", "netthrottle", "vbs" },
        // меньше фона/телеметрии/назойливости (приватность и тишина)
        ["quiet"] = new[] { "tips", "startads", "widgets", "telemetry", "activity", "diagtrack", "bgapps", "bgsearch" },
    };

    public static List<Tweak> BuildTweaks() => new()
    {
        // ---------- Безопасно ----------
        Reg("anim", "tw_anim", "tw_anim_d", Safe, EfSmall,
            RegistryHive.CurrentUser, @"Control Panel\Desktop\WindowMetrics", "MinAnimate",
            RegistryValueKind.String, "0", "1"),
        Reg("transp", "tw_transp", "tw_transp_d", Safe, EfSmall,
            RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "EnableTransparency", RegistryValueKind.DWord, 0, 1),
        Reg("tips", "tw_tips", "tw_tips_d", Safe, EfConvenience,
            RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
            "SubscribedContent-338389Enabled", RegistryValueKind.DWord, 0, 1),
        Reg("startdelay", "tw_startdelay", "tw_startdelay_d", Safe, EfSmall,
            RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize",
            "StartupDelayInMSec", RegistryValueKind.DWord, 0, null),
        Reg("explorerads", "tw_explorerads", "tw_explorerads_d", Safe, EfConvenience,
            RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
            "ShowSyncProviderNotifications", RegistryValueKind.DWord, 0, 1),
        // мгновенное открытие меню - реально ощущается, риск ноль
        Reg("menudelay", "tw_menudelay", "tw_menudelay_d", Safe, EfSmall,
            RegistryHive.CurrentUser, @"Control Panel\Desktop", "MenuShowDelay",
            RegistryValueKind.String, "0", "400"),
        // фоновая запись игр (Game DVR) - если включена, ест ресурсы
        Reg("recording", "tw_recording", "tw_recording_d", Safe, EfSmall,
            RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR",
            "AppCaptureEnabled", RegistryValueKind.DWord, 0, 1),
        // виджеты W11 висят в фоне - если не используешь, реальная экономия RAM/CPU
        // политика: по умолчанию её нет - откат удаляет значение, а не ставит 1
        // (иначе Windows пишет «некоторыми параметрами управляет организация»)
        Reg("widgets", "tw_widgets", "tw_widgets_d", Safe, EfSmall,
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Dsh",
            "AllowNewsAndInterests", RegistryValueKind.DWord, 0, null),
        // реклама/«рекомендации» в Пуске - это приватность/чистота, не скорость
        RegMulti("startads", "tw_startads", "tw_startads_d", Safe, EfConvenience,
            RegistryHive.CurrentUser, new (string, string, RegistryValueKind, object, object?)[]
            {
                (@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338393Enabled", RegistryValueKind.DWord, 0, 1),
                (@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-353694Enabled", RegistryValueKind.DWord, 0, 1),
                (@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-353696Enabled", RegistryValueKind.DWord, 0, 1),
                (@"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", RegistryValueKind.DWord, 0, 1),
            }),

        // ---------- Средне ----------
        Reg("gamedvr", "tw_gamedvr", "tw_gamedvr_d", Medium, EfSmall,
            RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled",
            RegistryValueKind.DWord, 0, 1),
        Reg("bgapps", "tw_bgapps", "tw_bgapps_d", Medium, EfSmall,
            RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications",
            "GlobalUserDisabled", RegistryValueKind.DWord, 1, 0),
        // добивает фоновые приложения: глобальный тоггл поиска/UWP
        Reg("bgsearch", "tw_bgsearch", "tw_bgsearch_d", Medium, EfSmall,
            RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Search",
            "BackgroundAppGlobalToggle", RegistryValueKind.DWord, 0, 1),
        Reg("telemetry", "tw_telemetry", "tw_telemetry_d", Medium, EfConvenience,
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection",
            "AllowTelemetry", RegistryValueKind.DWord, 0, null),
        Reg("activity", "tw_activity", "tw_activity_d", Medium, EfConvenience,
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System",
            "EnableActivityFeed", RegistryValueKind.DWord, 0, null),
        // сетевой троттлинг MMCSS - помогает ТОЛЬКО связке звук/видео+сеть (стримы, онлайн)
        Reg("netthrottle", "tw_netthrottle", "tw_netthrottle_d", Medium, EfSmall,
            RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
            "NetworkThrottlingIndex", RegistryValueKind.DWord, -1, 10),
        // VBS / Memory Integrity - единственный твик с реально измеримым приростом (5-15%),
        // НО это функция безопасности. Честный размен: FPS ↔ защита.
        // по умолчанию значения нет и Windows решает сама - откат удаляет его, а не включает VBS силой
        Reg("vbs", "tw_vbs", "tw_vbs_d", Medium, EfSecurity,
            RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\DeviceGuard",
            "EnableVirtualizationBasedSecurity", RegistryValueKind.DWord, 0, null),

        // ---------- Максимум ----------
        Svc("sysmain", "tw_sysmain", "tw_sysmain_d", Max, EfSmall, "SysMain"),
        Svc("wsearch", "tw_wsearch", "tw_wsearch_d", Max, EfSmall, "WSearch"),
        Svc("diagtrack", "tw_diagtrack", "tw_diagtrack_d", Max, EfConvenience, "DiagTrack"),
        Hibernate(),
    };

    // ---------- построители ----------
    private static Tweak Reg(string id, string nameKey, string descKey, string tierKey, string effectKey,
        RegistryHive hive, string sub, string val, RegistryValueKind kind, object opt, object? def)
    {
        bool IsApplied()
        {
            try
            {
                using var k = Base(hive).OpenSubKey(sub);
                var cur = k?.GetValue(val);
                return cur != null && cur.ToString() == opt.ToString();
            }
            catch { return false; }
        }
        bool ApplyFn()
        {
            try { using var k = Base(hive).CreateSubKey(sub, true); k!.SetValue(val, opt, kind); return true; }
            catch { return false; }
        }
        bool RevertFn()
        {
            try
            {
                using var k = Base(hive).CreateSubKey(sub, true);
                if (def is null) k!.DeleteValue(val, false);
                else k!.SetValue(val, def, kind);
                return true;
            }
            catch { return false; }
        }
        return new Tweak
        {
            Id = id, NameKey = nameKey, DescKey = descKey, TierKey = tierKey, EffectKey = effectKey,
            IsApplied = IsApplied, Apply = ApplyFn, Revert = RevertFn,
            Snapshot = () => SnapshotRegs(new[] { (hive, sub, val) }),
            Restore = RestoreRegs,
        };
    }

    /// <summary>Твик, меняющий сразу несколько значений (в одном кусте). Применён = все значения совпали.</summary>
    private static Tweak RegMulti(string id, string nameKey, string descKey, string tierKey, string effectKey,
        RegistryHive hive, (string sub, string val, RegistryValueKind kind, object opt, object? def)[] entries)
    {
        bool IsApplied()
        {
            try
            {
                foreach (var e in entries)
                {
                    using var k = Base(hive).OpenSubKey(e.sub);
                    var cur = k?.GetValue(e.val);
                    if (cur == null || cur.ToString() != e.opt.ToString()) return false;
                }
                return true;
            }
            catch { return false; }
        }
        bool ApplyFn()
        {
            try
            {
                foreach (var e in entries)
                { using var k = Base(hive).CreateSubKey(e.sub, true); k!.SetValue(e.val, e.opt, e.kind); }
                return true;
            }
            catch { return false; }
        }
        bool RevertFn()
        {
            try
            {
                foreach (var e in entries)
                {
                    using var k = Base(hive).CreateSubKey(e.sub, true);
                    if (e.def is null) k!.DeleteValue(e.val, false);
                    else k!.SetValue(e.val, e.def, e.kind);
                }
                return true;
            }
            catch { return false; }
        }
        return new Tweak
        {
            Id = id, NameKey = nameKey, DescKey = descKey, TierKey = tierKey, EffectKey = effectKey,
            IsApplied = IsApplied, Apply = ApplyFn, Revert = RevertFn,
            Snapshot = () => SnapshotRegs(entries.Select(e => (hive, e.sub, e.val))),
            Restore = RestoreRegs,
        };
    }

    // ---------- снимок и возврат исходного состояния ----------
    internal sealed record RegState(RegistryHive Hive, string Sub, string Val, bool KeyExisted, bool Exists,
        RegistryValueKind Kind, string? Data);
    internal sealed record SvcState(int Start, int Delayed, bool Running);
    internal sealed record HibState(bool WasOff);

    private static string SnapshotRegs(IEnumerable<(RegistryHive hive, string sub, string val)> items)
    {
        var list = new List<RegState>();
        foreach (var (hive, sub, val) in items)
        {
            using var k = Base(hive).OpenSubKey(sub);
            object? v = k?.GetValue(val, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (k is null || v is null)
            {
                list.Add(new RegState(hive, sub, val, k != null, false, RegistryValueKind.Unknown, null));
                continue;
            }
            var kind = k.GetValueKind(val);
            string data = kind switch
            {
                RegistryValueKind.Binary => Convert.ToBase64String((byte[])v),
                RegistryValueKind.MultiString => string.Join("\0", (string[])v),
                RegistryValueKind.DWord => ((int)v).ToString(CultureInfo.InvariantCulture),
                RegistryValueKind.QWord => ((long)v).ToString(CultureInfo.InvariantCulture),
                _ => v.ToString() ?? "",
            };
            list.Add(new RegState(hive, sub, val, true, true, kind, data));
        }
        return JsonSerializer.Serialize(list);
    }

    private static bool RestoreRegs(string json)
    {
        try
        {
            var list = JsonSerializer.Deserialize<List<RegState>>(json);
            if (list is null) return false;
            foreach (var s in list)
            {
                if (!s.Exists)
                {
                    // значения не было - убираем; ключ тоже, если его создали мы и он пуст
                    bool empty;
                    using (var k = Base(s.Hive).OpenSubKey(s.Sub, writable: true))
                    {
                        if (k is null) continue;
                        k.DeleteValue(s.Val, throwOnMissingValue: false);
                        empty = k.ValueCount == 0 && k.SubKeyCount == 0;
                    }
                    if (!s.KeyExisted && empty)
                    {
                        using var b = Base(s.Hive);
                        b.DeleteSubKey(s.Sub, throwOnMissingSubKey: false);
                    }
                    continue;
                }
                object data = s.Kind switch
                {
                    RegistryValueKind.Binary => Convert.FromBase64String(s.Data ?? ""),
                    RegistryValueKind.MultiString => (s.Data ?? "").Split('\0'),
                    RegistryValueKind.DWord => int.Parse(s.Data ?? "0", CultureInfo.InvariantCulture),
                    RegistryValueKind.QWord => long.Parse(s.Data ?? "0", CultureInfo.InvariantCulture),
                    _ => s.Data ?? "",
                };
                using var w = Base(s.Hive).CreateSubKey(s.Sub, writable: true);
                w.SetValue(s.Val, data, s.Kind);
            }
            return true;
        }
        catch { return false; }
    }

    private static string SnapshotSvc(string svc)
    {
        using var k = Base(RegistryHive.LocalMachine).OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{svc}");
        int start = k?.GetValue("Start") is int s ? s : -1;
        int delayed = k?.GetValue("DelayedAutostart") is int d ? d : 0;
        bool running = string.Equals(ServicesService.QueryState(svc)?.state, "Running", StringComparison.OrdinalIgnoreCase);
        return JsonSerializer.Serialize(new SvcState(start, delayed, running));
    }

    private static bool RestoreSvc(string svc, string json)
    {
        try
        {
            var st = JsonSerializer.Deserialize<SvcState>(json);
            if (st is null) return false;
            string? mode = st.Start switch
            {
                2 => st.Delayed == 1 ? "delayed-auto" : "auto",
                3 => "demand",
                4 => "disabled",
                _ => null,   // службы не было / загрузочная - не трогаем
            };
            if (mode is null) return false;
            bool ok = RunProc("sc.exe", $"config \"{svc}\" start= {mode}");
            if (ok && st.Running) RunProc("sc.exe", $"start \"{svc}\"");   // была запущена - запускаем снова
            return ok;
        }
        catch { return false; }
    }

    /// <summary>
    /// Откат одного твика. Есть снимок - возвращаем ровно то, что было. Записи без снимка (старые версии)
    /// или твик включён не нами - ставим стандартное значение Windows. Не применён - не трогаем.
    /// </summary>
    public static RevertResult RevertOne(Tweak t)
    {
        var entry = ChangeLogService.Get(t.Id);
        bool ok;
        if (entry?.Original is { } orig) ok = t.Restore(orig);
        else if (entry != null || t.IsApplied()) ok = t.Revert();
        else return RevertResult.NotApplied;

        if (!ok) return RevertResult.Failed;
        ChangeLogService.Remove(t.Id);
        return RevertResult.Reverted;
    }

    private static Tweak Svc(string id, string nameKey, string descKey, string tierKey, string effectKey, string svc)
    {
        bool IsApplied() => string.Equals(ServicesService.QueryState(svc)?.startMode, "Disabled", StringComparison.OrdinalIgnoreCase);
        bool ApplyFn() { RunProc("sc.exe", $"stop \"{svc}\""); return RunProc("sc.exe", $"config \"{svc}\" start= disabled"); }
        bool RevertFn() { return RunProc("sc.exe", $"config \"{svc}\" start= auto"); }
        return new Tweak
        {
            Id = id, NameKey = nameKey, DescKey = descKey, TierKey = tierKey, EffectKey = effectKey,
            IsApplied = IsApplied, Apply = ApplyFn, Revert = RevertFn,
            Snapshot = () => SnapshotSvc(svc),
            Restore = json => RestoreSvc(svc, json),
        };
    }

    private static Tweak Hibernate()
    {
        bool IsApplied()
        {
            try
            {
                using var k = Base(RegistryHive.LocalMachine).OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power");
                return k?.GetValue("HibernateEnabled") is int v && v == 0;
            }
            catch { return false; }
        }
        return new Tweak
        {
            Id = "hibernate", NameKey = "tw_hibernate", TierKey = Max, EffectKey = EfSpace,
            DescKey = "tw_hibernate_d",
            IsApplied = IsApplied,
            Apply = () => RunProc("powercfg.exe", "-h off"),
            Revert = () => RunProc("powercfg.exe", "-h on"),
            Snapshot = () => JsonSerializer.Serialize(new HibState(IsApplied())),
            // гибернация и до нас была выключена - так и оставляем
            Restore = json => JsonSerializer.Deserialize<HibState>(json) is { } h
                              && (h.WasOff || RunProc("powercfg.exe", "-h on")),
        };
    }

    /// <summary>
    /// Создаёт точку восстановления. Итог проверяем по факту - появилась ли новая точка:
    /// Windows разрешает одну в сутки и при повторе молча ничего не создаёт (а команда «успешна»).
    /// Возвращает итог и время последней точки (новой или той, что уже защищает).
    /// </summary>
    public static (RestorePointResult Result, DateTime? Last) CreateRestorePoint(string description)
    {
        var before = LatestRestorePoint();
        RunProc("powershell.exe",
            $"-NoProfile -NonInteractive -Command \"Checkpoint-Computer -Description '{description.Replace("'", "''")}' -RestorePointType 'MODIFY_SETTINGS'\"",
            120000);
        var after = LatestRestorePoint();

        if (after is { } a && (before is null || a.Seq > before.Value.Seq)) return (RestorePointResult.Created, a.When);
        if (before is { } b && DateTime.Now - b.When < TimeSpan.FromHours(24)) return (RestorePointResult.RecentExists, b.When);
        return (RestorePointResult.Failed, null);
    }

    /// <summary>Самая свежая точка восстановления (номер, время). null - точек нет или защита выключена.</summary>
    private static (uint Seq, DateTime When)? LatestRestorePoint()
    {
        try
        {
            using var s = new System.Management.ManagementObjectSearcher(@"root\default",
                "SELECT SequenceNumber, CreationTime FROM SystemRestore");
            (uint Seq, DateTime When)? best = null;
            foreach (System.Management.ManagementObject o in s.Get())
                using (o)
                {
                    uint seq = Convert.ToUInt32(o["SequenceNumber"]);
                    if (best is { } cur && cur.Seq >= seq) continue;
                    var when = System.Management.ManagementDateTimeConverter.ToDateTime(o["CreationTime"]?.ToString());
                    best = (seq, when);
                }
            return best;
        }
        catch { return null; }
    }

    private static bool RunProc(string file, string args, int timeoutMs = 15000)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = file, Arguments = args,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(timeoutMs);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}
