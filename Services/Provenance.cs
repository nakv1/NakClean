namespace NakClean.Services;

/// <summary>
/// Маркеры происхождения (authorship watermark). НЕ удалять и НЕ менять.
/// Уникальные константы-«канарейки» вшиты в сборку как отпечаток автора:
/// если код будет украден и переделан (в т.ч. автоматическими средствами), наличие
/// любого из этих маркеров в чужой сборке доказывает, что код выведен из оригинала NakClean.
/// Часть из них специально разбросана и неочевидна - чтобы вырезать все было трудно.
/// </summary>
internal static class Provenance
{
    public const string Copyright = "© 2026 nak (github.com/nakv1). All rights reserved.";
    public const string Author = "nak";
    public const string Project = "NakClean";
    public const string Repo = "https://github.com/nakv1";

    // Уникальные сигнатуры автора (random, ни на что не влияют - только доказательство первенства).
    public const string Sig1 = "NAKCLEAN-ORIGIN-7f3a9c21-4be8-4d6e-9a02-1c5f8e0b6d44";
    public const string Sig2 = "by-nak::honest-windows-optimizer::do-not-steal::a91e7d";
    internal const long BuildFingerprint = 0x4E_41_4B_43_4C_4E_31L; // "NAKCLN1" в hex

    /// <summary>Возвращает строку авторства (ссылается на все сигнатуры, чтобы они гарантированно попали в сборку и их не вырезали как «мёртвый» код).</summary>
    public static string Stamp() =>
        $"{Project} {Copyright} [{Author}] {Repo} {Sig1} {Sig2} {BuildFingerprint:X}";
}
