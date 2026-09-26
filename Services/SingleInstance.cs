using System.Runtime.InteropServices;

namespace NakClean.Services;

/// <summary>
/// Только одна копия NakClean (любая сборка - портабл и обычная делят настройки и журнал изменений).
/// Мьютекс ОС снимается сам, даже если процесс упал - залипнуть не может.
/// </summary>
public static class SingleInstance
{
    private const string MutexName = @"Local\NakClean.SingleInstance";
    private static Mutex? _mutex;
    private static bool _owned;

    /// <summary>Сообщение «покажись» для уже открытой копии.</summary>
    public static readonly uint ShowMessage = RegisterWindowMessage("NakClean.ShowMe");

    /// <param name="waitForPrevious">после самообновления старая копия ещё закрывается - ждём её, а не выходим</param>
    public static bool Acquire(bool waitForPrevious)
    {
        _mutex = new Mutex(false, MutexName);
        try { _owned = _mutex.WaitOne(waitForPrevious ? TimeSpan.FromSeconds(15) : TimeSpan.Zero); }
        catch (AbandonedMutexException) { _owned = true; }   // прошлая копия упала - мьютекс перешёл к нам
        return _owned;
    }

    public static void Release()
    {
        if (!_owned) return;
        try { _mutex?.ReleaseMutex(); } catch { }
        _owned = false;
    }

    /// <summary>Попросить открытую копию выйти на передний план (разрешаем ей забрать фокус у нас).</summary>
    public static void ActivateFirst()
    {
        AllowSetForegroundWindow(ASFW_ANY);
        PostMessage(HWND_BROADCAST, ShowMessage, IntPtr.Zero, IntPtr.Zero);
    }

    private const int ASFW_ANY = -1;
    private static readonly IntPtr HWND_BROADCAST = new(0xffff);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);
}
