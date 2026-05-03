/*
 *  This file is part of CounterStrikeSharp.
 *  CounterStrikeSharp is free software: you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation, either version 3 of the License, or
 *  (at your option) any later version.
 *
 *  CounterStrikeSharp is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 *
 *  You should have received a copy of the GNU General Public License
 *  along with CounterStrikeSharp.  If not, see <https://www.gnu.org/licenses/>. *
 */

using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Core.Commands;
using CounterStrikeSharp.API.Core.Hosting;
using CounterStrikeSharp.API.Core.Logging;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Core.Plugin.Host;
using McMaster.NETCore.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Serilog;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using System.Threading;
using System;

namespace CounterStrikeSharp.API.Core.Plugin
{
    public interface ISelfPluginControl
    {
        void TerminateSelf(string reason);
    }

    public class PluginContext : IPluginContext, ISelfPluginControl
    {
        public PluginState State { get; set; } = PluginState.Unregistered;
        public IPlugin Plugin { get; private set; }

        private PluginLoader Loader { get; set; }

        private ServiceProvider ServiceProvider { get; set; }

        public int PluginId { get; }

        private readonly ICommandManager _commandManager;
        private readonly IScriptHostConfiguration _hostConfiguration;
        private readonly string _path;
        private readonly SharedPluginFileWatcher? _sharedFileWatcher;
        private readonly IServiceProvider _applicationServiceProvider;

        public string FilePath => _path;
        private IServiceScope _serviceScope;

        public string TerminationReason { get; private set; }

        // TOOD: ServiceCollection
        private ILogger _logger = CoreLogging.Factory.CreateLogger<PluginContext>();

        public PluginContext(IServiceProvider applicationServiceProvider, ICommandManager commandManager,
            IScriptHostConfiguration hostConfiguration,
            string path, int id,
            SharedPluginFileWatcher? sharedFileWatcher = null)
        {
            _commandManager = commandManager;
            _hostConfiguration = hostConfiguration;
            _path = path;
            _sharedFileWatcher = sharedFileWatcher;
            PluginId = id;

            Loader = CreateLoader(path);

            if (CoreConfig.PluginHotReloadEnabled)
            {
                _sharedFileWatcher?.RegisterDelete(path, () =>
                {
                    Server.NextWorldUpdate(() =>
                    {
                        _logger.LogInformation("Plugin {Name} has been deleted, unloading...", Plugin.ModuleName);
                        Unload(true);
                    });
                });

                Loader.Reloaded += async (s, e) => await OnReloadedAsync(s, e);
            }
        }

