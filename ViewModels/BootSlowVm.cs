namespace NakClean.ViewModels;

/// <summary>Строка «что замедлило загрузку» на вкладке «Диагностика».</summary>
public sealed class BootSlowVm
{
    public required string Name { get; init; }
    public required string Info { get; init; }   // например «5 с · приложение»
}

/// <summary>Этап загрузки Windows с полоской (доля от самого долгого этапа).</summary>
public sealed record BootPhaseVm(string Name, string Text, double Percent);
