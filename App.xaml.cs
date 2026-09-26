using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace NakClean;

public partial class App : Application
{
    private static readonly string LogPath =
        Path.Combine(AppContext.BaseDirectory, "nakclean-errors.log");

    public App()
    {
        // Глобальный перехват: прога не должна падать молча.
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log("AppDomain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log("Task", e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        bool afterUpdate = e.Args.Contains(NakClean.Services.UpdateService.UpdatedArg);
        if (!NakClean.Services.SingleInstance.Acquire(afterUpdate))
        {
            NakClean.Services.SingleInstance.ActivateFirst();
            Shutdown();
            return;
        }
        new MainWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        NakClean.Services.SingleInstance.Release();
        base.OnExit(e);
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("Dispatcher", e.Exception);
        MessageBox.Show(
            string.Format(NakClean.Services.Loc.I["app_error"], e.Exception.Message),
            "NakClean", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true; // не даём приложению закрыться
    }

    // лог не растёт бесконечно: больше 1 МБ - оставляем только свежий конец (~256 КБ)
    private const long LogMaxBytes = 1024 * 1024;
    private const int LogKeepChars = 256 * 1024;

    private static void Log(string source, Exception? ex)
    {
        if (ex is null) return;
        try
        {
            TrimLog();
            File.AppendAllText(LogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: {ex}\n\n");
        }
        catch { /* лог не должен ломать приложение */ }
    }

    private static void TrimLog()
    {
        var fi = new FileInfo(LogPath);
        if (!fi.Exists || fi.Length <= LogMaxBytes) return;

        string text = File.ReadAllText(LogPath);
        string tail = text[^Math.Min(LogKeepChars, text.Length)..];
        int cut = tail.IndexOf("\n[", StringComparison.Ordinal);   // начать с целой записи
        if (cut >= 0) tail = tail[(cut + 1)..];
        File.WriteAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] (старые записи обрезаны)\n\n" + tail);
    }
}
