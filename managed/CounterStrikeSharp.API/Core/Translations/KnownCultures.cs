using System.Globalization;

namespace CounterStrikeSharp.API.Core.Translations;

/// <summary>
/// Culture names the runtime lists (CultureInfo.GetCultures), enumerated once. Used to accept a language
/// name from config or css_lang: CultureInfo.GetCultureInfo alone accepts any well-formed tag under ICU,
/// so a typo ("dk") would become a culture with no translations.
/// </summary>
internal static class KnownCultures
{
    private static readonly Lazy<Dictionary<string, CultureInfo>> ByName = new(() =>
        CultureInfo.GetCultures(CultureTypes.AllCultures)
            .Where(c => !c.Equals(CultureInfo.InvariantCulture))
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase));

    /// <summary>Case-insensitive lookup; null for an unknown name or in invariant globalization mode.</summary>
    public static CultureInfo? Find(string? name) =>
        !string.IsNullOrWhiteSpace(name) && ByName.Value.TryGetValue(name.Trim(), out var culture) ? culture : null;
}
