using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using NakClean.Services;
using NakClean.ViewModels;

namespace NakClean;

public partial class MainWindow : Window
{
    private IntPtr _hwnd;
    private readonly AppSettings _settings = AppSettings.Load();
    private bool _isLight;

    public MainWindow()
    {
        InitializeComponent();
        Tag = Services.Provenance.Stamp();   // вшитый отпечаток автора (не удалять)
        Treemap.NodeClicked += OnTreemapNodeClicked;

        // применяем сохранённые настройки (язык/тема)
        Loc.I.Lang = _settings.Language;
        _isLight = _settings.Theme == "light";
        ApplyTheme(_isLight);
        UpdateToolButtons();

        // живой опрос только на «Обзоре» и когда окно не свёрнуто
        NavDash.Checked += (_, _) => UpdateLiveActive();
        NavDash.Unchecked += (_, _) => UpdateLiveActive();
        StateChanged += (_, _) => UpdateLiveActive();

        // ленивая загрузка данных вкладок при первом открытии
        NavCheck.Checked += (_, _) => Vm?.EnsureHealthLoaded();
        NavStartup.Checked += (_, _) => Vm?.EnsureStartupLoaded();
        NavApps.Checked += (_, _) => Vm?.EnsureAppsLoaded();
        NavDiag.Checked += (_, _) => Vm?.EnsureDiagnosticsLoaded();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void UpdateLiveActive()
        => Vm?.SetLiveActive(NavDash.IsChecked == true && WindowState != WindowState.Minimized);

    // клик по квадрату карты → раскрыть и выделить этот узел в дереве
    private void OnTreemapNodeClicked(string fullPath)
    {
        if (DataContext is not MainViewModel vm) return;
        FlTabTree.IsChecked = true;             // показать вкладку «Дерево»
        var row = vm.RevealInTree(fullPath);
        if (row is null) return;
        TreeListBox.SelectedItem = row;
        TreeListBox.ScrollIntoView(row);
    }

    // ---------- Поиск файлов: дерево/файлы - двойной клик и контекстное меню ----------
    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = VisualTreeHelper.GetParent(d);
        return d as T;
    }

    private static string? PathOf(object? dataContext) => dataContext switch
    {
        TreeRowVm t => t.FullPath,
        FileRowVm f => f.Path,
        _ => null,
    };

