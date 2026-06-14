using NakClean.Services;

namespace NakClean.ViewModels;

/// <summary>Строка журнала изменений: имя твика + когда применён.</summary>
public sealed class ChangeEntryVm
{
    private readonly ChangeEntry _e;
    public ChangeEntryVm(ChangeEntry e) => _e = e;

    public string Name => Loc.I[_e.NameKey];
    public string When => DateTime.TryParse(_e.When, out var d) ? d.ToString("g") : "";
}
