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

    private static void Log(string source, Exception? ex)
    {
        if (ex is null) return;
        try
        {
            File.AppendAllText(LogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: {ex}\n\n");
        }
        catch { /* лог не должен ломать приложение */ }
    }
}
