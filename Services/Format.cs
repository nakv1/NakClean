namespace NakClean.Services;

public static class Format
{
    private static readonly string[] UnitsRu = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
    private static readonly string[] UnitsEn = { "B", "KB", "MB", "GB", "TB" };
    private static string[] Units => Loc.I.IsEn ? UnitsEn : UnitsRu;

    public static string Bytes(long value)
    {
        if (value <= 0) return $"0 {Units[0]}";
        double v = value;
        int u = 0;
        while (v >= 1024 && u < Units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{v:0} {Units[u]}" : $"{v:0.0} {Units[u]}";
    }

    /// <summary>Ёмкость диска в десятичных единицах (как на коробке/в CrystalDiskInfo): 1 ГБ = 1000 МБ.</summary>
    public static string Capacity(long value)
    {
        if (value <= 0) return $"0 {Units[3]}";
        double v = value;
        int u = 0;
        while (v >= 1000 && u < Units.Length - 1) { v /= 1000; u++; }
        return u == 0 ? $"{v:0} {Units[u]}" : $"{v:0.0} {Units[u]}";
    }

    /// <summary>Русское склонение: Plural(2, "ядро","ядра","ядер") → "ядра".</summary>
    public static string Plural(int n, string one, string few, string many)
    {
        int m100 = n % 100, m10 = n % 10;
        if (m100 is >= 11 and <= 14) return many;
        if (m10 == 1) return one;
        if (m10 is >= 2 and <= 4) return few;
        return many;
    }
}
