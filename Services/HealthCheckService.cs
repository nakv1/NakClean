using System.IO;
using System.Management;
using NakClean.Models;
using NakClean.ViewModels;

namespace NakClean.Services;

/// <summary>
/// Комплексная проверка состояния ПК: память, диски (место + S.M.A.R.T.),
/// антивирус, драйверы, аптайм, батарея, мусор, версия ОС.
/// Возвращает список пунктов и общий балл здоровья 0-100.
/// </summary>
public static class HealthCheckService
{
    public static (int score, List<HealthItem> items) Run()
    {
        var items = new List<HealthItem>();

        CheckMemory(items);
        CheckMemoryTest(items);
        CheckDiskSpace(items);
        CheckSmart(items);
        CheckDefender(items);
        CheckDrivers(items);
        CheckUptime(items);
        CheckBattery(items);
        CheckJunk(items);
        CheckOs(items);

        int score = 100;
        foreach (var i in items)
            score -= i.Level switch { CheckLevel.Bad => 20, CheckLevel.Warning => 8, _ => 0 };
        score = Math.Clamp(score, 0, 100);

        return (score, items);
    }

    private static void CheckMemory(List<HealthItem> items)
    {
        var (total, used, pct) = Native.GetMemory();
        var level = pct >= 92 ? CheckLevel.Bad : pct >= 80 ? CheckLevel.Warning : CheckLevel.Good;
        string value = string.Format(Loc.I["hm_v"], $"{pct:0}", Format.Bytes((long)used), Format.Bytes((long)total));
        // если установлено заметно больше доступного - часть ОЗУ зарезервирована железом (встроенная графика);
        // показываем установленный объём, чтобы "13.9 ГБ" не сбивало с толку на ПК с 16 ГБ.
        ulong installed = Native.GetInstalledMemoryBytes();
        if (installed > total + 256L * 1024 * 1024)
            value += "  " + string.Format(Loc.I["hm_installed"], Format.Bytes((long)installed));
        items.Add(new HealthItem
        {
            Category = Loc.I["hc_mem"],
            Title = Loc.I["hm_t"],
            Value = value,
            Level = level,
            Hint = level == CheckLevel.Good ? null : Loc.I["hm_h"],
        });
    }

    /// <summary>
    /// Итог последнего теста памяти Windows. Ошибки ОЗУ - серьёзная неисправность (красный).
    /// Не запускали / прерван - без штрафа к оценке, только подсказка.
    /// </summary>
    private static void CheckMemoryTest(List<HealthItem> items)
    {
        var r = MaintenanceService.GetLastMemoryTest();
        string date = r is { } x
            ? x.When.ToString("d MMMM yyyy", System.Globalization.CultureInfo.GetCultureInfo(Loc.I.IsEn ? "en-US" : "ru-RU"))
            : "";

        var (key, level, hint) = r?.Outcome switch
        {
            MemTestOutcome.NoErrors => ("hmt_ok", CheckLevel.Good, (string?)null),
            MemTestOutcome.Errors => ("hmt_err", CheckLevel.Bad, Loc.I["hmt_err_h"]),
            MemTestOutcome.Interrupted => ("hmt_int", CheckLevel.Info, Loc.I["hmt_run_h"]),
            MemTestOutcome.Failed => ("hmt_fail", CheckLevel.Info, Loc.I["hmt_run_h"]),
            _ => ("hmt_none", CheckLevel.Info, Loc.I["hmt_run_h"]),
        };

        items.Add(new HealthItem
        {
            Category = Loc.I["hc_mem"],
            Title = Loc.I["hmt_t"],
            Value = string.Format(Loc.I[key], date),
            Level = level,
            Hint = hint,
        });
    }

    private static void CheckDiskSpace(List<HealthItem> items)
    {
        foreach (var d in SystemStatsService.GetDisks())
        {
            double freePct = d.TotalBytes > 0 ? d.FreeBytes * 100.0 / d.TotalBytes : 100;
            var level = freePct < 7 ? CheckLevel.Bad : freePct < 15 ? CheckLevel.Warning : CheckLevel.Good;
            items.Add(new HealthItem
            {
                Category = Loc.I["hc_disks"],
                Title = string.Format(Loc.I["hd_t"], d.Name),
                Value = string.Format(Loc.I["hd_v"], Format.Bytes(d.FreeBytes), $"{freePct:0}"),
                Level = level,
                Hint = level == CheckLevel.Good ? null : Loc.I["hd_h"],
            });
        }
    }

    private static void CheckSmart(List<HealthItem> items)
    {
        foreach (var disk in DiskHealthService.GetDisks())
        {
            var level = disk.State switch
            {
                HealthState.Healthy => CheckLevel.Good,
                HealthState.Warning => CheckLevel.Warning,
                HealthState.Unhealthy => CheckLevel.Bad,
                _ => CheckLevel.Info,
            };
            items.Add(new HealthItem
            {
                Category = Loc.I["hc_disks"],
                Title = $"S.M.A.R.T. - {disk.Name}",
                Value = (disk.State switch
                {
                    HealthState.Healthy => Loc.I["st_healthy"],
                    HealthState.Warning => Loc.I["st_warning"],
                    HealthState.Unhealthy => Loc.I["st_unhealthy"],
                    _ => Loc.I["st_unknown"],
                }) + (disk.TemperatureC.HasValue ? $", {disk.TemperatureC} °C" : ""),
                Level = level,
                Hint = level == CheckLevel.Bad ? Loc.I["hs_h"] : null,
            });
        }
    }

