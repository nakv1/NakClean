using System.IO;
using System.Text.Json;

namespace NakClean.Services;

/// <summary>Настройки между запусками (язык, тема). JSON в %AppData%\NakClean.</summary>
public sealed class AppSettings
{
    public string Language { get; set; } = "ru";
    public string Theme { get; set; } = "dark";

    private static string PathFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NakClean", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(PathFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(PathFile)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathFile)!);
            File.WriteAllText(PathFile, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
