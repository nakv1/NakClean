using System.IO;
using Microsoft.Win32;
using NakClean.Models;

namespace NakClean.Services;

/// <summary>Определяет список категорий очистки с реальными путями Windows.</summary>
public static class CleanEngine
{
    private static string Env(string var) => Environment.GetEnvironmentVariable(var) ?? "";
    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string WinDir => Env("WINDIR");

    /// <summary>Путь установки Steam из реестра (для кэша шейдеров).</summary>
    private static string SteamPath()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return k?.GetValue("SteamPath") as string ?? "";
        }
        catch { return ""; }
    }

    /// <summary>Возвращает только реально существующие пути.</summary>
    private static List<string> Existing(params string[] paths)
        => paths.Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p)).ToList();

    /// <summary>Раскрывает шаблон с одной «*» в середине пути (для профилей браузеров).</summary>
    private static List<string> Glob(string baseDir, string subPattern, string tail)
    {
        var result = new List<string>();
        if (!Directory.Exists(baseDir)) return result;
        try
        {
            foreach (var profile in Directory.GetDirectories(baseDir, subPattern))
            {
                string p = string.IsNullOrEmpty(tail) ? profile : Path.Combine(profile, tail);
                if (Directory.Exists(p)) result.Add(p);
            }
        }
        catch { }
        return result;
    }

    public static List<CleanCategory> BuildCategories()
    {
        string temp = Path.GetTempPath();
        string winTemp = Path.Combine(WinDir, "Temp");
        string prefetch = Path.Combine(WinDir, "Prefetch");
        string winUpdate = Path.Combine(WinDir, "SoftwareDistribution", "Download");
        string crashDumps = Path.Combine(Local, "CrashDumps");
        string thumbs = Path.Combine(Local, "Microsoft", "Windows", "Explorer");
        string recent = Path.Combine(Roaming, "Microsoft", "Windows", "Recent");
        string winLogs = Path.Combine(WinDir, "Logs");
        string sysDrive = Env("SystemDrive");
        string winOld = string.IsNullOrEmpty(sysDrive) ? "" : sysDrive + "\\Windows.old";
        string d3d = Path.Combine(Local, "D3DSCache");

        // отчёты об ошибках Windows (WER)
        string progData = Env("PROGRAMDATA");
        var wer = Existing(
            Path.Combine(Local, "Microsoft", "Windows", "WER"),
            Path.Combine(progData, "Microsoft", "Windows", "WER"));

        // кэши шейдеров видеокарты (безопасно, пересоздаются)
        var shaderCache = Existing(
            d3d,
            Path.Combine(Local, "NVIDIA", "DXCache"),
            Path.Combine(Local, "NVIDIA", "GLCache"),
            Path.Combine(Local, "NVIDIA Corporation", "NV_Cache"),
            Path.Combine(Local, "AMD", "DxCache"));

        // Кэши браузеров
        var chrome = new List<string>();
        chrome.AddRange(Glob(Path.Combine(Local, "Google", "Chrome", "User Data"), "*", "Cache"));
        chrome.AddRange(Glob(Path.Combine(Local, "Google", "Chrome", "User Data"), "*", Path.Combine("Code Cache", "js")));

        var edge = new List<string>();
        edge.AddRange(Glob(Path.Combine(Local, "Microsoft", "Edge", "User Data"), "*", "Cache"));

        var firefox = Glob(Path.Combine(Local, "Mozilla", "Firefox", "Profiles"), "*", "cache2");

        // ---------- следы: история открытых файлов (списки переходов) ----------
        var jumplists = Existing(
            Path.Combine(Roaming, "Microsoft", "Windows", "Recent", "AutomaticDestinations"),
            Path.Combine(Roaming, "Microsoft", "Windows", "Recent", "CustomDestinations"));

        // ---------- кэши конкретных программ (безопасно, пересоздаются) ----------
        var discord = Existing(
            Path.Combine(Roaming, "discord", "Cache"),
            Path.Combine(Roaming, "discord", "Code Cache"),
            Path.Combine(Roaming, "discord", "GPUCache"));

        var spotify = Existing(
            Path.Combine(Local, "Spotify", "Storage"),
            Path.Combine(Local, "Spotify", "Data"));

        var teams = Existing(
            Path.Combine(Roaming, "Microsoft", "Teams", "Cache"),
            Path.Combine(Roaming, "Microsoft", "Teams", "Code Cache"),
            Path.Combine(Roaming, "Microsoft", "Teams", "GPUCache"),
            Path.Combine(Roaming, "Microsoft", "Teams", "blob_storage"));

        var slack = Existing(
            Path.Combine(Roaming, "Slack", "Cache"),
            Path.Combine(Roaming, "Slack", "GPUCache"),
            Path.Combine(Roaming, "Slack", "Service Worker", "CacheStorage"));

        string steam = SteamPath();
        var steamShader = string.IsNullOrEmpty(steam)
            ? new List<string>()
            : Existing(Path.Combine(steam, "steamapps", "shadercache"));

        var brave = new List<string>();
        brave.AddRange(Glob(Path.Combine(Local, "BraveSoftware", "Brave-Browser", "User Data"), "*", "Cache"));
        brave.AddRange(Glob(Path.Combine(Local, "BraveSoftware", "Brave-Browser", "User Data"), "*", Path.Combine("Code Cache", "js")));

        var opera = Existing(
            Path.Combine(Local, "Opera Software", "Opera Stable", "Cache"),
            Path.Combine(Roaming, "Opera Software", "Opera Stable", "Cache"));

        var yandex = new List<string>();
        yandex.AddRange(Glob(Path.Combine(Local, "Yandex", "YandexBrowser", "User Data"), "*", "Cache"));
        yandex.AddRange(Glob(Path.Combine(Local, "Yandex", "YandexBrowser", "User Data"), "*", Path.Combine("Code Cache", "js")));

        var vivaldi = new List<string>();
        vivaldi.AddRange(Glob(Path.Combine(Local, "Vivaldi", "User Data"), "*", "Cache"));
        vivaldi.AddRange(Glob(Path.Combine(Local, "Vivaldi", "User Data"), "*", Path.Combine("Code Cache", "js")));

        // Telegram: картинки и видео из чатов (user_data, user_data#2... - по аккаунтам).
        // Переписка и вход в аккаунт хранятся в других файлах tdata - их не трогаем.
        var telegram = new List<string>();
        telegram.AddRange(Glob(Path.Combine(Roaming, "Telegram Desktop", "tdata"), "user_data*", ""));
        foreach (var pkg in Glob(Path.Combine(Local, "Packages"), "TelegramMessengerLLP.TelegramDesktop_*", ""))
            telegram.AddRange(Glob(Path.Combine(pkg, "LocalCache", "Roaming", "Telegram Desktop UWP", "tdata"), "user_data*", ""));

        // системные дампы после синего экрана (MEMORY.DMP бывает в десятки ГБ) и отчёты о сбоях драйверов
        var sysDumpFiles = File.Exists(Path.Combine(WinDir, "MEMORY.DMP"))
            ? new List<string> { Path.Combine(WinDir, "MEMORY.DMP") } : new List<string>();
        var sysDumpDirs = Existing(Path.Combine(WinDir, "Minidump"), Path.Combine(WinDir, "LiveKernelReports"));

        // «Оптимизация доставки»: копии обновлений, которые Windows раздаёт другим ПК
        var delivery = Existing(
            Path.Combine(WinDir, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache"),
            Path.Combine(WinDir, "SoftwareDistribution", "DeliveryOptimization"));

        // распакованные установщики драйверов - нужны только во время установки.
        // NVIDIA\Installer2 НЕ трогаем: без него может не удалиться драйвер.
        var driverPkgs = string.IsNullOrEmpty(sysDrive) ? new List<string>() : Existing(
            sysDrive + "\\AMD",
            sysDrive + "\\NVIDIA",
            Path.Combine(progData, "NVIDIA Corporation", "Downloader"));

        var list = new List<CleanCategory>
        {
            new()
            {
                Id = "temp_user", Group = "Система", Glyph = "🗂",
                Name = "Временные файлы (пользователь)",
                Description = "Папка %TEMP% - самый частый источник мусора",
                Roots = Existing(temp),
            },
            new()
            {
                Id = "temp_win", Group = "Система", Glyph = "🪟",
                Name = "Временные файлы Windows",
                Description = "Windows\\Temp - общесистемная временная папка",
                Roots = Existing(winTemp), NeedsAdmin = true,
            },
            new()
            {
                Id = "recycle", Group = "Система", Glyph = "🗑",
                Name = "Корзина",
                Description = "Удалённые файлы, ещё занимающие место",
                Kind = TargetKind.RecycleBin,
            },
            new()
            {
                Id = "crashdumps", Group = "Система", Glyph = "💥",
                Name = "Дампы и отчёты об ошибках",
                Description = "CrashDumps - снимки упавших программ",
                Roots = Existing(crashDumps),
            },
            new()
            {
                Id = "thumbnails", Group = "Система", Glyph = "🖼",
                Name = "Кэш эскизов",
                Description = "thumbcache_*.db - миниатюры проводника",
                Roots = Existing(thumbs), Pattern = "thumbcache_*.db", Recursive = false,
            },
            new()
            {
                Id = "prefetch", Group = "Система", Glyph = "⚡",
                Name = "Prefetch",
                Description = "Данные предзагрузки приложений (Windows пересоздаст)",
                Roots = Existing(prefetch), NeedsAdmin = true,
            },
            new()
            {
                Id = "winupdate", Group = "Система", Glyph = "⬇",
                Name = "Кэш Центра обновлений",
                Description = "SoftwareDistribution\\Download - скачанные обновления",
                Roots = Existing(winUpdate), NeedsAdmin = true,
            },
            new()
            {
                Id = "recent", Group = "Следы", Glyph = "🕘",
                Name = "Недавние документы",
                Description = "Ярлыки в списке «недавние» (история, не файлы)",
                Roots = Existing(recent), Recursive = false,
            },
            new()
            {
                Id = "jumplists", Group = "Следы", Glyph = "📌",
                Name = "Списки переходов",
                Description = "История недавних файлов в панели задач и Пуске",
                Roots = jumplists, Recursive = false,
            },
            new()
            {
                Id = "wer", Group = "Система", Glyph = "📋",
                Name = "Отчёты об ошибках Windows (WER)",
                Description = "Архивы отчётов о сбоях приложений",
                Roots = wer,
            },
            new()
            {
                Id = "shadercache", Group = "Система", Glyph = "🎮",
                Name = "Кэш шейдеров видеокарты",
                Description = "DirectX / NVIDIA / AMD - пересоздаётся автоматически",
                Roots = shaderCache,
            },
            new()
            {
                Id = "winlogs", Group = "Система", Glyph = "📄",
                Name = "Системные логи Windows",
                Description = "Windows\\Logs - журналы установки и обслуживания",
                Roots = Existing(winLogs), NeedsAdmin = true,
            },
            new()
            {
                Id = "sysdumps", Group = "Система", Glyph = "🛑",
                Name = "Дампы памяти после синего экрана",
                Description = "MEMORY.DMP и Minidump - бывают в десятки ГБ. Нужны, только если разбираетесь с причиной синего экрана",
                Roots = sysDumpDirs, Files = sysDumpFiles, NeedsAdmin = true,
            },
            new()
            {
                Id = "delivery", Group = "Система", Glyph = "📡",
                Name = "Оптимизация доставки",
                Description = "Копии обновлений, которые Windows раздаёт другим компьютерам. Удаляются штатной командой Windows",
                Roots = delivery, NeedsAdmin = true,
                CustomClean = ct => CleanDelivery(delivery, ct),
            },
            new()
            {
                Id = "driverpkgs", Group = "Система", Glyph = "🧩",
                Name = "Остатки установщиков драйверов",
                Description = "Распакованные установщики AMD и скачанные драйверы NVIDIA - нужны только во время установки",
                Roots = driverPkgs, NeedsAdmin = true,
            },
            new()
            {
                Id = "winold", Group = "Система", Glyph = "📦",
                Name = "Папка Windows.old",
                Description = "Старая копия Windows после обновления (может весить десятки ГБ)",
                Roots = Existing(winOld), NeedsAdmin = true, Selected = false,
            },
            new()
            {
                Id = "chrome", Group = "Браузеры", Glyph = "🌐",
                Name = "Кэш Google Chrome",
                Description = "Кэш страниц и кода (история и пароли не трогаем)",
                Roots = chrome,
            },
            new()
            {
                Id = "edge", Group = "Браузеры", Glyph = "🧭",
                Name = "Кэш Microsoft Edge",
                Description = "Кэш страниц Edge",
                Roots = edge,
            },
            new()
            {
                Id = "firefox", Group = "Браузеры", Glyph = "🦊",
                Name = "Кэш Mozilla Firefox",
                Description = "cache2 - кэш страниц Firefox",
                Roots = firefox,
            },
            new()
            {
                Id = "brave", Group = "Браузеры", Glyph = "🦁",
                Name = "Кэш Brave",
                Description = "Кэш страниц и кода Brave",
                Roots = brave,
            },
            new()
            {
                Id = "opera", Group = "Браузеры", Glyph = "🅾",
                Name = "Кэш Opera",
                Description = "Кэш страниц Opera",
                Roots = opera,
            },
            new()
            {
                Id = "yandex", Group = "Браузеры", Glyph = "Я",
                Name = "Кэш Яндекс.Браузера",
                Description = "Кэш страниц и кода (история, пароли и вкладки не трогаются)",
                Roots = yandex,
            },
            new()
            {
                Id = "vivaldi", Group = "Браузеры", Glyph = "🌐",
                Name = "Кэш Vivaldi",
                Description = "Кэш страниц и кода Vivaldi",
                Roots = vivaldi,
            },
            new()
            {
                Id = "discord", Group = "Программы", Glyph = "🎧",
                Name = "Кэш Discord",
                Description = "Кэш страниц, кода и GPU (история чатов не трогается)",
                Roots = discord,
            },
            new()
            {
                Id = "spotify", Group = "Программы", Glyph = "🎵",
                Name = "Кэш Spotify",
                Description = "Кэш загруженных данных. Плейлисты останутся, но скачанные для офлайна треки придётся скачать заново",
                Roots = spotify,
            },
            new()
            {
                Id = "teams", Group = "Программы", Glyph = "👥",
                Name = "Кэш Microsoft Teams",
                Description = "Кэш страниц, кода, GPU и вложений",
                Roots = teams,
            },
            new()
            {
                Id = "slack", Group = "Программы", Glyph = "💬",
                Name = "Кэш Slack",
                Description = "Кэш страниц и сервис-воркеров",
                Roots = slack,
            },
            new()
            {
                Id = "telegram", Group = "Программы", Glyph = "✈",
                Name = "Кэш Telegram",
                Description = "Картинки и видео из чатов - скачаются снова при просмотре. Переписка и вход не трогаются. Telegram нужно закрыть",
                Roots = telegram, MustBeClosed = new[] { "Telegram" },
            },
            new()
            {
                Id = "steam_shader", Group = "Программы", Glyph = "🎮",
                Name = "Кэш шейдеров Steam",
                Description = "shadercache - пересоздаётся при запуске игр",
                Roots = steamShader,
            },
        };

        // Скрываем категории, для которых на этой машине нет ни одного пути
        // (кроме корзины - она всегда есть).
        return list
            .Where(c => c.Kind == TargetKind.RecycleBin || c.Roots.Count > 0 || c.Files.Count > 0)
            .ToList();
    }

    /// <summary>
    /// «Оптимизация доставки»: штатная команда Windows (как «Очистка диска») - служба сама отпускает файлы.
    /// Не помогла - удаляем обычным способом. Освобождено = сколько было минус сколько осталось.
    /// </summary>
    private static long CleanDelivery(IReadOnlyList<string> roots, CancellationToken ct)
    {
        var (before, _) = FileOps.Measure(roots, "*", true, ct);
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -Command \"Delete-DeliveryOptimizationCache -Force\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p != null)
            {
                _ = p.StandardError.ReadToEndAsync();
                p.StandardOutput.ReadToEnd();
                p.WaitForExit(120_000);
            }
        }
        catch { }
        var (after, _) = FileOps.Measure(roots, "*", true, ct);
        if (after >= before && after > 0)
        {
            FileOps.Delete(roots, "*", true, null, ct);
            (after, _) = FileOps.Measure(roots, "*", true, ct);
        }
        return Math.Max(0, before - after);
    }
}
