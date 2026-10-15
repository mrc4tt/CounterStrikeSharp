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
        // Assigned during Load() and asserted non-null there; treated as non-null
        // for the lifetime a context is exposed.
        public IPlugin Plugin { get; private set; } = null!;

        private bool _disposed;

        // Set by PluginManager. Invoked when the context wants to be fully torn
        // down (e.g. its DLL was deleted) so the manager drops + disposes it.
        internal Action? OnRequestRemoval { get; set; }

        private PluginLoader Loader { get; set; }

        // Whether this context's loader watches for changes (PluginHotReloadEnabled, minus a
        // fallback when the inotify limit was hit). RefreshLoader builds the same kind again.
        private bool _hotReload;

        private ServiceProvider? ServiceProvider { get; set; }

        public int PluginId { get; }

        private readonly ICommandManager _commandManager;
        private readonly IScriptHostConfiguration _hostConfiguration;
        private readonly string _path;
        // Only created when hot-reload is enabled, so genuinely optional.
        private readonly FileSystemWatcher? _fileWatcher;

        public string FilePath => _path;
        private IServiceScope _serviceScope = null!;

        public string? TerminationReason { get; private set; }

        // TOOD: ServiceCollection
        private ILogger _logger = CoreLogging.Factory.CreateLogger<PluginContext>();

        public PluginContext(IServiceProvider applicationServiceProvider, ICommandManager commandManager,
            IScriptHostConfiguration hostConfiguration,
            string path, int id)
        {
            _commandManager = commandManager;
            _hostConfiguration = hostConfiguration;
            _path = path;
            CssRootPath = hostConfiguration.RootPath;
            PluginId = id;

            // EnableHotReload makes McMaster open a FileSystemWatcher (one inotify instance on
            // Linux) inside the PluginLoader ctor. It used to be hard-coded true, so every plugin
            // cost an instance even with PluginHotReloadEnabled=false; on a box running several
            // servers under one user that exhausted fs.inotify.max_user_instances (1024) and the
            // ctor threw "The configured user limit (1024) on the number of inotify instances has
            // been reached", failing the plugin load (an admin plugin, in the live incident).
            var hotReload = CoreConfig.PluginHotReloadEnabled;
            try
            {
                Loader = CreateLoader(path, hotReload);
            }
            catch (IOException ex) when (hotReload && InotifyDiagnostics.IsInotifyLimitError(ex))
            {
                // Watching is a convenience; loading is not. Drop hot reload for this plugin
                // rather than refusing to load it.
                _logger.LogWarning("Hot reload disabled for {Path}: could not create a file watcher ({Message})",
                    path, ex.Message);
                hotReload = false;
                Loader = CreateLoader(path, hotReload: false);
            }

            _hotReload = hotReload;

            if (hotReload)
            {
                var watcher = new FileSystemWatcher
                {
                    Path = Path.GetDirectoryName(path)!
                };

                watcher.Deleted += async (s, e) =>
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

                watcher.Filter = "*.dll";
                try
                {
                    watcher.EnableRaisingEvents = true;
                    _fileWatcher = watcher;
                }
                catch (IOException ex) when (InotifyDiagnostics.IsInotifyLimitError(ex))
                {
                    // Same inotify limit as above; McMaster's reload still works, only delete
                    // detection is lost.
                    watcher.Dispose();
                    _logger.LogWarning("Delete detection disabled for {Path}: could not create a file watcher ({Message})",
                        path, ex.Message);
                }

                Loader.Reloaded += async (s, e) => await OnReloadedAsync(s, e);
            }
        }

        /// <summary>
        /// Replaces the loader with a new one, i.e. a new AssemblyLoadContext that reads the
        /// plugin's files from disk again. For css_plugins load/reload of an unloaded context:
        /// the old loader's context still holds the previously loaded assembly, so loading
        /// through it again ran the old code even after the DLL on disk was replaced. With a
        /// DLL that had been overwritten while mapped it also kept the corrupt image.
        /// </summary>
        internal void RefreshLoader()
        {
            if (State == PluginState.Loaded || State == PluginState.Loading)
                throw new InvalidOperationException("Unload the plugin before refreshing its loader.");

            var old = Loader;
            PluginLoader fresh;
            try
            {
                fresh = CreateLoader(_path, _hotReload);
            }
            catch (IOException ex) when (_hotReload && InotifyDiagnostics.IsInotifyLimitError(ex))
            {
                _logger.LogWarning("Hot reload disabled for {Path}: could not create a file watcher ({Message})",
                    _path, ex.Message);
                _hotReload = false;
                fresh = CreateLoader(_path, hotReload: false);
            }

            if (_hotReload)
                fresh.Reloaded += async (s, e) => await OnReloadedAsync(s, e);

            Loader = fresh;
            // Unloads the old AssemblyLoadContext (collectible) and stops its file watcher.
            old.Dispose();
        }

        private static PluginLoader CreateLoader(string path, bool hotReload)
        {
            return PluginLoader.CreateFromAssemblyFile(path,
                new[]
                {
                    typeof(IPlugin), typeof(ILogger), typeof(IServiceCollection), typeof(IPluginServiceCollection<>),
                    typeof(ICommandManager)
                }, config =>
                {
                    config.EnableHotReload = hotReload;
                    config.IsUnloadable = true;
                    config.PreferSharedTypes = true;
                });
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
                    LogLoadFailure(_logger, ex, _path, null, "failed to hot-reload");
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
                Diagnostics.PluginDiagnostics.Reset(Diagnostics.PluginDiagnostics.SafeAssemblyName(defaultAssembly));

                Type? pluginType = defaultAssembly.GetExportedTypes()
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
                    var loggerConfig = new LoggerConfiguration()
                        .Enrich.FromLogContext()
                        .Enrich.With(new PluginNameEnricher(this))
                        // Same ANSI treatment as the core logger (CoreLogging.cs): the shared
                        // theme colors the level (INF/WRN/ERR) so it survives docker/screen
                        // pipes, and PluginTag arrives pre-colored magenta to set plugin
                        // output apart from the cyan (cssharp:...) framework lines. Timestamp
                        // format, level width and tag padding are deliberately identical to
                        // the core logger's so both streams share one aligned column. Console
                        // sink only; the file sinks stay plain text.
                        // Async for the same reason as the core console sink (CoreLogging.cs):
                        // stdout is a pipe on a real server, and a console write from the game
                        // thread blocks once the pipe buffer fills. Plugins log far more often
                        // than the framework does, so this is the sink most likely to stall a
                        // tick. blockWhenFull: false drops events during a storm instead of
                        // pushing backpressure onto the tick; the file sinks remain the
                        // durable record.
                        .WriteTo.Async(a => a.Console(
                            theme: CoreLogging.ConsoleTheme,
                            outputTemplate:
                            "{Timestamp:HH:mm:ss.fff} [" + CoreLogging.LevelToken +
                            "] {PluginTag:l} {Message:lj}{NewLine}{Exception}"),
                            bufferSize: 10000, blockWhenFull: false)
                        // log-<date>.txt is written by ONE sink owned by CoreLogging that every
                        // plugin forwards into; this logger does not open the file itself.
                        // Opening it per plugin needed Serilog's shared mode, whose named
                        // Mutex is a PAL SharedMemory object — and closing one of those on
                        // .NET 10 frees a glibc pointer through libtier0.so's operator
                        // delete (g_pMemAlloc->Free), which SIGSEGVs the server. See the
                        // comment on CoreLogging.PluginAggregateLogger. Forwarding does not
                        // take ownership: WriteTo.Logger leaves the target undisposed, so
                        // the dispose: true below still only releases this plugin's sinks.
                        .WriteTo.Logger(CoreLogging.PluginAggregateLogger);

                    // This plugin's own file is opt-in (core.json "PluginLogFiles"): its lines
                    // already land in the shared log-<date>.txt, and one extra daily file per
                    // plugin is what filled logs/ with hundreds of files. When on, it runs
                    // through Async so file rolls + Serilog's retention scan run off the game
                    // thread (a synchronous roll was measured at ~469ms on the game thread).
                    // One Async wrapper = one background queue/thread per plugin. Single
                    // writer, so no shared mode is needed.
                    if (CoreConfig.PluginLogFiles)
                    {
                        loggerConfig.WriteTo.Async(a => a.File(
                            Path.Join(new[]
                            {
                                _hostConfiguration.RootPath, "logs",
                                $"log-{Diagnostics.PluginDiagnostics.SafeAssemblyName(pluginType.Assembly)}-.txt"
                            }), rollingInterval: RollingInterval.Day,
                            retainedFileCountLimit: CoreLogging.RetainedLogDays,
                            outputTemplate: CoreLogging.FileOutputTemplate));
                    }

                    builder.AddSerilog(loggerConfig.CreateLogger(),
                        // dispose: true is what actually makes the unload path's
                        // ServiceProvider.Dispose() release this logger. Without it
                        // AddSerilog defaults to dispose: false, so every reload leaked
                        // the plugin's open log file handles AND (now that the console
                        // sink is async too) one background sink thread per reload. On a
                        // hot-reload server those accumulate into the thread-starvation
                        // that shows up as "SteamNetworkingSockets lock held for N ms".
                        dispose: true);
                });

                Type interfaceType = typeof(IPluginServiceCollection<>).MakeGenericType(pluginType);
                // GetLoadableTypes, NOT assembly.GetTypes(): a failed/leaked plugin ALC
                // can leave an assembly in the AppDomain whose own dependency (e.g. a
                // missing 'shared' lib) won't resolve. Raw GetTypes() then throws
                // ReflectionTypeLoadException for that ONE dead assembly and aborts the
                // whole scan, so every later plugin fails the load with the unrelated
                // assembly's missing-dependency error. Swallowing per-assembly keeps the
                // blame on the plugin that actually needs the missing shared library.
                //
                // Only this plugin's own AssemblyLoadContext is scanned: an implementation of
                // IPluginServiceCollection<ThisPlugin> has to reference the plugin type, so it
                // lives in the plugin or a library loaded beside it. Scanning every assembly in
                // the AppDomain cost O(plugins x all types) at boot.
                var pluginAssemblies = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(defaultAssembly)?.Assemblies
                                       ?? new[] { defaultAssembly };
                Type[] serviceCollectionConfiguratorTypes = pluginAssemblies
                    .SelectMany(GetLoadableTypes)
                    .Where(type => interfaceType.IsAssignableFrom(type) && !type.IsInterface && !type.IsAbstract)
                    .ToArray();

                if (serviceCollectionConfiguratorTypes.Any())
                {
                    foreach (var t in serviceCollectionConfiguratorTypes)
                    {
                        var pluginServiceCollection = Activator.CreateInstance(t);
                        MethodInfo? method = t.GetMethod("ConfigureServices");
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

                // Until the instance exists, a throw is a pre-instance failure that the caller
                // reports; release what this attempt built so it is not leaked per retry.
                IPlugin plugin;
                try
                {
                    plugin = CreatePluginInstance(pluginType);
                }
                catch
                {
                    _serviceScope?.Dispose();
                    ServiceProvider?.Dispose();
                    ServiceProvider = null;
                    throw;
                }

                Plugin = plugin;

                State = PluginState.Loading;

                // Wiring and config run plugin code (attribute registration, OnConfigParsed)
                // and can throw on a bad plugin config. Outside a try that left the context
                // in State=Loading with its commands registered and the ServiceProvider live,
                // and on reload/hot-reload paths nothing cleaned it up. Treat it like a
                // Load() failure: report, unload, stay listed with the reason.
                try
                {
                    Plugin.ModulePath = _path;
                    Plugin.Logger = _serviceScope.ServiceProvider.GetRequiredService<ILoggerFactory>()
                        .CreateLogger(pluginType);
                    Plugin.CommandManager = _serviceScope.ServiceProvider.GetRequiredService<ICommandManager>();
                    Plugin.RegisterAllAttributes(Plugin);
                    Plugin.Localizer = ServiceProvider.GetRequiredService<IStringLocalizer>();
                    Plugin.Logger = ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(pluginType);

                    Plugin.InitializeConfig(Plugin, pluginType);
                }
                catch (Exception ex)
                {
                    LogLoadFailure(_logger, ex, _path, Plugin, "failed to initialize");
                    this.TerminationReason = ex.Message ?? "Unknown";
                    Unload(hotReload);
                    return;
                }

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
                NativeAPI.SetFatalSuspectPlugin(Plugin.ModuleName ?? Diagnostics.PluginDiagnostics.SafeAssemblyName(Plugin.GetType().Assembly));
                // Attributes callbacks the plugin registers while loading (wrapped by BasePlugin) to it.
                var previousCrashOwner = CrashRecorder.EnterPlugin(Diagnostics.PluginDiagnostics.SafeAssemblyName(Plugin.GetType().Assembly), "load");
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
                        var report = LogLoadFailure(_logger, ex, _path, Plugin);
                        this.TerminationReason = ex.Message ?? "Unknown";

                        // Stash the rendered banner keyed by assembly name. If this plugin
                        // leaks a callback after the failed load, the runtime dead-callback
                        // path re-pastes THIS exact banner (capped) so an operator who missed
                        // it the first time still gets the full, actionable report.
                        FunctionReference.RecordLoadFailure(
                            Diagnostics.PluginDiagnostics.SafeAssemblyName(Plugin.GetType().Assembly),
                            Plugin.ModuleName!,
                            report);
                    }

                    CrashRecorder.Exit(previousCrashOwner);
                    Unload(hotReload);
                    return;
                }

                CrashRecorder.Exit(previousCrashOwner);
                loadTimer.Stop();
                // Loaded cleanly: clear the suspect so a later unrelated fatal does not
                // blame this plugin. A failed load deliberately leaves it set, because
                // that is exactly when the leftover-callback crash fires (next frame).
                NativeAPI.SetFatalSuspectPlugin(string.Empty);
                // Boot-time per-plugin lines are Debug: the startup summary table already
                // lists every plugin, so at Info this was N duplicate lines interleaved
                // with plugin stdout. A hot reload has no summary table after it, so that
                // one stays at Info.
                if (hotReload)
                    _logger.LogInformation("Reloaded plugin {Name} in {Ms}ms", Plugin.ModuleName, loadTimer.ElapsedMilliseconds);
                else
                    _logger.LogDebug("Finished loading plugin {Name} in {Ms}ms", Plugin.ModuleName, loadTimer.ElapsedMilliseconds);

                State = PluginState.Loaded;
            }
        }


        // Version gate + DI resolution of the plugin type; throws for pre-instance failures.
        private IPlugin CreatePluginInstance(Type pluginType)
        {
            var minimumApiVersion = pluginType.GetCustomAttribute<MinimumApiVersion>()?.Version;
            var currentVersion = Api.GetVersion();

            // Ignore version 0 for local development
            if (currentVersion > 0 && minimumApiVersion != null && minimumApiVersion > currentVersion)
                throw new Exception(
                    $"Plugin \"{Path.GetFileName(_path)}\" requires a newer version of CounterStrikeSharp. The plugin expects version [{minimumApiVersion}] but the current version is [{currentVersion}].");

            // Pre-load line is Debug — the "Finished loading … in Nms" line below plus
            // the startup summary table already cover the roster. Keeps boot output lean.
            _logger.LogDebug("Loading plugin {Name}", Diagnostics.PluginDiagnostics.SafeAssemblyName(pluginType.Assembly));

            _serviceScope = ServiceProvider!.CreateScope();

            var plugin = _serviceScope.ServiceProvider.GetRequiredService(pluginType) as IPlugin;

            if (plugin == null) throw new Exception("Unable to create plugin instance");

            return plugin;
        }

        // Domain-wide reflection must tolerate assemblies whose dependencies don't
        // resolve (leaked dead ALCs, a plugin missing its 'shared' lib). Return the
        // types that DID resolve instead of throwing and killing the whole scan.
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
        }

        // The CounterStrikeSharp root (addons/counterstrikesharp), for relative paths in reports and
        // for the dependency scan. Set by every context from its host configuration.
        internal static string? CssRootPath { get; set; }

        /// <summary>
        /// Logs a plugin load failure the way operators read it: one ERR line with the cause, then
        /// the blame banner. The raw exception, mostly reflection frames that the banner already
        /// condenses into Location, goes to Debug. Returns the banner.
        /// </summary>
        internal static string LogLoadFailure(ILogger logger, Exception ex, string path, IPlugin? plugin,
            string what = "failed to load")
        {
            var name = plugin?.ModuleName ?? System.IO.Path.GetFileNameWithoutExtension(path);
            logger.LogError("{Plugin} {What}: {Summary}", name, what, Summarize(ex));
            logger.LogDebug(ex, "Full exception for {Plugin}", name);
            var report = BuildReport(ex, path, plugin);
            logger.LogError("\n{Report}", report);
            return report;
        }

        // One line: "missing dependency X 1.2.3.0", or "ExceptionType: first line of the message".
        internal static string Summarize(Exception ex)
        {
            var root = ex.GetBaseException();
            var missing = DependencyDiagnostics.MissingAssembly(root);
            if (missing != null)
                return "missing dependency " + missing.Name + (missing.Version != null ? " " + missing.Version : "");
            return root.GetType().Name + ": " + FirstLine(root.Message);
        }

        internal static string BuildLoadFailureReport(Exception ex, IPlugin plugin) =>
            BuildReport(ex, plugin.ModulePath ?? "", plugin);

        internal static string BuildLoadFailureReportFromPath(Exception ex, string path) => BuildReport(ex, path, null);

        // Builds the blame banner. Decides from the exception and stack whether the fault is an
        // install problem (missing dependency / unreadable deps.json), a DLL that is not a plugin,
        // an API version mismatch, the plugin's own code, or CounterStrikeSharp, and says what to
        // do. ASCII only: a game-panel console that re-decodes the log as Latin-1 showed the old
        // em dashes as "â€”" garbage.
        private static string BuildReport(Exception ex, string path, IPlugin? plugin)
        {
            var root = ex.GetBaseException();
            var name = plugin?.ModuleName ?? System.IO.Path.GetFileNameWithoutExtension(path);
            var pluginAsmName = plugin != null
                ? Diagnostics.PluginDiagnostics.SafeAssemblyName(plugin.GetType().Assembly)
                : System.IO.Path.GetFileNameWithoutExtension(path);

            // Closest-to-throw frame in the plugin's own assembly, and whether the throw started in core.
            System.Diagnostics.StackFrame? pluginFrame = null;
            bool throwInsideCssharp = false;
            var frames = new System.Diagnostics.StackTrace(root, true).GetFrames() ?? Array.Empty<System.Diagnostics.StackFrame>();
            for (int i = 0; i < frames.Length; i++)
            {
                var asm = frames[i].GetMethod()?.DeclaringType?.Assembly;
                if (asm == null) continue;
                if (i == 0) throwInsideCssharp = asm == typeof(PluginContext).Assembly;
                if (pluginFrame == null && Diagnostics.PluginDiagnostics.SafeAssemblyName(asm) == pluginAsmName) pluginFrame = frames[i];
            }

            var missing = DependencyDiagnostics.MissingAssembly(root);
            if (missing != null && string.Equals(missing.Name, pluginAsmName, StringComparison.OrdinalIgnoreCase))
                missing = null; // the plugin DLL itself is missing/unreadable, not a dependency

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("==================== PLUGIN LOAD FAILURE ====================");
            sb.AppendLine("Plugin:    " + name);
            if (!string.IsNullOrEmpty(path))
                sb.AppendLine("File:      " + RelativeToRoot(path));
            sb.AppendLine("Error:     " + (missing != null
                ? "Could not load '" + missing.Name + "'" + (missing.Version != null ? " version " + missing.Version : "")
                : root.GetType().Name + ": " + Truncate(FirstLine(root.Message), 300)));

            if (pluginFrame != null)
            {
                var m = pluginFrame.GetMethod();
                var file = pluginFrame.GetFileName();
                sb.AppendLine("Location:  " + m?.DeclaringType?.FullName + "." + m?.Name + "()" +
                              (file != null ? " (" + System.IO.Path.GetFileName(file) + ":" + pluginFrame.GetFileLineNumber() + ")" : ""));
            }

            if (missing != null)
            {
                AppendMissingDependency(sb, DependencyDiagnostics.Analyze(missing, path, CssRootPath), name);
            }
            else if (IsDependencyResolutionFailure(root))
            {
                sb.AppendLine("Blame:     Install problem - the .NET host could not read this plugin's dependency list. The plugin itself is probably fine.");
                sb.AppendLine("Fix:       " + FixHintFor(root));
                sb.AppendLine("Action:    Re-upload the plugin's complete release (all files), then load it again.");
            }
            else if ((root.Message ?? "").Contains("Unable to find plugin in assembly"))
            {
                sb.AppendLine("Blame:     Not a plugin - this DLL has no BasePlugin/IPlugin class.");
                sb.AppendLine("Fix:       " + FixHintFor(root));
                sb.AppendLine("Action:    Move it to shared/ if it is a library, or remove it from plugins/.");
            }
            else if ((root.Message ?? "").Contains("requires a newer version of CounterStrikeSharp"))
            {
                sb.AppendLine("Blame:     Version - the plugin needs a newer CounterStrikeSharp.");
                sb.AppendLine("Action:    Update CounterStrikeSharp, or use an older release of the plugin.");
            }
            else if (throwInsideCssharp && pluginFrame == null)
            {
                sb.AppendLine("Blame:     CounterStrikeSharp - no plugin code in the call chain. Possibly a framework/gamedata issue.");
                if (FixHintFor(root) is { } hint) sb.AppendLine("Fix:       " + hint);
                sb.AppendLine("Action:    Report it with this log (LogVerbosity \"debug\" adds the full stack trace).");
            }
            else
            {
                sb.AppendLine("Blame:     Plugin '" + name + "' - the failure is in the plugin's own code.");
                if (FixHintFor(root) is { } hint) sb.AppendLine("Fix:       " + hint);
                sb.AppendLine("Action:    Disable the plugin, or send this log to its author (LogVerbosity \"debug\" adds the full stack trace).");
            }

            sb.Append("============================================================");
            return sb.ToString();
        }

        private static void AppendMissingDependency(System.Text.StringBuilder sb, DependencyDiagnostics.Finding f, string pluginName)
        {
            var dep = f.Missing.Name!;
            var required = f.Missing.Version;

            sb.AppendLine("Blame:     Install problem - a file the plugin needs is missing. The plugin itself is probably fine.");

            if (f.Copies.Count == 0)
            {
                sb.AppendLine("Found:     " + dep + ".dll is not anywhere under plugins/ or shared/.");
            }
            else
            {
                foreach (var copy in f.Copies.Take(3))
                {
                    var note = copy.Version != null && required != null && copy.Version < required
                        ? " (older than the required " + required + ")"
                        : "";
                    sb.AppendLine("Found:     " + dep + ".dll " + (copy.Version?.ToString() ?? "?") + " in " +
                                  RelativeToRoot(System.IO.Path.GetDirectoryName(copy.Path)!) + "/" + note);
                }
            }

            var usable = f.Copies.FirstOrDefault(c => required == null || c.Version == null || c.Version >= required);

            switch (f.ShipsWithPlugin)
            {
                case true:
                    sb.AppendLine("Expects:   " + pluginName + "'s own release includes " + dep + ".dll" +
                                  (f.DepsVersion != null ? " " + f.DepsVersion : "") + " (per its .deps.json).");
                    sb.AppendLine("Fix:       Put " + dep + ".dll next to " + pluginName + ".dll - re-upload the plugin's complete release" +
                                  (usable != null ? ", or copy it from " + RelativeToRoot(usable.Path) : "") + ".");
                    break;
                case false:
                    sb.AppendLine("Expects:   " + dep + " to be provided by the server, not shipped with " + pluginName + " (per its .deps.json).");
                    sb.AppendLine("Fix:       Install the plugin/library that provides " + dep + " into shared/" + dep + "/" + dep + ".dll" +
                                  (required != null ? " (version " + required + " or newer)" : "") + ".");
                    break;
                default:
                    sb.AppendLine("Expects:   Unknown - " + pluginName + " has no .deps.json saying whether " + dep + " ships with it or belongs in shared/.");
                    sb.AppendLine("Fix:       Check the plugin's install instructions for " + dep + ".");
                    break;
            }

            sb.AppendLine("Action:    Do not delete the plugin; install the missing file and load it again.");
        }

        internal static string RelativeToRoot(string path)
        {
            if (string.IsNullOrEmpty(CssRootPath)) return path;
            var relative = System.IO.Path.GetRelativePath(CssRootPath, path);
            return relative.StartsWith("..", StringComparison.Ordinal) ? path : relative.Replace('\\', '/');
        }

        private static string FirstLine(string? message)
        {
            if (string.IsNullOrEmpty(message)) return "";
            var newline = message.IndexOfAny(new[] { '\r', '\n' });
            return newline >= 0 ? message[..newline] : message;
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

        // True when the load died inside .NET host dependency resolution (McMaster's
        // AssemblyDependencyResolver ctor). Means the runtime couldn't read this DLL's
        // dependency metadata — an install/packaging fault, not bad plugin code.
        internal static bool IsDependencyResolutionFailure(Exception ex)
        {
            var msg = ex.Message ?? string.Empty;
            return msg.Contains("Dependency resolution failed")
                   || msg.Contains("Failed to locate managed application");
        }

        // Maps common load-failure exceptions to a one-line remediation hint.
        internal static string? FixHintFor(Exception ex)
        {
            var msg = ex.Message ?? string.Empty;

            // Checked first: the runtime's message contains "configured", which the generic
            // config hint further down would otherwise claim.
            if (InotifyDiagnostics.IsInotifyLimitError(ex))
                return InotifyDiagnostics.IsWatchLimitError(ex)
                    ? "The plugin watches files, and this Linux user has no inotify watches left "
                      + "(fs.inotify.max_user_watches, shared by every process the user runs). Raise that limit, "
                      + "or ask the author to watch fewer directories."
                    : "The plugin creates a file watcher (FileSystemWatcher), and this Linux user has no inotify "
                      + "instances left. The limit (fs.inotify.max_user_instances) is shared by every process the "
                      + "user runs, so other servers count too. "
                      + (InotifyDiagnostics.DescribeUsage() is { } usage ? usage + " " : "")
                      + "Raise the limit, run fewer servers per user, or ask the author to make the watcher optional.";

            if (IsDependencyResolutionFailure(ex))
                return "The .NET host could not initialize dependency resolution for this DLL. Usually an INSTALL "
                     + "problem, not a broken plugin: a missing/corrupt '<PluginName>.deps.json', an incomplete "
                     + "upload (only the .dll copied without its companion files), or the file was missing/locked "
                     + "when load ran. Re-upload the plugin's complete release (all files, intact folder structure), "
                     + "then load it again.";

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

                if (simpleName != null)
                    return "Missing dependency '" + simpleName + "'. If it's a SHARED library (an API other plugins "
                         + "use), install it in the 'shared' folder (addons/counterstrikesharp/shared/" + simpleName
                         + "/" + simpleName + ".dll). Otherwise ship it next to the plugin's own .dll. Install the "
                         + "plugin/library that provides '" + simpleName + "'.";

                return "A dependency DLL is missing or mismatched. If it's a SHARED library, install it in "
                     + "addons/counterstrikesharp/shared/<Name>/<Name>.dll; otherwise ship it next to the plugin's .dll.";
            }

            if (msg.Contains("Gamedata key") || msg.Contains("gamedata.json"))
                return "Ship/update the missing signature. If plugin-specific: drop the plugin's own "
                     + "'<PluginName>.gamedata.json' into the gamedata folder (or update it). If core: add the key "
                     + "to gamedata.json - a CS2 update may have renamed/removed it.";

            if (ex is TypeInitializationException && (msg.Contains("GameData") || msg.Contains("Signature")))
                return "A gamedata signature is missing/outdated. Update gamedata.json - this can be a CS2 update issue.";

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

            // Safe name: a plugin whose DLL was overwritten while mapped has unreadable metadata,
            // and GetName() throwing here aborted the unload before anything was torn down.
            var previousCrashOwner = CrashRecorder.EnterPlugin(Diagnostics.PluginDiagnostics.SafeAssemblyName(Plugin.GetType().Assembly), "unload");
            try
            {
                Plugin.Unload(hotReload);
            }
            catch (Exception ex)
            {
                // The exception used to be dropped, leaving no clue why the unload failed.
                _logger.LogError(ex, "Plugin {Name} threw from Unload(), forcing cleanup", Plugin.ModuleName);
                return;
            }
            finally
            {
                CrashRecorder.Exit(previousCrashOwner);
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