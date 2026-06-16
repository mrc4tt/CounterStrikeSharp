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
using Serilog.Sinks.SystemConsole.Themes;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using System.Threading;
using System;

namespace CounterStrikeSharp.API.Core.Plugin
{
    public interface ISelfPluginControl
    {
        void TerminateSelf(string reason);
    }

    public class PluginContext : IPluginContext, ISelfPluginControl, IDisposable
    {
        public PluginState State { get; set; } = PluginState.Unregistered;
        public IPlugin Plugin { get; private set; }

        private bool _disposed;

        // Set by PluginManager. Invoked when the context wants to be fully torn
        // down (e.g. its DLL was deleted) so the manager drops + disposes it.
        internal Action OnRequestRemoval { get; set; }

        private PluginLoader Loader { get; set; }

        private ServiceProvider ServiceProvider { get; set; }

        public int PluginId { get; }

        private readonly ICommandManager _commandManager;
        private readonly IScriptHostConfiguration _hostConfiguration;
        private readonly string _path;
        private readonly FileSystemWatcher _fileWatcher;
        private readonly IServiceProvider _applicationServiceProvider;

        public string FilePath => _path;
        private IServiceScope _serviceScope;

        public string TerminationReason { get; private set; }

        // TOOD: ServiceCollection
        private ILogger _logger = CoreLogging.Factory.CreateLogger<PluginContext>();

        public PluginContext(IServiceProvider applicationServiceProvider, ICommandManager commandManager,
            IScriptHostConfiguration hostConfiguration,
            string path, int id)
        {
            _commandManager = commandManager;
            _hostConfiguration = hostConfiguration;
            _path = path;
            PluginId = id;

            Loader = PluginLoader.CreateFromAssemblyFile(path,
                new[]
                {
                    typeof(IPlugin), typeof(ILogger), typeof(IServiceCollection), typeof(IPluginServiceCollection<>),
                    typeof(ICommandManager)
                }, config =>
                {
                    config.EnableHotReload = true;
                    config.IsUnloadable = true;
                    config.PreferSharedTypes = true;
                });

            if (CoreConfig.PluginHotReloadEnabled)
            {
                _fileWatcher = new FileSystemWatcher
                {
                    Path = Path.GetDirectoryName(path)
                };

                _fileWatcher.Deleted += async (s, e) =>
                {
                    Server.NextWorldUpdate(() =>
                    {
                        if (e.FullPath == path)
                        {
                            _logger.LogInformation("Plugin {Name} has been deleted, unloading...", Plugin.ModuleName);
                            Unload(true);
                            // DLL is gone for good — release the ALC/watcher and
                            // drop the context from the manager rather than
                            // leaving a dead entry that leaks its Loader.
                            OnRequestRemoval?.Invoke();
                        }
                    });
                };

                _fileWatcher.Filter = "*.dll";
                _fileWatcher.EnableRaisingEvents = true;
                Loader.Reloaded += async (s, e) => await OnReloadedAsync(s, e);
            }
        }

