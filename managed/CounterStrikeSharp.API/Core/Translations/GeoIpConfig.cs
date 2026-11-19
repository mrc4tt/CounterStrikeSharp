using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace CounterStrikeSharp.API.Core.Translations;

/// <summary>
/// configs/geoip.json: everything about the GeoIP language guess, kept out of core.json.
/// </summary>
internal sealed class GeoIpConfigData
{
    /// <summary>
    /// Guess a player's language from the country of their IP address when they never chose one with css_lang.
    /// </summary>
    [JsonPropertyName("Enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Tell a player in chat, once per connection, that their language was set from their location.
    /// </summary>
    [JsonPropertyName("Notify")]
    public bool Notify { get; set; } = true;

    /// <summary>
    /// Country .mmdb file, relative to the counterstrikesharp directory unless absolute.
    /// </summary>
    [JsonPropertyName("DatabasePath")]
    public string? DatabasePath { get; set; } = "configs/geoip/GeoLite2-Country.mmdb";

    /// <summary>
    /// Where the database is downloaded from, tried in order. Empty: never download, use the file as is.
    /// </summary>
    [JsonPropertyName("DownloadUrls")]
    public List<string>? DownloadUrls { get; set; } = new()
    {
        "https://github.com/P3TERX/GeoLite.mmdb/raw/download/GeoLite2-Country.mmdb",
        "https://github.com/P3TERX/GeoLite.mmdb/releases/latest/download/GeoLite2-Country.mmdb",
    };

    /// <summary>
    /// Re-download the database when the file is older than this many days. 0 or less: never re-download.
    /// </summary>
    [JsonPropertyName("MaxAgeDays")]
    public int MaxAgeDays { get; set; } = 7;

    /// <summary>
    /// Language -> ISO 3166-1 country codes, merged over the built-in table (GeoIpLanguages).
    /// </summary>
    [JsonPropertyName("CountryLanguages")]
    public Dictionary<string, List<string>>? CountryLanguages { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownKeys { get; set; }
}

internal static class GeoIpConfig
{
    public const string FileName = "geoip.json";

    private static GeoIpConfigData _data = new();

    public static bool Enabled => _data.Enabled;
    public static bool Notify => _data.Notify;
    public static string DatabasePath => _data.DatabasePath ?? "";
    public static IReadOnlyList<string> DownloadUrls => (IReadOnlyList<string>?)_data.DownloadUrls ?? System.Array.Empty<string>();
    public static int MaxAgeDays => _data.MaxAgeDays;
    public static IReadOnlyDictionary<string, List<string>>? CountryLanguages => _data.CountryLanguages;

    /// <summary>
    /// Reads configs/geoip.json. A missing file means the defaults (the file is optional); a file that
    /// does not parse keeps the settings already in use, so a typo on css_core_reload changes nothing.
    /// </summary>
    public static void Load(string configsPath, ILogger logger)
    {
        var path = Path.Join(configsPath, FileName);
        if (!File.Exists(path))
        {
            _data = new GeoIpConfigData();
            return;
        }

        try
        {
            var data = JsonSerializer.Deserialize<GeoIpConfigData>(File.ReadAllText(path, Encoding.UTF8),
                new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (data == null) return;

            _data = data;
            if (data.UnknownKeys is { Count: > 0 } unknown)
            {
                logger.LogWarning("Ignoring unknown {File} key(s): {Keys}", FileName, string.Join(", ", unknown.Keys));
            }
        }
        catch (System.Exception ex)
        {
            logger.LogWarning(ex, "Failed to load {Path}, the GeoIP settings already in use are kept", path);
        }
    }

    internal static void SetForTests(GeoIpConfigData data) => _data = data;
}
