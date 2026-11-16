using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace CounterStrikeSharp.API.Core.Translations;

/// <summary>
/// Country -> language mapping and the address filter for GeoIP language guesses.
/// </summary>
internal static class GeoIpLanguages
{
    // Only languages the core and our usual plugins ship. Countries with several common languages
    // (BE, CH-fr/it, UA, CY, FI, EE, LV, LT, MK, ...) are left out on purpose; hosts add them in
    // geoip.json "CountryLanguages".
    public static readonly IReadOnlyDictionary<string, string[]> DefaultCountryLanguages = new Dictionary<string, string[]>
    {
        ["da"] = new[] { "DK", "GL", "FO" },
        ["de"] = new[] { "DE", "AT", "CH", "LI", "LU" },
        ["es"] = new[]
        {
            "ES", "MX", "AR", "CO", "CL", "PE", "VE", "EC", "GT", "CU", "BO", "DO", "HN", "PY", "SV", "NI", "CR", "PA",
            "UY", "PR",
        },
        ["fr"] = new[] { "FR", "MC", "SN", "CI", "CM", "ML", "BF", "NE", "TG", "BJ", "GA", "CG", "CD", "MG", "HT" },
        ["nb"] = new[] { "NO", "SJ" },
        ["pl"] = new[] { "PL" },
        ["pt-BR"] = new[] { "BR", "PT", "AO", "MZ", "CV", "GW", "ST", "TL" },
        ["ru"] = new[] { "RU", "BY", "KZ", "KG" },
        ["sv"] = new[] { "SE", "AX" },
        ["tr"] = new[] { "TR" },
        ["sq"] = new[] { "AL", "XK" },
        ["en"] = new[] { "GB", "IE", "US", "CA", "AU", "NZ", "ZA", "IN", "SG", "PH" },
    };

    /// <summary>
    /// Country code (upper case) -> culture. Host entries win over the built-in ones; an unknown culture
    /// name is skipped.
    /// </summary>
    public static Dictionary<string, CultureInfo> BuildCountryMap(IReadOnlyDictionary<string, List<string>>? overrides)
    {
        var map = new Dictionary<string, CultureInfo>(StringComparer.OrdinalIgnoreCase);

        void Add(string language, IEnumerable<string> countries)
        {
            if (KnownCultures.Find(language) is not { } culture) return;

            foreach (var country in countries)
            {
                if (!string.IsNullOrWhiteSpace(country)) map[country.Trim()] = culture;
            }
        }

        foreach (var (language, countries) in DefaultCountryLanguages) Add(language, countries);
        if (overrides != null)
        {
            foreach (var (language, countries) in overrides)
            {
                if (countries != null) Add(language, countries);
            }
        }

        return map;
    }

    /// <summary>
    /// Parses "1.2.3.4", "1.2.3.4:27005", "[::1]:27005" or a bare IPv6 address, and returns null for
    /// anything that cannot be looked up: unparsable, loopback, private/LAN, link-local, unspecified.
    /// </summary>
    public static IPAddress? ParsePublicAddress(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();

        IPAddress? address;
        if (text.StartsWith('['))
        {
            var end = text.IndexOf(']');
            if (end < 0 || !IPAddress.TryParse(text.AsSpan(1, end - 1), out address)) return null;
        }
        else if (!IPAddress.TryParse(text, out address))
        {
            // IPv4 with port. (A bare IPv6 address parses above, so only one ':' remains here.)
            var colon = text.LastIndexOf(':');
            if (colon <= 0 || text.IndexOf(':') != colon || !IPAddress.TryParse(text.AsSpan(0, colon), out address))
                return null;
        }

        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return IsPublic(address) ? address : null;
    }

    private static bool IsPublic(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 0
                     || b[0] == 10
                     || (b[0] == 100 && (b[1] & 0xC0) == 64) // 100.64/10 carrier-grade NAT
                     || (b[0] == 169 && b[1] == 254)
                     || (b[0] == 172 && (b[1] & 0xF0) == 16)
                     || (b[0] == 192 && b[1] == 168)
                     || b[0] >= 224);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6None) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal ||
                address.IsIPv6UniqueLocal || address.IsIPv6Multicast)
                return false;
            return true;
        }

        return false;
    }
}
