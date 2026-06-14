using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace NakClean.ViewModels;

public enum ToastLevel { Info, Success, Warning, Error }

/// <summary>Всплывающее уведомление (тост) в правом нижнем углу.</summary>
public sealed class ToastVm : ViewModelBase
{
    public ToastVm(string message, ToastLevel level)
    {
        Message = message;
        Level = level;
    }

    public string Message { get; }
    public ToastLevel Level { get; }

    public string Glyph => Level switch
    {
        ToastLevel.Success => "✓",
        ToastLevel.Warning => "!",
        ToastLevel.Error => "✕",
        _ => "ℹ",
    };

    public Brush Accent => Level switch
    {
        ToastLevel.Success => Freeze(0x3D, 0xD6, 0x8C),
        ToastLevel.Warning => Freeze(0xDD, 0xB4, 0x4B),
        ToastLevel.Error => Freeze(0xE5, 0x48, 0x4D),
        _ => Freeze(0x6E, 0x9A, 0xD6),
    };

    public Brush Soft => Level switch
    {
        ToastLevel.Success => Freeze(0x3D, 0xD6, 0x8C, 0x26),
        ToastLevel.Warning => Freeze(0xDD, 0xB4, 0x4B, 0x26),
        ToastLevel.Error => Freeze(0xE5, 0x48, 0x4D, 0x26),
        _ => Freeze(0x6E, 0x9A, 0xD6, 0x26),
    };

    private static Brush Freeze(byte r, byte g, byte b, byte a = 0xFF)
    {
        var br = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        br.Freeze();
        return br;
    }
}

/// <summary>Глобальная очередь тостов. Использование: ToastService.Ok("..."), .Warn, .Error, .Info.</summary>
public sealed class ToastService
{
    public static ToastService I { get; } = new();
    private ToastService() { }

    public ObservableCollection<ToastVm> Toasts { get; } = new();

    public void Show(string message, ToastLevel level = ToastLevel.Info, int seconds = 4)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        void Add()
        {
            var t = new ToastVm(message, level);
            Toasts.Add(t);
            if (Toasts.Count > 4) Toasts.RemoveAt(0);   // не больше 4 на экране

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            timer.Tick += (_, _) => { timer.Stop(); Toasts.Remove(t); };
            timer.Start();
        }

        var disp = Application.Current?.Dispatcher;
        if (disp != null && !disp.CheckAccess()) disp.Invoke(Add);
        else Add();
    }

    public static void Info(string m) => I.Show(m, ToastLevel.Info);
    public static void Ok(string m) => I.Show(m, ToastLevel.Success);
    public static void Warn(string m) => I.Show(m, ToastLevel.Warning);
    public static void Error(string m) => I.Show(m, ToastLevel.Error);
}
