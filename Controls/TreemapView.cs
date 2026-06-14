using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NakClean.Services;

namespace NakClean.Controls;

/// <summary>
/// Карта диска (squarified treemap): вложенные цветные прямоугольники по размеру.
/// Клик по папке - заход внутрь (двусторонняя привязка Root). Тултип с путём и размером.
/// </summary>
public sealed class TreemapView : FrameworkElement
{
    private const int MaxDepth = 4;
    private readonly List<(Rect rect, Child item)> _hits = new();
    private readonly Typeface _font = new("Segoe UI");
    private static readonly Pen Border = MakePen();
    private static readonly Brush LabelShadow = MakeBrush(Color.FromArgb(0xC0, 0, 0, 0));
    private static readonly Brush LabelDim = MakeBrush(Color.FromArgb(0xE0, 0xF0, 0xF0, 0xF0));

    /// <summary>Одиночный клик по блоку - навигация в дереве (полный путь узла).</summary>
    public event Action<string>? NodeClicked;

    private static Brush MakeBrush(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public TreemapView()
    {
        // прозрачный фон - пустая карта показывает поверхность карточки родителя (по теме),
        // при наличии данных её полностью перекрывают цветные блоки
        Background = Brushes.Transparent;
        ClipToBounds = true;
        Cursor = Cursors.Hand;
    }

    private static Pen MakePen()
    {
        var p = new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), 0.6);
        p.Freeze();
        return p;
    }

    public Brush Background { get; set; }

    public static readonly DependencyProperty RootProperty =
        DependencyProperty.Register(nameof(Root), typeof(TreeNode), typeof(TreemapView),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                (d, _) => ((TreemapView)d).InvalidateVisual()));

    public TreeNode? Root
    {
        get => (TreeNode?)GetValue(RootProperty);
        set => SetValue(RootProperty, value);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        _hits.Clear();
        var area = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(Background, null, area);

        var root = Root;
        if (root is null || !root.HasChildren || area.Width < 4 || area.Height < 4) return;

        Squarify(root.SortedChildren(), new Rect(2, 2, area.Width - 4, area.Height - 4), dc, 0, default);
    }

    private void Squarify(List<Child> items, Rect r, DrawingContext dc, int depth, Color parentColor)
    {
        items = items.Where(n => n.Size > 0).ToList();
        if (items.Count == 0 || r.Width < 1 || r.Height < 1) return;

        double total = items.Sum(n => (double)n.Size);
        double scale = r.Width * r.Height / total;
        var areas = items.Select(n => n.Size * scale).ToList();

        int i = 0;
        double x = r.X, y = r.Y, w = r.Width, h = r.Height;

        while (i < items.Count && w > 0.5 && h > 0.5)
        {
            double side = Math.Min(w, h);
            int j = i;
            double rowSum = 0, rowMin = double.MaxValue, rowMax = 0, prevWorst = double.MaxValue;
            while (j < items.Count)
            {
                double a = areas[j];
                double nMin = Math.Min(rowMin, a), nMax = Math.Max(rowMax, a), nSum = rowSum + a;
                double worst = Math.Max(side * side * nMax / (nSum * nSum), nSum * nSum / (side * side * nMin));
                if (j == i || worst <= prevWorst) { rowMin = nMin; rowMax = nMax; rowSum = nSum; prevWorst = worst; j++; }
                else break;
            }

            double thickness = rowSum / side;
            if (w >= h)
            {
                double yy = y;
                for (int k = i; k < j; k++)
                {
                    double ih = areas[k] / thickness;
                    DrawNode(items[k], new Rect(x, yy, thickness, ih), dc, depth, k);
                    yy += ih;
                }
                x += thickness; w -= thickness;
            }
            else
            {
                double xx = x;
                for (int k = i; k < j; k++)
                {
                    double iw = areas[k] / thickness;
                    DrawNode(items[k], new Rect(xx, y, iw, thickness), dc, depth, k);
                    xx += iw;
                }
                y += thickness; h -= thickness;
            }
            i = j;
            _ = parentColor; // depth>0 цвет наследуется через DrawNode
        }
    }

    private void DrawNode(Child item, Rect rect, DrawingContext dc, int depth, int index)
    {
        if (rect.Width < 1 || rect.Height < 1) return;

        Color color = depth == 0 ? Palette(index) : default;
        // для глубины >0 берём цвет «предка» - пробрасываем через рекурсию ниже
        if (depth > 0) color = _currentColor;

        var fill = new SolidColorBrush(Lighten(color, depth * 0.06));
        fill.Freeze();
        dc.DrawRectangle(fill, Border, rect);

        _hits.Add((rect, item)); // кликабелен любой блок (для навигации в дереве)

        bool recurse = item.IsFolder && item.HasChildren && depth < MaxDepth && rect.Width > 50 && rect.Height > 34;
        Rect inner = rect;

        // подпись: имя + размер (если есть место)
        if (rect.Width > 46 && rect.Height > 16)
        {
            DrawLabel(dc, item, rect);
            if (recurse) inner = new Rect(rect.X + 2, rect.Y + 18, Math.Max(0, rect.Width - 4), Math.Max(0, rect.Height - 20));
        }

        if (recurse)
        {
            var prev = _currentColor;
            _currentColor = color;
            Squarify(item.Folder!.SortedChildren(), inner, dc, depth + 1, color);
            _currentColor = prev;
        }
    }

    private Color _currentColor;

    private void DrawLabel(DrawingContext dc, Child item, Rect rect)
    {
        try
        {
            // имя с тенью для читаемости на любом цвете
            DrawText(dc, item.Name, rect.X + 4.6, rect.Y + 2.6, 11, LabelShadow, rect.Width - 8);
            DrawText(dc, item.Name, rect.X + 4, rect.Y + 2, 11, Brushes.White, rect.Width - 8);

            // размер второй строкой, если хватает высоты
            if (rect.Height > 30 && rect.Width > 56)
            {
                string sz = Format.Bytes(item.Size);
                DrawText(dc, sz, rect.X + 4, rect.Y + 16, 10, LabelDim, rect.Width - 8);
            }
        }
        catch { }
    }

    private void DrawText(DrawingContext dc, string text, double x, double y, double size, Brush brush, double maxWidth)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            _font, size, brush, 1.0)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            MaxTextHeight = size + 4,
            Trimming = TextTrimming.CharacterEllipsis,
            MaxLineCount = 1,
        };
        dc.DrawText(ft, new Point(x, y));
    }

    // ---------- взаимодействие ----------
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        for (int i = _hits.Count - 1; i >= 0; i--) // с конца = самый верхний (глубокий) блок
            if (_hits[i].rect.Contains(p))
            {
                var item = _hits[i].item;
                if (e.ClickCount >= 2 && item.IsFolder && item.HasChildren)
                    Root = item.Folder;                  // двойной клик - углубиться в карту
                else
                    NodeClicked?.Invoke(item.FullPath);  // одиночный - открыть узел в дереве
                return;
            }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        for (int i = _hits.Count - 1; i >= 0; i--)
            if (_hits[i].rect.Contains(p))
            {
                var item = _hits[i].item;
                ToolTip = $"{item.FullPath}\n{Format.Bytes(item.Size)}";
                return;
            }
        ToolTip = null;
    }

    // ---------- цвета ----------
    private static Color Palette(int i)
    {
        double hue = i * 49.0 % 360.0;   // золотой угол даёт разнообразие
        return FromHsl(hue, 0.50, 0.45);
    }

    private static Color Lighten(Color c, double amt)
    {
        amt = Math.Clamp(amt, 0, 0.5);
        byte L(byte v) => (byte)Math.Clamp(v + (255 - v) * amt, 0, 255);
        return Color.FromRgb(L(c.R), L(c.G), L(c.B));
    }

    private static Color FromHsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = l - c / 2;
        double r = 0, g = 0, b = 0;
        if (h < 60) { r = c; g = x; }
        else if (h < 120) { r = x; g = c; }
        else if (h < 180) { g = c; b = x; }
        else if (h < 240) { g = x; b = c; }
        else if (h < 300) { r = x; b = c; }
        else { r = c; b = x; }
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
