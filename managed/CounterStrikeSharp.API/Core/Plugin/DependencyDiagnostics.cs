using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CounterStrikeSharp.API.Core.Plugin;

/// <summary>
/// Explains a "Could not load file or assembly 'X'" plugin failure from facts instead of
/// guesses: the plugin's own <c>.deps.json</c> says whether X ships with the plugin (so it
/// belongs next to the plugin's .dll and the upload is incomplete) or is expected from the
/// host (so it belongs in shared/), and a scan of plugins/ and shared/ says where copies of X
/// actually are and which version each is. When neither source can tell, the report says so.
/// </summary>
internal static class DependencyDiagnostics
{
    internal sealed record Copy(string Path, Version? Version);

    internal sealed record Finding(
        AssemblyName Missing,
        bool HasDepsJson,
        // true: the plugin's build ships the DLL beside it; false: listed or referenced but
        // expected from the host; null: no deps.json, so unknown.
        bool? ShipsWithPlugin,
        string? DepsVersion,
        IReadOnlyList<Copy> Copies);

    private static readonly Regex AssemblyInMessage = new(@"assembly '([^']+)'", RegexOptions.Compiled);

    /// <summary>The assembly a load failed to find, or null if this is not that kind of failure.</summary>
    internal static AssemblyName? MissingAssembly(Exception root)
    {
        string? fullName = root switch
        {
            FileNotFoundException f => f.FileName,
            FileLoadException f => f.FileName,
            _ => null,
        };

        if (fullName == null)
        {
            var match = AssemblyInMessage.Match(root.Message ?? "");
            if (!match.Success || !(root is IOException || root is BadImageFormatException)) return null;
            fullName = match.Groups[1].Value;
        }

        try
        {
            var name = new AssemblyName(fullName);
            return string.IsNullOrEmpty(name.Name) ? null : name;
        }
        catch
        {
            return null;
        }
    }

    internal static Finding Analyze(AssemblyName missing, string pluginPath, string? cssRoot)
    {
        var (hasDeps, ships, depsVersion) = ReadDepsJson(missing.Name!, pluginPath);
        return new Finding(missing, hasDeps, ships, depsVersion, FindCopies(missing.Name!, cssRoot));
    }

    private static (bool HasDepsJson, bool? Ships, string? Version) ReadDepsJson(string name, string pluginPath)
    {
        var depsPath = Path.ChangeExtension(pluginPath, ".deps.json");
        if (!File.Exists(depsPath)) return (false, null, null);

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(depsPath));
            if (!doc.RootElement.TryGetProperty("targets", out var targets)) return (true, false, null);

            string? version = null;
            foreach (var target in targets.EnumerateObject())
            {
                foreach (var library in target.Value.EnumerateObject())
                {
                    // Keys look like "CSSharpUtils/1.0.9".
                    var slash = library.Name.IndexOf('/');
                    var libName = slash >= 0 ? library.Name[..slash] : library.Name;
                    var libVersion = slash >= 0 ? library.Name[(slash + 1)..] : null;

                    if (library.Value.TryGetProperty("runtime", out var runtime))
                    {
                        foreach (var asset in runtime.EnumerateObject())
                        {
                            if (Path.GetFileNameWithoutExtension(asset.Name).Equals(name, StringComparison.OrdinalIgnoreCase))
                                return (true, true, libVersion);
                        }
                    }

                    if (libName.Equals(name, StringComparison.OrdinalIgnoreCase))
                        version = libVersion;
                }
            }

            // Listed without a runtime asset (e.g. a compile-only reference), or not listed at
            // all: the plugin's build did not ship it, so it expects the host to provide it.
            return (true, false, version);
        }
        catch
        {
            return (false, null, null);
        }
    }

    private static IReadOnlyList<Copy> FindCopies(string name, string? cssRoot)
    {
        var copies = new List<Copy>();
        if (string.IsNullOrEmpty(cssRoot)) return copies;

        foreach (var dir in new[] { "plugins", "shared" })
        {
            var root = Path.Combine(cssRoot, dir);
            if (!Directory.Exists(root)) continue;

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, name + ".dll", SearchOption.AllDirectories).Take(8).ToList();
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                Version? version = null;
                try
                {
                    version = AssemblyName.GetAssemblyName(file).Version;
                }
                catch
                {
                    // not a managed assembly, or unreadable
                }

                copies.Add(new Copy(file, version));
            }
        }

        return copies;
    }
}