        private Task OnReloadedAsync(object sender, PluginReloadedEventArgs eventargs)
        {
            Server.NextWorldUpdate(() =>
            {
                _logger.LogInformation("Reloading plugin {Name}", Plugin.ModuleName);
                Loader = eventargs.Loader;
                try
                {
                    Unload(hotReload: true);
                    Load(hotReload: true);
                    Plugin?.OnAllPluginsLoaded(hotReload: true);
                }
                catch (Exception ex)
                {
                    // Pre-instance failures (bad DLL, no IPlugin type, version
                    // mismatch) throw out of Load() and would otherwise crash the
                    // world-update tick. Catch, report, leave the plugin unloaded.
                    _logger.LogError(ex, "Failed to hot-reload plugin from {Path}", _path);
                    _logger.LogError("\n{Report}", BuildLoadFailureReportFromPath(ex, _path));
                }
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
                        // Same ANSI treatment as the core logger (CoreLogging.cs): a theme
                        // colors the level (INFO/WARN/EROR) so it survives docker/screen
                        // pipes, and the (plugin:...) prefix is magenta to set plugin output
                        // apart from the cyan (cssharp:...) framework lines. Console sink
                        // only; the file sinks below stay plain text.
                        .WriteTo.Console(
                            theme: AnsiConsoleTheme.Code,
                            outputTemplate:
                            "{Timestamp:HH:mm:ss} [{Level:u4}] \x1b[35m(plugin:{PluginName})\x1b[0m {Message:lj}{NewLine}{Exception}")
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
                    .SelectMany(assembly => assembly.GetTypes())
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
                var loadTimer = System.Diagnostics.Stopwatch.StartNew();
                // Mark this plugin as the prime suspect for the native fatal handler.
                // If OnLoad (or a hook/timer it schedules) triggers a garbage-collected-
                // delegate FailFast, the SIGABRT handler names this plugin as the last
                // console line instead of an anonymous "Process terminated".
                NativeAPI.SetFatalSuspectPlugin(Plugin.ModuleName ?? Plugin.GetType().Assembly.GetName().Name ?? "unknown");
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
                        var report = BuildLoadFailureReport(ex, Plugin);
                        _logger.LogError(ex, "Failed to load plugin {Name}", Plugin.ModuleName);
                        _logger.LogError("\n{Report}", report);
                        this.TerminationReason = ex.Message ?? "Unknown";

                        // Stash the rendered banner keyed by assembly name. If this plugin
                        // leaks a callback after the failed load, the runtime dead-callback
                        // path re-pastes THIS exact banner (capped) so an operator who missed
                        // it the first time still gets the full, actionable report.
                        FunctionReference.RecordLoadFailure(
                            Plugin.GetType().Assembly.GetName().Name,
                            Plugin.ModuleName,
                            report);
                    }

                    Unload(hotReload);
                    return;
                }

                loadTimer.Stop();
                // Loaded cleanly: clear the suspect so a later unrelated fatal does not
                // blame this plugin. A failed load deliberately leaves it set, because
                // that is exactly when the leftover-callback crash fires (next frame).
                NativeAPI.SetFatalSuspectPlugin(string.Empty);
                _logger.LogInformation("Finished loading plugin {Name} in {Ms}ms", Plugin.ModuleName, loadTimer.ElapsedMilliseconds);

                State = PluginState.Loaded;
            }
        }


