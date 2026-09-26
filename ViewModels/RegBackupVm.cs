using NakClean.Services;

namespace NakClean.ViewModels;

/// <summary>Строка списка резервных копий реестра.</summary>
public sealed class RegBackupVm : ViewModelBase
{
    public RegBackup Backup { get; }
    public RegBackupVm(RegBackup b) => Backup = b;

    public string WhenText => Backup.When.ToString(Loc.I.IsEn ? "MMMM d, yyyy  HH:mm" : "d MMMM yyyy,  HH:mm",
        System.Globalization.CultureInfo.GetCultureInfo(Loc.I.IsEn ? "en-US" : "ru-RU"));

    public string KindText
    {
        get
        {
            string kind = Loc.I[$"rbk_{Backup.Kind}"];
            if (Backup.Count <= 0) return kind;
            int n = Backup.Count;
            string word = Loc.I.IsEn ? (n == 1 ? "entry" : "entries") : Loc.I.Plural(n, "rb_entries");
            return $"{kind}  ·  {n} {word}";
        }
    }

    public string FileName => System.IO.Path.GetFileName(Backup.Path);

    public void RaiseLocalized()
    {
        OnPropertyChanged(nameof(WhenText));
        OnPropertyChanged(nameof(KindText));
    }
}
