using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using CounterStrikeSharp.API.Core.Hosting;
using CounterStrikeSharp.API.Modules.Entities;
using Microsoft.Extensions.Logging;

namespace CounterStrikeSharp.API.Core.Translations;

public class PlayerLanguageManager : IPlayerLanguageManager
{
    private readonly ConcurrentDictionary<SteamID, CultureInfo> _playerLanguages = new();
    private readonly object _saveLock = new();
    private readonly string? _storePath;
    private readonly ILogger<PlayerLanguageManager>? _logger;

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

    public void SetLanguage(SteamID steamId, CultureInfo cultureInfo)
    {
        _playerLanguages[steamId] = cultureInfo;
        Save();
    }

    public CultureInfo GetLanguage(SteamID steamId)
    {
        return _playerLanguages.TryGetValue(steamId, out var cultureInfo) ? cultureInfo : GetDefaultLanguage();
    }

    public CultureInfo GetDefaultLanguage()
    {
        return CultureInfo.DefaultThreadCurrentUICulture!;
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

    private void Save()
    {
        if (_storePath == null) return;

        try
        {
            var snapshot = _playerLanguages.ToDictionary(x => x.Key.SteamId64.ToString(), x => x.Value.Name);
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            lock (_saveLock)
            {
                var tmpPath = _storePath + ".tmp";
                File.WriteAllText(tmpPath, json);
                File.Move(tmpPath, _storePath, true);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to save player languages to {Path}", _storePath);
        }
    }
}
