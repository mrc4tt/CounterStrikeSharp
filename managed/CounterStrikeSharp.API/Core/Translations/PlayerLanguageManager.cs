using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CounterStrikeSharp.API.Core.Hosting;
using CounterStrikeSharp.API.Modules.Entities;
using Microsoft.Extensions.Logging;

namespace CounterStrikeSharp.API.Core.Translations;

/// <summary>
/// Where a player's current language comes from.
/// </summary>
public enum PlayerLanguageSource
{
    /// <summary>No choice and no guess: the server language from core.json.</summary>
    Server,

    /// <summary>Guessed from the country of the player's IP address. Not persisted.</summary>
    GeoIp,

    /// <summary>Chosen by the player with css_lang. Always wins over a guess.</summary>
    Manual,
}

public class PlayerLanguageManager : IPlayerLanguageManager
{
    private readonly ConcurrentDictionary<SteamID, CultureInfo> _playerLanguages = new();
    private readonly ConcurrentDictionary<SteamID, (CultureInfo Culture, long Sequence)> _guessedLanguages = new();

    // A guess is kept after the player disconnects (see the OnClientDisconnect listener in Application),
    // so without a limit the map grew by one entry per player that ever joined. The oldest guesses go
    // first; with far more room than a server has slots, a connected player's guess is never the oldest.
    // Sequence tells a queue entry of an earlier guess for the same player from the current one.
    internal const int MaxGuessedLanguages = 4096;
    private readonly Queue<(SteamID SteamId, long Sequence)> _guessOrder = new();
    private long _guessSequence;
    private readonly object _saveLock = new();
    private readonly string? _storePath;
    private readonly ILogger<PlayerLanguageManager>? _logger;
    private long _saveSequence;
    private Task _pendingSave = Task.CompletedTask;

    private GeoIpDatabase? _geoIp;
    private string? _geoIpPath;
    private Dictionary<string, CultureInfo>? _countryLanguages;

    public static IPlayerLanguageManager Instance { get; private set; } = null!;

    public PlayerLanguageManager()
    {
        Instance = this;
    }

    // Languages chosen with css_lang are kept in configs/player_languages.json (SteamID64 -> culture
    // name), so they survive a server restart instead of resetting to the server language.
    public PlayerLanguageManager(IScriptHostConfiguration scriptHostConfiguration,
        ILogger<PlayerLanguageManager> logger) : this()
    {
        _logger = logger;
        _storePath = Path.Join(scriptHostConfiguration.ConfigsPath, "player_languages.json");
        Load();
    }

    /// <summary>
    /// Sets the player's chosen language (source <see cref="PlayerLanguageSource.Manual"/>) and stores it.
    /// </summary>
    public void SetLanguage(SteamID steamId, CultureInfo cultureInfo)
    {
        _playerLanguages[steamId] = cultureInfo;
        Save();
    }

    /// <summary>
    /// Forgets the player's chosen language, so a GeoIP guess or the server language applies again.
    /// Returns false when the player had no chosen language.
    /// </summary>
    public bool ResetLanguage(SteamID steamId)
    {
        if (!_playerLanguages.TryRemove(steamId, out _)) return false;
        Save();
        return true;
    }

    public CultureInfo GetLanguage(SteamID steamId)
    {
        if (_playerLanguages.TryGetValue(steamId, out var cultureInfo)) return cultureInfo;
        if (_guessedLanguages.TryGetValue(steamId, out var guessed)) return guessed.Culture;
        return GetDefaultLanguage();
    }

    /// <summary>
    /// Where <see cref="GetLanguage"/>'s answer for this player comes from.
    /// </summary>
    public PlayerLanguageSource GetLanguageSource(SteamID steamId)
    {
        if (_playerLanguages.ContainsKey(steamId)) return PlayerLanguageSource.Manual;
        if (_guessedLanguages.ContainsKey(steamId)) return PlayerLanguageSource.GeoIp;
        return PlayerLanguageSource.Server;
    }

    public CultureInfo GetDefaultLanguage()
    {
        return CultureInfo.DefaultThreadCurrentUICulture!;
    }

    /// <summary>
    /// Starts (or restarts, after css_core_reload) the GeoIP database according to configs/geoip.json.
    /// </summary>
    internal void ConfigureGeoIp(string rootPath)
    {
        _countryLanguages = GeoIpLanguages.BuildCountryMap(GeoIpConfig.CountryLanguages);

        if (!GeoIpConfig.Enabled)
        {
            StopGeoIp();
            ClearGuesses();
            return;
        }

        var configured = GeoIpConfig.DatabasePath;
        var path = string.IsNullOrWhiteSpace(configured)
            ? null
            : Path.IsPathRooted(configured) ? configured : Path.GetFullPath(Path.Join(rootPath, configured));
        if (path == null)
        {
            _logger?.LogWarning("GeoIP language guess is off: DatabasePath in geoip.json is empty");
            StopGeoIp();
            ClearGuesses();
            return;
        }

        // Same file: keep the running database (css_core_reload must not re-read it for nothing).
        // Another file: start over, so the new path is loaded or downloaded now instead of after a restart.
        if (_geoIp != null && _geoIpPath == path) return;
        StopGeoIp();

        _geoIpPath = path;
        _geoIp = new GeoIpDatabase(_logger ?? (ILogger)Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            () => path, () => GeoIpConfig.DownloadUrls, () => GeoIpConfig.MaxAgeDays);
        _geoIp.Start();
    }