        /// <summary>
        /// Returns the types from <paramref name="assembly"/> that successfully loaded, skipping any
        /// that would otherwise throw <see cref="ReflectionTypeLoadException"/> due to stale or
        /// unresolved references. This protects the AppDomain-wide scan in <see cref="Load"/> from
        /// being killed by a single leaked <see cref="System.Runtime.Loader.AssemblyLoadContext"/>
        /// (e.g. one left behind by an earlier failed reload) whose remaining types reference an
        /// assembly version that no longer exists.
        /// </summary>
        private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(t => t != null)!;
            }
            catch
            {
                return Array.Empty<Type>();
            }
        }

        private static PluginLoader CreateLoader(string path)
        {
            return PluginLoader.CreateFromAssemblyFile(path,
                new[]
                {
                    typeof(IPlugin), typeof(ILogger), typeof(IServiceCollection), typeof(IPluginServiceCollection<>),
                    typeof(ICommandManager)
                }, config =>
                {
                    // Each EnableHotReload=true allocates its own inotify instance on Linux.
                    // Gate it on the user-facing flag so disabling hot-reload actually frees those FDs.
                    config.EnableHotReload = CoreConfig.PluginHotReloadEnabled;
                    config.IsUnloadable = true;
                    config.PreferSharedTypes = true;
                });
        }

        /// <summary>
        /// Unload, dispose the AssemblyLoadContext, and re-create a fresh PluginLoader so the next
        /// Load() reads the current bytes from disk. Use this for manual <c>css_plugins restart</c>
        /// flows where the operator may have replaced the DLL on disk before issuing the command.
        /// Calling Unload() + Load() directly reuses the same AssemblyLoadContext, which either
        /// returns the cached old assembly or throws FileLoadException with the previous identity.
        /// </summary>
        /// <returns>
        /// True if the plugin was reloaded successfully. False if pre-load validation failed —
        /// in that case the previously loaded plugin remains active and unchanged.
        /// </returns>
        public bool Reload(bool hotReload = true)
        {
            if (!File.Exists(_path))
            {
                _logger.LogError("Cannot reload: plugin DLL is missing at {Path}", _path);
                return false;
            }

            // Pre-validate the file in a throwaway AssemblyLoadContext. If the new bytes on disk
            // are malformed (corrupted upload, broken strong-name blob from a bad obfuscator,
            // wrong file type, etc.), catching it here lets us *abort* the reload before we
            // destroy the working plugin instance. Without this, a failed Load() leaves the plugin
            // permanently unloaded until the operator notices and restarts the host.
            if (!TryProbeLoadAssembly(_path, out var probeError))
            {
                var displayName = (State == PluginState.Loaded ? Plugin?.ModuleName : null)
                                  ?? Path.GetFileNameWithoutExtension(_path);
                _logger.LogError(
                    "Aborting reload of {Name}: the file at {Path} failed pre-load validation. " +
                    "The currently loaded version remains active. Reason: {Reason}",
                    displayName, _path, probeError);
                return false;
            }

            Unload(hotReload);

            try
            {
                (Loader as IDisposable)?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to dispose previous PluginLoader for {Path} — continuing with fresh loader anyway", _path);
            }

            Loader = CreateLoader(_path);

            if (CoreConfig.PluginHotReloadEnabled)
            {
                Loader.Reloaded += async (s, e) => await OnReloadedAsync(s, e);
            }

            try
            {
                Load(hotReload);
            }
            catch (Exception ex)
            {
                // Probe passed but real load failed (rare — usually a CSSharp-side init error,
                // not an assembly-validity error). Surface clearly; the plugin is now unloaded.
                _logger.LogError(ex,
                    "Reload of {Path} passed pre-load validation but failed during plugin initialization. " +
                    "Plugin is now unloaded.", _path);
                return false;
            }

            try
            {
                Plugin?.OnAllPluginsLoaded(hotReload);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OnAllPluginsLoaded threw after reload of {Name}", Plugin?.ModuleName ?? _path);
            }

            return true;
        }

        /// <summary>
        /// Probe-load an assembly in a throwaway collectible AssemblyLoadContext. Surfaces the
        /// same exceptions the real load path would (BadImageFormatException, SecurityException
        /// for invalid strong-name, FileLoadException for identity collisions, etc.) without
        /// touching the live plugin's load context. The probe ALC is unloaded before returning.
        /// </summary>
        private static bool TryProbeLoadAssembly(string path, out string? error)
        {
            var alc = new System.Runtime.Loader.AssemblyLoadContext(
                name: $"PluginProbe::{Path.GetFileNameWithoutExtension(path)}",
                isCollectible: true);

            try
            {
                alc.LoadFromAssemblyPath(path);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message.Trim()}";
                return false;
            }
            finally
            {
                try { alc.Unload(); }
                catch { /* best-effort cleanup; the probe ALC will be GC'd eventually anyway */ }
            }
        }

        private Task OnReloadedAsync(object sender, PluginReloadedEventArgs eventargs)
        {
            Server.NextWorldUpdate(() =>
            {
                _logger.LogInformation("Reloading plugin {Name}", Plugin.ModuleName);
                Loader = eventargs.Loader;
                Unload(hotReload: true);
                Load(hotReload: true);
                Plugin.OnAllPluginsLoaded(hotReload: true);
            });

            return Task.CompletedTask;
        }

        public void Load(bool hotReload = false)
        {
            if (State == PluginState.Loaded) return;

            using (Loader.EnterContextualReflection())
            {
                var defaultAssembly = Loader.LoadDefaultAssembly();

                Type pluginType = defaultAssembly.GetExportedTypes()
                    .FirstOrDefault(t => typeof(IPlugin).IsAssignableFrom(t));

                if (pluginType == null) throw new Exception("Unable to find plugin in assembly");

                var serviceCollection = new ServiceCollection();

                serviceCollection.Scan(scan =>
                    scan.FromAssemblies(defaultAssembly)
                        .AddClasses(c => c.AssignableTo<IPlugin>())
                        .AsSelf()
                        .WithSingletonLifetime()
                );

                serviceCollection.AddLogging(builder =>
                {
                    builder.ClearProviders();
                    builder.AddSerilog(new LoggerConfiguration()
                        .Enrich.FromLogContext()
                        .Enrich.With(new PluginNameEnricher(this))
                        .WriteTo.Console(
                            outputTemplate:
                            "{Timestamp:HH:mm:ss} [{Level:u4}] (plugin:{PluginName}) {Message:lj}{NewLine}{Exception}")
                        .WriteTo.File(
                            Path.Join(new[]
                            {
                                _hostConfiguration.RootPath, "logs",
                                $"log-{pluginType.Assembly.GetName().Name}.txt"
                            }), rollingInterval: RollingInterval.Day,
                            outputTemplate:
                            "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u4}] plugin:{PluginName} {Message:lj}{NewLine}{Exception}")
                        .WriteTo.File(Path.Join(new[] { _hostConfiguration.RootPath, "logs", $"log-all.txt" }),
                            rollingInterval: RollingInterval.Day, shared: true,
                            outputTemplate:
                            "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u4}] plugin:{PluginName} {Message:lj}{NewLine}{Exception}")
                        .CreateLogger());
                });

                Type interfaceType = typeof(IPluginServiceCollection<>).MakeGenericType(pluginType);
                Type[] serviceCollectionConfiguratorTypes = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(assembly => GetLoadableTypes(assembly))
                    .Where(type => interfaceType.IsAssignableFrom(type) && !type.IsInterface && !type.IsAbstract)
                    .ToArray();

                if (serviceCollectionConfiguratorTypes.Any())
                {
                    foreach (var t in serviceCollectionConfiguratorTypes)
                    {
                        var pluginServiceCollection = Activator.CreateInstance(t);
                        MethodInfo method = t.GetMethod("ConfigureServices");
                        method?.Invoke(pluginServiceCollection, new object[] { serviceCollection });
                    }
                }

                serviceCollection.AddScoped<ICommandManager>(c => _commandManager);
                serviceCollection.DecorateSingleton<ICommandManager, PluginCommandManagerDecorator>();

                serviceCollection.AddSingleton<IPluginContext>(this);
                serviceCollection.TryAddSingleton<IStringLocalizerFactory, JsonStringLocalizerFactory>();
                serviceCollection.TryAddTransient(typeof(IStringLocalizer<>), typeof(StringLocalizer<>));
                serviceCollection.TryAddTransient(typeof(IStringLocalizer), typeof(StringLocalizer));
                ServiceProvider = serviceCollection.BuildServiceProvider();

                var minimumApiVersion = pluginType.GetCustomAttribute<MinimumApiVersion>()?.Version;
                var currentVersion = Api.GetVersion();

                // Ignore version 0 for local development
                if (currentVersion > 0 && minimumApiVersion != null && minimumApiVersion > currentVersion)
                    throw new Exception(
                        $"Plugin \"{Path.GetFileName(_path)}\" requires a newer version of CounterStrikeSharp. The plugin expects version [{minimumApiVersion}] but the current version is [{currentVersion}].");

                _logger.LogInformation("Loading plugin {Name}", pluginType.Assembly.GetName().Name);

                _serviceScope = ServiceProvider.CreateScope();

                Plugin = _serviceScope.ServiceProvider.GetRequiredService(pluginType) as IPlugin;

                if (Plugin == null) throw new Exception("Unable to create plugin instance");

                State = PluginState.Loading;

                Plugin.ModulePath = _path;
                Plugin.Logger = _serviceScope.ServiceProvider.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(pluginType);
                Plugin.CommandManager = _serviceScope.ServiceProvider.GetRequiredService<ICommandManager>();
                Plugin.RegisterAllAttributes(Plugin);
                Plugin.Localizer = ServiceProvider.GetRequiredService<IStringLocalizer>();
                Plugin.Logger = ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(pluginType);

                Plugin.InitializeConfig(Plugin, pluginType);

                if (Plugin is BasePlugin basePlugin)
                {
                    basePlugin.SelfControl = this;
                }

                this.TerminationReason = string.Empty;
                try
                {
                    Plugin.Load(hotReload);
                }
                catch (Exception ex)
                {
                    if ((ex.InnerException ?? ex) is PluginTerminationException pluginEx)
                    {
                        _logger.LogCritical("Terminating plugin {Name} with reason: {Reason}", Plugin.ModuleName, pluginEx.TerminationReason);
                        this.TerminationReason = pluginEx.TerminationReason;
                    }
                    else
                    {
                        _logger.LogError(ex, "Failed to load plugin {Name}", Plugin.ModuleName);
                        this.TerminationReason = ex.Message ?? "Unknown";
                    }

                    Unload(hotReload);
                    return;
                }

                _logger.LogInformation("Finished loading plugin {Name}", Plugin.ModuleName);

                State = PluginState.Loaded;
            }
        }


        public void Unload(bool hotReload = false)
        {
            if (State == PluginState.Unloaded) return;

            State = PluginState.Unloaded;
            var cachedName = Plugin.ModuleName;

            _logger.LogInformation("Unloading plugin {Name}", Plugin.ModuleName);

            if (!hotReload)
            {
                _sharedFileWatcher?.UnregisterDelete(_path);
            }

            try
            {
                Plugin.Unload(hotReload);
            }
            catch
            {
                _logger.LogError("Failed to unload {Name} during error recovery, forcing cleanup", Plugin.ModuleName);
                return;
            }
            finally
            {
                Plugin.Dispose();
                _serviceScope.Dispose();
            }

            _logger.LogInformation("Finished unloading plugin {Name}", cachedName);
        }

        public void TerminateWithReason(string reason)
        {
            this.TerminationReason = reason;

            switch (State)
            {
                case PluginState.Unloaded:
                case PluginState.Loading:
                    break;
                case PluginState.Loaded:
                    _logger.LogInformation("Terminating plugin {Name} with reason: {Reason}", Plugin.ModuleName, reason);
                    Unload(false);
                    break;
            }

            // Force execution flow interruption via globally-handled exception to prevent stack unwinding
            throw new PluginTerminationException(reason);
        }

        void ISelfPluginControl.TerminateSelf(string reason)
        {
            if (State != PluginState.Unloaded)
            {
                if (Thread.CurrentThread.IsThreadPoolThread)
                {
                    Server.NextFrame(() => TerminateWithReason(reason));
                }
                else
                {
                    TerminateWithReason(reason);
                }

                // **Failsafe mechanism** ensures execution termination
                // Prevents control flow leakage back to plugin execution context
                throw new NotImplementedException();
            }
        }
    }
}