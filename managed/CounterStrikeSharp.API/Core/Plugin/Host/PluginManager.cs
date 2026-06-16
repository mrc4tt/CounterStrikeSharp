using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Core.Commands;
using CounterStrikeSharp.API.Core.Hosting;
using McMaster.NETCore.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CounterStrikeSharp.API.Core.Plugin.Host;

public class PluginManager : IPluginManager
{
    private readonly HashSet<PluginContext> _loadedPluginContexts = new();
    private readonly IScriptHostConfiguration _scriptHostConfiguration;
    private readonly ICommandManager _commandManager;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<PluginManager> _logger;
    private readonly Dictionary<string, Assembly> _sharedAssemblies = new();
    private bool _loadedSharedLibs = false;

    public PluginManager(IScriptHostConfiguration scriptHostConfiguration, ICommandManager commandManager,
        ILogger<PluginManager> logger, IServiceProvider serviceProvider, IServiceScopeFactory serviceScopeFactory)
    {
        _scriptHostConfiguration = scriptHostConfiguration;
        _commandManager = commandManager;
        _logger = logger;
        _serviceProvider = serviceProvider;
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
                    _logger.LogError("\n{Report}", PluginContext.BuildLoadFailureReportFromPath(e, path));
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
                if (plugin.Plugin != null)
                    _logger.LogError("\n{Report}", PluginContext.BuildLoadFailureReport(e, plugin.Plugin));
            }
        }

        LogPluginSummary();
    }

    // One-shot startup snapshot: a table of every plugin context with its version
    // and OK/FAILED status, plus loaded/failed counts and the API version. Gives
    // operators a single "what's running" reference next to any crash blame above.
    private void LogPluginSummary()
    {
        var rows = _loadedPluginContexts
            .OrderBy(c => c.PluginId)
            .ToList();
        if (rows.Count == 0) return;

        int loaded = 0, failed = 0;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        sb.AppendLine("============== COUNTERSTRIKESHARP PLUGINS LOADED ==============");
        sb.AppendLine(string.Format("  {0,-2} {1,-30} {2,-12} {3}", "#", "Plugin", "Version", "Status"));

        foreach (var c in rows)
        {
            bool ok = c.State == PluginState.Loaded;
            if (ok) loaded++; else failed++;

            var name = c.Plugin?.ModuleName
                       ?? System.IO.Path.GetFileNameWithoutExtension(c.FilePath);
            var version = c.Plugin?.ModuleVersion ?? "-";
            var status = ok ? "OK" : "FAILED (see log above)";
            if (name.Length > 30) name = name.Substring(0, 29) + "~";

            sb.AppendLine(string.Format("  {0,-2} {1,-30} {2,-12} {3}", c.PluginId, name, version, status));
        }

        sb.AppendLine("--------------------------------------------------------------");
        sb.AppendLine(string.Format("  {0} loaded, {1} failed  |  CSSharp API v{2}",
            loaded, failed, Api.GetVersion()));
        sb.Append("==============================================================");

        // Use Error level when any plugin failed so the summary also lands in
        // log-errors.txt alongside the individual crash reports.
        if (failed > 0)
            _logger.LogError("{Summary}", sb.ToString());
        else
            _logger.LogInformation("{Summary}", sb.ToString());
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
            _loadedPluginContexts.Select(x => x.PluginId).DefaultIfEmpty(0).Max() + 1);
        plugin.OnRequestRemoval = () => RemovePlugin(plugin);
        _loadedPluginContexts.Add(plugin);

        try
        {
            plugin.Load();
        }
        catch
        {
            // Load() threw before producing a usable plugin (pre-instance failure:
            // dep resolve, GetExportedTypes, version mismatch...). Leaving the
            // half-built context in the list makes it a "zombie" with Plugin == null
            // that poisons every later css_plugins query via NRE. Tear it down and
            // drop it, then rethrow so the caller still logs the real failure.
            RemovePlugin(plugin);
            throw;
        }
    }

    public void RemovePlugin(IPluginContext plugin)
    {
        // Dispose (full teardown: ALC, ServiceProvider, file watcher) and drop
        // the reference so the context is actually collectable. Without the
        // Remove, _loadedPluginContexts roots every plugin ever loaded.
        if (plugin is not PluginContext ctx) return;
        ctx.Dispose();
        _loadedPluginContexts.Remove(ctx);
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

        var pluginDirectories = new List<string>();

        foreach (var subDir in rootSubDirs)
        {
            var stack = new Stack<string>();
            stack.Push(subDir);

            while (stack.Count > 0)
            {
                var currentDir = stack.Pop();
                var dirName = Path.GetFileName(currentDir);
                var expectedDll = Path.Combine(currentDir, dirName + ".dll");

                if (File.Exists(expectedDll))
                {
                    pluginDirectories.Add(currentDir);
                    // Stop scanning deeper in this directory
                    continue;
                }

                // Add subdirectories to stack for further scanning
                foreach (var child in Directory.GetDirectories(currentDir))
                    stack.Push(child);
            }
        }

        return pluginDirectories
                .Select(d => Path.Combine(d, Path.GetFileName(d) + ".dll"))
                .ToArray();
    }
}
