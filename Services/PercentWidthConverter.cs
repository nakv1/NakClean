using System.Globalization;
using System.Windows.Data;

namespace NakClean.Services;

/// <summary>Процент (0-100) -> ширина заливки. ConverterParameter = полная ширина дорожки.</summary>
public sealed class PercentWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double pct = value is null ? 0 : System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
        double full = parameter is null ? 100 : System.Convert.ToDouble(parameter, CultureInfo.InvariantCulture);
        pct = Math.Clamp(pct, 0, 100);
        return full * pct / 100.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
