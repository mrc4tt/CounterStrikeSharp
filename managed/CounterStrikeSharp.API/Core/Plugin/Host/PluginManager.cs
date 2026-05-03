using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Core.Commands;
using CounterStrikeSharp.API.Core.Hosting;
using McMaster.NETCore.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CounterStrikeSharp.API.Core.Plugin.Host;

public class PluginManager : IPluginManager, IDisposable
{
    private readonly HashSet<PluginContext> _loadedPluginContexts = new();
    private readonly IScriptHostConfiguration _scriptHostConfiguration;
    private readonly ICommandManager _commandManager;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<PluginManager> _logger;
    private readonly Dictionary<string, Assembly> _sharedAssemblies = new();
    private bool _loadedSharedLibs = false;
    private SharedPluginFileWatcher? _sharedFileWatcher;

    public PluginManager(IScriptHostConfiguration scriptHostConfiguration, ICommandManager commandManager,
        ILogger<PluginManager> logger, IServiceProvider serviceProvider, IServiceScopeFactory serviceScopeFactory)
    {
        _scriptHostConfiguration = scriptHostConfiguration;
        _commandManager = commandManager;
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    internal SharedPluginFileWatcher? GetOrCreateSharedFileWatcher()
    {
        if (!CoreConfig.PluginHotReloadEnabled) return null;
        return _sharedFileWatcher ??= new SharedPluginFileWatcher(_scriptHostConfiguration.PluginPath, _logger);
    }

    public void Dispose()
    {
        _sharedFileWatcher?.Dispose();
        _sharedFileWatcher = null;
    }

    private void LoadLibrary(string path)
    {
        var loader = PluginLoader.CreateFromAssemblyFile(path, new[] { typeof(IPlugin), typeof(PluginCapability<>), typeof(PlayerCapability<>) },
            config => { config.PreferSharedTypes = true; });
        var assembly = loader.LoadDefaultAssembly();

        if (CoreConfig.PluginResolveNugetPackages)
        {
            foreach (var assemblyName in assembly.GetReferencedAssemblies())
            {
                if (TryLoadDependency(path, assembly.GetName().Name, assemblyName, out var dependency))
                {
                    _sharedAssemblies.TryAdd(dependency.GetName().Name, dependency);
                }
            }
        }

        _sharedAssemblies[assembly.GetName().Name] = assembly;
    }

    private void LoadSharedLibraries()
    {
        var sharedDirectory = Directory.GetDirectories(_scriptHostConfiguration.SharedPath);
        var sharedAssemblyPaths = sharedDirectory
            .Select(dir => Path.Combine(dir, Path.GetFileName(dir) + ".dll"))
            .Where(File.Exists)
            .ToArray();

        foreach (var sharedAssemblyPath in sharedAssemblyPaths)
        {
            try
            {
                LoadLibrary(sharedAssemblyPath);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to load shared assembly from {Path}", sharedAssemblyPath);
            }
        }
    }

    public void Load()
    {
        var pluginAssemblyPaths = GetPluginsAssemblyPaths();

        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            if (!_loadedSharedLibs)
            {
                LoadSharedLibraries();
                _loadedSharedLibs = true;
            }

            if (!_sharedAssemblies.TryGetValue(name.Name, out var assembly))
            {
                if (CoreConfig.PluginResolveNugetPackages && TryLoadExternalLibrary(name, out assembly))
                {
                    return assembly;
                }

                return null;
            }

            return assembly;
        };

        if (CoreConfig.PluginAutoLoadEnabled)
        {
            foreach (var path in pluginAssemblyPaths)
            {
                try
                {
                    LoadPlugin(path);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Failed to load plugin from {Path}", path);
                }
            }
        }

        foreach (var plugin in _loadedPluginContexts)
        {
            try
            {
                plugin.Plugin?.OnAllPluginsLoaded(false);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "OnAllPluginsLoaded failed");
            }
        }
    }

    private bool TryLoadExternalLibrary(AssemblyName assemblyName, out Assembly? assembly)
    {
        assembly = null;
        if (!TryResolveReflectionAssemblyPath(out var pluginName, out var pluginPath))
        {
            return false;
        }

        if (!TryLoadDependency(pluginPath, pluginName, assemblyName, out assembly))
        {
            return false;
        }

        return true;
    }

    private bool TryLoadDependency(string pluginAssemblyPath,
        string pluginAssemblyName,
        AssemblyName dependencyAssemblyName,
        out Assembly? assembly)
    {
        assembly = null;

        var dependencyName = dependencyAssemblyName.Name!;
        if (string.IsNullOrEmpty(pluginAssemblyPath) || _sharedAssemblies.ContainsKey(dependencyName))
        {
            return false;
        }

        var resolver = new PluginContextNuGetDependencyResolver(
            rootAssemblyName: pluginAssemblyName,
            rootAssemblyPath: Path.GetDirectoryName(pluginAssemblyPath)!,
            assemblyName: dependencyAssemblyName);

        var dependencyPath = resolver.ResolvePath();
        if (string.IsNullOrWhiteSpace(dependencyPath))
        {
            return false;
        }

        var loader = PluginLoader.CreateFromAssemblyFile(dependencyPath, configure: c =>
        {
            c.PreferSharedTypes = true;
        });

        assembly = loader.LoadDefaultAssembly();
        _sharedAssemblies[dependencyAssemblyName.Name!] = assembly;

        return true;
    }

    public IEnumerable<PluginContext> GetLoadedPlugins()
    {
        return _loadedPluginContexts;
    }

    public void LoadPlugin(string path)
    {
        var plugin = new PluginContext(_serviceProvider, _commandManager, _scriptHostConfiguration, path,
            _loadedPluginContexts.Select(x => x.PluginId).DefaultIfEmpty(0).Max() + 1,
            GetOrCreateSharedFileWatcher());
        _loadedPluginContexts.Add(plugin);
        plugin.Load();
    }

    private static bool TryResolveReflectionAssemblyPath(out string? assemblyName, out string? assemblyPath)
    {
        assemblyPath = null;
        assemblyName = null;

        if (AssemblyLoadContext.CurrentContextualReflectionContext is var reflectionContext && reflectionContext is null)
        {
            return false;
        }

        var mainAssemblyPathField = reflectionContext
            .GetType()
            .GetField("_mainAssemblyPath", BindingFlags.NonPublic | BindingFlags.Instance);

        if (mainAssemblyPathField is null)
        {
            return false;
        }

        assemblyPath = (string)mainAssemblyPathField.GetValue(reflectionContext)!;
        return !string.IsNullOrEmpty(assemblyPath);
    }
    
    private string[] GetPluginsAssemblyPaths()
    {
        // Skip "disabled" at root level
        var rootSubDirs = Directory.GetDirectories(_scriptHostConfiguration.PluginPath)
            .Where(d => !Path.GetFileName(d).Equals("disabled", StringComparison.OrdinalIgnoreCase));

        var pluginPaths = new List<string>();

        foreach (var subDir in rootSubDirs)
        {
            var stack = new Stack<string>();
            stack.Push(subDir);

            while (stack.Count > 0)
            {
                var currentDir = stack.Pop();
                var dirName = Path.GetFileName(currentDir);

                // 1. Conventional layout: <dirname>/<dirname>.dll — fastest, no metadata read.
                var conventional = Path.Combine(currentDir, dirName + ".dll");
                if (File.Exists(conventional))
                {
                    pluginPaths.Add(conventional);
                    continue;
                }

                // 2. Fallback: folder name doesn't match the DLL (e.g. FSH-MatchZy/MatchZy.dll).
                //    Identify the entry assembly by finding the top-level DLL that references
                //    CounterStrikeSharp.API. Read PE metadata only — no JIT, no AssemblyLoadContext.
                var entry = TryResolvePluginEntryByApiReference(currentDir);
                if (entry != null)
                {
                    _logger.LogInformation(
                        "Plugin entry resolved by CSSharp.API reference: {Path} (folder name '{Dir}' does not match DLL name)",
                        entry, dirName);
                    pluginPaths.Add(entry);
                    continue;
                }

                // 3. Recurse into subdirectories (preserves nested-plugin behavior).
                foreach (var child in Directory.GetDirectories(currentDir))
                    stack.Push(child);
            }
        }

        return pluginPaths.ToArray();
    }

    private string? TryResolvePluginEntryByApiReference(string directory)
    {
        string[] dlls;
        try
        {
            dlls = Directory.GetFiles(directory, "*.dll", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not enumerate DLLs in {Dir}", directory);
            return null;
        }

        if (dlls.Length == 0) return null;

        var candidates = new List<string>();
        foreach (var dll in dlls)
        {
            if (ReferencesCounterStrikeSharpApi(dll))
            {
                candidates.Add(dll);
            }
        }

        if (candidates.Count == 1) return candidates[0];

        if (candidates.Count > 1)
        {
            _logger.LogWarning(
                "Skipping {Dir}: ambiguous plugin entry — multiple DLLs reference CounterStrikeSharp.API. " +
                "Rename the folder to match one DLL, or place exactly one plugin DLL here. Candidates: {Candidates}",
                directory,
                string.Join(", ", candidates.Select(Path.GetFileName)));
        }

        return null;
    }

    private static bool ReferencesCounterStrikeSharpApi(string dllPath)
    {
        try
        {
            using var stream = File.OpenRead(dllPath);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return false;

            var md = pe.GetMetadataReader();
            foreach (var handle in md.AssemblyReferences)
            {
                var refName = md.GetString(md.GetAssemblyReference(handle).Name);
                if (string.Equals(refName, "CounterStrikeSharp.API", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }
        catch
        {
            // Not a managed PE, corrupt metadata, locked file, etc. — treat as non-plugin.
            return false;
        }
    }
}