    internal void StopGeoIp()
    {
        _geoIp?.Dispose();
        _geoIp = null;
        _geoIpPath = null;
    }

    /// <summary>
    /// Guesses the player's language from their IP when they have not chosen one. Game thread, no I/O.
    /// Returns the guessed culture, or null when nothing was guessed (chosen language, no database,
    /// private address, unmapped country).
    /// </summary>
    internal CultureInfo? GuessLanguage(SteamID steamId, string? ipAddress)
    {
        _guessedLanguages.TryRemove(steamId, out _);

        if (_playerLanguages.ContainsKey(steamId)) return null;
        var culture = GuessFromAddress(ipAddress);
        if (culture == null || SameLanguage(culture, GetDefaultLanguage())) return null;

        RememberGuess(steamId, culture);
        return culture;
    }

    private void ClearGuesses()
    {
        lock (_guessOrder)
        {
            _guessedLanguages.Clear();
            _guessOrder.Clear();
        }
    }

    internal int GuessedLanguageCount => _guessedLanguages.Count;

    internal void RememberGuess(SteamID steamId, CultureInfo culture)
    {
        lock (_guessOrder)
        {
            var sequence = ++_guessSequence;
            _guessedLanguages[steamId] = (culture, sequence);
            _guessOrder.Enqueue((steamId, sequence));

            while (_guessOrder.Count > MaxGuessedLanguages)
            {
                var oldest = _guessOrder.Dequeue();
                // Only if it is still that guess: the player may have been guessed again since.
                if (_guessedLanguages.TryGetValue(oldest.SteamId, out var current) && current.Sequence == oldest.Sequence)
                    _guessedLanguages.TryRemove(oldest.SteamId, out _);
            }
        }
    }

    // A guess that the server culture already covers through its parents ("de" on a "de-DE" server) is
    // no change for the player. Keep the server culture then: plugins shipping only de-DE.json still match,
    // and no notice is sent.
    internal static bool SameLanguage(CultureInfo guessed, CultureInfo server)
    {
        for (var c = server; !c.Equals(CultureInfo.InvariantCulture); c = c.Parent)
        {
            if (c.Equals(guessed)) return true;
        }
        return false;
    }

    internal CultureInfo? GuessFromAddress(string? ipAddress)
    {
        if (_geoIp == null || _countryLanguages == null) return null;

        var address = GeoIpLanguages.ParsePublicAddress(ipAddress);
        if (address == null) return null;

        var country = _geoIp.LookupCountry(address);
        return country != null && _countryLanguages.TryGetValue(country, out var culture) ? culture : null;
    }

    /// <summary>Waits for queued writes of player_languages.json (tests, shutdown).</summary>
    internal void FlushSaves()
    {
        Task pending;
        lock (_saveLock) pending = _pendingSave;
        pending.Wait();
    }

    private void Load()
    {
        if (_storePath == null || !File.Exists(_storePath)) return;

        try
        {
            var stored = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_storePath));
            if (stored == null) return;

            foreach (var (steamId64, language) in stored)
            {
                if (!ulong.TryParse(steamId64, out var id)) continue;
                try
                {
                    _playerLanguages[new SteamID(id)] = CultureInfo.GetCultureInfo(language);
                }
                catch (CultureNotFoundException)
                {
                    // Culture no longer known to the runtime, drop it.
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read player languages from {Path}", _storePath);
        }
    }

    // The snapshot is taken on the caller's thread; the write runs on the thread pool so css_lang never
    // does disk I/O on the game thread. Writes are chained and numbered, so an older snapshot can never
    // overwrite a newer one; a snapshot already superseded by a queued one is skipped.
    private void Save()
    {
        if (_storePath == null || !CoreConfig.PersistPlayerLanguage) return;

        var snapshot = _playerLanguages.ToDictionary(x => x.Key.SteamId64.ToString(), x => x.Value.Name);
        var path = _storePath;
        var logger = _logger;

        lock (_saveLock)
        {
            var sequence = ++_saveSequence;
            _pendingSave = _pendingSave.ContinueWith(_ => Write(path, snapshot, sequence, logger),
                TaskScheduler.Default);
        }
    }

    private void Write(string path, Dictionary<string, string> snapshot, long sequence, ILogger? logger)
    {
        // A newer snapshot is queued behind this one: let that one write.
        if (sequence < Interlocked.Read(ref _saveSequence)) return;

        try
        {
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            var tmpPath = path + ".tmp";
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, path, true);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to save player languages to {Path}", path);
        }
    }
}