        // Builds a human-readable blame report for a plugin load failure. Walks the
        // exception stack to decide whether the fault lies in the plugin's own code
        // or in CounterStrikeSharp itself, names the offending plugin frame, and
        // suggests a fix for common failure modes. Goal: stop operators blaming the
        // framework for crashes that originate inside third-party plugin code.
        internal static string BuildLoadFailureReport(Exception ex, IPlugin plugin)
        {
            var root = ex.GetBaseException();
            var pluginAsm = plugin.GetType().Assembly;
            var pluginName = plugin.ModuleName;

            // Find the deepest frame (closest to the throw) that belongs to the
            // plugin's own assembly, and the deepest CSSharp frame.
            var trace = new System.Diagnostics.StackTrace(root, true);
            System.Diagnostics.StackFrame pluginFrame = null;
            bool throwInsideCssharp = false;
            bool sawPluginFrame = false;

            var frames = trace.GetFrames() ?? Array.Empty<System.Diagnostics.StackFrame>();
            for (int i = 0; i < frames.Length; i++)
            {
                var m = frames[i].GetMethod();
                var asm = m?.DeclaringType?.Assembly;
                if (asm == null) continue;

                bool isCssharp = asm == typeof(PluginContext).Assembly;
                bool isPlugin = asm == pluginAsm;

                if (i == 0)
                    throwInsideCssharp = isCssharp;

                if (isPlugin)
                {
                    sawPluginFrame = true;
                    if (pluginFrame == null) pluginFrame = frames[i];
                }
            }

            // Blame logic:
            //  - throw originates in CSSharp but plugin code called into it    -> plugin misuse
            //  - throw originates in plugin code                              -> plugin fault
            //  - throw in CSSharp with NO plugin frame in the chain           -> likely framework
            bool frameworkFault = throwInsideCssharp && !sawPluginFrame;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("==================== PLUGIN LOAD FAILURE ====================");
            sb.AppendLine("Plugin:    " + pluginName);
            // No "Error:" line — the full exception + message is already logged right above
            // this banner as the raw stack trace, so repeating it here is duplication.

            if (pluginFrame != null)
            {
                var m = pluginFrame.GetMethod();
                var loc = m?.DeclaringType?.FullName + "." + m?.Name + "()";
                var file = pluginFrame.GetFileName();
                var line = pluginFrame.GetFileLineNumber();
                sb.AppendLine("Location:  " + loc + (file != null ? " at " + file + ":" + line : ""));
            }

            if (frameworkFault)
            {
                sb.AppendLine("Blame:     CounterStrikeSharp (no plugin code in the call chain).");
                sb.AppendLine("           This one may be a framework/gamedata issue. Report it with this log.");
            }
            else
            {
                sb.AppendLine("Blame:     Plugin '" + pluginName + "' — the failure is in the plugin's own code.");
            }

            var hint = FixHintFor(root);
            if (hint != null)
            {
                sb.AppendLine("Fix:       " + hint);
            }

            sb.AppendLine("Action:    Disable/remove this plugin, or contact its author with the trace above.");
            sb.AppendLine("Support:   " + Diagnostics.PluginDiagnostics.SupportContact);
            sb.Append("============================================================");
            return sb.ToString();
        }

