using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NakClean.Controls;

public partial class CircularGauge : UserControl
{
    private const double Cx = 66, Cy = 66, R = 60;

    public CircularGauge()
    {
        InitializeComponent();
        UpdateVisual();
    }

    public static readonly DependencyProperty PercentProperty =
        DependencyProperty.Register(nameof(Percent), typeof(double), typeof(CircularGauge),
            new PropertyMetadata(0.0, OnChanged));

    public static readonly DependencyProperty ValueTextProperty =
        DependencyProperty.Register(nameof(ValueText), typeof(string), typeof(CircularGauge),
            new PropertyMetadata("", OnChanged));

    public static readonly DependencyProperty CaptionProperty =
        DependencyProperty.Register(nameof(Caption), typeof(string), typeof(CircularGauge),
            new PropertyMetadata("", OnChanged));

    public static readonly DependencyProperty AccentProperty =
        DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(CircularGauge),
            new PropertyMetadata(new SolidColorBrush(Color.FromRgb(0xDD, 0xB4, 0x4B))));

    public double Percent { get => (double)GetValue(PercentProperty); set => SetValue(PercentProperty, value); }
    public string ValueText { get => (string)GetValue(ValueTextProperty); set => SetValue(ValueTextProperty, value); }
    public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((CircularGauge)d).UpdateVisual();

    private void UpdateVisual()
    {
        ValueLabel.Text = string.IsNullOrEmpty(ValueText) ? $"{Percent:0}%" : ValueText;
        CaptionLabel.Text = Caption;
        Arc.Data = BuildArc(Math.Clamp(Percent, 0, 100));
    }

    private static Geometry BuildArc(double percent)
    {
        double sweep = Math.Min(percent / 100.0 * 360.0, 359.9);
        if (sweep <= 0.01)
            return Geometry.Empty;

        double startAngle = -90 * Math.PI / 180.0;
        double endAngle = (-90 + sweep) * Math.PI / 180.0;

        var start = new Point(Cx + R * Math.Cos(startAngle), Cy + R * Math.Sin(startAngle));
        var end = new Point(Cx + R * Math.Cos(endAngle), Cy + R * Math.Sin(endAngle));

        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment
        {
            Point = end,
            Size = new Size(R, R),
            IsLargeArc = sweep > 180,
            SweepDirection = SweepDirection.Clockwise
        });

        var geo = new PathGeometry();
        geo.Figures.Add(figure);
        geo.Freeze();
        return geo;
    }
}
