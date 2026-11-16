using System.Globalization;
using CounterStrikeSharp.API.Modules.Entities;

namespace CounterStrikeSharp.API.Core.Translations;

public static class PlayerLanguageExtensions
{
    /// <summary>
    /// Returns the player's language: the one chosen with the "css_lang" command (kept across reconnects
    /// and restarts), else a guess from the country of their IP address (GeoIP, core.json
    /// "GeoIpLanguageEnabled"), else the server language.
    /// </summary>
    public static CultureInfo GetLanguage(this CCSPlayerController? player)
    {
        if (player == null || !player.IsValid) return PlayerLanguageManager.Instance.GetDefaultLanguage();
        
        return PlayerLanguageManager.Instance.GetLanguage((SteamID)player.SteamID);
    }

    /// <summary>
    /// Where <see cref="GetLanguage"/>'s answer comes from: chosen with css_lang, guessed from GeoIP, or the server language.
    /// </summary>
    public static PlayerLanguageSource GetLanguageSource(this CCSPlayerController? player)
    {
        if (player == null || !player.IsValid) return PlayerLanguageSource.Server;

        return PlayerLanguageManager.Instance is PlayerLanguageManager manager
            ? manager.GetLanguageSource((SteamID)player.SteamID)
            : PlayerLanguageSource.Server;
    }
}
