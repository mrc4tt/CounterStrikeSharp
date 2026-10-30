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
    [JsonPropertyName("library")] public string? Library { get; set; }

    [JsonPropertyName("windows")] public string? Windows { get; set; }

    [JsonPropertyName("linux")] public string? Linux { get; set; }
}

public class Offsets
{
    // Nullable so "absent" and "slot 0" stay distinguishable. A non-nullable int
    // deserializes a missing key to 0, which is a VALID vtable index -
    // CEntityResourceManifest::AddResource is linux slot 0, windows slot 2 - so the two
    // cases could not be told apart: the validator called a correct entry broken, and
    // GetOffset silently returned slot 0 for an entry that had no offset at all.
    [JsonPropertyName("windows")] public int? Windows { get; set; }

    [JsonPropertyName("linux")] public int? Linux { get; set; }
}

public sealed class GameDataProvider : IStartupService
{
    private readonly string _gameDataDirectoryPath;
    public Dictionary<string,LoadedGameData> Methods = new();
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
            // Per-file breakdown collected for ONE combined summary line instead of
            // N "Successfully loaded …" lines (one per gamedata file) at boot.
            var fileBreakdown = new List<string>();

            // Fixed order, because a duplicate key is resolved by "later file wins": core
            // gamedata.json first, then the rest by name. EnumerateFiles order is whatever the
            // filesystem returns, so which copy of a duplicated key won differed between hosts.
            var files = Directory.EnumerateFiles(_gameDataDirectoryPath, "*.json")
                .OrderBy(f => Path.GetFileName(f).Equals("gamedata.json", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);

            foreach (string filePath in files)
            {
                var fileName = Path.GetFileName(filePath);

                // Per file: one malformed plugin gamedata file used to abort the whole loop and
                // drop every file after it, core signatures included.
                Dictionary<string, LoadedGameData>? loadedMethods;
                try
                {
                    string jsonContent = IsCoreFile(fileName) ? ReadCoreGameData(filePath) : File.ReadAllText(filePath, Encoding.UTF8);
                    loadedMethods = JsonSerializer.Deserialize<Dictionary<string, LoadedGameData>>(jsonContent);
                }
                catch (Exception ex)
                {
                    _logger.LogError("Skipping gamedata file {Path}, it could not be read: {Error}", filePath, ex.Message);
                    continue;
                }

                if (loadedMethods == null)
                {
                    _logger.LogWarning("Unable to load game data entries from {Path}, game data file is empty", filePath);
                    continue;
                }

                loadedFiles.Add(fileName);

                foreach (KeyValuePair<string, LoadedGameData> loadedMethod in loadedMethods)
                {
                    if (Methods.ContainsKey(loadedMethod.Key))
                    {
                        _logger.LogWarning("GameData Method \"{Key}\" loaded a duplicate entry from {filePath}.", loadedMethod.Key, filePath);
                    }

                    Methods[loadedMethod.Key] = loadedMethod.Value;
                }

                _logger.LogDebug("Loaded {Count} game data entries from {Path}", loadedMethods.Count, filePath);
                fileBreakdown.Add($"{fileName} ({loadedMethods.Count})");
            }

            LoadedFiles = loadedFiles;

            ValidateCurrentPlatformEntries(fileBreakdown);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load game data");
        }
    }

    private static bool IsCoreFile(string fileName) => fileName.Equals("gamedata.json", StringComparison.OrdinalIgnoreCase);

    // Core gamedata comes from native, which has already layered gamedata/custom/*.json over
    // gamedata.json (per-field merge, _gameVersion pins, signature checks: core/gamedata_custom.h).
    // Reading the file here instead would let managed bindings and native hooks disagree on the
    // same key. The file is the fallback for when there is no native side (tests, tooling).
    private string ReadCoreGameData(string filePath)
    {
        try
        {
            var effective = NativeAPI.GetCoreGamedata();
            if (!string.IsNullOrEmpty(effective)) return effective;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Core gamedata not available from native ({Error}), reading {Path}", ex.Message, filePath);
        }

        return File.ReadAllText(filePath, Encoding.UTF8);
    }

    // Fork-only: after loading, warn about entries that are missing a signature/offset
    // for the CURRENT platform. Consuming such a key (e.g. via VirtualFunctions) throws
    // at type-init time and takes down every plugin that touches that class, so surfacing
    // it here — early, by name — turns a cryptic TypeInitializationException cascade into
    // an actionable startup warning.
    //
    // On success this is also the one boot line for gamedata: entry count, per-file breakdown,
    // and the signature/offset split. The signature count matches the native
    // "Gamedata: all N signatures resolve" line, which scans exactly those entries.
    private void ValidateCurrentPlatformEntries(List<string> fileBreakdown)
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
        int signatures = 0;

        foreach (var kv in Methods)
        {
            var data = kv.Value;
            var sig = data.Signatures;
            var off = data.Offsets;

            // An entry is usable if it has a non-empty signature OR an offset for this platform.
            // Test for PRESENCE, not for non-zero: 0 is a real vtable index.
            bool hasSig = sig != null && !string.IsNullOrWhiteSpace(linux ? sig.Linux : sig.Windows);
            bool hasOff = off != null && (linux ? off.Linux : off.Windows).HasValue;

            if (hasSig) signatures++;
            if (!hasSig && !hasOff)
                broken.Add(kv.Key);
        }

        var platform = linux ? "Linux" : "Windows";
        var summary = string.Format("{0} entries from {1} file(s) [{2}]: {3} signatures, {4} offsets",
            Methods.Count, fileBreakdown.Count, string.Join(", ", fileBreakdown), signatures, Methods.Count - signatures);

        if (broken.Count > 0)
        {
            _logger.LogInformation("Gamedata: {Summary}", summary);
            _logger.LogWarning(
                "{Count} game data entr{Suffix} have no {Platform} signature/offset and will throw if used: {Keys}",
                broken.Count, broken.Count == 1 ? "y" : "ies", platform,
                string.Join(", ", broken));
        }
        else
        {
            _logger.LogInformation("Gamedata: {Summary}, all present for {Platform}", summary, platform);
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

    // Called per invoke by high-level wrappers (e.g. CBaseEntity.IsPlayerPawn), so one lookup.
    public static int GetOffset(string key)
    {
        if (!GameDataProvider.Methods.TryGetValue(key, out var methodMetadata))
        {
            throw new ArgumentException(BuildMissingKeyMessage(key));
        }

        if (methodMetadata.Offsets == null)
        {
            throw new Exception($"No offsets found for {key} in gamedata.json");
        }

        int offset;
        if (OperatingSystem.IsLinux())
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
                var name = Diagnostics.PluginDiagnostics.SafeAssemblyName(asm);
                if (string.IsNullOrEmpty(name) || name == "unknown") continue;
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