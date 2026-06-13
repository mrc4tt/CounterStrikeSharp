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
    // File names (not full paths) of every gamedata JSON merged into Methods. Lets a
    // missing-key error name WHERE the operator can add the key, and show that the key
    // was searched across all files, not only gamedata.json.
    public IReadOnlyList<string> LoadedFiles { get; private set; } = Array.Empty<string>();
    public string DirectoryPath => _gameDataDirectoryPath;
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
            var loadedFiles = new List<string>();

            foreach (string filePath in Directory.EnumerateFiles(_gameDataDirectoryPath, "*.json"))
            {
                loadedFiles.Add(Path.GetFileName(filePath));
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

            LoadedFiles = loadedFiles;

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
            throw new ArgumentException(BuildMissingKeyMessage(key));
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
            throw new ArgumentException(BuildMissingKeyMessage(key));
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

    // Builds an actionable message for a key that is in NONE of the loaded gamedata
    // files. All *.json in the gamedata dir merge into one flat table, so a key is not
    // strictly owned by any one file — but plugins follow a naming convention
    // (jRandomSkills -> jRandomSkills.gamedata.json, WeaponPaints -> weaponpaints.json),
    // so we can match the requesting plugin to its own file and tell the operator
    // whether that file is MISSING (plugin shipped none) or OUTDATED (shipped but lacks
    // this key).
    private static string BuildMissingKeyMessage(string key)
    {
        var files = GameDataProvider?.LoadedFiles ?? Array.Empty<string>();
        var fileList = files.Count > 0 ? string.Join(", ", files) : "(none)";

        var plugin = RequestingPluginName();
        var ownFile = plugin != null ? FindPluginGameDataFile(plugin, files) : null;

        // Facts only — keep it to one scannable line. The "what to do" guidance lives in
        // the banner's Fix: line (PluginContext.FixHintFor) so it is not duplicated three
        // times across the raw exception log, the banner Error: line, and the Fix: line.
        var sb = new StringBuilder();
        sb.Append("Gamedata key '").Append(key).Append("' missing");
        if (plugin != null) sb.Append(" (requested by plugin '").Append(plugin).Append("')");
        sb.Append(". ");
        if (ownFile != null)
            sb.Append("Plugin file '").Append(ownFile).Append("' is loaded but lacks this key (outdated).");
        else if (plugin != null)
            sb.Append("Plugin ships no gamedata file (expected '").Append(plugin).Append(".gamedata.json').");
        sb.Append(" Searched ").Append(files.Count).Append(" file(s): [").Append(fileList).Append("].");
        return sb.ToString();
    }

    // Matches a plugin (assembly) name to its shipped gamedata file by convention:
    // strip ".gamedata"/".json" and all non-alphanumerics, lowercase, then compare.
    // "jRandomSkills" matches "jRandomSkills.gamedata.json"; "WeaponPaints" matches
    // "weaponpaints.json". Returns the file name, or null if none looks like the plugin's.
    private static string? FindPluginGameDataFile(string plugin, IReadOnlyList<string> files)
    {
        string Normalize(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        // Drop the generic core/shared files so they never get attributed to a plugin.
        var pn = Normalize(plugin);
        if (pn.Length == 0) return null;

        foreach (var f in files)
        {
            var stem = f;
            if (stem.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) stem = stem[..^5];
            if (stem.EndsWith(".gamedata", StringComparison.OrdinalIgnoreCase)) stem = stem[..^9];
            var fn = Normalize(stem);
            if (fn.Length == 0 || fn == "gamedata" || fn == "stgamedata") continue;
            if (fn == pn || fn.Contains(pn) || pn.Contains(fn)) return f;
        }
        return null;
    }

    // Deepest stack frame whose assembly is neither CounterStrikeSharp nor the BCL ->
    // the plugin (or its dependency) that asked for the key. Best-effort; null if the
    // call came from framework code with no plugin in the chain.
    private static string? RequestingPluginName()
    {
        try
        {
            var self = typeof(GameData).Assembly;
            foreach (var f in new System.Diagnostics.StackTrace(false).GetFrames() ?? Array.Empty<System.Diagnostics.StackFrame>())
            {
                var asm = f.GetMethod()?.DeclaringType?.Assembly;
                if (asm == null || asm == self) continue;
                var name = asm.GetName().Name;
                if (string.IsNullOrEmpty(name)) continue;
                if (name.StartsWith("System.") || name.StartsWith("Microsoft.")
                    || name == "System.Private.CoreLib" || name == "mscorlib")
                    continue;
                return name;
            }
        }
        catch { /* diagnostics only — never throw from an error path */ }
        return null;
    }
}