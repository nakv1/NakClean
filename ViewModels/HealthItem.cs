using System.Collections.ObjectModel;
using System.Windows.Media;

namespace NakClean.ViewModels;

public enum CheckLevel { Good, Info, Warning, Bad }

/// <summary>Секция проверки ПК: категория (иконка + название) и её пункты. Раскладывается по колонкам.</summary>
public sealed class HealthSection
{
    public required string Title { get; init; }
    public required string Glyph { get; init; }
    public ObservableCollection<HealthItem> Items { get; } = new();
}

/// <summary>Один пункт проверки состояния ПК (готов к показу).</summary>
public sealed class HealthItem
{
    public required string Category { get; init; }
    public required string Title { get; init; }
    public required string Value { get; init; }
    public string? Hint { get; init; }
    public CheckLevel Level { get; init; } = CheckLevel.Info;

    public bool HasHint => !string.IsNullOrEmpty(Hint);

    public string Glyph => Level switch
    {
        CheckLevel.Good => "✔",
        CheckLevel.Warning => "!",
        CheckLevel.Bad => "✕",
        _ => "ℹ",
    };

    private Color LevelColor => Level switch
    {
        CheckLevel.Good => Color.FromRgb(0x3D, 0xD6, 0x8C),
        CheckLevel.Warning => Color.FromRgb(0xDD, 0xB4, 0x4B),
        CheckLevel.Bad => Color.FromRgb(0xE5, 0x48, 0x4D),
        _ => Color.FromRgb(0x6E, 0x9A, 0xD6),
    };

    /// <summary>Насыщенный цвет уровня (полоса, глиф).</summary>
    public Brush LevelBrush => Frozen(LevelColor);

    /// <summary>Мягкая заливка иконки (тот же цвет, низкая прозрачность).</summary>
    public Brush LevelSoftBrush => Frozen(WithAlpha(LevelColor, 0x26));

    /// <summary>Тонкая обводка иконки.</summary>
    public Brush LevelBorderBrush => Frozen(WithAlpha(LevelColor, 0x55));

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