    // правый клик по строке (дерево или файлы) → контекстное меню
    private void Row_RightClick(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item is null) return;
        item.IsSelected = true;
        string? path = PathOf(item.DataContext);
        if (path is null) return;
        e.Handled = true;
        bool isFolder = item.DataContext is TreeRowVm { HasChildren: true };
        ShowFileMenu(item, path, isFolder);
    }

    // надёжное (полностью управляемое) меню действий - без падений
    private void ShowFileMenu(FrameworkElement target, string path, bool isFolder)
    {
        var menu = new ContextMenu { PlacementTarget = target };

        MenuItem Mk(string header, Action act)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => { try { act(); } catch { } };
            return mi;
        }

        menu.Items.Add(Mk(Loc.I[isFolder ? "ctx_open_folder" : "ctx_open"],
            () => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })));
        menu.Items.Add(Mk(Loc.I["ctx_reveal"], () => SelectInExplorer(path)));
        if (!isFolder)
            menu.Items.Add(Mk(Loc.I["ctx_openwith"],
                () => Process.Start("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {path}")));
        menu.Items.Add(Mk(Loc.I["ctx_copypath"], () => Clipboard.SetText(path)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Mk(Loc.I["ctx_props"], () => SHObjectProperties(_hwnd, 2 /*SHOP_FILEPATH*/, path, null)));

        menu.IsOpen = true;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHObjectProperties(IntPtr hwnd, uint shopObjectType, string pszObjectName, string? pszPropertyPage);

    // двойной клик по строке дерева: папку - развернуть/свернуть, файл - показать в Проводнике
    private void TreeRow_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is not TreeRowVm t) return;
        e.Handled = true;
        if (t.HasChildren) t.ToggleCommand.Execute(null);
        else SelectInExplorer(t.FullPath);
    }

    // двойной клик по файлу: показать в Проводнике
    private void FileRow_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is not FileRowVm f) return;
        e.Handled = true;
        SelectInExplorer(f.Path);
    }

    private static void SelectInExplorer(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
    }

    // ---------- Окно ----------
    private void MinBtn_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaxBtn_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
        => Close();

    // ---------- Тёмная рамка окна (DWM) ----------
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        ApplyDarkTitleBar();
        ApplySurfaces();
    }

    // ---------- Тема (тёмная / светлая) ----------
    private void ThemeBtn_Click(object sender, RoutedEventArgs e)
    {
        _isLight = !_isLight;
        ApplyTheme(_isLight);
        _settings.Theme = _isLight ? "light" : "dark";
        _settings.Save();
    }

    private void ApplyTheme(bool light)
    {
        _isLight = light;
        var res = Application.Current.Resources;
        // текст: тёплый почти-чёрный на светлой / мягкий белый на тёмной
        res["TextBrush"] = new SolidColorBrush(light ? Color.FromRgb(0x21, 0x1D, 0x16) : Color.FromRgb(0xF5, 0xF5, 0xF7));
        res["MutedBrush"] = new SolidColorBrush(light ? Color.FromRgb(0x6E, 0x69, 0x5E) : Color.FromRgb(0x8A, 0x8A, 0x94));
        res["TrackBrush"] = new SolidColorBrush(light ? Color.FromRgb(0xED, 0xE9, 0xE0) : Color.FromRgb(0x26, 0x26, 0x2C));

        // обводка карточек: на светлой - тёплая мягкая рамка, на тёмной - еле заметная белая
        res["CardBorderBrush"] = new SolidColorBrush(light
            ? Color.FromRgb(0xE6, 0xDF, 0xD2) : Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));
        // тень карточек: на светлой - мягкая лёгкая (тёплая), на тёмной - глубокая
        res["CardShadow"] = light
            ? new System.Windows.Media.Effects.DropShadowEffect { Color = Color.FromRgb(0x6B, 0x57, 0x33), BlurRadius = 16, ShadowDepth = 2, Opacity = 0.13 }
            : new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 30, ShadowDepth = 0, Opacity = 0.5 };

        // акцентный ТЕКСТ/иконки: на светлой - глубже золото (антик) для контраста на белом
        res["AccentTextBrush"] = new SolidColorBrush(light ? Color.FromRgb(0x9A, 0x77, 0x1C) : Color.FromRgb(0xDD, 0xB4, 0x4B));
        // состояния контролов (ховер/заливка/обводка): на светлой - лёгкие тёмные, на тёмной - лёгкие белые
        res["HoverBrush"] = new SolidColorBrush(light ? Color.FromArgb(0x0D, 0, 0, 0) : Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        res["CtrlFillBrush"] = new SolidColorBrush(light ? Color.FromArgb(0x07, 0, 0, 0) : Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        res["CtrlBorderBrush"] = new SolidColorBrush(light ? Color.FromArgb(0x1F, 0, 0, 0) : Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));

        ApplyDarkTitleBar();
        ApplySurfaces();
        UpdateToolButtons();
    }

    private void ApplyDarkTitleBar()
    {
        if (_hwnd == IntPtr.Zero) return;
        int dark = _isLight ? 0 : 1;
        DwmSetWindowAttribute(_hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    // ---------- О программе (открывается иконкой снизу) ----------
    private void AboutBtn_Click(object sender, RoutedEventArgs e) => NavAbout.IsChecked = true;

    // ---------- Язык ----------
    private void LangBtn_Click(object sender, RoutedEventArgs e)
    {
        Loc.I.Lang = Loc.I.IsEn ? "ru" : "en";
        UpdateToolButtons();
        _settings.Language = Loc.I.Lang;
        _settings.Save();
    }

    private void UpdateToolButtons()
    {
        LangText.Text = Loc.I.IsEn ? "RU" : "EN";   // показываем язык, на который переключим
        ThemeIcon.Text = _isLight ? "☀" : "🌙";
    }

    // ---------- Ссылка на GitHub ----------
    private void GithubLink_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://github.com/nakv1") { UseShellExecute = true }); } catch { }
    }

    private void ApplySurfaces()
    {
        var res = Application.Current.Resources;
        if (_isLight)
        {
            // премиальная тёплая «кремово-золотая» светлая тема
            res["WindowSurface"] = WarmGradient(0xFB, 0xF9, 0xF5, 0xF2, 0xEE, 0xE5);   // фон: тёплый крем
            res["CardSurface"] = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)); // карточки: чистый белый
            res["SidebarSurface"] = WarmGradient(0xF5, 0xF1, 0xEA, 0xEC, 0xE7, 0xDD);  // сайдбар: тёплая панель
        }
        else
        {
            res["WindowSurface"] = res["BgGradient"];
            res["CardSurface"] = res["CardGradient"];
            res["SidebarSurface"] = res["SidebarGradient"];
        }
    }

    private static LinearGradientBrush WarmGradient(byte r1, byte g1, byte b1, byte r2, byte g2, byte b2)
    {
        var br = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0.9, 1) };
        br.GradientStops.Add(new GradientStop(Color.FromRgb(r1, g1, b1), 0));
        br.GradientStops.Add(new GradientStop(Color.FromRgb(r2, g2, b2), 1));
        br.Freeze();
        return br;
    }
}
