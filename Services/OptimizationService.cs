using System.Diagnostics;
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
    public required Func<bool> Revert { get; init; }
}

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
        Reg("widgets", "tw_widgets", "tw_widgets_d", Safe, EfSmall,
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Dsh",
            "AllowNewsAndInterests", RegistryValueKind.DWord, 0, 1),
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
        Reg("vbs", "tw_vbs", "tw_vbs_d", Medium, EfSecurity,
            RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\DeviceGuard",
            "EnableVirtualizationBasedSecurity", RegistryValueKind.DWord, 0, 1),

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
        return new Tweak { Id = id, NameKey = nameKey, DescKey = descKey, TierKey = tierKey, EffectKey = effectKey, IsApplied = IsApplied, Apply = ApplyFn, Revert = RevertFn };
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
        return new Tweak { Id = id, NameKey = nameKey, DescKey = descKey, TierKey = tierKey, EffectKey = effectKey, IsApplied = IsApplied, Apply = ApplyFn, Revert = RevertFn };
    }

    private static Tweak Svc(string id, string nameKey, string descKey, string tierKey, string effectKey, string svc)
    {
        bool IsApplied() => string.Equals(ServicesService.QueryState(svc)?.startMode, "Disabled", StringComparison.OrdinalIgnoreCase);
        bool ApplyFn() { RunProc("sc.exe", $"stop \"{svc}\""); return RunProc("sc.exe", $"config \"{svc}\" start= disabled"); }
        bool RevertFn() { return RunProc("sc.exe", $"config \"{svc}\" start= auto"); }
        return new Tweak { Id = id, NameKey = nameKey, DescKey = descKey, TierKey = tierKey, EffectKey = effectKey, IsApplied = IsApplied, Apply = ApplyFn, Revert = RevertFn };
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
        };
    }

    /// <summary>Создаёт точку восстановления системы. Требует включённой защиты системы.</summary>
    public static bool CreateRestorePoint(string description)
    {
        try
        {
            return RunProc("powershell.exe",
                $"-NoProfile -NonInteractive -Command \"Checkpoint-Computer -Description '{description}' -RestorePointType 'MODIFY_SETTINGS'\"",
                60000);
        }
        catch { return false; }
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
