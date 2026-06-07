using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core.Hosting;
using Microsoft.Extensions.Logging;

namespace CounterStrikeSharp.API.Core;

public class LoadedGameData
{
    [JsonPropertyName("signatures")] public Signatures? Signatures { get; set; }
    [JsonPropertyName("offsets")] public Offsets? Offsets { get; set; }
}

public class Signatures
{
    [JsonPropertyName("library")] public string Library { get; set; }

    [JsonPropertyName("windows")] public string Windows { get; set; }

    [JsonPropertyName("linux")] public string Linux { get; set; }
}

public class Offsets
{
    [JsonPropertyName("windows")] public int Windows { get; set; }

    [JsonPropertyName("linux")] public int Linux { get; set; }
}

public sealed class GameDataProvider : IStartupService
{
    private readonly string _gameDataDirectoryPath;
    public Dictionary<string,LoadedGameData> Methods;
    private readonly ILogger<GameDataProvider> _logger;

    public GameDataProvider(IScriptHostConfiguration scriptHostConfiguration, ILogger<GameDataProvider> logger)
    {
        _logger = logger;
        _gameDataDirectoryPath = scriptHostConfiguration.GameDataPath;
    }
    
    public void Load()
    {
        try
        {
            Methods = new Dictionary<string, LoadedGameData>();

            foreach (string filePath in Directory.EnumerateFiles(_gameDataDirectoryPath, "*.json"))
            {
                string jsonContent = File.ReadAllText(filePath, Encoding.UTF8);
                Dictionary<string, LoadedGameData> loadedMethods = JsonSerializer.Deserialize<Dictionary<string, LoadedGameData>>(jsonContent)!;

                foreach (KeyValuePair<string, LoadedGameData> loadedMethod in loadedMethods)
                {
                    if (Methods.ContainsKey(loadedMethod.Key))
                    {
                        _logger.LogWarning("GameData Method \"{Key}\" loaded a duplicate entry from {filePath}.", loadedMethod.Key, filePath);
                    }
                    
                    Methods[loadedMethod.Key] = loadedMethod.Value;
                }
                
                if (loadedMethods != null)
                {
                    _logger.LogInformation("Successfully loaded {Count} game data entries from {Path}", loadedMethods.Count, filePath);
                }
                else
                {
                    _logger.LogWarning("Unable to load game data entries from {Path}, game data file is empty", filePath);
                }
            }

            ValidateCurrentPlatformEntries();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load game data");
        }
    }

    // Fork-only: after loading, warn about entries that are missing a signature/offset
    // for the CURRENT platform. Consuming such a key (e.g. via VirtualFunctions) throws
    // at type-init time and takes down every plugin that touches that class, so surfacing
    // it here — early, by name — turns a cryptic TypeInitializationException cascade into
    // an actionable startup warning.
    private void ValidateCurrentPlatformEntries()
    {
        if (Methods == null || Methods.Count == 0)
        {
            _logger.LogError(
                "No game data entries loaded. Every plugin using engine functions (VirtualFunctions) will fail. " +
                "Check that gamedata.json exists in {Path}.", _gameDataDirectoryPath);
            return;
        }

        bool linux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
        var broken = new List<string>();

        foreach (var kv in Methods)
        {
            var data = kv.Value;
            var sig = data.Signatures;
            var off = data.Offsets;

            // An entry is usable if it has a non-empty signature OR an offset for this platform.
            bool hasSig = sig != null && !string.IsNullOrWhiteSpace(linux ? sig.Linux : sig.Windows);
            bool hasOff = off != null && (linux ? off.Linux : off.Windows) != 0;

            if (!hasSig && !hasOff)
                broken.Add(kv.Key);
        }

        if (broken.Count > 0)
        {
            _logger.LogWarning(
                "{Count} game data entr{Suffix} have no {Platform} signature/offset and will throw if used: {Keys}",
                broken.Count, broken.Count == 1 ? "y" : "ies", linux ? "Linux" : "Windows",
                string.Join(", ", broken));
        }
        else
        {
            _logger.LogInformation("Game data validated: all {Count} entries have a {Platform} signature/offset.",
                Methods.Count, linux ? "Linux" : "Windows");
        }
    }
}

public static class GameData
{
    internal static GameDataProvider GameDataProvider { get; set; } = null!;
    
    public static string GetSignature(string key)
    {
        Application.Instance.Logger.LogDebug("Getting signature: {Key}", key);
        if (!GameDataProvider.Methods.ContainsKey(key))
        {
            throw new ArgumentException($"Method {key} not found in gamedata.json");
        }

        var methodMetadata = GameDataProvider.Methods[key];
        if (methodMetadata.Signatures == null)
        {
            throw new InvalidOperationException($"No signatures found for {key} in gamedata.json");
        }

        string signature;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            signature = methodMetadata.Signatures?.Linux ?? throw new InvalidOperationException($"No Linux signature for {key} in gamedata.json");
        }
        else
        {
            signature = methodMetadata.Signatures?.Windows ?? throw new InvalidOperationException($"No Windows signature for {key} in gamedata.json");
        }

        return signature;
    }

    public static int GetOffset(string key)
    {
        if (!GameDataProvider.Methods.ContainsKey(key))
        {
            throw new Exception($"Method {key} not found in gamedata.json");
        }

        var methodMetadata = GameDataProvider.Methods[key];

        if (methodMetadata.Offsets == null)
        {
            throw new Exception($"No offsets found for {key} in gamedata.json");
        }

        int offset;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            offset = methodMetadata.Offsets?.Linux ?? throw new InvalidOperationException($"No Linux offset for {key} in gamedata.json");
        }
        else
        {
            offset = methodMetadata.Offsets?.Windows ?? throw new InvalidOperationException($"No Windows offset for {key} in gamedata.json");
        }

        return offset;
    }
}