        // Builds a blame report for failures that happen BEFORE a plugin instance
        // exists (bad/missing DLL, no IPlugin type, version mismatch). These throw
        // out of Load() and are caught by PluginManager with only the file path in
        // hand, so this variant works from the path + exception alone.
        internal static string BuildLoadFailureReportFromPath(Exception ex, string path)
        {
            var root = ex.GetBaseException();
            var name = System.IO.Path.GetFileNameWithoutExtension(path);

            // Surface the deepest non-CSSharp frame if the trace has one.
            string thirdPartyLoc = null;
            var trace = new System.Diagnostics.StackTrace(root, true);
            foreach (var f in trace.GetFrames() ?? Array.Empty<System.Diagnostics.StackFrame>())
            {
                var m = f.GetMethod();
                var asm = m?.DeclaringType?.Assembly;
                if (asm == null || asm == typeof(PluginContext).Assembly) continue;
                thirdPartyLoc = m.DeclaringType.FullName + "." + m.Name + "()";
                break;
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("==================== PLUGIN LOAD FAILURE ====================");
            sb.AppendLine("Plugin:    " + name + " (failed before it could initialize)");
            sb.AppendLine("File:      " + path);
            // No "Error:" line — the exception is already logged right above as a raw trace.
            if (thirdPartyLoc != null)
                sb.AppendLine("Location:  " + thirdPartyLoc);
            sb.AppendLine("Blame:     Plugin file '" + name + "' — CounterStrikeSharp could not turn this DLL into a working plugin.");

            var hint = FixHintFor(root);
            if (hint != null)
                sb.AppendLine("Fix:       " + hint);

            sb.AppendLine("Action:    Remove this DLL from the plugins folder, or contact its author.");
            sb.AppendLine("Support:   " + Diagnostics.PluginDiagnostics.SupportContact);
            sb.Append("============================================================");
            return sb.ToString();
        }

        // Maps common load-failure exceptions to a one-line remediation hint.
        internal static string FixHintFor(Exception ex)
        {
            var msg = ex.Message ?? string.Empty;

            if (msg.Contains("Unable to find plugin in assembly"))
                return "This DLL has no class extending BasePlugin / implementing IPlugin, so it is NOT a plugin. "
                     + "Most common cause: a SHARED library/dependency DLL was put in the 'plugins' folder. Shared "
                     + "libraries belong in the 'shared' folder (addons/counterstrikesharp/shared/<Name>/<Name>.dll), "
                     + "not 'plugins'. Move it there. (Also check the plugin DLL matches its folder name: "
                     + "plugins/<Name>/<Name>.dll.)";

            if (ex is NativeException && msg.Contains("Global Variables not initialized"))
                return "Plugin calls server/player API (e.g. MaxPlayers, GetPlayers) during Load() before the "
                     + "server is ready. Move that logic into OnMapStart / a Listener / OnAllPluginsLoaded.";

            if (msg.Contains("requires a newer version of CounterStrikeSharp"))
                return "Update CounterStrikeSharp to the version this plugin needs (see version numbers above).";

            if (ex is System.IO.FileNotFoundException || ex is System.IO.FileLoadException
                || ex is BadImageFormatException || msg.Contains("Could not load file or assembly"))
            {
                // Pull the missing assembly's simple name out of the exception so the
                // hint can name it and detect shared libraries (".Shared" convention).
                var missing = (ex as System.IO.FileNotFoundException)?.FileName
                              ?? (ex as System.IO.FileLoadException)?.FileName;
                var simpleName = missing?.Split(',')[0];

                if (simpleName != null && simpleName.IndexOf("Shared", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "Missing SHARED dependency '" + simpleName + "'. Shared libraries must be installed in "
                         + "the 'shared' folder (addons/counterstrikesharp/shared/" + simpleName + "/" + simpleName
                         + ".dll). Install the plugin/library that provides it.";

                return "A dependency DLL is missing or mismatched"
                     + (simpleName != null ? " ('" + simpleName + "')" : "")
                     + ". Ship the plugin's required libraries next to its .dll, or install the shared library it needs.";
            }

            if (msg.Contains("Gamedata key") || msg.Contains("gamedata.json"))
                return "Ship/update the missing signature. If plugin-specific: drop the plugin's own "
                     + "'<PluginName>.gamedata.json' into the gamedata folder (or update it). If core: add the key "
                     + "to gamedata.json — a CS2 update may have renamed/removed it.";

            if (ex is TypeInitializationException && (msg.Contains("GameData") || msg.Contains("Signature")))
                return "A gamedata signature is missing/outdated. Update gamedata.json — this can be a CS2 update issue.";

            if (ex is System.IO.DirectoryNotFoundException || msg.Contains("config") || msg.Contains("Config"))
                return "A config/file path the plugin expects is missing. Check the plugin's config files exist.";

            return null;
        }

        public void Unload(bool hotReload = false)
        {
            if (State == PluginState.Unloaded) return;

            // A context whose Load() threw before building the instance has a null
            // Plugin (State still Unregistered). Nothing to tear down — just mark it
            // unloaded so Dispose() can release the ALC without an NRE.
            if (Plugin == null)
            {
                State = PluginState.Unloaded;
                return;
            }

            State = PluginState.Unloaded;
            var cachedName = Plugin.ModuleName;

            _logger.LogInformation("Unloading plugin {Name}", Plugin.ModuleName);

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
                Plugin?.Dispose();
                _serviceScope?.Dispose();
                // Each Load() builds a fresh ServiceProvider (Serilog file sinks
                // + singletons). Dispose the old one or every reload/restart
                // leaks open log file handles and the singleton graph.
                ServiceProvider?.Dispose();
                ServiceProvider = null;
            }

            _logger.LogInformation("Finished unloading plugin {Name}", cachedName);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (State != PluginState.Unloaded)
            {
                Unload(false);
            }

            // ALC + inotify instance live for the lifetime of the context, not a
            // single Load/Unload cycle — only release them on full teardown.
            _fileWatcher?.Dispose();
            Loader?.Dispose();
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