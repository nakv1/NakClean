using System.Collections.ObjectModel;
using NakClean.Services;

namespace NakClean.ViewModels;

/// <summary>Строка списка остатков: галочка (по умолчанию снята), что это и сколько весит.</summary>
public sealed class LeftoverVm : ViewModelBase
{
    public LeftoverVm(Leftover item) => Item = item;
    public Leftover Item { get; }

    private bool _selected;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }

    public string Display => Item.Display;
    public string Glyph => Item.Kind switch
    {
        LeftoverKind.Folder => "📁",
        LeftoverKind.Shortcut => "🔗",
        LeftoverKind.Registry => "🗂",
        LeftoverKind.Startup => "🚀",
        _ => "🕘",
    };
    public string KindText => Loc.I["lo_k_" + Item.Kind.ToString().ToLowerInvariant()];
    public string SizeText => Item.Kind is LeftoverKind.Folder or LeftoverKind.Shortcut ? Format.Bytes(Item.Size) : "";
}

// ---------- Удаление программ: остатки после деинсталлятора ----------
public sealed partial class MainViewModel
{
    public ObservableCollection<LeftoverVm> Leftovers { get; } = new();
    public RelayCommand RemoveLeftoversCommand { get; private set; } = null!;
    public RelayCommand CloseLeftoversCommand { get; private set; } = null!;

    private bool _leftoversOpen;
    public bool LeftoversOpen { get => _leftoversOpen; private set => Set(ref _leftoversOpen, value); }

    private string _leftoversTitle = "";
    public string LeftoversTitle { get => _leftoversTitle; private set => Set(ref _leftoversTitle, value); }

    private bool _leftoversBusy;
    public bool LeftoversBusy
    {
        get => _leftoversBusy;
        private set { if (Set(ref _leftoversBusy, value)) RemoveLeftoversCommand.RaiseCanExecuteChanged(); }
    }

    private void InitLeftovers()
    {
        RemoveLeftoversCommand = new RelayCommand(async () => await RemoveLeftoversAsync(), () => !LeftoversBusy);
        CloseLeftoversCommand = new RelayCommand(() => { LeftoversOpen = false; Leftovers.Clear(); });
    }

    /// <summary>
    /// Деинсталлятор запущен - ждём, пока запись программы исчезнет из реестра (человек прошёл мастер удаления).
    /// Деинсталляторы часто перезапускают себя из временной папки, поэтому ждём не процесс, а результат.
    /// Закрыли мастер, ничего не удалив, - через 30 минут перестаём ждать молча.
    /// </summary>
    private async Task WatchUninstallAsync(Models.InstalledApp app, LeftoverService.AppPrint print)
    {
        var until = DateTime.Now.AddMinutes(30);
        while (DateTime.Now < until)
        {
            await Task.Delay(2000);
            if (!await Task.Run(() => InstalledAppsService.StillInstalled(app))) break;
        }
        if (InstalledAppsService.StillInstalled(app)) return;

        await LoadAppsAsync();
        if (System.Windows.MessageBox.Show(string.Format(Loc.I["lo_ask"], print.Name), "NakClean",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.Yes)
            return;
        await FindLeftoversAsync(print);
    }

    private async Task FindLeftoversAsync(LeftoverService.AppPrint print)
    {
        LeftoversBusy = true;
        AppsNote = string.Format(Loc.I["lo_searching"], print.Name);
        List<Leftover> found;
        try { found = await Task.Run(() => LeftoverService.Find(print)); }
        catch { found = new(); }
        LeftoversBusy = false;

        Leftovers.Clear();
        if (found.Count == 0)
        {
            AppsNote = string.Format(Loc.I["lo_none"], print.Name);
            ToastService.Ok(AppsNote);
            return;
        }
        foreach (var f in found) Leftovers.Add(new LeftoverVm(f));
        long size = found.Where(f => f.Kind is LeftoverKind.Folder or LeftoverKind.Shortcut).Sum(f => f.Size);
        LeftoversTitle = string.Format(Loc.I["lo_title"], print.Name, found.Count, Format.Bytes(size));
        AppsNote = string.Format(Loc.I["lo_short"], print.Name, found.Count, Format.Bytes(size));
        LeftoversOpen = true;
    }

    private async Task RemoveLeftoversAsync()
    {
        var sel = Leftovers.Where(l => l.Selected).Select(l => l.Item).ToList();
        if (sel.Count == 0) { AppsNote = Loc.I["lo_nosel"]; return; }
        if (System.Windows.MessageBox.Show(string.Format(Loc.I["lo_confirm"], sel.Count), "NakClean",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.Yes)
            return;

        LeftoversBusy = true;
        var owner = System.Windows.Application.Current?.MainWindow is { } w
            ? new System.Windows.Interop.WindowInteropHelper(w).Handle : IntPtr.Zero;
        var (ok, fail) = await Task.Run(() => LeftoverService.Remove(sel, owner));
        LeftoversBusy = false;

        foreach (var vm in Leftovers.Where(l => l.Selected).ToList()) Leftovers.Remove(vm);
        AppsNote = string.Format(Loc.I["lo_done"], ok, fail);
        if (fail > 0) ToastService.Warn(AppsNote); else ToastService.Ok(AppsNote);
        if (Leftovers.Count == 0) LeftoversOpen = false;
        RefreshBackups();   // в «Реестр → Резервные копии» появилась копия удалённых ключей
    }
}