    private static void CheckDefender(List<HealthItem> items)
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Defender");
            scope.Connect();
            using var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_MpComputerStatus"));
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                {
                    bool av = ToBool(o["AntivirusEnabled"]);
                    bool rt = ToBool(o["RealTimeProtectionEnabled"]);
                    long sigAge = ToLong(o["AntivirusSignatureAge"]);
                    var level = (av && rt) ? (sigAge > 7 ? CheckLevel.Warning : CheckLevel.Good) : CheckLevel.Bad;
                    items.Add(new HealthItem
                    {
                        Category = Loc.I["hc_sec"],
                        Title = Loc.I["hf_t"],
                        Value = av && rt
                            ? (sigAge <= 0 ? Loc.I["hf_on_fresh"] : string.Format(Loc.I["hf_on"], sigAge))
                            : Loc.I["hf_off"],
                        Level = level,
                        Hint = level == CheckLevel.Bad ? Loc.I["hf_h_bad"]
                            : level == CheckLevel.Warning ? Loc.I["hf_h_warn"] : null,
                    });
                }
                return;
            }
        }
        catch
        {
            items.Add(new HealthItem
            {
                Category = Loc.I["hc_sec"],
                Title = Loc.I["hf_t"],
                Value = Loc.I["hf_err"],
                Level = CheckLevel.Info,
            });
        }
    }

    private static void CheckDrivers(List<HealthItem> items)
    {
        try
        {
            int bad = 0;
            using var s = new ManagementObjectSearcher(
                "SELECT Name, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0");
            foreach (ManagementObject o in s.Get())
            {
                using (o) bad++;
            }
            items.Add(new HealthItem
            {
                Category = Loc.I["hc_hw"],
                Title = Loc.I["hdr_t"],
                Value = bad == 0 ? Loc.I["hdr_ok"] : string.Format(Loc.I["hdr_bad"], bad),
                Level = bad == 0 ? CheckLevel.Good : CheckLevel.Warning,
                Hint = bad == 0 ? null : Loc.I["hdr_h"],
            });
        }
        catch { }
    }

    private static void CheckUptime(List<HealthItem> items)
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem");
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                {
                    var boot = ManagementDateTimeConverter.ToDateTime(o["LastBootUpTime"].ToString());
                    var up = DateTime.Now - boot;
                    var level = up.TotalDays >= 7 ? CheckLevel.Warning : CheckLevel.Good;
                    items.Add(new HealthItem
                    {
                        Category = Loc.I["hc_sys"],
                        Title = Loc.I["hu_t"],
                        Value = up.TotalDays >= 1 ? string.Format(Loc.I["hu_vd"], $"{up.TotalDays:0}", up.Hours) : string.Format(Loc.I["hu_vh"], up.Hours, up.Minutes),
                        Level = level,
                        Hint = level == CheckLevel.Warning ? Loc.I["hu_h"] : null,
                    });
                }
                return;
            }
        }
        catch { }
    }

    private static void CheckBattery(List<HealthItem> items)
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT EstimatedChargeRemaining FROM Win32_Battery");
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                {
                    long charge = ToLong(o["EstimatedChargeRemaining"]);
                    items.Add(new HealthItem
                    {
                        Category = Loc.I["hc_hw"],
                        Title = Loc.I["hb_t"],
                        Value = string.Format(Loc.I["hb_v"], charge),
                        Level = CheckLevel.Info,
                    });
                }
                return;
            }
            // батареи нет - стационарный ПК
            items.Add(new HealthItem
            {
                Category = Loc.I["hc_hw"], Title = Loc.I["hb_t"],
                Value = Loc.I["hb_none"], Level = CheckLevel.Info,
            });
        }
        catch { }
    }

    private static void CheckJunk(List<HealthItem> items)
    {
        try
        {
            var (bytes, _) = FileOps.Measure(new[] { Path.GetTempPath() }, "*", true);
            var level = bytes > 2L * 1024 * 1024 * 1024 ? CheckLevel.Warning : CheckLevel.Good;
            items.Add(new HealthItem
            {
                Category = Loc.I["hc_sys"],
                Title = Loc.I["hj_t"],
                Value = string.Format(Loc.I["hj_v"], Format.Bytes(bytes)),
                Level = level,
                Hint = level == CheckLevel.Warning ? Loc.I["hj_h"] : null,
            });
        }
        catch { }
    }

    private static void CheckOs(List<HealthItem> items)
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Caption, Version, BuildNumber FROM Win32_OperatingSystem");
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                {
                    items.Add(new HealthItem
                    {
                        Category = Loc.I["hc_sys"],
                        Title = Loc.I["ho_t"],
                        Value = string.Format(Loc.I["ho_v"], o["Caption"], o["BuildNumber"]),
                        Level = CheckLevel.Info,
                    });
                }
                return;
            }
        }
        catch { }
    }

    private static bool ToBool(object? v) { try { return Convert.ToBoolean(v); } catch { return false; } }
    private static long ToLong(object? v) { try { return v is null ? 0 : Convert.ToInt64(v); } catch { return 0; } }
}
